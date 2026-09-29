using System.Text.RegularExpressions;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Fiscal;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Text;

namespace Pos.Modules.Parties.Domain;

public enum PersonType
{
    Legal,
    Natural,
}

public enum PartyStatus
{
    Active,
    Inactive,

    /// <summary>Fusionado en otro tercero (duplicado creado sin conexión en dos tiendas, D5-02).</summary>
    Merged,
}

/// <summary>Tipo de identificación de la DIAN (ref.identification_types).</summary>
public sealed record IdentificationTypeInfo(string Code, bool RequiresCheckDigit, bool AllowsNatural, bool AllowsLegal);

/// <summary>Datos de un tercero. Persona jurídica: razón social; persona natural: nombres y apellidos.</summary>
public sealed record PartyData(
    PersonType PersonType,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string? LegalName,
    string? FirstNames,
    string? LastNames,
    string? TradeName,
    string TaxRegime,
    IReadOnlyCollection<string> FiscalResponsibilities,
    string? Email,
    string? Phone,
    string? Address,
    string? MunicipalityCode,
    string? Notes);

public sealed record ContactData(string Name, string? Position, string? Phone, string? Email, bool IsPrimary);

/// <summary>Contacto de un tercero (vendedor, cartera…).</summary>
public sealed class PartyContact : Entity<Guid>
{
    private PartyContact(Guid id, string name)
        : base(id)
    {
        Name = name;
    }

    public string Name { get; private set; }

    public string? Position { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public bool IsPrimary { get; private set; }

    internal static PartyContact Create(Guid id, ContactData data) => new(id, data.Name.Trim())
    {
        Position = Party.Clean(data.Position),
        Phone = Party.Clean(data.Phone),
        Email = Party.Clean(data.Email)?.ToLowerInvariant(),
        IsPrimary = data.IsPrimary,
    };
}

/// <summary>
/// Tercero (D5-01): persona natural o jurídica identificada una sola vez por empresa (tipo + número, con DV validado
/// para NIT, D5-02). Proveedor (Fase 5) y cliente (Fase 8) son roles que lo referencian. El "Consumidor final" es un
/// tercero del sistema que no se modifica.
/// </summary>
[Audited("parties")]
public sealed partial class Party : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    public const string NitType = "NIT";

    public const string FinalConsumerType = "CC";

    public const string FinalConsumerNumber = "222222222222";

    public const string DefaultResponsibility = "R-99-PN";

