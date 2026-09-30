using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Customers.Contracts;
using Pos.Modules.Customers.Domain;
using Pos.Modules.Parties.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Customers.Application;

/// <summary>Clientes, grupos, políticas, autorizaciones y solicitudes (EF Core).</summary>
public interface ICustomerStore
{
    void Add(CustomerGroup group);

    void Add(Customer customer);

    void Add(PrivacyPolicy policy);

    void Add(CustomerConsent consent);

    void Add(DataRequest request);

    Task<Customer?> GetCustomerAsync(Guid partyId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, Customer>> GetCustomersAsync(IReadOnlyCollection<Guid> partyIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomerGroup>> GetGroupsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PrivacyPolicy>> GetPoliciesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomerConsent>> GetConsentsAsync(Guid partyId, CancellationToken cancellationToken);

    Task<DataRequest?> GetRequestAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<DataRequest>> ListRequestsAsync(DataRequestStatus? status, CancellationToken cancellationToken);
}

public sealed record CustomerRow(Guid PartyId, string DisplayName, string IdentificationType, string IdentificationNumber, string? CheckDigit, string? Phone,
    string? Email, string GroupCode, string Status, bool ServiceConsent);

/// <summary>Lecturas con SQL (cruzan con terceros para mostrar nombres).</summary>
public interface ICustomerQueries
{
    Task<(IReadOnlyList<CustomerRow> Items, int Total)> ListAsync(string? search, Guid? groupId, string? status, int page, int pageSize, CancellationToken cancellationToken);

    Task<int> CountWithoutConsentAsync(CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

/// <summary>Configuraciones de clientes. REGLA: el valor por defecto de una clave publicada no se cambia.</summary>
public static class CustomersSettings
{
    /// <summary>D8-07: sin autorización SERVICE no se muestra el historial del cliente.</summary>
    public static readonly SettingDefinition<bool> HistoryRequiresConsent = new(
        "customers.history_requires_consent", true, SettingScope.Company,
        "Solo se muestra el historial de compras de clientes que autorizaron el tratamiento de datos para atención (Ley 1581).");

    /// <summary>Días de anticipación de la alerta de solicitudes de titulares por vencer.</summary>
    public static readonly SettingDefinition<int> RequestAlertDays = new(
        "customers.request_alert_days", 3, SettingScope.Company, "Días hábiles antes del vencimiento en que se alerta una solicitud de un titular.",
        v => v is >= 0 and <= 15 ? null : "Entre 0 y 15.");

    public static IEnumerable<SettingDefinition> All => [HistoryRequiresConsent, RequestAlertDays];
}

public sealed class CustomersSettingsProvider : ISettingDefinitionProvider
{
    public IEnumerable<SettingDefinition> GetDefinitions() => CustomersSettings.All;
}

public sealed class CustomersPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => CustomersPermissions.All;
}

internal static class CustomersMapping
{
    public static readonly Error SetupRequired = Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");

    public static string Db<TEnum>(this TEnum value)
        where TEnum : struct, Enum
    {
        var name = value.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }

    public static CustomerDto ToDto(this Customer c, PartyProfile p, CustomerGroup? group) => new(
        c.PartyId, p.DisplayName, p.PersonType, p.IdentificationType, p.IdentificationNumber, p.CheckDigit, p.Email, p.Phone, p.Address, p.MunicipalityCode,
        p.TaxRegime, p.FiscalResponsibilities, c.GroupId, group?.Code ?? string.Empty, c.PriceListId, c.Status.Db(), c.BlockReason, c.Origin.Db(),
        c.AlwaysRequestsInvoice, c.ServiceConsent, c.MarketingConsent, c.MarketingChannels?.Split(';') ?? [], c.ConsentPolicyVersion, c.AnonymizedAt,
        string.IsNullOrWhiteSpace(p.Email));

    public static CustomerGroupDto ToDto(this CustomerGroup g) => new(g.Id, g.Code, g.Name, g.PriceListId, g.IsDefault, g.Status.Db());

    public static PrivacyPolicyDto ToDto(this PrivacyPolicy p) => new(p.Id, p.Version, p.Status.Db(), p.ShortNotice, p.Text, p.TextHash, p.ActivatedAt);
}

/// <summary>
/// Datos iniciales de cada empresa: grupo GENERAL (por defecto, lista general) y una PLANTILLA de política de tratamiento de datos
/// pendiente de revisión del propietario (el tablero lo avisa hasta que la active). Idempotente.
/// </summary>
public sealed class CustomersInitializer(ICustomerStore store, IIdGenerator ids) : ICompanyInitializer
{
    public const string TemplateNotice =
        "Sus datos se usan para su factura, su historial de compras y atenderle. Puede consultarlos, corregirlos o pedir su supresión " +
        "en la caja o por los canales de la empresa. Política completa disponible en la tienda.";

    public const string TemplateText =
        "POLÍTICA DE TRATAMIENTO DE DATOS PERSONALES (PLANTILLA: el responsable debe revisarla con su asesor legal antes de activarla).\n\n" +
        "1. Responsable: la empresa que opera este punto de venta, identificada con su razón social y NIT en el tiquete.\n" +
        "2. Finalidades: (a) expedir comprobantes y facturas y cumplir obligaciones tributarias; (b) con autorización, registrar el " +
        "historial de compras y atender solicitudes, cambios y garantías; (c) solo con autorización expresa, enviar información comercial " +
        "por los canales que el titular elija.\n" +
        "3. Derechos del titular (Ley 1581 de 2012): conocer, actualizar, rectificar y suprimir sus datos, revocar la autorización y " +
        "presentar quejas ante la Superintendencia de Industria y Comercio. Las consultas se atienden en 10 días hábiles y los reclamos " +
        "en 15 días hábiles.\n" +
        "4. Los documentos contables y fiscales se conservan por el término legal aunque el titular pida la supresión de sus datos.\n" +
        "5. Canales de atención: en la caja de la tienda o por los medios de contacto publicados por la empresa.";

    public int Order => 70;

    public async Task InitializeAsync(Guid companyId, CancellationToken cancellationToken)
    {
        if ((await store.GetGroupsAsync(cancellationToken)).All(g => !g.IsDefault))
        {
            store.Add(CustomerGroup.Create(ids.NewId(), companyId, CustomerGroup.DefaultCode, "General", null, isDefault: true).Value);
        }

        if ((await store.GetPoliciesAsync(cancellationToken)).Count == 0)
        {
            store.Add(PrivacyPolicy.Create(ids.NewId(), companyId, 1, TemplateText, TemplateNotice).Value);
        }
    }
}

/// <summary>Crédito sin activar (D8-15): siempre rechaza.</summary>
public sealed class NullCustomerCreditGate : ICustomerCreditGate
{
    public Task<Result> AuthorizeAsync(Guid partyId, decimal amount, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result>(Error.BusinessRule("CUSTOMERS.CREDIT_NOT_AVAILABLE", "La venta a crédito se activa en la Fase 8-B."));
}

/// <summary>Puntos sin activar (D8-16): siempre rechaza.</summary>
public sealed class NullLoyaltyProgram : ILoyaltyProgram
{
    public Task<Result> RedeemAsync(Guid partyId, decimal amount, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result>(Error.BusinessRule("CUSTOMERS.LOYALTY_NOT_AVAILABLE", "El programa de puntos se activa en la Fase 8-B."));
}

/// <summary>
/// Clientes vistos por ventas (D8-02): crea el rol la primera vez que un tercero compra (grupo por defecto) y da la lista efectiva
/// (propia → grupo). Un cliente bloqueado no se asigna a ventas (RN-CUS-03).
/// </summary>
public sealed class CustomerDirectory(ICustomerStore store, IInstallationContext installation) : ICustomerDirectory
{
    public async Task<Result<CustomerSaleProfile>> ResolveForSaleAsync(Guid partyId, Guid? branchId, CancellationToken cancellationToken = default)
    {
        var groups = await store.GetGroupsAsync(cancellationToken);
        var customer = await store.GetCustomerAsync(partyId, cancellationToken);
        if (customer is null)
        {
            if (installation.CompanyId is not { } companyId)
            {
                return CustomersMapping.SetupRequired;
            }

            customer = Customer.Create(partyId, companyId, groups.Single(g => g.IsDefault).Id, CustomerOrigin.AutoOnSale, branchId);
            store.Add(customer);
        }

        if (customer.Status == CustomerStatus.Blocked)
        {
            return Error.BusinessRule(CustomerErrors.Blocked.Code, $"{CustomerErrors.Blocked.Message} Motivo: {customer.BlockReason}");
        }

        var group = groups.FirstOrDefault(g => g.Id == customer.GroupId);
        return new CustomerSaleProfile(
            partyId, group?.Code ?? CustomerGroup.DefaultCode, customer.PriceListId ?? group?.PriceListId, customer.AlwaysRequestsInvoice, customer.ServiceConsent,
            customer.ConsentPolicyVersion);
    }
}
