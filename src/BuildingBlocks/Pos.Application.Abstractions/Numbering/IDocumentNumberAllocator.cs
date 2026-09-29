namespace Pos.Application.Abstractions.Numbering;

/// <summary>Número interno asignado a un documento (docs/fases/fase-02-propuesta.md §14).</summary>
/// <param name="SeriesId">Serie de la que sale el número.</param>
/// <param name="SequenceNumber">Consecutivo dentro de la serie.</param>
/// <param name="Number">Número formateado <c>{prefijo}-{consecutivo con ceros}</c>, p. ej. <c>S01C01-000123</c>.</param>
public sealed record DocumentNumber(Guid SeriesId, long SequenceNumber, string Number);

/// <summary>
/// Asigna el número INTERNO de un documento dentro de la transacción del caso de uso: si la transacción se revierte,
/// el número no se consume. No es el número fiscal (lo asigna el proveedor de facturación electrónica).
/// </summary>
public interface IDocumentNumberAllocator
{
    /// <summary>Número de la serie activa del tipo para la caja indicada (tipos de alcance TERMINAL).</summary>
    Task<DocumentNumber> NextForTerminalAsync(string documentType, Guid posTerminalId, CancellationToken cancellationToken = default);

    /// <summary>Número de la serie activa del tipo para la sucursal indicada (tipos de alcance BRANCH).</summary>
    Task<DocumentNumber> NextForBranchAsync(string documentType, Guid branchId, CancellationToken cancellationToken = default);
}
