using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Time;

namespace Pos.Infrastructure.Outbox;

/// <summary>Cómo llegó un evento de otro nodo.</summary>
public enum InboxChannel
{
    Online,
    File,
}

/// <summary>
/// Idempotencia al recibir eventos de otros nodos (nube, paquete .possync, caja autónoma): un mismo evento nunca se
/// aplica dos veces, llegue por Internet, por archivo o por ambos. Se usa DENTRO de la transacción que aplica el evento.
/// </summary>
public sealed class InboxStore(PosDbContext context, IClock clock)
{
    /// <summary>Registra el evento. Devuelve <c>false</c> si ya se había aplicado (y no hay que aplicarlo otra vez).</summary>
    public async Task<bool> TryRegisterAsync(
        Guid messageId, Guid sourceNodeId, long sourceSeq, string type, InboxChannel channel, CancellationToken cancellationToken = default)
    {
        if (await context.Set<InboxMessageRecord>().AsNoTracking()
                .AnyAsync(m => m.MessageId == messageId || (m.SourceNodeId == sourceNodeId && m.SourceSeq == sourceSeq), cancellationToken)
            || context.ChangeTracker.Entries<InboxMessageRecord>().Any(e => e.Entity.MessageId == messageId))
        {
            return false;
        }

        context.Add(new InboxMessageRecord
        {
            MessageId = messageId,
            SourceNodeId = sourceNodeId,
            SourceSeq = sourceSeq,
            Type = type,
            ReceivedVia = channel == InboxChannel.File ? "FILE" : "ONLINE",
            AppliedAt = clock.UtcNow,
            Result = "APPLIED",
        });
        return true;
    }
}
