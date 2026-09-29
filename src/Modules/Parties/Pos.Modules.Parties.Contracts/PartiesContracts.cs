using Pos.Application.Abstractions.Security;

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

public sealed record PartyContactDto(Guid Id, string Name, string? Position, string? Phone, string? Email, bool IsPrimary);

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
