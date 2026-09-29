using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Cash.Domain;

public enum PaymentMethodKind
{
    Cash,
    DebitCard,
    CreditCard,
    Transfer,
    Wallet,
    Voucher,
    Other,
}

public enum MasterStatus
{
    Active,
    Inactive,
}

/// <summary>
/// Medio de pago de la empresa (D5-13) con su código de la factura electrónica. Solo el efectivo afecta el cajón: entra o
/// sale físicamente y se cuenta por denominación en el arqueo (Fase 6). Los medios del sistema no cambian de código ni de
/// tipo, y el efectivo no se inactiva.
/// </summary>
[Audited("cash")]
public sealed class PaymentMethod : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    public const string CashCode = "EFECTIVO";

    private PaymentMethod(Guid id, Guid companyId, string code, string name)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        Name = name;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public PaymentMethodKind Kind { get; private set; }

    public string? DianCode { get; private set; }

    public bool RequiresReference { get; private set; }

    public bool AffectsCashDrawer { get; private set; }

    public int SortOrder { get; private set; }

    public bool IsSystem { get; private set; }

    public MasterStatus Status { get; private set; } = MasterStatus.Active;

    public string AuditLabel => $"Medio de pago {Code} · {Name}";

    public static Result<PaymentMethod> Create(
        Guid id, Guid companyId, string code, string name, PaymentMethodKind kind, string? dianCode, bool requiresReference, int sortOrder, bool isSystem = false)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length is < 2 or > 20 || !normalized.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            return CashErrors.InvalidPaymentMethod;
        }

        var method = new PaymentMethod(id, companyId, normalized, string.Empty) { Kind = kind, IsSystem = isSystem };
        var result = method.Update(name, dianCode, requiresReference, sortOrder, isActive: true);
        return result.IsSuccess ? method : result.Error;
    }

    public Result Update(string name, string? dianCode, bool requiresReference, int sortOrder, bool isActive)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var dian = string.IsNullOrWhiteSpace(dianCode) ? null : dianCode.Trim().ToUpperInvariant();
        if (trimmed.Length is 0 or > 60 || dian is { Length: > 5 } || sortOrder is < 0 or > 999)
        {
            return CashErrors.InvalidPaymentMethod;
        }

        if (!isActive && Code == CashCode && IsSystem)
        {
            return CashErrors.CashMethodRequired;
        }

        Name = trimmed;
        DianCode = dian;
        RequiresReference = requiresReference && Kind != PaymentMethodKind.Cash;
        AffectsCashDrawer = Kind == PaymentMethodKind.Cash;
        SortOrder = sortOrder;
        Status = isActive ? MasterStatus.Active : MasterStatus.Inactive;
        return Result.Success();
    }
}

/// <summary>Errores de negocio de la caja con código estable.</summary>
public static class CashErrors
{
    public static readonly Error PaymentMethodNotFound = Error.NotFound("CASH.PAYMENT_METHOD_NOT_FOUND", "El medio de pago no existe o está inactivo.");

    public static readonly Error InvalidPaymentMethod = Error.Validation(
        "CASH.INVALID_PAYMENT_METHOD", "Código de 2 a 20 mayúsculas, dígitos o _, nombre de hasta 60 caracteres, código DIAN de hasta 5 y orden 0–999.");

    public static readonly Error CashMethodRequired = Error.BusinessRule("CASH.CASH_METHOD_REQUIRED", "El efectivo no se puede inactivar.");

    public static readonly Error PaymentMethodCodeDuplicated = Error.Conflict("CASH.PAYMENT_METHOD_CODE_DUPLICATED", "Ya existe un medio de pago con ese código.");
}
