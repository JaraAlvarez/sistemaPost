using Pos.Application.Abstractions.Data;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Cash.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Cash.Application;

/// <summary>Movimientos de una jornada con SQL directo en la transacción de la petición.</summary>
public interface ICashLedger
{
    /// <summary>Bloquea la jornada (FOR UPDATE) y devuelve el siguiente número de línea: serializa los movimientos de una caja.</summary>
    Task<int> LockAndNextLineAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Esperado y transacciones por medio de pago, calculados de los movimientos (D6-02).</summary>
    Task<IReadOnlyList<MethodMovements>> ExpectedAsync(Guid sessionId, CancellationToken cancellationToken);
}

/// <summary>Datos de pantalla y de reportes (cruza con org e identity para mostrar nombres).</summary>
public interface ICashReadModel
{
    Task<SessionHeader?> GetHeaderAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CashMovementDto>> GetMovementsAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CashSessionSummaryDto>> ListSessionsAsync(SessionFilter filter, CancellationToken cancellationToken);
}

public sealed record SessionHeader(string CompanyName, string BranchName, string TerminalCode, string CashierName);

public sealed record SessionFilter(Guid BranchId, string? Status, DateOnly? From, DateOnly? To, bool PendingReviewOnly);

/// <summary>Configuraciones de la caja. REGLA: el valor por defecto de una clave publicada no se cambia.</summary>
public static class CashSettings
{
    /// <summary>D6-05 (pregunta 1): el cajero cuenta sin ver lo esperado.</summary>
    public static readonly SettingDefinition<bool> BlindCount = new(
        "cash.blind_count", true, SettingScope.Company | SettingScope.Branch, "Arqueo ciego: el cajero no ve lo esperado hasta confirmar su conteo.");

    /// <summary>RN-CSH-05 (pregunta 3): diferencia a partir de la cual se exige observación y revisión del supervisor.</summary>
    public static readonly SettingDefinition<decimal> DifferenceThreshold = new(
        "cash.difference_threshold",
        5_000m,
        SettingScope.Company | SettingScope.Branch,
        "Diferencia (en pesos, valor absoluto) del cierre que exige observación del cajero y revisión del supervisor.",
        v => v >= 0m ? null : "Debe ser mayor o igual a cero.");

    /// <summary>Alerta de efectivo acumulado en el cajón (sugerir un retiro).</summary>
    public static readonly SettingDefinition<decimal> MaxCashInDrawer = new(
        "cash.max_cash_in_drawer",
        2_000_000m,
        SettingScope.Company | SettingScope.Branch | SettingScope.Terminal,
        "Efectivo en el cajón a partir del cual se sugiere un retiro.",
        v => v > 0m ? null : "Debe ser mayor que cero.");

    /// <summary>Alerta de jornada abierta por demasiado tiempo (cajero que olvidó cerrar).</summary>
    public static readonly SettingDefinition<int> OpenSessionAlertHours = new(
        "cash.open_session_alert_hours",
        14,
        SettingScope.Company | SettingScope.Branch,
        "Horas de una jornada abierta a partir de las cuales se alerta (y se sugiere el cierre por supervisor).",
        v => v is >= 1 and <= 72 ? null : "Entre 1 y 72.");

    public static IEnumerable<SettingDefinition> All => [BlindCount, DifferenceThreshold, MaxCashInDrawer, OpenSessionAlertHours];
}

public sealed class CashSettingsProvider : ISettingDefinitionProvider
{
    public IEnumerable<SettingDefinition> GetDefinitions() => CashSettings.All;
}

/// <summary>Quién puede operar una jornada y ver lo esperado.</summary>
public sealed class SessionAccess(ICurrentUser current, IPermissionChecker permissions)
{
    public bool IsCashier(CashSession session) => current.UserId == session.CashierId;

    /// <summary>El cajero de la jornada o quien puede cerrar cualquier caja (supervisor).</summary>
    public async Task<Result> CanOperateAsync(CashSession session, CancellationToken cancellationToken) =>
        IsCashier(session) || await permissions.HasPermissionAsync(CashPermissions.SessionCloseAny, cancellationToken: cancellationToken)
            ? Result.Success()
            : CashErrors.NotYourSession;

    /// <summary>Arqueo ciego (D6-05): lo esperado se ve con la jornada cerrada, sin arqueo ciego o con cash.report.view.</summary>
    public async Task<bool> CanSeeExpectedAsync(CashSession session, CancellationToken cancellationToken) =>
        session.Status == CashSessionStatus.Closed || !session.BlindCount
        || await permissions.HasPermissionAsync(CashPermissions.ReportView, cancellationToken: cancellationToken);
}