    public const string NotVatResponsible = "49";

    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal) { "RC", "TI", "CC", "TE", "CE", "NIT", "NUIP" };

    private readonly List<PartyContact> _contacts = [];

    private Party(Guid id, Guid companyId)
        : base(id)
    {
        CompanyId = companyId;
    }

    public Guid CompanyId { get; private set; }

    public PersonType PersonType { get; private set; }

    public string IdentificationType { get; private set; } = string.Empty;

    public string IdentificationNumber { get; private set; } = string.Empty;

    public string? CheckDigit { get; private set; }

    public string? LegalName { get; private set; }

    public string? FirstNames { get; private set; }

    public string? LastNames { get; private set; }

    public string? TradeName { get; private set; }

    public string TaxRegime { get; private set; } = string.Empty;

    /// <summary>Responsabilidades fiscales DIAN separadas por ";" (O-13;O-15…).</summary>
    public string FiscalResponsibilities { get; private set; } = DefaultResponsibility;

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public string? Address { get; private set; }

    public string? MunicipalityCode { get; private set; }

    public string? Notes { get; private set; }

    [NotAudited]
    public string SearchText { get; private set; } = string.Empty;

    public bool IsSystem { get; private set; }

    public Guid? MergedIntoId { get; private set; }

    public PartyStatus Status { get; private set; } = PartyStatus.Active;

    public IReadOnlyList<PartyContact> Contacts => _contacts;

    public string DisplayName => PersonType == PersonType.Legal ? LegalName! : $"{FirstNames} {LastNames}".Trim();

    public string FullIdentification => CheckDigit is null ? $"{IdentificationType} {IdentificationNumber}" : $"{IdentificationType} {IdentificationNumber}-{CheckDigit}";

    public string AuditLabel => $"Tercero {DisplayName} ({FullIdentification})";

    public static Result<Party> Create(Guid id, Guid companyId, PartyData data, IdentificationTypeInfo type)
    {
        var party = new Party(id, companyId);
        var result = party.Apply(data, type);
        return result.IsSuccess ? party : result.Error;
    }

    /// <summary>"Consumidor final" (identificación 222222222222) para las ventas sin cliente identificado (Fase 7).</summary>
    public static Party FinalConsumer(Guid id, Guid companyId)
    {
        var party = new Party(id, companyId) { IsSystem = true };
        var applied = party.Apply(
            new PartyData(PersonType.Natural, FinalConsumerType, FinalConsumerNumber, null, null, "Consumidor", "final", null, NotVatResponsible,
                [DefaultResponsibility], null, null, null, null, "Tercero del sistema para ventas sin cliente identificado."),
            new IdentificationTypeInfo(FinalConsumerType, false, true, false));
        return applied.IsSuccess ? party : throw new DomainException(applied.Error.Message);
    }

    public Result Update(PartyData data, IdentificationTypeInfo type)
    {
        if (IsSystem)
        {
            return PartyErrors.SystemParty;
        }

        return Status == PartyStatus.Merged ? PartyErrors.Merged : Apply(data, type);
    }

    public Result SetActive(bool active)
    {
        if (IsSystem)
        {
            return PartyErrors.SystemParty;
        }

        if (Status == PartyStatus.Merged)
        {
            return PartyErrors.Merged;
        }

        Status = active ? PartyStatus.Active : PartyStatus.Inactive;
        return Result.Success();
    }

    /// <summary>Reemplaza los contactos (a lo sumo uno principal).</summary>
    public Result SetContacts(IReadOnlyList<ContactData> contacts, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(newId);
        if (contacts.Count > 20 || contacts.Count(c => c.IsPrimary) > 1
            || contacts.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Trim().Length > 120 || !IsValidEmail(c.Email)))
        {
            return PartyErrors.InvalidContact;
        }

        _contacts.Clear();
        _contacts.AddRange(contacts.Select(c => PartyContact.Create(newId(), c)));
        return Result.Success();
    }

    /// <summary>Fusión (la ejecuta la sincronización en la nube): este tercero queda apuntando al que se conserva.</summary>
    public Result MergeInto(Guid survivorId)
    {
        if (IsSystem || survivorId == Id)
        {
            return PartyErrors.SystemParty;
        }

        MergedIntoId = survivorId;
        Status = PartyStatus.Merged;
        return Result.Success();
    }

    /// <summary>Número de identificación válido para el tipo (y DV del NIT).</summary>
    public static bool IsValidIdentification(string type, string? number, string? checkDigit, bool requiresCheckDigit)
    {
        if (string.IsNullOrEmpty(number))
        {
            return false;
        }

        if (requiresCheckDigit)
        {
            return Nit.IsValid(number, checkDigit ?? string.Empty);
        }

        return NumericTypes.Contains(type) ? NumericIdentification().IsMatch(number) : AlphanumericIdentification().IsMatch(number);
    }

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsValidEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) || (email.Trim().Length <= 200 && EmailPattern().IsMatch(email.Trim()));

    private Result Apply(PartyData data, IdentificationTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(type);
        var number = (data.IdentificationNumber ?? string.Empty).Trim().ToUpperInvariant();
        var checkDigit = Clean(data.CheckDigit);
        if (type.Code != data.IdentificationType)
        {
            throw new ArgumentException("El tipo de identificación no corresponde a los datos.", nameof(type));
        }

        if (!IsValidIdentification(type.Code, number, checkDigit, type.RequiresCheckDigit))
        {
            return type.RequiresCheckDigit ? PartyErrors.InvalidNitCheckDigit : PartyErrors.InvalidIdentification;
        }

        if ((data.PersonType == PersonType.Natural && !type.AllowsNatural) || (data.PersonType == PersonType.Legal && !type.AllowsLegal))
        {
            return PartyErrors.PersonTypeNotAllowed;
        }

        var legalName = Clean(data.LegalName);
        var firstNames = Clean(data.FirstNames);
        var lastNames = Clean(data.LastNames);
        var names = data.PersonType == PersonType.Legal
            ? legalName is { Length: <= 200 }
            : firstNames is { Length: <= 100 } && lastNames is { Length: <= 100 };
        if (!names || Clean(data.TradeName) is { Length: > 200 })
        {
            return PartyErrors.InvalidName;
        }

        var responsibilities = (data.FiscalResponsibilities ?? []).Select(r => r.Trim().ToUpperInvariant()).Where(r => r.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (responsibilities.Count == 0)
        {
            responsibilities.Add(DefaultResponsibility);
        }

        var joined = string.Join(';', responsibilities);
        if (joined.Length > 100 || string.IsNullOrWhiteSpace(data.TaxRegime))
        {
            return PartyErrors.InvalidFiscalData;
        }

        if (!IsValidEmail(data.Email))
        {
            return PartyErrors.InvalidEmail;
        }

        PersonType = data.PersonType;
        IdentificationType = type.Code;
        IdentificationNumber = number;
        CheckDigit = type.RequiresCheckDigit ? checkDigit : null;
        LegalName = data.PersonType == PersonType.Legal ? legalName : null;
        FirstNames = data.PersonType == PersonType.Natural ? firstNames : null;
        LastNames = data.PersonType == PersonType.Natural ? lastNames : null;
        TradeName = Clean(data.TradeName);
        TaxRegime = data.TaxRegime.Trim();
        FiscalResponsibilities = joined;
        Email = Clean(data.Email)?.ToLowerInvariant();
        Phone = Clean(data.Phone);
        Address = Clean(data.Address);
        MunicipalityCode = Clean(data.MunicipalityCode);
        Notes = Clean(data.Notes);
        SearchText = TextNormalization.ForSearch($"{DisplayName} {TradeName} {IdentificationNumber}");
        return Result.Success();
    }

    [GeneratedRegex("^[0-9]{3,15}$")]
    private static partial Regex NumericIdentification();

    [GeneratedRegex("^[0-9A-Z-]{3,30}$")]
    private static partial Regex AlphanumericIdentification();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}

