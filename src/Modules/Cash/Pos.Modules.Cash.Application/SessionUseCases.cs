using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Cash.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Cash.Application;

/// <summary>Línea de un conteo: por denominación (cantidad) o por total del medio de pago.</summary>
public sealed record CountLineRequest(Guid PaymentMethodId, Guid? DenominationId, int? Quantity, decimal? Amount);


/// <summary>Arma DTOs y reportes de una jornada (nombres, totales por medio, arqueo ciego).</summary>
public sealed class SessionViews(ICashStore store, ICashLedger ledger, ICashReadModel readModel, SessionAccess access, IClock clock)
{
    public async Task<IReadOnlyList<CashMethodTotalDto>> TotalsAsync(CashSession session, bool showExpected, CancellationToken cancellationToken)
    {
        var methods = (await store.GetPaymentMethodsAsync(cancellationToken)).ToDictionary(m => m.Id);
        IEnumerable<(Guid Id, decimal Expected, decimal? Counted, int Transactions)> rows = session.Status == CashSessionStatus.Closed
            ? session.Totals.Select(t => (t.PaymentMethodId, t.Expected, (decimal?)t.Counted, t.Transactions))
            : (await ledger.ExpectedAsync(session.Id, cancellationToken)).Select(e => (e.PaymentMethodId, e.Expected, (decimal?)null, e.Transactions));
        return [.. rows.Select(r =>
            {
                var method = methods.GetValueOrDefault(r.Id);
                return new CashMethodTotalDto(
                    r.Id, method?.Code ?? string.Empty, method?.Name ?? string.Empty, method?.AffectsCashDrawer ?? false, showExpected ? r.Expected : null,
                    r.Counted, showExpected && r.Counted is { } counted ? counted - r.Expected : null, r.Transactions);
            })
            .OrderBy(t => methods.GetValueOrDefault(t.PaymentMethodId)?.SortOrder ?? int.MaxValue)];
    }

    public async Task<CashSessionDto> ToDtoAsync(CashSession session, CancellationToken cancellationToken)
    {
        var showExpected = await access.CanSeeExpectedAsync(session, cancellationToken);
        var header = await readModel.GetHeaderAsync(session.Id, cancellationToken);
        var movements = await readModel.GetMovementsAsync(session.Id, cancellationToken);
        return new CashSessionDto(
            session.Id, session.Number, session.BranchId, session.PosTerminalId, header?.TerminalCode ?? string.Empty, session.CashierId,
            header?.CashierName ?? string.Empty, session.BusinessDate, session.OpenedAt, session.OpeningFloat, session.Status.Db(), session.BlindCount,
            !showExpected, session.ClosingStartedAt, session.ClosedAt, session.ClosedBySupervisor, session.CloseReason,
            showExpected ? session.ExpectedTotal : null, session.CountedTotal, showExpected ? session.Difference : null, session.DifferenceNote,
            session.ReviewRequired, session.ReviewedAt, session.ReviewNote, session.ZSealNo, session.ZSealCode,
            await TotalsAsync(session, showExpected, cancellationToken), movements);
    }

    public async Task<CashReportDto> ReportAsync(string kind, CashSession session, CancellationToken cancellationToken)
    {
        var header = (await readModel.GetHeaderAsync(session.Id, cancellationToken))!;
        var movements = await readModel.GetMovementsAsync(session.Id, cancellationToken);
        return CashReports.Build(kind, session, header, movements, await TotalsAsync(session, showExpected: true, cancellationToken), clock.UtcNow, clock);
    }
}

internal static class CashMapping
{
    public static string Db<TEnum>(this TEnum value)
        where TEnum : struct, Enum => PaymentMethodMapping.Db(value);
}

/// <summary>Carga la jornada y verifica que quien opera sea su cajero o un supervisor.</summary>
public sealed class SessionLoader(ICashStore store, SessionAccess access)
{
    public async Task<Result<CashSession>> OperableAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await store.GetSessionAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return CashErrors.SessionNotFound;
        }

        var allowed = await access.CanOperateAsync(session, cancellationToken);
        return allowed.IsSuccess ? session : allowed.Error;
    }
}

// ─────────────────────────────── Apertura ───────────────────────────────

