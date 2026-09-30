using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Modules.Purchasing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Purchasing.Application;

/// <summary>Cuentas por pagar (<c>OpenOnly</c>: con saldo pendiente), con su rango de edad a la fecha.</summary>
public sealed record ListPayablesQuery(Guid? SupplierId, bool OpenOnly, DateOnly? AsOf) : IQuery<IReadOnlyList<PayableDto>>;

internal sealed class ListPayablesHandler(IPurchasingQueries queries, IClock clock) : IQueryHandler<ListPayablesQuery, IReadOnlyList<PayableDto>>
{
    public async Task<Result<IReadOnlyList<PayableDto>>> Handle(ListPayablesQuery request, CancellationToken cancellationToken) =>
        Result.Success(await queries.ListPayablesAsync(request.SupplierId, request.OpenOnly, request.AsOf ?? clock.Today, withEntries: false, cancellationToken));
}

public sealed record GetPayableQuery(Guid PayableId) : IQuery<PayableDto>;

internal sealed class GetPayableHandler(IPurchasingStore store, IPurchasingQueries queries, IClock clock) : IQueryHandler<GetPayableQuery, PayableDto>
{
    public async Task<Result<PayableDto>> Handle(GetPayableQuery request, CancellationToken cancellationToken)
    {
        if (await store.GetPayableAsync(request.PayableId, cancellationToken) is not { } account)
        {
            return PurchasingErrors.PayableNotFound;
        }

        return (await queries.ListPayablesAsync(account.SupplierId, openOnly: false, clock.Today, withEntries: true, cancellationToken))
            .Single(p => p.Id == account.Id);
    }
}

/// <summary>Cartera por edades por proveedor: corriente, 1–30, 31–60, 61–90 y más de 90 días vencidos.</summary>
public sealed record GetAgingQuery(DateOnly? AsOf) : IQuery<AgingReportDto>;

internal sealed class GetAgingHandler(IPurchasingQueries queries, IClock clock) : IQueryHandler<GetAgingQuery, AgingReportDto>
{
    public async Task<Result<AgingReportDto>> Handle(GetAgingQuery request, CancellationToken cancellationToken)
    {
        var asOf = request.AsOf ?? clock.Today;
        var open = await queries.ListPayablesAsync(null, openOnly: true, asOf, withEntries: false, cancellationToken);
        var suppliers = (await queries.ListSuppliersAsync(null, includeInactive: true, cancellationToken)).ToDictionary(s => s.Id);
        var rows = open.GroupBy(p => p.SupplierId).Select(g =>
        {
            decimal Sum(string bucket) => g.Where(p => p.AgingBucket == bucket).Sum(p => p.Balance);
            var supplier = suppliers.GetValueOrDefault(g.Key);
            return new AgingRowDto(g.Key, supplier?.Code ?? string.Empty, supplier?.Name ?? g.First().SupplierName, Sum(Aging.Current), Sum(Aging.Days1To30),
                Sum(Aging.Days31To60), Sum(Aging.Days61To90), Sum(Aging.Over90), g.Sum(p => p.Balance));
        }).OrderByDescending(r => r.Total).ToList();
        var totals = new AgingRowDto(Guid.Empty, string.Empty, "TOTAL", rows.Sum(r => r.Current), rows.Sum(r => r.Days1To30), rows.Sum(r => r.Days31To60),
            rows.Sum(r => r.Days61To90), rows.Sum(r => r.Over90), rows.Sum(r => r.Total));
        return new AgingReportDto(asOf, rows, totals);
    }
}

/// <summary>Estado de cuenta del proveedor: sus cuentas con los asientos y sus pagos.</summary>
public sealed record GetSupplierStatementQuery(Guid SupplierId) : IQuery<SupplierStatementDto>;

internal sealed class GetSupplierStatementHandler(IInstallationContext installation, IPurchasingQueries queries, IClock clock)
    : IQueryHandler<GetSupplierStatementQuery, SupplierStatementDto>
{
    public async Task<Result<SupplierStatementDto>> Handle(GetSupplierStatementQuery request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        if (await queries.GetSupplierAsync(request.SupplierId, cancellationToken) is not { } supplier)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        var accounts = await queries.ListPayablesAsync(supplier.Id, openOnly: false, clock.Today, withEntries: true, cancellationToken);
        var payments = await queries.ListDocumentsAsync(new DocumentFilter("PAYMENT", local.Value.BranchId, supplier.Id, null, null), cancellationToken);
        return new SupplierStatementDto(supplier.Id, supplier.Name, accounts.Where(a => a.Status != "VOIDED").Sum(a => a.Balance), accounts, payments);
    }
}

