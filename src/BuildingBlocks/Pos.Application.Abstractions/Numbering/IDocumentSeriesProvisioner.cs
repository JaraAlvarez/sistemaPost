namespace Pos.Application.Abstractions.Numbering;

/// <summary>
/// Crea las series de numeración interna por defecto. El prefijo se GENERA con los códigos de sucursal y caja
/// (no es editable ni es el prefijo fiscal): <c>S01C01</c> para las series de caja, <c>S01</c> para las de sucursal.
/// </summary>
public interface IDocumentSeriesProvisioner
{
    /// <summary>Series de todos los tipos de alcance BRANCH para la sucursal.</summary>
    Task CreateBranchSeriesAsync(Guid companyId, Guid branchId, string branchCode, CancellationToken cancellationToken = default);

    /// <summary>Series de todos los tipos de alcance TERMINAL para la caja.</summary>
    Task CreateTerminalSeriesAsync(
        Guid companyId, Guid branchId, string branchCode, Guid posTerminalId, string terminalCode, CancellationToken cancellationToken = default);

    /// <summary>Inactiva las series de una caja (al inactivar o eliminar la caja). Los números emitidos se conservan.</summary>
    Task DeactivateTerminalSeriesAsync(Guid posTerminalId, CancellationToken cancellationToken = default);
}
