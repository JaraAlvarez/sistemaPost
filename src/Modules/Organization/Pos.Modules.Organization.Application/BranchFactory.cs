using Pos.Modules.Organization.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Organization.Application;

/// <summary>Sucursal nueva con sus bodegas de sistema.</summary>
internal sealed record NewBranch(Branch Branch, Warehouse SalesFloor);

/// <summary>
/// Toda sucursal nace con tres bodegas (docs/fases/fase-02-propuesta.md §10): Piso de venta (admite ventas y es la
/// bodega por defecto), Averías y En tránsito (una por sucursal, para traslados).
/// </summary>
internal static class BranchFactory
{
    public static async Task<Result<NewBranch>> CreateWithWarehousesAsync(
        IOrganizationStore store,
        IIdGenerator ids,
        Guid companyId,
        Guid branchId,
        string code,
        string name,
        string municipalityCode,
        string address,
        string? phone,
        CancellationToken cancellationToken)
    {
        if (!await store.MunicipalityExistsAsync(municipalityCode, cancellationToken))
        {
            return Error.Validation("ORGANIZATION.UNKNOWN_MUNICIPALITY", $"El municipio {municipalityCode} no existe en DIVIPOLA.");
        }

        var branch = Branch.Create(branchId, companyId, code, name, municipalityCode, address, phone);
        if (branch.IsFailure)
        {
            return branch.Error;
        }

        var salesFloor = Warehouse.Create(ids.NewId(), companyId, branchId, "PISO", "Piso de venta", WarehouseKind.SalesFloor, allowsSales: true).Value;
        var damaged = Warehouse.Create(ids.NewId(), companyId, branchId, "AVERIAS", "Averías", WarehouseKind.Damaged, allowsSales: false).Value;
        var inTransit = Warehouse.Create(ids.NewId(), companyId, branchId, "TRANSITO", "En tránsito", WarehouseKind.InTransit, allowsSales: false).Value;

        branch.Value.SetDefaultWarehouse(salesFloor);
        store.Add(branch.Value);
        store.Add(salesFloor);
        store.Add(damaged);
        store.Add(inTransit);
        return new NewBranch(branch.Value, salesFloor);
    }
}
