using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Cloud.Abstractions;
using Pos.Infrastructure.Auditing;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Infrastructure.Persistence;

/// <summary>
/// Se ejecuta en cada SaveChanges, DENTRO de la transacción (mismo orden que el del POS):
/// 1. columnas de control (created/updated) con el usuario del portal o el usuario técnico <c>system</c>;
/// 2. auditoría automática de las entidades <see cref="AuditedAttribute"/> (solo campos modificados; <see cref="SensitiveAttribute"/> como ***);
/// 3. seq de auditoría reservado en la transacción, contexto (usuario, sesión, IP, correlación) y row_hash de cada fila nueva.
/// </summary>
internal sealed class CloudSaveChangesInterceptor(
    IClock clock,
    IIdGenerator ids,
    IPortalUserContext user,
    IRequestContext request,
    IInstallationContext node) : SaveChangesInterceptor
{
    public const string Masked = "***";

    private static readonly HashSet<string> ControlProperties =
        [ModelConventions.CreatedAt, ModelConventions.CreatedBy, ModelConventions.UpdatedAt, ModelConventions.UpdatedBy, ModelConventions.Xmin];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not { } context)
        {
            return result;
        }

        var now = AuditHasher.TruncateToMicroseconds(clock.UtcNow);
        var entries = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        ApplyControlColumns(entries, now);
        CaptureAudit(context, entries, now);
        await FinalizeAuditRowsAsync(context, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        throw new NotSupportedException("Use SaveChangesAsync.");

    private Guid Actor => user.UserId ?? SystemActor.Id;

    private void ApplyControlColumns(List<EntityEntry> entries, DateTimeOffset now)
    {
        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"En la nube nada se borra (L-09): {entry.Metadata.ClrType.Name} debe cambiar de estado en lugar de eliminarse.");
            }

            if (entry.State == EntityState.Added && entry.Metadata.FindProperty(ModelConventions.CreatedBy) is not null)
            {
                entry.Property(ModelConventions.CreatedAt).CurrentValue = now;
                entry.Property(ModelConventions.CreatedBy).CurrentValue = Actor;
            }

            if (entry.State == EntityState.Modified && entry.Metadata.FindProperty(ModelConventions.UpdatedBy) is not null)
            {
                entry.Property(ModelConventions.UpdatedAt).CurrentValue = now;
                entry.Property(ModelConventions.UpdatedBy).CurrentValue = Actor;
            }
        }
    }

    private void CaptureAudit(DbContext context, List<EntityEntry> entries, DateTimeOffset now)
    {
        foreach (var entry in entries)
        {
            var audited = entry.Metadata.ClrType.GetCustomAttribute<AuditedAttribute>();
            if (audited is null)
            {
                continue;
            }

            var created = entry.State == EntityState.Added;
            var newValues = Values(entry, p => created || p.IsModified, current: true);
            if (newValues.Count == 0)
            {
                continue;
            }

            var oldValues = created ? null : Values(entry, p => p.IsModified, current: false);
            var label = (entry.Entity as IHasAuditLabel)?.AuditLabel;
            context.Add(new AuditLogRecord
            {
                Id = ids.NewId(),
                OccurredAt = now,
                Module = audited.Module,
                Action = $"{ToUpperSnake(entry.Metadata.ClrType.Name)}_{(created ? "CREATED" : "UPDATED")}",
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = entry.Metadata.FindPrimaryKey()?.Properties is [{ ClrType: var keyType } key] && keyType == typeof(Guid)
                    ? (Guid?)entry.Property(key.Name).CurrentValue
                    : null,
                EntityLabel = label is { Length: > 200 } ? label[..200] : label,
                OldValues = ToDocument(oldValues),
                NewValues = ToDocument(newValues),
                Severity = "INFO",
            });
        }
    }

    /// <summary>Completa contexto, seq (reservado en la transacción) y row_hash de las filas de auditoría nuevas.</summary>
    private async Task FinalizeAuditRowsAsync(DbContext context, CancellationToken cancellationToken)
    {
        var pending = context.ChangeTracker.Entries<AuditLogRecord>()
            .Where(e => e.State == EntityState.Added && e.Entity.Seq == 0)
            .Select(e => e.Entity)
            .OrderBy(r => r.OccurredAt)
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var sequence = await context.Database
            .SqlQuery<long>($"""SELECT nextval(pg_get_serial_sequence('audit.audit_log', 'seq')) AS "Value" FROM generate_series(1, {pending.Count})""")
            .ToListAsync(cancellationToken);
        sequence.Sort();

        for (var i = 0; i < pending.Count; i++)
        {
            var row = pending[i];
            row.Seq = sequence[i];
            row.NodeId = node.NodeId;
            row.OccurredAt = AuditHasher.TruncateToMicroseconds(row.OccurredAt);
            row.UserId ??= Actor;
            row.UserDisplayName ??= user.DisplayName ?? SystemActor.DisplayName;
            row.SessionId ??= user.SessionId;
            row.IpAddress ??= request.IpAddress;
            row.CorrelationId ??= request.CorrelationId;
            row.HashVersion = AuditHasher.CurrentVersion;
            row.RowHash = AuditHasher.ComputeRowHash(row);
        }
    }

    private static JsonObject Values(EntityEntry entry, Func<PropertyEntry, bool> include, bool current)
    {
        var values = new JsonObject();
        var table = StoreObjectIdentifier.Table(entry.Metadata.GetTableName()!, entry.Metadata.GetSchema());
        foreach (var property in entry.Properties)
        {
            if (ControlProperties.Contains(property.Metadata.Name) || property.Metadata.IsPrimaryKey() || !include(property))
            {
                continue;
            }

            var member = property.Metadata.PropertyInfo;
            if (member?.GetCustomAttribute<NotAuditedAttribute>() is not null)
            {
                continue;
            }

            var column = property.Metadata.GetColumnName(table) ?? property.Metadata.Name;
            var raw = current ? property.CurrentValue : property.OriginalValue;
            var converter = property.Metadata.GetValueConverter();
            values[column] = member?.GetCustomAttribute<SensitiveAttribute>() is not null
                ? (raw is null ? null : Masked)
                : AuditValue(raw is not null && converter is not null ? converter.ConvertToProvider(raw) : raw);
        }

        return values;
    }

    /// <summary>Todo valor se registra como texto (salvo booleanos y null): el hash no depende de formatos numéricos.</summary>
    internal static JsonNode? AuditValue(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        Guid g => JsonValue.Create(g.ToString("D")),
        DateTimeOffset d => JsonValue.Create(
            AuditHasher.TruncateToMicroseconds(d).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)),
        DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        Enum e => JsonValue.Create(ToUpperSnake(e.ToString())),
        IFormattable f => JsonValue.Create(f.ToString(null, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.ToString()),
    };

    internal static JsonDocument? ToDocument(JsonObject? values) => values is null ? null : JsonDocument.Parse(values.ToJsonString());

    internal static string ToUpperSnake(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }
}
