using Pos.Modules.Organization.Contracts;
using Pos.Modules.Organization.Domain;

namespace Pos.Modules.Organization.Application;

/// <summary>Puerto de persistencia del módulo (lo implementa la capa Infrastructure con EF Core).</summary>
public interface IOrganizationStore
{
    void Add(Company company);

    void Add(Branch branch);

    void Add(Warehouse warehouse);

    void Add(PosTerminal terminal);

    void Add(Node node);

    Task<Company?> GetCompanyAsync(Guid companyId, CancellationToken cancellationToken);

    Task<bool> CompanyExistsAsync(string identificationType, string identificationNumber, CancellationToken cancellationToken);

    Task<Branch?> GetBranchAsync(Guid branchId, CancellationToken cancellationToken);

    Task<Warehouse?> GetWarehouseAsync(Guid warehouseId, CancellationToken cancellationToken);

    Task<PosTerminal?> GetTerminalAsync(Guid terminalId, CancellationToken cancellationToken);

    Task<bool> BranchCodeExistsAsync(Guid companyId, string code, CancellationToken cancellationToken);

    Task<bool> WarehouseCodeExistsAsync(Guid branchId, string code, CancellationToken cancellationToken);

    Task<bool> TerminalCodeExistsAsync(Guid branchId, string code, CancellationToken cancellationToken);

    Task<bool> BranchHasInTransitWarehouseAsync(Guid branchId, CancellationToken cancellationToken);

    Task<int> CountActiveBranchesAsync(Guid companyId, CancellationToken cancellationToken);

    Task<int> CountActiveTerminalsAsync(Guid? branchId, CancellationToken cancellationToken);

    /// <summary>Cajas no eliminadas de la empresa (la edición Caja Única admite una).</summary>
    Task<int> CountTerminalsAsync(CancellationToken cancellationToken);

    Task<bool> WarehouseAssignedToActiveTerminalAsync(Guid warehouseId, CancellationToken cancellationToken);

    Task<IReadOnlyList<BranchDto>> ListBranchesAsync(Guid? homeBranchId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WarehouseDto>> ListWarehousesAsync(Guid branchId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(Guid? branchId, CancellationToken cancellationToken);

    /// <summary>Validación de referencia: el municipio existe en el catálogo DIVIPOLA.</summary>
    Task<bool> MunicipalityExistsAsync(string code, CancellationToken cancellationToken);
}
