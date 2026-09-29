using Pos.SharedKernel;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Fiscal;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Organization.Domain;

public enum PersonType
{
    Legal,
    Natural,
}

public enum RecordStatus
{
    Active,
    Inactive,
}

/// <summary>Datos fiscales y de contacto de la empresa (los exige la factura electrónica).</summary>
public sealed record CompanyData(
    string LegalName,
    string TradeName,
    PersonType PersonType,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string TaxRegime,
    IReadOnlyCollection<string> FiscalResponsibilities,
    string CountryCode,
    string MunicipalityCode,
    string Address,
    string? Phone,
    string? Email,
    string CurrencyCode,
    string Timezone);

/// <summary>Responsabilidad fiscal DIAN de la empresa (O-13, O-15, R-99-PN…).</summary>
public sealed record FiscalResponsibility(string Code);

/// <summary>
/// Empresa: la entidad legal que factura (NIT + DV). Todo documento fiscal sale a su nombre.
/// </summary>
[Audited("organization")]
public sealed class Company : AggregateRoot<Guid>, ISyncVersioned, IHasAuditLabel
{
    public const string NitType = "NIT";

    private readonly List<FiscalResponsibility> _fiscalResponsibilities = [];

    private Company(Guid id)
        : base(id)
    {
    }

    public string LegalName { get; private set; } = string.Empty;

    public string TradeName { get; private set; } = string.Empty;

    public PersonType PersonType { get; private set; }

    public string IdentificationType { get; private set; } = string.Empty;

    public string IdentificationNumber { get; private set; } = string.Empty;

    public string? CheckDigit { get; private set; }

    public string TaxRegime { get; private set; } = string.Empty;

    public string CountryCode { get; private set; } = string.Empty;

    public string MunicipalityCode { get; private set; } = string.Empty;

    public string Address { get; private set; } = string.Empty;

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string CurrencyCode { get; private set; } = string.Empty;

    public string Timezone { get; private set; } = string.Empty;

    [NotAudited]
    public byte[]? Logo { get; private set; }

    public RecordStatus Status { get; private set; } = RecordStatus.Active;

    public IReadOnlyCollection<FiscalResponsibility> FiscalResponsibilities => _fiscalResponsibilities.AsReadOnly();

    public string AuditLabel => $"{TradeName} ({IdentificationType} {IdentificationNumber}{(CheckDigit is null ? string.Empty : "-" + CheckDigit)})";

    public static Result<Company> Create(Guid id, CompanyData data)
    {
        var company = new Company(id);
        var result = company.Apply(data, identityChangeAllowed: true);
        return result.IsSuccess ? company : result.Error;
    }

    /// <summary>Actualiza los datos. La identificación (tipo, número y DV) no se cambia: define a la empresa.</summary>
    public Result Update(CompanyData data) => Apply(data, identityChangeAllowed: false);

    private Result Apply(CompanyData data, bool identityChangeAllowed)
    {
        Guard.NotNull(data);
        if (!identityChangeAllowed
            && (data.IdentificationType != IdentificationType || data.IdentificationNumber != IdentificationNumber || data.CheckDigit != CheckDigit))
        {
            return Error.BusinessRule(
                "ORGANIZATION.IDENTIFICATION_IMMUTABLE",
                "La identificación de la empresa no se puede cambiar: los documentos emitidos la referencian.");
        }

        if (data.IdentificationType == NitType && !Nit.IsValid(data.IdentificationNumber, data.CheckDigit ?? string.Empty))
        {
            return OrganizationErrors.InvalidNitCheckDigit;
        }

        LegalName = data.LegalName.Trim();
        TradeName = data.TradeName.Trim();
        PersonType = data.PersonType;
        IdentificationType = data.IdentificationType;
        IdentificationNumber = data.IdentificationNumber;
        CheckDigit = data.CheckDigit;
        TaxRegime = data.TaxRegime;
        CountryCode = data.CountryCode;
        MunicipalityCode = data.MunicipalityCode;
        Address = data.Address.Trim();
        Phone = data.Phone;
        Email = data.Email;
        CurrencyCode = data.CurrencyCode;
        Timezone = data.Timezone;

        var codes = data.FiscalResponsibilities.Distinct(StringComparer.Ordinal).ToList();
        _fiscalResponsibilities.RemoveAll(r => !codes.Contains(r.Code));
        foreach (var code in codes.Where(c => _fiscalResponsibilities.All(r => r.Code != c)))
        {
            _fiscalResponsibilities.Add(new FiscalResponsibility(code));
        }

        return Result.Success();
    }
}
