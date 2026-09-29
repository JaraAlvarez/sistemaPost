using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;

namespace Pos.Infrastructure.Settings;

internal sealed class SettingsCatalog : ISettingsCatalog
{
    private readonly Dictionary<string, SettingDefinition> _definitions;

    public SettingsCatalog(IEnumerable<ISettingDefinitionProvider> providers)
    {
        _definitions = new Dictionary<string, SettingDefinition>(StringComparer.Ordinal);
        foreach (var definition in providers.SelectMany(p => p.GetDefinitions()))
        {
            if (!_definitions.TryAdd(definition.Key, definition))
            {
                throw new InvalidOperationException($"La configuración '{definition.Key}' está declarada dos veces.");
            }
        }
    }

    public IReadOnlyCollection<SettingDefinition> All => _definitions.Values;

    public SettingDefinition? Find(string key) => _definitions.GetValueOrDefault(key);
}

/// <summary>
/// Caché de excepciones por nivel (empresa, sucursal o caja). Vive en el servidor: en multicaja todas las cajas leen
/// del mismo servidor, así que no hay incoherencias. Se invalida DESPUÉS del commit de cada escritura.
/// </summary>
internal sealed class SettingsCache(NpgsqlDataSource dataSource)
{
    private readonly ConcurrentDictionary<(string ScopeType, Guid ScopeId), Lazy<Task<Dictionary<string, JsonElement>>>> _scopes = new();

    public async Task<Dictionary<string, JsonElement>> GetScopeAsync(string scopeType, Guid scopeId)
    {
        var entry = _scopes.GetOrAdd(
            (scopeType, scopeId),
            key => new Lazy<Task<Dictionary<string, JsonElement>>>(() => LoadAsync(key.ScopeType, key.ScopeId)));
        try
        {
            return await entry.Value;
        }
        catch
        {
            // Un error de lectura no queda en caché: el siguiente intento vuelve a consultar la BD.
            _scopes.TryRemove(new KeyValuePair<(string, Guid), Lazy<Task<Dictionary<string, JsonElement>>>>((scopeType, scopeId), entry));
            throw;
        }
    }

    public void Invalidate(string scopeType, Guid scopeId) => _scopes.TryRemove((scopeType, scopeId), out _);

    private async Task<Dictionary<string, JsonElement>> LoadAsync(string scopeType, Guid scopeId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync<(string Key, string Value)>(
            "SELECT key, value::text FROM system.settings WHERE scope_type = @scopeType AND scope_id = @scopeId",
            new { scopeType, scopeId });
        return rows.ToDictionary(r => r.Key, r => JsonDocument.Parse(r.Value).RootElement.Clone(), StringComparer.Ordinal);
    }
}

internal sealed class SettingsReader(SettingsCache cache) : ISettingsReader
{
    public async Task<ResolvedSetting<T>> ResolveAsync<T>(
        SettingDefinition<T> definition, SettingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        var chain = await SettingsResolution.ResolveChainAsync(cache, definition, context);
        return new ResolvedSetting<T>(
            definition.Deserialize(chain[0].Value),
            chain[0].Source,
            definition.Deserialize(chain.Count > 1 ? chain[1].Value : chain[0].Value),
            chain.Count > 1 ? chain[1].Source : chain[0].Source);
    }
}

/// <summary>Resolución caja → sucursal → empresa → defecto (solo los niveles que la definición admite).</summary>
internal static class SettingsResolution
{
    /// <summary>Valores disponibles del más específico al menos específico; el último siempre es el defecto del código.</summary>
    public static async Task<List<(JsonElement Value, SettingSource Source)>> ResolveChainAsync(
        SettingsCache cache, SettingDefinition definition, SettingContext context)
    {
        var chain = new List<(JsonElement, SettingSource)>();
        if (context.PosTerminalId is { } terminal && definition.Scopes.HasFlag(SettingScope.Terminal)
            && (await cache.GetScopeAsync("TERMINAL", terminal)).TryGetValue(definition.Key, out var t))
        {
            chain.Add((t, SettingSource.Terminal));
        }

        if (context.BranchId is { } branch && definition.Scopes.HasFlag(SettingScope.Branch)
            && (await cache.GetScopeAsync("BRANCH", branch)).TryGetValue(definition.Key, out var b))
        {
            chain.Add((b, SettingSource.Branch));
        }

        if (definition.Scopes.HasFlag(SettingScope.Company)
            && (await cache.GetScopeAsync("COMPANY", context.CompanyId)).TryGetValue(definition.Key, out var c))
        {
            chain.Add((c, SettingSource.Company));
        }

        chain.Add((definition.DefaultJson, SettingSource.Default));
        return chain;
    }
}