/// <summary>
/// Registra movimientos en una jornada (D6-01): bloquea la jornada, numera la línea, valida que una salida de efectivo no
/// deje el esperado negativo (RN-CSH-06) y guarda. Implementa <see cref="ICashRegister"/> para gastos y compras.
/// </summary>
public sealed class CashRegisterService(
    ICashStore store,
    ICashLedger ledger,
    IPaymentMethodDirectory methods,
    SessionAccess access,
    IActorContext actor,
    IUnitOfWork unitOfWork,
    IIdGenerator ids,
    IClock clock) : ICashRegister
{
    /// <summary>Guarda el movimiento y devuelve el efectivo esperado del medio después de él.</summary>
    public async Task<Result<(CashMovement Movement, decimal ExpectedAfter)>> RecordAsync(
        CashSession session, CashMovementData data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(data);
        var line = await ledger.LockAndNextLineAsync(session.Id, cancellationToken);
        var expected = (await ledger.ExpectedAsync(session.Id, cancellationToken)).FirstOrDefault(e => e.PaymentMethodId == data.PaymentMethodId)?.Expected ?? 0m;
        var created = CashMovement.Create(ids.NewId(), session, line, data, actor.ActorId!.Value, clock.UtcNow);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var after = expected + created.Value.SignedAmount;
        if (created.Value.Direction < 0 && after < 0m)
        {
            return Error.BusinessRule(CashErrors.InsufficientCash.Code, $"{CashErrors.InsufficientCash.Message} Faltan {-after:N2}.");
        }

        store.Add(created.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (created.Value, after);
    }

    public async Task<Result<CashMovementReceipt>> RecordOutflowAsync(CashOutflowRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var type = request.MovementType switch
        {
            "EXPENSE" => CashMovementType.Expense,
            "SUPPLIER_PAYMENT" => CashMovementType.SupplierPayment,
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Solo gastos y pagos a proveedores salen de la caja por este medio."),
        };
        var session = await store.GetSessionAsync(request.SessionId, cancellationToken);
        if (session is null)
        {
            return CashErrors.SessionNotFound;
        }

        var allowed = await access.CanOperateAsync(session, cancellationToken);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        var method = await methods.GetAsync(request.PaymentMethodId, cancellationToken);
        if (method is not { IsActive: true })
        {
            return CashErrors.PaymentMethodNotFound;
        }

        if (!method.AffectsCashDrawer)
        {
            return CashErrors.MethodNotCash;
        }

        var recorded = await RecordAsync(
            session,
            new CashMovementData(type, method.Id, request.Amount, request.Reason, SourceType: request.SourceType, SourceId: request.SourceId,
                SourceNumber: request.SourceNumber),
            cancellationToken);
        return recorded.IsSuccess
            ? new CashMovementReceipt(recorded.Value.Movement.Id, recorded.Value.Movement.LineNo, recorded.Value.ExpectedAfter)
            : recorded.Error;
    }

    public async Task<Result<CashMovementReceipt>> ReturnOutflowAsync(CashOutflowRequest original, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        var session = await store.GetSessionAsync(original.SessionId, cancellationToken);
        if (session is null)
        {
            return CashErrors.SessionNotFound;
        }

        if (session.Status != CashSessionStatus.Open)
        {
            return Error.BusinessRule(CashErrors.SessionNotOpen.Code, $"La jornada {session.Number} ya no está abierta: registre una corrección en la jornada actual.");
        }

        var recorded = await RecordAsync(
            session,
            new CashMovementData(CashMovementType.Correction, original.PaymentMethodId, original.Amount, reason, actor.ActorId, original.SourceType, original.SourceId,
                original.SourceNumber, CorrectionDirection: 1),
            cancellationToken);
        return recorded.IsSuccess
            ? new CashMovementReceipt(recorded.Value.Movement.Id, recorded.Value.Movement.LineNo, recorded.Value.ExpectedAfter)
            : recorded.Error;
    }

    public async Task<bool> HasOpenSessionAsync(Guid cashierId, CancellationToken cancellationToken = default) =>
        await store.GetUnclosedSessionAsync(null, cashierId, cancellationToken) is not null;

    public async Task<CashSessionInfo?> GetOpenSessionAsync(Guid posTerminalId, CancellationToken cancellationToken = default) =>
        await store.GetUnclosedSessionAsync(posTerminalId, null, cancellationToken) is { Status: CashSessionStatus.Open } session
            ? new CashSessionInfo(session.Id, session.Number, session.PosTerminalId, session.CashierId, session.BusinessDate, session.Status.Db())
            : null;

    public async Task<Result> RecordSaleMovementsAsync(SaleCashRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var type = request.MovementType switch
        {
            "SALE" => CashMovementType.Sale,
            "SALE_VOID" => CashMovementType.SaleVoid,
            "CUSTOMER_REFUND" => CashMovementType.CustomerRefund,
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Solo ventas, anulaciones y reintegros registran movimientos de venta."),
        };
        var session = await store.GetSessionAsync(request.SessionId, cancellationToken);
        if (session is null)
        {
            return CashErrors.SessionNotFound;
        }

        var allowed = await access.CanOperateAsync(session, cancellationToken);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        foreach (var amount in request.Amounts.Where(a => a.Amount > 0m).GroupBy(a => a.PaymentMethodId))
        {
            var method = await methods.GetAsync(amount.Key, cancellationToken);
            if (method is null || method.Kind == "EXCHANGE_CREDIT")
            {
                return CashErrors.PaymentMethodNotFound;
            }

            var recorded = await RecordAsync(
                session,
                new CashMovementData(type, method.Id, amount.Sum(a => a.Amount), request.Reason, request.AuthorizedBy, request.SourceType, request.SourceId,
                    request.SourceNumber),
                cancellationToken);
            if (recorded.IsFailure)
            {
                return recorded.Error;
            }
        }

        return Result.Success();
    }
}
