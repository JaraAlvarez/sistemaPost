using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Cash.Contracts;

/// <summary>Permisos del módulo Cash (docs/fases/fase-05-propuesta.md §7 y fase-06-propuesta.md §7).</summary>
public static class CashPermissions
{
    public const string PaymentMethodManage = "cash.payment_method.manage";
    public const string SessionOperate = "cash.session.operate";
    public const string MovementWithdraw = "cash.movement.withdraw";
    public const string DrawerOpen = "cash.drawer.open";
    public const string SessionCloseAny = "cash.session.close_any";
    public const string SessionReview = "cash.session.review";
    public const string ReportView = "cash.report.view";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(PaymentMethodManage, "Crear y modificar medios de pago", isSensitive: true),
        new(SessionOperate, "Abrir y cerrar la propia jornada, registrar ingresos y gastos menores", isSensitive: false),
        new(MovementWithdraw, "Retirar efectivo de la caja (admite autorización de supervisor)", isSensitive: true),
        new(DrawerOpen, "Abrir el cajón sin venta (admite autorización de supervisor)", isSensitive: true),
        new(SessionCloseAny, "Cerrar la jornada de otro cajero (cierre por supervisor)", isSensitive: true),
        new(SessionReview, "Revisar cierres con diferencia y registrar correcciones de caja", isSensitive: true),
        new(ReportView, "Ver reportes X y Z de cualquier caja y el esperado antes del arqueo", isSensitive: true),
    ];
}

/// <summary>Salida de dinero de una jornada pedida por otro módulo: EXPENSE (gastos) o SUPPLIER_PAYMENT (compras).</summary>
public sealed record CashOutflowRequest(
    Guid SessionId, string MovementType, Guid PaymentMethodId, decimal Amount, string SourceType, Guid SourceId, string? SourceNumber, string? Reason);

/// <summary>Movimiento registrado y efectivo esperado que queda en la caja.</summary>
public sealed record CashMovementReceipt(Guid MovementId, int LineNo, decimal ExpectedCash);

/// <summary>
/// La caja vista por otros módulos (gastos, compras, identidad; ventas en la Fase 7). Registra la salida en la
/// transacción del documento origen: jornada abierta, medio que afecta el cajón y efectivo suficiente (RN-CSH-06).
/// </summary>
public interface ICashRegister
{
    Task<Pos.SharedKernel.Results.Result<CashMovementReceipt>> RecordOutflowAsync(CashOutflowRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve a la caja una salida anulada (gasto o pago): movimiento CORRECTION de entrada en la MISMA jornada, que debe
    /// seguir abierta (el cierre es definitivo, RN-CSH-08).
    /// </summary>
    Task<Pos.SharedKernel.Results.Result<CashMovementReceipt>> ReturnOutflowAsync(CashOutflowRequest original, string reason, CancellationToken cancellationToken = default);

    /// <summary>RN-SEC-07: el usuario tiene una jornada sin cerrar.</summary>
    Task<bool> HasOpenSessionAsync(Guid cashierId, CancellationToken cancellationToken = default);
}

/// <summary>Medio de pago visto por otros módulos (compras, ventas). <c>Kind</c>: CASH, DEBIT_CARD, CREDIT_CARD, TRANSFER, WALLET, VOUCHER u OTHER.</summary>
public sealed record PaymentMethodInfo(
    Guid Id, string Code, string Name, string Kind, string? DianCode, bool RequiresReference, bool AffectsCashDrawer, bool IsActive);

public interface IPaymentMethodDirectory
{
    Task<PaymentMethodInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record PaymentMethodDto(
    Guid Id, string Code, string Name, string Kind, string? DianCode, bool RequiresReference, bool AffectsCashDrawer, int SortOrder, bool IsSystem, string Status);

/// <summary>Resultado de registrar un movimiento. <c>ExpectedCash</c> solo si quien opera puede ver lo esperado.</summary>
public sealed record CashMovementResultDto(Guid MovementId, int LineNo, string MovementType, decimal Amount, decimal? ExpectedCash, bool CashAboveMaximum);

public sealed record DenominationDto(Guid Id, string CurrencyCode, decimal Value, string Kind, int SortOrder, string Status);

public sealed record CashMovementDto(
    Guid Id, int LineNo, string MovementType, Guid PaymentMethodId, string PaymentMethodName, int Direction, decimal Amount, string? Reason,
    string? SourceType, Guid? SourceId, string? SourceNumber, Guid UserId, string? UserName, Guid? AuthorizedBy, string? AuthorizedByName,
    DateTimeOffset OccurredAt);

/// <summary>Esperado (null si el arqueo es ciego para quien consulta), contado y diferencia de un medio de pago.</summary>
public sealed record CashMethodTotalDto(
    Guid PaymentMethodId, string Code, string Name, bool AffectsCashDrawer, decimal? Expected, decimal? Counted, decimal? Difference, int Transactions);

/// <summary>
/// Jornada de caja. Con arqueo ciego, quien no tiene <c>cash.report.view</c> no ve lo esperado mientras la jornada no
/// esté cerrada (<c>ExpectedHidden</c>).
/// </summary>
public sealed record CashSessionDto(
    Guid Id,
    string Number,
    Guid BranchId,
    Guid PosTerminalId,
    string TerminalCode,
    Guid CashierId,
    string CashierName,
    DateOnly BusinessDate,
    DateTimeOffset OpenedAt,
    decimal OpeningFloat,
    string Status,
    bool BlindCount,
    bool ExpectedHidden,
    DateTimeOffset? ClosingStartedAt,
    DateTimeOffset? ClosedAt,
    bool ClosedBySupervisor,
    string? CloseReason,
    decimal? ExpectedTotal,
    decimal? CountedTotal,
    decimal? Difference,
    string? DifferenceNote,
    bool ReviewRequired,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote,
    long? ZSealNo,
    string? ZSealCode,
    IReadOnlyList<CashMethodTotalDto> Totals,
    IReadOnlyList<CashMovementDto> Movements);

public sealed record CashSessionSummaryDto(
    Guid Id, string Number, string TerminalCode, string CashierName, DateOnly BusinessDate, string Status, decimal? Difference, bool ReviewRequired,
    bool Reviewed, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt);

public sealed record CashMovementTypeTotalDto(string MovementType, int Count, decimal Total);

public sealed record CashWithdrawalDto(DateTimeOffset OccurredAt, decimal Amount, string? Reason, string? AuthorizedByName);

/// <summary>Reporte X (parcial, sin cerrar) o Z (cierre, con el sello de la auditoría). <c>Text</c>: impresión de 80 mm (42 columnas).</summary>
public sealed record CashReportDto(
    string Kind,
    Guid SessionId,
    string Number,
    string CompanyName,
    string BranchName,
    string TerminalCode,
    string CashierName,
    DateOnly BusinessDate,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset PrintedAt,
    decimal OpeningFloat,
    IReadOnlyList<CashMovementTypeTotalDto> MovementsByType,
    IReadOnlyList<CashMethodTotalDto> Totals,
    decimal ExpectedTotal,
    decimal? CountedTotal,
    decimal? Difference,
    string? DifferenceNote,
    int NoSaleOpenings,
    IReadOnlyList<CashWithdrawalDto> Withdrawals,
    bool ClosedBySupervisor,
    long? SealNo,
    string? SealCode,
    string Text);
