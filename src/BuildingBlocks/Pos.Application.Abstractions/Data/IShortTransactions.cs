using Pos.SharedKernel.Results;

namespace Pos.Application.Abstractions.Data;

/// <summary>
/// Guardado en transacciones CORTAS para los comandos que manejan sus propias transacciones (<see cref="Messaging.ISelfTransactedCommand"/>)
/// y para los procesos en segundo plano: nada de lo que pase aquí sale como error 500.
/// </summary>
public interface IShortTransactions
{
    /// <summary>
    /// Guarda lo pendiente en una transacción corta. Si otro proceso cambió una de las filas (concurrencia), esas filas se recargan desde
    /// la base de datos y, si llega <paramref name="reapply"/>, se vuelve a aplicar el cambio sobre lo recargado y se reintenta UNA vez.
    /// Las violaciones de restricciones y un segundo conflicto se devuelven como error de negocio.
    /// </summary>
    Task<Result> SaveAsync(Action? reapply = null, CancellationToken cancellationToken = default);
}
