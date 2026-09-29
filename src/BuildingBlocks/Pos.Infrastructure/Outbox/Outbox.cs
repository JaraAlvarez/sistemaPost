using System.Text.Json;
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