/// <summary>
/// Abre la jornada del cajero en SU caja (D6-09: sesión de caja con código + PIN desde la caja emparejada o el equipo
/// Caja Única). Registra el fondo inicial (OPENING_FLOAT en efectivo) y, si se envía, el conteo de apertura (debe
/// sumar el fondo). Una jornada sin cerrar por caja y una abierta por cajero (RN-CSH-01; lo garantiza la BD).
/// </summary>
public sealed record OpenSessionCommand(decimal OpeningFloat, IReadOnlyList<CountLineRequest>? OpeningCount) : ICommand<CashSessionDto>;

internal sealed class OpenSessionHandler(
    ICurrentUser current,
    IClientContext client,
    ICashStore store,
    CashRegisterService register,
    SessionViews views,
    ISettingsReader settings,
    IDocumentNumberAllocator numbers,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<OpenSessionCommand, CashSessionDto>
{
    public async Task<Result<CashSessionDto>> Handle(OpenSessionCommand request, CancellationToken cancellationToken)
    {
        if (!current.IsTerminalSession || current.PosTerminalId is not { } terminalId || current.UserId is not { } cashierId
            || current.CompanyId is not { } companyId || current.BranchId is not { } branchId)
        {
            return CashErrors.TerminalRequired;
        }

        if (await store.GetUnclosedSessionAsync(terminalId, null, cancellationToken) is { } busy)
        {
            return Error.Conflict(CashErrors.SessionAlreadyOpen.Code, $"La caja ya tiene la jornada {busy.Number} sin cerrar (RN-CSH-01).");
        }

        if (await store.GetUnclosedSessionAsync(null, cashierId, cancellationToken) is { } own)
        {
            return Error.Conflict(CashErrors.SessionAlreadyOpen.Code, $"El cajero ya tiene abierta la jornada {own.Number} en otra caja (RN-CSH-01).");
        }

        var methods = await store.GetPaymentMethodsAsync(cancellationToken);
        var cash = methods.Single(m => m.Code == PaymentMethod.CashCode);
        var context = new SettingContext(companyId, branchId, terminalId);
        var number = await numbers.NextForTerminalAsync("CASH_SESSION", terminalId, cancellationToken);
        var now = clock.UtcNow;
        var opened = CashSession.Open(
            ids.NewId(), companyId, branchId, terminalId, client.DeviceId, cashierId, number.Number, now, clock.BusinessDateOf(now), request.OpeningFloat,
            await settings.GetAsync(CashSettings.BlindCount, context, cancellationToken));
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        var session = opened.Value;
        if (request.OpeningCount is { Count: > 0 } lines)
        {
            var count = await CountInputs.BuildAsync(store, lines, cancellationToken);
            if (count.IsFailure)
            {
                return count.Error;
            }

            var registered = session.RegisterCount(CashCountKind.Opening, count.Value, cashierId, now, ids.NewId);
            if (registered.IsFailure)
            {
                return registered.Error;
            }

            if (registered.Value.Total != request.OpeningFloat)
            {
                return Error.Validation(CashErrors.InvalidCount.Code, $"El conteo de apertura ({registered.Value.Total:N2}) no coincide con el fondo ({request.OpeningFloat:N2}).");
            }
        }

        store.Add(session);
        var movement = await register.RecordAsync(
            session, new CashMovementData(CashMovementType.OpeningFloat, cash.Id, request.OpeningFloat, "Fondo inicial"), cancellationToken);
        if (movement.IsFailure)
        {
            return movement.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("cash", "CASH_SESSION_OPENED", nameof(CashSession), session.Id, session.AuditLabel,
                $"Jornada {session.Number} abierta con fondo {session.OpeningFloat:N2} (fecha de negocio {session.BusinessDate:yyyy-MM-dd})."),
            cancellationToken);
        return await views.ToDtoAsync(session, cancellationToken);
    }
}

internal static class CountInputs
{
    /// <summary>Resuelve el valor de cada denominación contada (solo denominaciones activas).</summary>
    public static async Task<Result<IReadOnlyList<CountLineInput>>> BuildAsync(ICashStore store, IReadOnlyList<CountLineRequest> lines, CancellationToken cancellationToken)
    {
        var denominations = (await store.GetDenominationsAsync(cancellationToken)).Where(d => d.Status == MasterStatus.Active).ToDictionary(d => d.Id);
        var methods = (await store.GetPaymentMethodsAsync(cancellationToken)).ToDictionary(m => m.Id);
        var result = new List<CountLineInput>();
        foreach (var line in lines)
        {
            if (!methods.TryGetValue(line.PaymentMethodId, out var method)
                || (line.DenominationId is { } id && (!denominations.ContainsKey(id) || !method.AffectsCashDrawer)))
            {
                return CashErrors.InvalidCount;
            }

            result.Add(new CountLineInput(
                line.PaymentMethodId, line.DenominationId, line.DenominationId is { } d ? denominations[d].Value : null, line.Quantity, line.Amount));
        }

        return result;
    }
}

// ─────────────────────────────── Consultas ───────────────────────────────

/// <summary>Jornada sin cerrar de la caja de la sesión (o del cajero, en backoffice).</summary>
public sealed record GetCurrentSessionQuery : IQuery<CashSessionDto>;

internal sealed class GetCurrentSessionHandler(ICurrentUser current, ICashStore store, SessionViews views) : IQueryHandler<GetCurrentSessionQuery, CashSessionDto>
{
    public async Task<Result<CashSessionDto>> Handle(GetCurrentSessionQuery request, CancellationToken cancellationToken)
    {
        var session = current.IsTerminalSession && current.PosTerminalId is { } terminal
            ? await store.GetUnclosedSessionAsync(terminal, null, cancellationToken)
            : await store.GetUnclosedSessionAsync(null, current.UserId, cancellationToken);
        return session is null ? CashErrors.NoOpenSession : await views.ToDtoAsync(session, cancellationToken);
    }
}

public sealed record GetSessionQuery(Guid SessionId) : IQuery<CashSessionDto>;

internal sealed class GetSessionHandler(ICashStore store, SessionAccess access, IPermissionChecker permissions, SessionViews views)
    : IQueryHandler<GetSessionQuery, CashSessionDto>
{
    public async Task<Result<CashSessionDto>> Handle(GetSessionQuery request, CancellationToken cancellationToken)
    {
        var session = await store.GetSessionAsync(request.SessionId, cancellationToken);
        if (session is null)
        {
            return CashErrors.SessionNotFound;
        }

        if (!access.IsCashier(session) && !await permissions.HasPermissionAsync(CashPermissions.ReportView, cancellationToken: cancellationToken)
            && !await permissions.HasPermissionAsync(CashPermissions.SessionCloseAny, cancellationToken: cancellationToken))
        {
            return CashErrors.NotYourSession;
        }

        return await views.ToDtoAsync(session, cancellationToken);
    }
}

/// <summary>Jornadas de la sucursal (filtros por estado, fechas de negocio y pendientes de revisión).</summary>
public sealed record ListSessionsQuery(string? Status, DateOnly? From, DateOnly? To, bool PendingReviewOnly) : IQuery<IReadOnlyList<CashSessionSummaryDto>>;

internal sealed class ListSessionsHandler(IInstallationContext installation, ICashReadModel readModel)
    : IQueryHandler<ListSessionsQuery, IReadOnlyList<CashSessionSummaryDto>>
{
    public async Task<Result<IReadOnlyList<CashSessionSummaryDto>>> Handle(ListSessionsQuery request, CancellationToken cancellationToken) =>
        installation.BranchId is { } branch
            ? Result.Success(await readModel.ListSessionsAsync(new SessionFilter(branch, request.Status, request.From, request.To, request.PendingReviewOnly), cancellationToken))
            : Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
}

public sealed record ListDenominationsQuery : IQuery<IReadOnlyList<DenominationDto>>;

internal sealed class ListDenominationsHandler(ICashStore store) : IQueryHandler<ListDenominationsQuery, IReadOnlyList<DenominationDto>>
{
    public async Task<Result<IReadOnlyList<DenominationDto>>> Handle(ListDenominationsQuery request, CancellationToken cancellationToken) =>
        (await store.GetDenominationsAsync(cancellationToken)).OrderBy(d => d.SortOrder)
            .Select(d => new DenominationDto(d.Id, d.CurrencyCode, d.Value, d.Kind.Db(), d.SortOrder, d.Status.Db())).ToList();
}

// ─────────────────────────────── Movimientos ───────────────────────────────

public enum ManualMovementKind
{
    /// <summary>Ingreso de efectivo (p. ej. cambio traído del banco).</summary>
    CashIn,

    /// <summary>Retiro o sangría (RN-CSH-06): permiso o autorización de supervisor.</summary>
    Withdrawal,

    /// <summary>Apertura del cajón sin venta (RN-CSH-07): motivo y permiso o autorización.</summary>
    NoSaleDrawerOpen,

    /// <summary>Corrección (el cierre es definitivo: los errores se corrigen con otro movimiento, RN-CSH-08).</summary>
    Correction,
}

/// <summary>
/// Movimiento manual de caja. El permiso lo exige el endpoint (retiro y apertura del cajón admiten autorización de
/// supervisor de un solo uso, que queda en <c>authorized_by</c>). Solo en efectivo, salvo la corrección.
/// </summary>
public sealed record RegisterMovementCommand(
    Guid SessionId, ManualMovementKind Kind, decimal Amount, string Reason, Guid? PaymentMethodId = null, int? Direction = null)
    : ICommand<CashMovementResultDto>;

internal sealed class RegisterMovementHandler(
    SessionLoader loader,
    ICashStore store,
    CashRegisterService register,
    SessionAccess access,
    IAuthorizationScope authorization,
    IActorContext actor,
    ISettingsReader settings,
    IAuditWriter audit) : ICommandHandler<RegisterMovementCommand, CashMovementResultDto>
{
    public async Task<Result<CashMovementResultDto>> Handle(RegisterMovementCommand request, CancellationToken cancellationToken)
    {
        var loaded = await loader.OperableAsync(request.SessionId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var session = loaded.Value;
        var methods = await store.GetPaymentMethodsAsync(cancellationToken);
        var cash = methods.Single(m => m.Code == PaymentMethod.CashCode);
        var methodId = request.Kind == ManualMovementKind.Correction ? request.PaymentMethodId ?? cash.Id : cash.Id;
        if (methods.All(m => m.Id != methodId))
        {
            return CashErrors.PaymentMethodNotFound;
        }

        var type = request.Kind switch
        {
            ManualMovementKind.CashIn => CashMovementType.CashIn,
            ManualMovementKind.Withdrawal => CashMovementType.CashOutWithdrawal,
            ManualMovementKind.NoSaleDrawerOpen => CashMovementType.NoSaleDrawerOpen,
            _ => CashMovementType.Correction,
        };
        var reason = (request.Reason ?? string.Empty).Trim();
        if (CashMovementRules.RequiresReason(type) && reason.Length < 5)
        {
            return CashErrors.ReasonRequired;
        }

        // Quien autoriza: el supervisor de la autorización de un solo uso o, si tiene el permiso, quien lo registra.
        Guid? authorizedBy = type is CashMovementType.CashOutWithdrawal or CashMovementType.NoSaleDrawerOpen or CashMovementType.Correction
            ? authorization.Current?.AuthorizedBy ?? actor.ActorId
            : null;
        var recorded = await register.RecordAsync(
            session, new CashMovementData(type, methodId, request.Amount, reason, authorizedBy, CorrectionDirection: request.Direction), cancellationToken);
        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        var (movement, expectedAfter) = recorded.Value;
        if (type is not CashMovementType.CashIn)
        {
            await audit.WriteAsync(
                new AuditEntry("cash", type switch
                    {
                        CashMovementType.CashOutWithdrawal => "CASH_WITHDRAWAL",
                        CashMovementType.NoSaleDrawerOpen => "CASH_DRAWER_OPENED_NO_SALE",
                        _ => "CASH_CORRECTION",
                    },
                    nameof(CashSession), session.Id, session.AuditLabel,
                    $"{CashReports.Label(type.Db())} {movement.Amount:N2} en la jornada {session.Number}: {reason}",
                    AuthorizedBy: authorization.Current?.AuthorizedBy, Severity: AuditSeverity.Warning),
                cancellationToken);
        }

        var max = await settings.GetAsync(CashSettings.MaxCashInDrawer, new SettingContext(session.CompanyId, session.BranchId, session.PosTerminalId), cancellationToken);
        var drawer = methodId == cash.Id;
        return new CashMovementResultDto(
            movement.Id, movement.LineNo, type.Db(), movement.Amount, await access.CanSeeExpectedAsync(session, cancellationToken) ? expectedAfter : null,
            drawer && expectedAfter > max);
    }
}

// ─────────────────────────────── Cierre ───────────────────────────────

/// <summary>Inicia el cierre: desde ahora no se admiten ventas (RN-CSH-03). Se puede cancelar antes de confirmar.</summary>
public sealed record StartClosingCommand(Guid SessionId) : ICommand<CashSessionDto>;

internal sealed class StartClosingHandler(SessionLoader loader, SessionViews views, IClock clock) : ICommandHandler<StartClosingCommand, CashSessionDto>
{
    public async Task<Result<CashSessionDto>> Handle(StartClosingCommand request, CancellationToken cancellationToken)
    {
        var session = await loader.OperableAsync(request.SessionId, cancellationToken);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var started = session.Value.StartClosing(clock.UtcNow);
        return started.IsSuccess ? await views.ToDtoAsync(session.Value, cancellationToken) : started.Error;
    }
}

public sealed record CancelClosingCommand(Guid SessionId) : ICommand<CashSessionDto>;

internal sealed class CancelClosingHandler(SessionLoader loader, SessionViews views) : ICommandHandler<CancelClosingCommand, CashSessionDto>
{
    public async Task<Result<CashSessionDto>> Handle(CancelClosingCommand request, CancellationToken cancellationToken)
    {
        var session = await loader.OperableAsync(request.SessionId, cancellationToken);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var cancelled = session.Value.CancelClosing();
        return cancelled.IsSuccess ? await views.ToDtoAsync(session.Value, cancellationToken) : cancelled.Error;
    }
}

/// <summary>
/// Confirma el arqueo (cajero, desde CLOSING) o cierra por supervisor (<c>BySupervisor</c>, con motivo; la jornada puede
/// estar abierta). Calcula lo esperado de los movimientos, guarda totales y diferencia, fuerza un sellado de la auditoría
/// y devuelve el reporte Z con el sello (D6-08). Definitivo: no hay reapertura (RN-CSH-08).
/// </summary>
public sealed record CloseSessionCommand(Guid SessionId, IReadOnlyList<CountLineRequest> Count, string? DifferenceNote, bool BySupervisor = false, string? Reason = null)
    : ICommand<CashReportDto>;

internal sealed class CloseSessionHandler(
    ICashStore store,
    SessionAccess access,
    IPermissionChecker permissions,
    ICashLedger ledger,
    SessionViews views,
    ISettingsReader settings,
    IAuditAnchor anchor,
    IActorContext actor,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<CloseSessionCommand, CashReportDto>
{
    public async Task<Result<CashReportDto>> Handle(CloseSessionCommand request, CancellationToken cancellationToken)
    {
        var session = await store.GetSessionAsync(request.SessionId, cancellationToken);
        if (session is null)
        {
            return CashErrors.SessionNotFound;
        }

        if (request.BySupervisor
                ? !await permissions.HasPermissionAsync(CashPermissions.SessionCloseAny, cancellationToken: cancellationToken)
                : !access.IsCashier(session))
        {
            return request.BySupervisor
                ? Error.Forbidden("AUTH.PERMISSION_DENIED", "El cierre por supervisor requiere el permiso cash.session.close_any.")
                : CashErrors.NotYourSession;
        }

        var count = await CountInputs.BuildAsync(store, request.Count ?? [], cancellationToken);
        if (count.IsFailure)
        {
            return count.Error;
        }

        await ledger.LockAndNextLineAsync(session.Id, cancellationToken);
        var context = new SettingContext(session.CompanyId, session.BranchId, session.PosTerminalId);
        var now = clock.UtcNow;
        var closed = session.Close(
            new SessionClosing(
                count.Value, await ledger.ExpectedAsync(session.Id, cancellationToken), await settings.GetAsync(CashSettings.DifferenceThreshold, context, cancellationToken),
                request.DifferenceNote, actor.ActorId!.Value, request.BySupervisor, request.Reason, now),
            ids.NewId);
        if (closed.IsFailure)
        {
            return closed.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("cash", request.BySupervisor ? "CASH_SESSION_CLOSED_BY_SUPERVISOR" : "CASH_SESSION_CLOSED", nameof(CashSession), session.Id,
                session.AuditLabel,
                $"Jornada {session.Number} cerrada: esperado {session.ExpectedTotal:N2}, contado {session.CountedTotal:N2}, diferencia {session.Difference:N2}"
                + (request.BySupervisor ? $". Cierre por supervisor: {session.CloseReason}" : string.Empty) + (session.ReviewRequired ? ". Pendiente de revisión." : "."),
                Severity: request.BySupervisor ? AuditSeverity.Critical : session.ReviewRequired ? AuditSeverity.Warning : AuditSeverity.Info),
            cancellationToken);

        // Ancla de la auditoría: el Z imprime el último sello del nodo (lo emitido antes del horizonte seguro queda sellado ahora).
        if (await anchor.SealNowAsync(cancellationToken) is { } seal)
        {
            session.SetSeal(seal.SealNo, seal.ShortCode);
        }

        return await views.ReportAsync("Z", session, cancellationToken);
    }
}

/// <summary>Revisión del supervisor de un cierre con diferencia o cerrado por supervisor (RN-CSH-05).</summary>
public sealed record ReviewSessionCommand(Guid SessionId, string Note) : ICommand<CashSessionDto>;

internal sealed class ReviewSessionHandler(ICashStore store, SessionViews views, IActorContext actor, IAuditWriter audit, IClock clock)
    : ICommandHandler<ReviewSessionCommand, CashSessionDto>
{
    public async Task<Result<CashSessionDto>> Handle(ReviewSessionCommand request, CancellationToken cancellationToken)
    {
        var session = await store.GetSessionAsync(request.SessionId, cancellationToken);
        if (session is null)
        {
            return CashErrors.SessionNotFound;
        }

        var reviewed = session.Review(actor.ActorId!.Value, request.Note, clock.UtcNow);
        if (reviewed.IsFailure)
        {
            return reviewed.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("cash", "CASH_SESSION_REVIEWED", nameof(CashSession), session.Id, session.AuditLabel,
                $"Cierre de la jornada {session.Number} revisado (diferencia {session.Difference:N2}): {session.ReviewNote}"),
            cancellationToken);
        return await views.ToDtoAsync(session, cancellationToken);
    }
}

// ─────────────────────────────── Reportes ───────────────────────────────

/// <summary>Reporte X (parcial) de una jornada sin cerrar: con arqueo ciego, solo lo ve quien tiene cash.report.view.</summary>
public sealed record GetXReportQuery(Guid SessionId) : IQuery<CashReportDto>;

internal sealed class GetXReportHandler(SessionLoader loader, SessionAccess access, SessionViews views) : IQueryHandler<GetXReportQuery, CashReportDto>
{
    public async Task<Result<CashReportDto>> Handle(GetXReportQuery request, CancellationToken cancellationToken)
    {
        var session = await loader.OperableAsync(request.SessionId, cancellationToken);
        if (session.IsFailure)
        {
            return session.Error;
        }

        if (session.Value.Status == CashSessionStatus.Closed)
        {
            return CashErrors.InvalidStatus;
        }

        return await access.CanSeeExpectedAsync(session.Value, cancellationToken)
            ? await views.ReportAsync("X", session.Value, cancellationToken)
            : CashErrors.ExpectedHidden;
    }
}

/// <summary>Reporte Z (cierre) con el sello de la auditoría; lo ve el cajero de la jornada o quien tiene cash.report.view.</summary>
public sealed record GetZReportQuery(Guid SessionId) : IQuery<CashReportDto>;

internal sealed class GetZReportHandler(ICashStore store, SessionAccess access, IPermissionChecker permissions, SessionViews views)
    : IQueryHandler<GetZReportQuery, CashReportDto>
{
    public async Task<Result<CashReportDto>> Handle(GetZReportQuery request, CancellationToken cancellationToken)
    {
        var session = await store.GetSessionAsync(request.SessionId, cancellationToken);
        if (session is null)
        {
            return CashErrors.SessionNotFound;
        }

        if (!access.IsCashier(session) && !await permissions.HasPermissionAsync(CashPermissions.ReportView, cancellationToken: cancellationToken))
        {
            return CashErrors.NotYourSession;
        }

        return session.Status == CashSessionStatus.Closed ? await views.ReportAsync("Z", session, cancellationToken) : CashErrors.NotClosed;
    }
}
