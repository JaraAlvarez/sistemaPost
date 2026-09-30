using System.Text.RegularExpressions;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Customers.Domain;

public enum CustomerStatus
{
    Active,
    Inactive,

    /// <summary>No se asigna a ventas nuevas (RN-CUS-03), con motivo.</summary>
    Blocked,
}

/// <summary>Cómo nació el rol de cliente.</summary>
public enum CustomerOrigin
{
    /// <summary>Alta rápida en la caja.</summary>
    PosQuick,

    Backoffice,

    /// <summary>Un tercero existente (p. ej. un proveedor) compró por primera vez (D8-02).</summary>
    AutoOnSale,

    Import,
}

/// <summary>Estado del crédito (fiado) del cliente. Hoy solo <c>None</c>: se activa en la Fase 8-B (D8-15).</summary>
public enum CreditStatus
{
    None,
}

/// <summary>Estado del programa de puntos. Hoy solo <c>None</c>: se activa en la Fase 8-B (D8-16).</summary>
public enum LoyaltyStatus
{
    None,
}

public enum MasterStatus
{
    Active,
    Inactive,
}

public static class CustomerErrors
{
    public static readonly Error NotFound = Error.NotFound("CUSTOMERS.NOT_FOUND", "El cliente no existe.");

    public static readonly Error GroupNotFound = Error.NotFound("CUSTOMERS.GROUP_NOT_FOUND", "El grupo de clientes no existe o está inactivo.");

    public static readonly Error InvalidGroup = Error.Validation(
        "CUSTOMERS.INVALID_GROUP", "Grupo inválido: código de 2 a 20 mayúsculas, dígitos o _ y nombre de hasta 80 caracteres.");

    public static readonly Error DefaultGroupRequired = Error.BusinessRule("CUSTOMERS.DEFAULT_GROUP_REQUIRED", "El grupo por defecto no se inactiva.");

    public static readonly Error Blocked = Error.BusinessRule("CUSTOMERS.BLOCKED", "El cliente está bloqueado: no se le puede vender a su nombre (RN-CUS-03).");

    public static readonly Error ReasonRequired = Error.Validation("CUSTOMERS.REASON_REQUIRED", "Indique el motivo (de 5 a 300 caracteres).");

    public static readonly Error Anonymized = Error.BusinessRule("CUSTOMERS.ANONYMIZED", "Los datos del titular fueron suprimidos.");

    public static readonly Error InvalidConsent = Error.Validation(
        "CUSTOMERS.INVALID_CONSENT", "Autorización inválida: finalidad, canal y política vigentes; canales de marketing solo si autoriza marketing.");

    public static readonly Error PolicyNotFound = Error.NotFound("CUSTOMERS.POLICY_NOT_FOUND", "No hay una política de tratamiento de datos.");

    public static readonly Error InvalidPolicy = Error.Validation(
        "CUSTOMERS.INVALID_POLICY", "Política inválida: texto completo (mín. 200 caracteres) y aviso corto (de 20 a 600 caracteres).");

    public static readonly Error InvalidRequest = Error.Validation("CUSTOMERS.INVALID_REQUEST", "Solicitud inválida: tipo, canal y detalle (hasta 1.000 caracteres).");

    public static readonly Error RequestClosed = Error.BusinessRule("CUSTOMERS.REQUEST_CLOSED", "La solicitud ya fue resuelta o rechazada.");
}

/// <summary>Grupo de clientes con su lista de precio (null = la general) (D8-09).</summary>
[Audited("customers")]
public sealed partial class CustomerGroup : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    public const string DefaultCode = "GENERAL";

    private CustomerGroup(Guid id, Guid companyId, string code)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public Guid? PriceListId { get; private set; }

    public bool IsDefault { get; private set; }

    public MasterStatus Status { get; private set; } = MasterStatus.Active;

    public string AuditLabel => $"Grupo de clientes {Code} · {Name}";

    public static Result<CustomerGroup> Create(Guid id, Guid companyId, string code, string name, Guid? priceListId, bool isDefault = false)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(normalized))
        {
            return CustomerErrors.InvalidGroup;
        }

        var group = new CustomerGroup(id, companyId, normalized) { IsDefault = isDefault };
        var updated = group.Update(name, priceListId, isActive: true);
        return updated.IsSuccess ? group : updated.Error;
    }

    public Result Update(string name, Guid? priceListId, bool isActive)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is < 2 or > 80)
        {
            return CustomerErrors.InvalidGroup;
        }

        if (!isActive && IsDefault)
        {
            return CustomerErrors.DefaultGroupRequired;
        }

        Name = trimmed;
        PriceListId = priceListId;
        Status = isActive ? MasterStatus.Active : MasterStatus.Inactive;
        return Result.Success();
    }

    [GeneratedRegex("^[A-Z0-9_]{2,20}$")]
    private static partial Regex CodePattern();
}

