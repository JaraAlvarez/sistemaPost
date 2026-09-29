using System.Text.RegularExpressions;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Organization.Domain;

/// <summary>
/// Sucursal: un establecimiento físico (dirección y municipio van en la factura). Su código lo asigna la autoridad
/// de la empresa y forma parte de los prefijos de numeración interna (<c>S01C01-000123</c>).
/// </summary>
[Audited("organization")]
public sealed partial class Branch : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private Branch(Guid id, Guid companyId, string code)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
    }

    public Guid CompanyId { get; private set; }

    /// <summary>Código corto e inmutable: está en los números de documento ya emitidos.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string MunicipalityCode { get; private set; } = string.Empty;

    public string Address { get; private set; } = string.Empty;

    public string? Phone { get; private set; }

    public Guid? DefaultWarehouseId { get; private set; }

    public RecordStatus Status { get; private set; } = RecordStatus.Active;

    public string AuditLabel => $"Sucursal {Code} · {Name}";

    public static bool IsValidCode(string? code) => code is not null && CodePattern().IsMatch(code);

    public static Result<Branch> Create(Guid id, Guid companyId, string code, string name, string municipalityCode, string address, string? phone)
    {
        if (!IsValidCode(code))
        {
            return Error.Validation("ORGANIZATION.INVALID_BRANCH_CODE", "El código de sucursal debe tener de 2 a 6 letras mayúsculas o dígitos.");
        }

        var branch = new Branch(id, companyId, code);
        branch.Update(name, municipalityCode, address, phone);
        return branch;
    }

    public void Update(string name, string municipalityCode, string address, string? phone)
    {
        Name = name.Trim();
        MunicipalityCode = municipalityCode;
        Address = address.Trim();
        Phone = phone;
    }

    public void SetDefaultWarehouse(Warehouse warehouse)
    {
        ArgumentNullException.ThrowIfNull(warehouse);
        if (warehouse.BranchId != Id || !warehouse.AllowsSales)
        {
            throw new DomainException(OrganizationErrors.TerminalWarehouseMustSell);
        }

        DefaultWarehouseId = warehouse.Id;
    }

    public void Deactivate() => Status = RecordStatus.Inactive;

    public void Activate() => Status = RecordStatus.Active;

    [GeneratedRegex("^[A-Z0-9]{2,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
