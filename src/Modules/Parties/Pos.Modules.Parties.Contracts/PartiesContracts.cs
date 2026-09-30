using Pos.Application.Abstractions.Security;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Parties.Contracts;

/// <summary>Permisos del módulo Parties (docs/fases/fase-05-propuesta.md §7).</summary>
public static class PartiesPermissions
{
    public const string PartyView = "parties.party.view";
    public const string PartyManage = "parties.party.manage";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(PartyView, "Consultar terceros (proveedores y clientes)", isSensitive: false),
        new(PartyManage, "Crear y modificar terceros y sus contactos", isSensitive: false),
    ];
}

/// <summary>Tercero visto por otros módulos (compras, ventas). <c>Status</c>: ACTIVE, INACTIVE o MERGED.</summary>
public sealed record PartyInfo(
    Guid Id,
    string DisplayName,
    string PersonType,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string? Email,
    string Status,
    bool IsSystem,
    Guid? MergedIntoId);

/// <summary>Consultas de terceros para otros módulos.</summary>
public interface IPartyDirectory
{
    Task<IReadOnlyDictionary<Guid, PartyInfo>> GetAsync(IReadOnlyCollection<Guid> partyIds, CancellationToken cancellationToken = default);

    /// <summary>"Consumidor final" de la empresa (ventas sin cliente identificado).</summary>
    Task<PartyInfo?> GetFinalConsumerAsync(CancellationToken cancellationToken = default);
}

/// <summary>Datos para registrar un tercero desde otro módulo (alta rápida de clientes). <c>PersonType</c>: NATURAL o LEGAL.</summary>
public sealed record PartyRegistration(
    string PersonType,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string? LegalName,
    string? FirstNames,
    string? LastNames,
    string? TradeName,
    string? TaxRegime,
    IReadOnlyList<string>? FiscalResponsibilities,
    string? Email,
    string? Phone,
    string? Address,
    string? MunicipalityCode);

/// <summary>Tercero con sus datos fiscales y de contacto (snapshot del comprador, ficha del cliente).</summary>
public sealed record PartyProfile(
    Guid Id,
    string DisplayName,
    string PersonType,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string? LegalName,
    string? FirstNames,
    string? LastNames,
    string TaxRegime,
    IReadOnlyList<string> FiscalResponsibilities,
    string? Email,
    string? Phone,
    string? Address,
    string? MunicipalityCode,
    string Status,
    bool IsSystem);

/// <summary>
/// Resultado de registrar: el tercero (nuevo o el que ya existía con esa identificación, sin modificarlo) y los posibles
/// duplicados (mismo número con otro tipo, p. ej. CC 123 y NIT 123-4 de la misma persona, D8-17).
/// </summary>
public sealed record PartyRegistrationResult(PartyProfile Party, bool Created, IReadOnlyList<PartyProfile> PossibleDuplicates);

/// <summary>Coincidencia de la búsqueda de la caja. <c>MatchedBy</c>: IDENTIFICATION, PHONE o NAME.</summary>
public sealed record PartyMatch(Guid Id, string DisplayName, string IdentificationType, string IdentificationNumber, string? CheckDigit, string? Phone,
    string? Email, string MatchedBy, bool Exact);

/// <summary>Terceros vistos por el módulo de clientes (Fase 8): registro, completar vacíos, corrección, supresión y búsqueda.</summary>
public interface IPartyRegistry
{
    /// <summary>Crea el tercero o devuelve el existente con esa identificación (dos cajas a la vez: una crea, la otra lo recibe).</summary>
    Task<Result<PartyRegistrationResult>> RegisterAsync(PartyRegistration registration, CancellationToken cancellationToken = default);

    /// <summary>Completa solo los datos de contacto vacíos; devuelve el perfil y los campos completados.</summary>
    Task<Result<(PartyProfile Party, IReadOnlyList<string> Filled)>> CompleteAsync(
        Guid partyId, string? email, string? phone, string? address, string? municipalityCode, CancellationToken cancellationToken = default);

    /// <summary>Corrección completa de los datos (supervisor o administración).</summary>
    Task<Result<PartyProfile>> CorrectAsync(Guid partyId, PartyRegistration registration, CancellationToken cancellationToken = default);

    Task<Result> AnonymizeAsync(Guid partyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, PartyProfile>> GetProfilesAsync(IReadOnlyCollection<Guid> partyIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Búsqueda de la caja (D8-03): el texto se interpreta como identificación (exacta o prefijo, sin DV ni puntos), teléfono
    /// (solo dígitos, 7 o más) o nombre (sin tildes). Exactos primero; sin el Consumidor final ni fusionados.
    /// </summary>
    Task<IReadOnlyList<PartyMatch>> LookupAsync(string text, int limit, CancellationToken cancellationToken = default);
}

public sealed record PartyContactDto(Guid Id, string Name, string? Position, string? Phone, string? Email, bool IsPrimary, string Role = "OTHER");

public sealed record PartyDto(
    Guid Id,
    string PersonType,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string DisplayName,
    string? LegalName,
    string? FirstNames,
    string? LastNames,
    string? TradeName,
    string TaxRegime,
    IReadOnlyList<string> FiscalResponsibilities,
    string? Email,
    string? Phone,
    string? Address,
    string? MunicipalityCode,
    string? Notes,
    bool IsSystem,
    string Status,
    Guid? MergedIntoId,
    IReadOnlyList<PartyContactDto> Contacts);

public sealed record PartySummaryDto(Guid Id, string DisplayName, string IdentificationType, string IdentificationNumber, string? CheckDigit, string Status, bool IsSystem);

public sealed record PartyPageDto(IReadOnlyList<PartySummaryDto> Items, int Page, int PageSize, int TotalCount);
