using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Auditing;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Time;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Se ejecuta en cada SaveChanges, DENTRO de la transacción, en este orden:
/// 1. columnas de control (created/updated), borrado lógico y row_version;
/// 2. captura de auditoría de las entidades [Audited] (solo campos modificados; [Sensitive] como ***);
/// 3. eventos de dominio → outbox;
/// 4. reserva del seq de auditoría y cálculo de row_hash de todas las filas de auditoría pendientes.
/// </summary>
internal sealed class PosSaveChangesInterceptor(
    IClock clock,
    IIdGenerator ids,
    IActorContext actor,
    IRequestContext request,
    IInstallationContext installation) : SaveChangesInterceptor
{
    public const string Masked = "***";

    private static readonly HashSet<string> ControlProperties =
    [
        ModelConventions.CreatedAt, ModelConventions.CreatedBy, ModelConventions.UpdatedAt, ModelConventions.UpdatedBy,
        ModelConventions.DeletedAt, ModelConventions.DeletedBy, ModelConventions.RowVersion, ModelConventions.Xmin,
    ];

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
        PublishDomainEvents(context, entries, now);
        await FinalizeAuditRowsAsync(context, cancellationToken);

        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        throw new NotSupportedException("Use SaveChangesAsync.");

    private Guid RequireActor() =>
        actor.ActorId ?? throw new InvalidOperationException(
            "No hay autor para registrar el cambio: complete el asistente inicial o autentique la petición.");

    private void ApplyControlColumns(List<EntityEntry> entries, DateTimeOffset now)
    {
        foreach (var entry in entries)
        {
            var type = entry.Metadata;

            // Borrado lógico: el registro se conserva y se marca (RN-GEN-02).
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable)
            {
                entry.State = EntityState.Modified;
                entry.Property(ModelConventions.DeletedAt).CurrentValue = now;
                entry.Property(ModelConventions.DeletedBy).CurrentValue = RequireActor();
            }

            if (entry.State == EntityState.Added && type.FindProperty(ModelConventions.CreatedAt) is not null)
            {
                entry.Property(ModelConventions.CreatedAt).CurrentValue = now;
                entry.Property(ModelConventions.CreatedBy).CurrentValue = RequireActor();
            }

            if (entry.State == EntityState.Modified)
            {
                if (type.FindProperty(ModelConventions.UpdatedAt) is not null)
                {
                    entry.Property(ModelConventions.UpdatedAt).CurrentValue = now;
                    entry.Property(ModelConventions.UpdatedBy).CurrentValue = RequireActor();
                }

                if (entry.Entity is ISyncVersioned)
                {
                    var rowVersion = entry.Property(ModelConventions.RowVersion);
                    rowVersion.CurrentValue = (long)rowVersion.OriginalValue! + 1;
                }
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

            var deletedLogically = entry.State == EntityState.Modified
                && entry.Metadata.FindProperty(ModelConventions.DeletedAt) is not null
                && entry.Property(ModelConventions.DeletedAt).IsModified
                && entry.Property(ModelConventions.DeletedAt).CurrentValue is not null;

            var (verb, oldValues, newValues) = entry.State switch
            {
                EntityState.Added => ("CREATED", null, Values(entry, p => true, current: true)),
                EntityState.Deleted => ("DELETED", Values(entry, p => true, current: false), null),
                _ when deletedLogically => ("DELETED", null, null),
                _ => ("UPDATED", Values(entry, p => p.IsModified, current: false), Values(entry, p => p.IsModified, current: true)),
            };

            if (verb == "UPDATED" && newValues is { Count: 0 })
            {
                continue;
            }

            var entityName = ToUpperSnake(entry.Metadata.ClrType.Name);
            context.Add(new AuditLogRecord
            {
                Id = ids.NewId(),
                OccurredAt = now,
                Module = audited.Module,
                Action = $"{entityName}_{verb}",
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = entry.Metadata.FindPrimaryKey()?.Properties is [{ ClrType: var keyType } key] && keyType == typeof(Guid)
                    ? (Guid?)entry.Property(key.Name).CurrentValue
                    : null,
                EntityLabel = (entry.Entity as IHasAuditLabel)?.AuditLabel,
                OldValues = ToDocument(oldValues),
                NewValues = ToDocument(newValues),
                Severity = verb == "DELETED" ? "WARNING" : "INFO",
            });
        }
    }

    private void PublishDomainEvents(DbContext context, List<EntityEntry> entries, DateTimeOffset now)
    {
        foreach (var aggregate in entries.Select(e => e.Entity).OfType<IAggregateRoot>().Distinct().ToList())
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                context.Add(new OutboxMessageRecord
                {
                    Id = domainEvent.EventId,
                    OccurredAt = domainEvent.OccurredAt,
                    Destination = "LOCAL",
                    Type = domainEvent.GetType().FullName!,
                    Payload = JsonSerializer.SerializeToDocument(domainEvent, domainEvent.GetType(), OutboxJson.Options),
                    CorrelationId = request.CorrelationId,
                    NextAttemptAt = now,
                });
            }

            aggregate.ClearDomainEvents();
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
            row.NodeId = installation.NodeId;
            row.OccurredAt = AuditHasher.TruncateToMicroseconds(row.OccurredAt);
            row.CompanyId ??= actor.CompanyId;
            row.BranchId ??= actor.BranchId;
            row.UserId ??= actor.ActorId;
            row.UserDisplayName ??= actor.ActorDisplayName;
            row.IpAddress ??= request.IpAddress;
            row.DeviceId ??= request.DeviceId;
            row.CorrelationId ??= request.CorrelationId;
            row.HashVersion = AuditHasher.CurrentVersion;
            row.RowHash = AuditHasher.ComputeRowHash(row);
        }
    }

    private static JsonObject Values(EntityEntry entry, Func<PropertyEntry, bool> include, bool current)
    {
        var values = new JsonObject();
        foreach (var property in entry.Properties.Where(p => !p.Metadata.IsShadowProperty() || !ControlProperties.Contains(p.Metadata.Name)))
        {
            if (ControlProperties.Contains(property.Metadata.Name) || !include(property))
            {
                continue;
            }

            var member = property.Metadata.PropertyInfo;
            if (member?.GetCustomAttribute<NotAuditedAttribute>() is not null)
            {
                continue;
            }

            var column = property.Metadata.GetColumnName(StoreObjectIdentifier.Table(entry.Metadata.GetTableName()!, entry.Metadata.GetSchema()))
                ?? property.Metadata.Name;
            values[column] = member?.GetCustomAttribute<SensitiveAttribute>() is not null
                ? Masked
                : AuditValue(current ? property.CurrentValue : property.OriginalValue);
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
        byte[] bytes => JsonValue.Create($"[{bytes.Length} bytes]"),
        Enum e => JsonValue.Create(e.ToString()),
        JsonDocument doc => JsonValue.Create(doc.RootElement.GetRawText()),
        IFormattable f => JsonValue.Create(f.ToString(null, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.ToString()),
    };

    private static JsonDocument? ToDocument(JsonObject? values) =>
        values is null ? null : JsonDocument.Parse(values.ToJsonString());

    internal static string ToUpperSnake(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
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

/// <summary>Opciones JSON de los payloads del outbox.</summary>
internal static class OutboxJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
