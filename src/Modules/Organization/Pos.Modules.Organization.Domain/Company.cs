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

    /// <summary>
    /// NIT provisional (DV 0) con el que el instalador premium crea la empresa en la prueba, sin preguntar los datos de la empresa
    /// (instalador 1.2.1). Mientras la empresa lo tenga, el NIT SÍ se puede cambiar en Configuración (es el único caso) y el Tablero pide
    /// completar los datos.
    /// </summary>
    public const string ProvisionalNit = "0";

    /// <summary>Dirección provisional del instalador premium: la empresa tiene datos por completar.</summary>
    public const string ProvisionalAddress = "Por completar";

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

    /// <summary>Tipo de negocio (Fase 7-C parte A). Lo audita <c>BUSINESS_FEATURES_CHANGED</c> con un resumen legible.</summary>
    [NotAudited]
    public BusinessProfile BusinessProfile { get; private set; } = BusinessProfiles.Default;

    /// <summary>Funciones encendidas, como se guardan (<c>LOTS_EXPIRY,SCALE</c>). Use <see cref="Features"/>.</summary>
    [NotAudited]
    public string EnabledFeatures { get; private set; } = BusinessProfiles.Format(BusinessProfiles.Preset(BusinessProfiles.Default));

    /// <summary>Funciones encendidas.</summary>
    public IReadOnlySet<BusinessFeature> Features => BusinessProfiles.Parse(EnabledFeatures);

    public IReadOnlyCollection<FiscalResponsibility> FiscalResponsibilities => _fiscalResponsibilities.AsReadOnly();

    public string AuditLabel => $"{TradeName} ({IdentificationType} {IdentificationNumber}{(CheckDigit is null ? string.Empty : "-" + CheckDigit)})";

    public static Result<Company> Create(Guid id, CompanyData data)
    {
        var company = new Company(id);
        var result = company.Apply(data, identityChangeAllowed: true);
        return result.IsSuccess ? company : result.Error;
    }

    /// <summary>¿La función está encendida?</summary>
    public bool Has(BusinessFeature feature) => Features.Contains(feature);

    /// <summary>Cambia el tipo de negocio y deja los interruptores en los valores de ese tipo. No borra datos.</summary>
    public void ApplyProfile(BusinessProfile profile)
    {
        BusinessProfile = profile;
        EnabledFeatures = BusinessProfiles.Format(BusinessProfiles.Preset(profile));
    }

    /// <summary>Enciende exactamente estas funciones (las demás quedan apagadas: se ocultan, sus datos se conservan).</summary>
    public void SetFeatures(IEnumerable<BusinessFeature> features)
    {
        Guard.NotNull(features);
        EnabledFeatures = BusinessProfiles.Format(features);
    }

    /// <summary>¿La identificación es la provisional del instalador (se puede cambiar una vez por la real)?</summary>
    public bool HasProvisionalIdentification => IdentificationType == NitType && IdentificationNumber == ProvisionalNit;

    /// <summary>¿Faltan datos de la empresa (NIT o dirección provisionales)? Son necesarios para facturar.</summary>
    public bool DataPending => HasProvisionalIdentification || string.Equals(Address, ProvisionalAddress, StringComparison.Ordinal);

    /// <summary>
    /// Actualiza los datos. La identificación (tipo, número y DV) no se cambia: define a la empresa. Excepción: la provisional del
    /// instalador, que se reemplaza por la real (con ella no se han emitido facturas electrónicas: falta la habilitación con la DIAN).
    /// </summary>
    public Result Update(CompanyData data) => Apply(data, identityChangeAllowed: HasProvisionalIdentification);

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
