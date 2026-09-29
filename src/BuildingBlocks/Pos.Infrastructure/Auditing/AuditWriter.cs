using System.Text.Json;
using System.Text.Json.Nodes;
using Pos.Application.Abstractions.Auditing;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Time;

namespace Pos.Infrastructure.Auditing;

/// <summary>
/// Auditoría explícita de eventos de negocio que no son cambios de fila (asistente inicial, reimpresiones, etc.).
/// La fila queda pendiente en el contexto y se guarda en la MISMA transacción del caso de uso (RN-GEN-04);
/// el interceptor le asigna seq y row_hash al guardar.
/// </summary>
internal sealed class AuditWriter(PosDbContext context, IClock clock, IIdGenerator ids) : IAuditWriter
{
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        context.Add(new AuditLogRecord
        {
            Id = ids.NewId(),
            OccurredAt = clock.UtcNow,
            Module = entry.Module,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            EntityLabel = entry.EntityLabel,
            Summary = entry.Summary,
            OldValues = ToDocument(entry.OldValues),
            NewValues = ToDocument(entry.NewValues),
            AuthorizedBy = entry.AuthorizedBy,
            Severity = entry.Severity.ToString().ToUpperInvariant(),
        });

        return Task.CompletedTask;
    }

    private static JsonDocument? ToDocument(IReadOnlyDictionary<string, object?>? values)
    {
        if (values is null)
        {
            return null;
        }

        var obj = new JsonObject();
        foreach (var (key, value) in values)
        {
            obj[key] = PosSaveChangesInterceptor.AuditValue(value);
        }

        return JsonDocument.Parse(obj.ToJsonString());
    }
}