// ─────────────────────────────── Pagos ───────────────────────────────

public sealed record AllocationRequest(Guid AccountId, decimal Amount);

/// <summary>
/// Pago a proveedor aplicado a una o varias cuentas del mismo proveedor (no puede superar el saldo de cada una). Con
/// <c>CashSessionId</c> el dinero sale de la jornada de caja (movimiento SUPPLIER_PAYMENT, en efectivo y sin dejarla
/// negativa, Fase 6).
/// </summary>
public sealed record RegisterPaymentCommand(
    Guid SupplierId, DateOnly? PaymentDate, Guid PaymentMethodId, string? Reference, string? Notes, IReadOnlyList<AllocationRequest> Allocations,
    Guid? CashSessionId = null)
    : ICommand<PaymentDto>;

internal sealed class RegisterPaymentHandler(
    IInstallationContext installation,
    IPurchasingStore store,
    IPurchasingQueries queries,
    PaymentMethodGuard methods,
    IDocumentNumberAllocator numbers,
    ICashRegister cash,
    IActorContext actor,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<RegisterPaymentCommand, PaymentDto>
{
    public async Task<Result<PaymentDto>> Handle(RegisterPaymentCommand request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        if (await store.GetSupplierAsync(request.SupplierId, cancellationToken) is not { } supplier)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        var method = await methods.ActiveAsync(request.PaymentMethodId, cancellationToken);
        if (method.IsFailure)
        {
            return method.Error;
        }

        if (method.Value.RequiresReference && string.IsNullOrWhiteSpace(request.Reference))
        {
            return Error.Validation("PURCHASING.REFERENCE_REQUIRED", $"El medio de pago {method.Value.Name} exige una referencia (número de transferencia, cheque…).");
        }

        var allocations = request.Allocations ?? [];
        var accounts = (await store.GetPayablesAsync([.. allocations.Select(a => a.AccountId).Distinct()], cancellationToken)).ToDictionary(a => a.Id);
        if (allocations.Any(a => !accounts.TryGetValue(a.AccountId, out var account) || account.SupplierId != supplier.Id))
        {
            return PurchasingErrors.InvalidPayment;
        }

        var number = await numbers.NextForBranchAsync("PAYABLE_PAYMENT", local.Value.BranchId, cancellationToken);
        var payment = PayablePayment.Create(
            ids.NewId(), local.Value.CompanyId, local.Value.BranchId, supplier.Id, number.Number, request.PaymentDate ?? clock.Today, method.Value.Id,
            request.Reference, request.Notes, [.. allocations.Select(a => new AllocationInput(a.AccountId, a.Amount))], ids.NewId, request.CashSessionId);
        if (payment.IsFailure)
        {
            return payment.Error;
        }

        if (request.CashSessionId is { } sessionId)
        {
            var moved = await cash.RecordOutflowAsync(
                new CashOutflowRequest(sessionId, "SUPPLIER_PAYMENT", method.Value.Id, payment.Value.Amount, "PAYABLE_PAYMENT", payment.Value.Id,
                    payment.Value.Number, $"Pago a proveedor {supplier.Code}"),
                cancellationToken);
            if (moved.IsFailure)
            {
                return moved.Error;
            }
        }

        var userId = actor.ActorId!.Value;
        var now = clock.UtcNow;
        foreach (var allocation in payment.Value.Allocations)
        {
            var applied = accounts[allocation.AccountId].ApplyPayment(ids.NewId(), allocation.Amount, payment.Value.Id, payment.Value.Number, userId, now);
            if (applied.IsFailure)
            {
                return applied.Error;
            }
        }

        store.Add(payment.Value);
        await audit.WriteAsync(
            new AuditEntry("purchasing", "PAYABLE_PAYMENT_POSTED", nameof(PayablePayment), payment.Value.Id, payment.Value.AuditLabel,
                $"Pago {payment.Value.Number} a proveedor {supplier.Code} por {payment.Value.Amount:N2} ({method.Value.Name}) aplicado a {allocations.Count} factura(s)."),
            cancellationToken);
        // Fase 8 (RN-PUR-09): si la cuenta principal del proveedor está por verificar, el pago se registra con una advertencia.
        var warnings = await BankAccountMapping.PaymentWarningsAsync(store, supplier.Id, method.Value.Kind, cancellationToken);
        return await PaymentMapping.ToDtoAsync(payment.Value, accounts, queries, cancellationToken, warnings);
    }
}

internal static class PaymentMapping
{
    public static async Task<PaymentDto> ToDtoAsync(
        PayablePayment p, IReadOnlyDictionary<Guid, AccountPayable> accounts, IPurchasingQueries queries, CancellationToken cancellationToken,
        IReadOnlyList<OperationWarningDto>? warnings = null)
    {
        var names = await queries.SupplierNamesAsync([p.SupplierId], cancellationToken);
        return new PaymentDto(
            p.Id, p.Number, p.SupplierId, names.GetValueOrDefault(p.SupplierId) ?? string.Empty, p.PaymentDate, p.PaymentMethodId, p.Reference, p.Amount, p.Status.Db(),
            p.Notes, p.VoidReason, p.CashSessionId,
            [.. p.Allocations.Select(a => new AllocationDto(a.AccountId, accounts.GetValueOrDefault(a.AccountId)?.DocumentNumber ?? string.Empty, a.Amount))],
            warnings ?? []);
    }
}

public sealed record GetPaymentQuery(Guid PaymentId) : IQuery<PaymentDto>;

internal sealed class GetPaymentHandler(IPurchasingStore store, IPurchasingQueries queries) : IQueryHandler<GetPaymentQuery, PaymentDto>
{
    public async Task<Result<PaymentDto>> Handle(GetPaymentQuery request, CancellationToken cancellationToken)
    {
        if (await store.GetPaymentAsync(request.PaymentId, cancellationToken) is not { } payment)
        {
            return PurchasingErrors.PaymentNotFound;
        }

        var accounts = (await store.GetPayablesAsync([.. payment.Allocations.Select(a => a.AccountId)], cancellationToken)).ToDictionary(a => a.Id);
        return await PaymentMapping.ToDtoAsync(payment, accounts, queries, cancellationToken);
    }
}

/// <summary>Anula un pago: asientos inversos (PAYMENT_VOID) en cada cuenta que cubría; el pago no se borra.</summary>
public sealed record VoidPaymentCommand(Guid PaymentId, string Reason) : ICommand<PaymentDto>;

internal sealed class VoidPaymentHandler(
    IPurchasingStore store, IPurchasingQueries queries, ICashRegister cash, IActorContext actor, IAuditWriter audit, IIdGenerator ids, IClock clock)
    : ICommandHandler<VoidPaymentCommand, PaymentDto>
{
    public async Task<Result<PaymentDto>> Handle(VoidPaymentCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetPaymentAsync(request.PaymentId, cancellationToken) is not { } payment)
        {
            return PurchasingErrors.PaymentNotFound;
        }

        var userId = actor.ActorId!.Value;
        var now = clock.UtcNow;
        var voided = payment.Void(request.Reason, userId, now);
        if (voided.IsFailure)
        {
            return voided.Error;
        }

        if (payment.CashSessionId is { } sessionId)
        {
            // El dinero vuelve al cajón con una corrección en la misma jornada, que debe seguir abierta (RN-CSH-08).
            var returned = await cash.ReturnOutflowAsync(
                new CashOutflowRequest(sessionId, "SUPPLIER_PAYMENT", payment.PaymentMethodId, payment.Amount, "PAYABLE_PAYMENT_VOID", payment.Id, payment.Number, null),
                $"Anulación del pago {payment.Number}: {payment.VoidReason}",
                cancellationToken);
            if (returned.IsFailure)
            {
                return returned.Error;
            }
        }

        var accounts = (await store.GetPayablesAsync([.. payment.Allocations.Select(a => a.AccountId)], cancellationToken)).ToDictionary(a => a.Id);
        foreach (var allocation in payment.Allocations)
        {
            var adjusted = accounts[allocation.AccountId].Adjust(
                ids.NewId(), PayableEntryType.PaymentVoid, allocation.Amount, "PAYABLE_PAYMENT_VOID", payment.Id, payment.Number, userId, now);
            if (adjusted.IsFailure)
            {
                return adjusted.Error;
            }
        }

        await audit.WriteAsync(
            new AuditEntry("purchasing", "PAYABLE_PAYMENT_VOIDED", nameof(PayablePayment), payment.Id, payment.AuditLabel,
                $"Pago {payment.Number} por {payment.Amount:N2} anulado: {payment.VoidReason}.", Severity: AuditSeverity.Warning),
            cancellationToken);
        return await PaymentMapping.ToDtoAsync(payment, accounts, queries, cancellationToken);
    }
}
