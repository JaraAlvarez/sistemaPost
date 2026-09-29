using System.Text.RegularExpressions;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Organization.Domain;

public enum WarehouseKind
{
    /// <summary>Piso de venta: admite ventas.</summary>
    SalesFloor,

    /// <summary>Depósito trasero.</summary>
    Storage,

    /// <summary>Averías: mercancía dañada, nunca se vende.</summary>
    Damaged,

    /// <summary>En tránsito: traslados entre sucursales (una por sucursal).</summary>
    InTransit,
}

/// <summary>Bodega: lugar con saldo de inventario (el kardex y el costo promedio son por bodega). Código único por sucursal.</summary>
[Audited("organization")]
public sealed partial class Warehouse : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private Warehouse(Guid id, Guid companyId, Guid branchId, string code, WarehouseKind kind)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        Code = code;
        Kind = kind;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public WarehouseKind Kind { get; private set; }

    public bool AllowsSales { get; private set; }

    public RecordStatus Status { get; private set; } = RecordStatus.Active;

    public string AuditLabel => $"Bodega {Code} · {Name}";

    /// <summary>Averías y tránsito son bodegas del sistema: toda sucursal las tiene.</summary>
    public bool IsSystemWarehouse => Kind is WarehouseKind.Damaged or WarehouseKind.InTransit;

    public static Result<Warehouse> Create(Guid id, Guid companyId, Guid branchId, string code, string name, WarehouseKind kind, bool allowsSales)
    {
        if (!CodePattern().IsMatch(code ?? string.Empty))
        {
            return Error.Validation("ORGANIZATION.INVALID_WAREHOUSE_CODE", "El código de bodega debe tener de 2 a 10 letras mayúsculas, dígitos o guiones.");
        }

        if (allowsSales && kind is WarehouseKind.Damaged or WarehouseKind.InTransit)
        {
            return OrganizationErrors.WarehouseKindCannotSell;
        }

        return new Warehouse(id, companyId, branchId, code!, kind) { Name = name.Trim(), AllowsSales = allowsSales };
    }

    public Result Update(string name, bool allowsSales)
    {
        if (allowsSales && IsSystemWarehouse)
        {
            return OrganizationErrors.WarehouseKindCannotSell;
        }

        Name = name.Trim();
        AllowsSales = allowsSales;
        return Result.Success();
    }

    public void Deactivate() => Status = RecordStatus.Inactive;

    public void Activate() => Status = RecordStatus.Active;

    [GeneratedRegex("^[A-Z0-9-]{2,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