/// <summary>Errores de negocio de los terceros con código estable.</summary>
public static class PartyErrors
{
    public static readonly Error NotFound = Error.NotFound("PARTIES.NOT_FOUND", "El tercero no existe.");

    public static readonly Error IdentificationTypeNotFound =
        Error.Validation("PARTIES.IDENTIFICATION_TYPE_NOT_FOUND", "El tipo de identificación no existe.");

    public static readonly Error InvalidIdentification = Error.Validation(
        "PARTIES.INVALID_IDENTIFICATION", "Número de identificación inválido: solo dígitos (3 a 15) o, en pasaportes y documentos extranjeros, letras, dígitos y guiones.");

    public static readonly Error InvalidNitCheckDigit =
        Error.Validation("PARTIES.INVALID_NIT_CHECK_DIGIT", "El dígito de verificación no corresponde al NIT.");

    public static readonly Error PersonTypeNotAllowed =
        Error.Validation("PARTIES.PERSON_TYPE_NOT_ALLOWED", "El tipo de identificación no admite ese tipo de persona (p. ej. una cédula es de persona natural).");

    public static readonly Error InvalidName = Error.Validation(
        "PARTIES.INVALID_NAME", "Persona jurídica: razón social (máx. 200). Persona natural: nombres y apellidos (máx. 100 cada uno).");

    public static readonly Error InvalidFiscalData =
        Error.Validation("PARTIES.INVALID_FISCAL_DATA", "Régimen o responsabilidades fiscales inválidos.");

    public static readonly Error InvalidEmail = Error.Validation("PARTIES.INVALID_EMAIL", "El correo electrónico no es válido.");

    public static readonly Error InvalidContact = Error.Validation(
        "PARTIES.INVALID_CONTACT", "Contactos inválidos: nombre obligatorio, correo válido, máximo 20 y un solo contacto principal.");

    public static readonly Error SystemParty = Error.BusinessRule("PARTIES.SYSTEM_PARTY", "El tercero del sistema (Consumidor final) no se modifica.");

    public static readonly Error Merged = Error.BusinessRule("PARTIES.MERGED", "El tercero fue fusionado en otro: modifique el que se conservó.");

    public static readonly Error Duplicated = Error.Conflict("PARTIES.IDENTIFICATION_DUPLICATED", "Ya existe un tercero con esa identificación.");

    public static readonly Error Inactive = Error.BusinessRule("PARTIES.INACTIVE", "El tercero está inactivo.");
}