/// <summary>
/// Rol cliente de un tercero (D8-01, D8-02): la clave ES el tercero (<c>party_id</c>), así dos tiendas que crean el rol sin
/// conexión convergen en la misma fila. Grupo, lista propia, estado y bloqueo; el estado vigente de las autorizaciones de datos
/// (calculado del libro, D8-06); crédito y puntos reservados para la 8-B.
/// </summary>
[Audited("customers")]
public sealed class Customer : AggregateRoot<Guid>, ICompanyOwned, ISyncVersioned, IHasAuditLabel
{
    private Customer(Guid id, Guid companyId)
        : base(id)
    {
        CompanyId = companyId;
    }

    public Guid CompanyId { get; private set; }

    public Guid GroupId { get; private set; }

    /// <summary>Lista propia del cliente (gana a la del grupo); null = la del grupo.</summary>
    public Guid? PriceListId { get; private set; }

    public CustomerStatus Status { get; private set; } = CustomerStatus.Active;

    public string? BlockReason { get; private set; }

    public CustomerOrigin Origin { get; private set; }

    public Guid? CreatedBranchId { get; private set; }

    public bool AlwaysRequestsInvoice { get; private set; }

    public bool ServiceConsent { get; private set; }

    public bool MarketingConsent { get; private set; }

    /// <summary>Canales de marketing autorizados separados por ";" (EMAIL;SMS…).</summary>
    public string? MarketingChannels { get; private set; }

    public int? ConsentPolicyVersion { get; private set; }

    public DateTimeOffset? ConsentUpdatedAt { get; private set; }

    public DateTimeOffset? AnonymizedAt { get; private set; }

    public CreditStatus CreditStatus { get; private set; } = CreditStatus.None;

    public decimal? CreditLimit { get; private set; }

    public int? CreditTermDays { get; private set; }

    public LoyaltyStatus LoyaltyStatus { get; private set; } = LoyaltyStatus.None;

    public Guid PartyId => Id;

    public string AuditLabel => $"Cliente {Id}";

    public static Customer Create(Guid partyId, Guid companyId, Guid groupId, CustomerOrigin origin, Guid? branchId) =>
        new(partyId, companyId) { GroupId = groupId, Origin = origin, CreatedBranchId = branchId };

    /// <summary>Grupo y lista del cliente (RN-PRL-04: solo propietario o administrador).</summary>
    public Result AssignPricing(Guid groupId, Guid? priceListId)
    {
        if (AnonymizedAt is not null)
        {
            return CustomerErrors.Anonymized;
        }

        GroupId = groupId;
        PriceListId = priceListId;
        return Result.Success();
    }

    public Result ChangeStatus(CustomerStatus status, string? reason)
    {
        if (AnonymizedAt is not null && status != CustomerStatus.Inactive)
        {
            return CustomerErrors.Anonymized;
        }

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (status == CustomerStatus.Blocked && trimmed is not { Length: >= 5 and <= 300 })
        {
            return CustomerErrors.ReasonRequired;
        }

        Status = status;
        BlockReason = status == CustomerStatus.Blocked ? trimmed : null;
        return Result.Success();
    }

    public void SetAlwaysRequestsInvoice(bool value) => AlwaysRequestsInvoice = value;

    /// <summary>Estado vigente de las autorizaciones = el último registro de cada finalidad (D8-06).</summary>
    public void ApplyConsents(IEnumerable<CustomerConsent> consents)
    {
        ArgumentNullException.ThrowIfNull(consents);
        var latest = consents.GroupBy(c => c.Purpose).Select(g => g.OrderBy(c => c.OccurredAt).ThenBy(c => c.Id).Last()).ToList();
        var service = latest.FirstOrDefault(c => c.Purpose == ConsentPurpose.Service);
        var marketing = latest.FirstOrDefault(c => c.Purpose == ConsentPurpose.Marketing);
        ServiceConsent = service?.Granted ?? false;
        MarketingConsent = marketing?.Granted ?? false;
        MarketingChannels = MarketingConsent ? marketing!.MarketingChannels : null;
        ConsentPolicyVersion = latest.Count == 0 ? null : latest.Max(c => c.PolicyVersion);
        ConsentUpdatedAt = latest.Count == 0 ? null : latest.Max(c => c.OccurredAt);
    }

    /// <summary>Supresión del titular (D8-08): el rol queda inactivo, sin autorizaciones ni lista propia.</summary>
    public void Anonymize(DateTimeOffset now)
    {
        Status = CustomerStatus.Inactive;
        BlockReason = null;
        PriceListId = null;
        AlwaysRequestsInvoice = false;
        ServiceConsent = false;
        MarketingConsent = false;
        MarketingChannels = null;
        AnonymizedAt = now;
    }
}
