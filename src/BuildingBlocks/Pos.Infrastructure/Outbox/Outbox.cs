using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Time;

namespace Pos.Infrastructure.Outbox;

/// <summary>Agrega el mensaje al contexto: se guarda en la misma transacción del caso de uso.</summary>
internal sealed class Outbox(PosDbContext context, IClock clock, IIdGenerator ids, IRequestContext request) : IOutbox
{
    public void Enqueue(string type, object payload, OutboxDestination destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payload);
        var now = clock.UtcNow;
        context.Add(new OutboxMessageRecord
        {
            Id = ids.NewId(),
            OccurredAt = now,
            Destination = destination == OutboxDestination.Sync ? "SYNC" : "LOCAL",
            Type = type,
            Payload = JsonSerializer.SerializeToDocument(payload, payload.GetType(), OutboxJson.Options),
            CorrelationId = request.CorrelationId,
            NextAttemptAt = now,
        });
    }
}

/// <summary>Procesa los mensajes LOCAL de un tipo. Debe ser idempotente: un mensaje puede entregarse más de una vez.</summary>
public interface IOutboxMessageHandler
{
    string MessageType { get; }

    Task HandleAsync(JsonElement payload, CancellationToken cancellationToken);
}

/// <summary>Enmascara en la transacción en curso los mensajes SYNC ya guardados de una entidad (supresión del titular).</summary>
internal sealed class SyncOutboxRedaction(PosDbContext context) : ISyncOutboxRedaction
{
    public const string Redacted = "(suprimido)";

    public Task<int> RedactAsync(string entity, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        var key = id.ToString("D");
        return context.Database.ExecuteSqlInterpolatedAsync(
            $$"""
            UPDATE system.outbox_messages
            SET payload = jsonb_set(payload, '{changes}',
                    COALESCE((SELECT jsonb_object_agg(c.key, to_jsonb({{Redacted}}::text)) FROM jsonb_each(payload -> 'changes') AS c(key, value)), '{}'::jsonb))
                || '{"redacted": true}'::jsonb
            WHERE destination = 'SYNC' AND type = 'sync.entity_changed.v1' AND payload ->> 'entity' = {{entity}} AND payload ->> 'id' = {{key}}
              AND jsonb_typeof(payload -> 'changes') = 'object'
            """,
            cancellationToken);
    }
}
