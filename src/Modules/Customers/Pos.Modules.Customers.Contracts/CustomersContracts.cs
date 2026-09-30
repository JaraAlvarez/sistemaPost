using Pos.Application.Abstractions.Security;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Customers.Contracts;

/// <summary>Permisos del módulo Customers (docs/fases/fase-08-propuesta.md §7).</summary>
public static class CustomersPermissions
{
    public const string CustomerView = "customers.customer.view";
    public const string CustomerQuickCreate = "customers.customer.quick_create";
    public const string CustomerManage = "customers.customer.manage";
    public const string PricingAssign = "customers.pricing.assign";
    public const string GroupManage = "customers.group.manage";
    public const string HistoryView = "customers.history.view";
    public const string PrivacyManage = "customers.privacy.manage";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(CustomerView, "Buscar clientes y ver su ficha básica", isSensitive: false),
        new(CustomerQuickCreate, "Crear clientes desde la caja, completar datos vacíos y registrar su autorización de datos", isSensitive: false),
        new(CustomerManage, "Corregir los datos de un cliente y bloquearlo", isSensitive: false),
        new(PricingAssign, "Asignar el grupo y la lista de precio de un cliente", isSensitive: true),
        new(GroupManage, "Crear y modificar grupos de clientes", isSensitive: false),
        new(HistoryView, "Ver el historial y el resumen de compras de un cliente", isSensitive: false),
        new(PrivacyManage, "Política de datos, solicitudes de titulares, exportación y supresión de datos", isSensitive: true),
    ];
}

/// <summary>
/// Cliente visto por la venta: lista de precio efectiva (la propia o la del grupo; null = general), grupo, si siempre pide factura
/// y la autorización de datos vigente (para el aviso del tiquete).
/// </summary>
public sealed record CustomerSaleProfile(
    Guid PartyId, string GroupCode, Guid? PriceListId, bool AlwaysRequestsInvoice, bool ServiceConsent, int? ConsentPolicyVersion);

/// <summary>Clientes vistos por ventas (D8-02, D8-09).</summary>
public interface ICustomerDirectory
{
    /// <summary>
    /// Perfil para vender a un tercero. Si el tercero aún no tiene rol de cliente se crea (origen AUTO_ON_SALE, grupo por
    /// defecto). Un cliente bloqueado devuelve CUSTOMERS.BLOCKED (RN-CUS-03).
    /// </summary>
    Task<Result<CustomerSaleProfile>> ResolveForSaleAsync(Guid partyId, Guid? branchId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Crédito (fiado) del cliente (D8-15). En esta fase la implementación es nula: el medio de pago de crédito no se puede crear
/// y, si llegara, la venta lo rechaza. Se activa en la Fase 8-B.
/// </summary>
public interface ICustomerCreditGate
{
    Task<Result> AuthorizeAsync(Guid partyId, decimal amount, CancellationToken cancellationToken = default);
}

/// <summary>Programa de puntos (D8-16). Implementación nula hasta la Fase 8-B.</summary>
public interface ILoyaltyProgram
{
    Task<Result> RedeemAsync(Guid partyId, decimal amount, CancellationToken cancellationToken = default);
}

public sealed record CustomerGroupDto(Guid Id, string Code, string Name, Guid? PriceListId, bool IsDefault, string Status);

public sealed record CustomerDto(
    Guid PartyId,
    string DisplayName,
    string PersonType,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string? Email,
    string? Phone,
    string? Address,
    string? MunicipalityCode,
    string TaxRegime,
    IReadOnlyList<string> FiscalResponsibilities,
    Guid GroupId,
    string GroupCode,
    Guid? PriceListId,
    string Status,
    string? BlockReason,
    string Origin,
    bool AlwaysRequestsInvoice,
    bool ServiceConsent,
    bool MarketingConsent,
    IReadOnlyList<string> MarketingChannels,
    int? ConsentPolicyVersion,
    DateTimeOffset? AnonymizedAt,
    bool MissingInvoiceEmail);

/// <summary>Resultado de la búsqueda de la caja. <c>HasCustomerRole</c> = false: tercero sin rol (p. ej. un proveedor).</summary>
public sealed record CustomerLookupDto(
    Guid PartyId, string DisplayName, string IdentificationType, string IdentificationNumber, string? CheckDigit, string? Phone, string MatchedBy, bool Exact,
    bool HasCustomerRole, string? GroupCode, string? Status, bool MissingInvoiceEmail);

/// <summary>Alta rápida: el cliente (nuevo o el existente con esa identificación) y los posibles duplicados CC/NIT (RN-CUS-04).</summary>
public sealed record QuickCreateResultDto(CustomerDto Customer, bool Created, IReadOnlyList<CustomerLookupDto> PossibleDuplicates);

public sealed record ConsentDto(
    Guid Id, string Purpose, bool Granted, string Channel, IReadOnlyList<string> MarketingChannels, int PolicyVersion, string? Evidence, Guid UserId,
    string? UserName, DateTimeOffset OccurredAt);

public sealed record PrivacyPolicyDto(Guid Id, int Version, string Status, string ShortNotice, string Text, string TextHash, DateTimeOffset? ActivatedAt);

public sealed record DataRequestDto(
    Guid Id, Guid PartyId, string PartyName, string Type, string Channel, string Detail, DateOnly ReceivedOn, DateOnly DueOn, string Status, bool Overdue,
    string? Response, DateTimeOffset? ResolvedAt);

/// <summary>Estado de la privacidad para el tablero: política pendiente de revisión, clientes sin autorización y solicitudes.</summary>
public sealed record PrivacyStatusDto(bool PolicyPendingReview, int? ActivePolicyVersion, int CustomersWithoutConsent, int OpenRequests, int DueSoonRequests,
    int OverdueRequests);
