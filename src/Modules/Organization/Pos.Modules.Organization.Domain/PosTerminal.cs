using System.Text.RegularExpressions;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Organization.Domain;

public enum TerminalStatus
{
    Active,
    Inactive,

    /// <summary>Bloqueada por seguridad (equipo robado o comprometido). No por licencia.</summary>
    Blocked,
}

/// <summary>
/// Caja: puesto LÓGICO de venta con sus series de numeración y jornadas. No es el PC: si el equipo se daña,
/// la caja continúa en otro equipo con su misma numeración.
/// </summary>
[Audited("organization")]
public sealed partial class PosTerminal : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private PosTerminal(Guid id, Guid companyId, Guid branchId, string code)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        Code = code;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    /// <summary>Código inmutable: forma el prefijo de las series de la caja.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public Guid WarehouseId { get; private set; }

    public Guid? DeviceId { get; private set; }

    public TerminalStatus Status { get; private set; } = TerminalStatus.Active;

    public string AuditLabel => $"Caja {Code} · {Name}";

    public static Result<PosTerminal> Create(Guid id, Guid companyId, Guid branchId, string code, string name, Warehouse warehouse)
    {
        if (!CodePattern().IsMatch(code ?? string.Empty))
        {
            return Error.Validation("ORGANIZATION.INVALID_TERMINAL_CODE", "El código de caja debe tener de 2 a 6 letras mayúsculas o dígitos.");
        }

        var terminal = new PosTerminal(id, companyId, branchId, code!) { Name = name.Trim() };
        var assigned = terminal.AssignWarehouse(warehouse);
        return assigned.IsSuccess ? terminal : assigned.Error;
    }

    public Result AssignWarehouse(Warehouse warehouse)
    {
        ArgumentNullException.ThrowIfNull(warehouse);
        if (warehouse.BranchId != BranchId || !warehouse.AllowsSales || warehouse.Status != RecordStatus.Active)
        {
            return OrganizationErrors.TerminalWarehouseMustSell;
        }

        WarehouseId = warehouse.Id;
        return Result.Success();
    }

    public void Rename(string name) => Name = name.Trim();

    /// <summary>La caja queda asociada al equipo emparejado (un equipo = una caja).</summary>
    public void AttachDevice(Guid deviceId) => DeviceId = deviceId;

    public void DetachDevice() => DeviceId = null;

    public void Deactivate() => Status = TerminalStatus.Inactive;

    public void Activate() => Status = TerminalStatus.Active;

    public void Block() => Status = TerminalStatus.Blocked;

    [GeneratedRegex("^[A-Z0-9]{2,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