internal sealed class SettingsWriter(
    PosDbContext context,
    ISettingsCatalog catalog,
    SettingsCache cache,
    IAuditWriter audit,
    IOutbox outbox,
    IIdGenerator ids,
    CommitCallbacks callbacks) : ISettingsWriter
{
    public const string SyncEventType = "settings.setting_changed.v1";

    public async Task<Result> SetAsync(SettingTarget target, string key, JsonElement value, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateTargetAsync(target, key, cancellationToken);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var definition = validation.Value;
        var invalid = definition.Validate(value);
        if (invalid is not null)
        {
            return Error.Validation("SETTINGS.INVALID_VALUE", $"Valor inválido para '{key}': {invalid}");
        }

        var scopeType = ScopeType(target.Scope);
        var record = await context.Set<SettingRecord>()
            .SingleOrDefaultAsync(s => s.ScopeType == scopeType && s.ScopeId == target.ScopeId && s.Key == key, cancellationToken);
        var oldValue = record?.Value.RootElement.GetRawText();
        var newDocument = JsonDocument.Parse(value.GetRawText());

        if (record is null)
        {
            record = new SettingRecord
            {
                Id = ids.NewId(),
                CompanyId = target.CompanyId,
                ScopeType = scopeType,
                ScopeId = target.ScopeId,
                Key = key,
                Value = newDocument,
            };
            context.Add(record);
        }
        else if (oldValue == newDocument.RootElement.GetRawText())
        {
            return Result.Success();
        }
        else
        {
            record.Value = newDocument;
            record.RowVersion++;
        }

        await RecordChangeAsync(target, key, record.Id, oldValue, newDocument.RootElement.GetRawText(), record.RowVersion, cancellationToken);
        return Result.Success();
    }

    public async Task<Result> RemoveAsync(SettingTarget target, string key, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateTargetAsync(target, key, cancellationToken);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var scopeType = ScopeType(target.Scope);
        var record = await context.Set<SettingRecord>()
            .SingleOrDefaultAsync(s => s.ScopeType == scopeType && s.ScopeId == target.ScopeId && s.Key == key, cancellationToken);
        if (record is null)
        {
            return Error.NotFound("SETTINGS.OVERRIDE_NOT_FOUND", $"No hay una excepción de '{key}' en ese nivel.");
        }

        context.Remove(record);
        await RecordChangeAsync(target, key, record.Id, record.Value.RootElement.GetRawText(), null, record.RowVersion + 1, cancellationToken);
        return Result.Success();
    }

    public async Task<IReadOnlyList<EffectiveSetting>> GetEffectiveAsync(SettingContext settingContext, CancellationToken cancellationToken = default)
    {
        var result = new List<EffectiveSetting>();
        foreach (var definition in catalog.All.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            var chain = await SettingsResolution.ResolveChainAsync(cache, definition, settingContext);
            var inherited = chain.Count > 1 ? chain[1] : chain[0];
            result.Add(new EffectiveSetting(
                definition.Key,
                definition.Description,
                chain[0].Value,
                chain[0].Source,
                inherited.Value,
                inherited.Source,
                [.. Enum.GetValues<SettingScope>().Where(s => s != SettingScope.None && definition.Scopes.HasFlag(s))]));
        }

        return result;
    }

    private async Task RecordChangeAsync(
        SettingTarget target, string key, Guid settingId, string? oldValue, string? newValue, long rowVersion, CancellationToken cancellationToken)
    {
        var scopeType = ScopeType(target.Scope);
        await audit.WriteAsync(
            new AuditEntry(
                Module: "settings",
                Action: newValue is null ? "SETTING_OVERRIDE_REMOVED" : "SETTING_CHANGED",
                EntityType: "Setting",
                EntityId: settingId,
                EntityLabel: $"{key} · {scopeType}",
                Summary: newValue is null
                    ? $"Se eliminó la excepción de {key} en {scopeType}: vuelve a heredar."
                    : $"{key} en {scopeType}: {oldValue ?? "(heredado)"} → {newValue}",
                OldValues: new Dictionary<string, object?> { ["value"] = oldValue },
                NewValues: new Dictionary<string, object?> { ["value"] = newValue }),
            cancellationToken);

        outbox.Enqueue(
            SyncEventType,
            new { target.CompanyId, ScopeType = scopeType, target.ScopeId, Key = key, Value = newValue, RowVersion = rowVersion },
            OutboxDestination.Sync);

        callbacks.OnCommitted(() => cache.Invalidate(scopeType, target.ScopeId));
    }

    private async Task<Result<SettingDefinition>> ValidateTargetAsync(SettingTarget target, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var definition = catalog.Find(key);
        if (definition is null)
        {
            return Error.NotFound("SETTINGS.UNKNOWN_KEY", $"La configuración '{key}' no existe.");
        }

        if (target.Scope is not (SettingScope.Company or SettingScope.Branch or SettingScope.Terminal))
        {
            return Error.Validation("SETTINGS.INVALID_SCOPE", "Indique un solo nivel: empresa, sucursal o caja.");
        }

        if (!definition.Scopes.HasFlag(target.Scope))
        {
            return Error.BusinessRule("SETTINGS.SCOPE_NOT_ALLOWED", $"'{key}' no admite excepciones a nivel {target.Scope}.");
        }

        var connection = context.Database.GetDbConnection();
        var sql = target.Scope switch
        {
            SettingScope.Company => "SELECT EXISTS (SELECT 1 FROM org.companies WHERE id = @id AND id = @company)",
            SettingScope.Branch => "SELECT EXISTS (SELECT 1 FROM org.branches WHERE id = @id AND company_id = @company AND deleted_at IS NULL)",
            _ => "SELECT EXISTS (SELECT 1 FROM org.pos_terminals WHERE id = @id AND company_id = @company AND deleted_at IS NULL)",
        };
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            sql,
            new { id = target.ScopeId, company = target.CompanyId },
            context.Database.CurrentTransaction?.GetDbTransaction(),
            cancellationToken: cancellationToken));

        return exists
            ? definition
            : Error.NotFound("SETTINGS.SCOPE_NOT_FOUND", "La empresa, sucursal o caja indicada no existe.");
    }

    private static string ScopeType(SettingScope scope) => scope switch
    {
        SettingScope.Company => "COMPANY",
        SettingScope.Branch => "BRANCH",
        _ => "TERMINAL",
    };
}

/// <summary>Acciones que se ejecutan solo si la transacción del caso de uso se confirma (p. ej. invalidar cachés).</summary>
internal sealed class CommitCallbacks
{
    private readonly List<Action> _actions = [];

    public void OnCommitted(Action action) => _actions.Add(action);

    public void RunAndClear()
    {
        foreach (var action in _actions)
        {
            action();
        }

        _actions.Clear();
    }

    public void Clear() => _actions.Clear();
}
