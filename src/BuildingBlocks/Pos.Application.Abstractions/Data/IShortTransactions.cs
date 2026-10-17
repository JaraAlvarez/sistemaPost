using Pos.SharedKernel.Results;

namespace Pos.Application.Abstractions.Data;

/// <summary>
/// Guardado en transacciones CORTAS para los comandos que manejan sus propias transacciones (<see cref="Messaging.ISelfTransactedCommand"/>)
/// y para los procesos en segundo plano: nada de lo que pase aquí sale como error 500.
/// <para>Solo para entidades SIN auditoría automática de filas (<c>[Audited]</c>), sin sincronización (<c>ISyncVersioned</c>) y sin eventos
/// de dominio pendientes: al reintentar, esos efectos de los interceptores se repetirían. Si llega una así, lanza
/// <see cref="InvalidOperationException"/>. La auditoría explícita (<c>IAuditWriter</c>) sí se puede: se guarda una sola vez.</para>
/// </summary>
public interface IShortTransactions
{
    /// <summary>
    /// Guarda lo pendiente en una transacción corta. Si otro proceso cambió una de las filas (concurrencia), esas filas se recargan desde
    /// la base de datos y, si llega <paramref name="reapply"/>, se vuelve a aplicar el cambio sobre lo recargado y se reintenta UNA vez.
    /// Si al final no se pudo guardar (segundo conflicto o una restricción de la base de datos), lo pendiente se descarta (filas nuevas
    /// fuera del contexto, filas cambiadas recargadas) para no afectar los guardados siguientes, y se devuelve un error de negocio.
    /// </summary>
    Task<Result> SaveAsync(Action? reapply = null, CancellationToken cancellationToken = default);
}
