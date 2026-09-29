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

    /// <summary>Crédito de un cambio de mercancía (Fase 7, D7-11): paga parte de la venta nueva; no es dinero ni entra al cajón.</summary>
    ExchangeCredit,
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

    /// <summary>Medio del sistema con el que el crédito de un cambio paga la venta nueva.</summary>
    public const string ExchangeCreditCode = "CAMBIO";

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

        if (!isActive && IsSystem && Code is CashCode or ExchangeCreditCode)
        {
            return CashErrors.CashMethodRequired;
        }

        Name = trimmed;
        DianCode = dian;
        RequiresReference = requiresReference && Kind is not (PaymentMethodKind.Cash or PaymentMethodKind.ExchangeCredit);
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

    public static readonly Error CashMethodRequired = Error.BusinessRule(
        "CASH.CASH_METHOD_REQUIRED", "El efectivo y el crédito por cambio del sistema no se pueden inactivar.");

    public static readonly Error ExchangeCreditReserved = Error.Validation(
        "CASH.EXCHANGE_CREDIT_RESERVED", "El crédito por cambio es un medio del sistema: no se crean otros de ese tipo.");

    public static readonly Error OpenSales = Error.Conflict(
        "CASH.OPEN_SALES", "La caja tiene ventas en curso o suspendidas: cóbrelas o cancélelas antes de cerrar (RN-CSH-03).");

    public static readonly Error PaymentMethodCodeDuplicated = Error.Conflict("CASH.PAYMENT_METHOD_CODE_DUPLICATED", "Ya existe un medio de pago con ese código.");

    public static readonly Error InvalidDenomination = Error.Validation("CASH.INVALID_DENOMINATION", "Denominación inválida: moneda de 3 letras y valor mayor que cero.");

    public static readonly Error SessionNotFound = Error.NotFound("CASH.SESSION_NOT_FOUND", "La jornada de caja no existe.");

    public static readonly Error NoOpenSession = Error.NotFound("CASH.NO_OPEN_SESSION", "No hay una jornada de caja abierta en esta caja.");

    public static readonly Error SessionAlreadyOpen = Error.Conflict(
        "CASH.SESSION_ALREADY_OPEN", "La caja ya tiene una jornada sin cerrar o el cajero ya tiene una jornada abierta (RN-CSH-01).");

    public static readonly Error TerminalRequired = Error.Forbidden(
        "CASH.TERMINAL_REQUIRED", "La jornada se abre desde la caja emparejada (entrada con código y PIN) o desde el equipo Caja Única (D6-09).");

    public static readonly Error SessionNotOpen = Error.BusinessRule("CASH.SESSION_NOT_OPEN", "La jornada no está abierta: no admite movimientos (RN-CSH-02).");

    public static readonly Error InvalidStatus = Error.BusinessRule("CASH.INVALID_STATUS", "La jornada no está en un estado que permita esta acción.");

    public static readonly Error NotYourSession = Error.Forbidden("CASH.NOT_YOUR_SESSION", "La jornada es de otro cajero.");

    public static readonly Error ReasonRequired = Error.Validation("CASH.REASON_REQUIRED", "Indique el motivo (de 5 a 300 caracteres).");

    public static readonly Error InvalidMovement = Error.Validation(
        "CASH.INVALID_MOVEMENT", "Movimiento inválido: valor mayor que cero con hasta 2 decimales (cero solo en la apertura del cajón sin venta).");

    public static readonly Error InvalidCount = Error.Validation(
        "CASH.INVALID_COUNT", "Conteo inválido: una línea por denominación o por medio, cantidades y valores mayores o iguales a cero.");

    public static readonly Error InsufficientCash = Error.BusinessRule(
        "CASH.INSUFFICIENT_CASH", "El efectivo esperado de la caja no alcanza: un retiro o pago no puede dejarlo negativo (RN-CSH-06).");

    public static readonly Error MethodNotCash = Error.Validation("CASH.METHOD_NOT_CASH", "Desde la caja solo se paga con un medio que afecte el cajón (efectivo).");

    public static readonly Error DifferenceNoteRequired = Error.Validation(
        "CASH.DIFFERENCE_NOTE_REQUIRED", "La diferencia supera el umbral: escriba una observación (RN-CSH-05).");

    public static readonly Error SelfReview = Error.BusinessRule("CASH.SELF_REVIEW", "El cajero no revisa su propio cierre.");

    public static readonly Error ExpectedHidden = Error.Forbidden(
        "CASH.BLIND_COUNT", "El arqueo es ciego: lo esperado se conoce al confirmar el conteo (o con el permiso cash.report.view).");

    public static readonly Error NotClosed = Error.BusinessRule("CASH.NOT_CLOSED", "El reporte Z existe cuando la jornada está cerrada.");
}
