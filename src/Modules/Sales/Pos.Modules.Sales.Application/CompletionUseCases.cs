using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Customers.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Modules.Sales.Domain;
using Pos.Printing;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Sales.Application;

/// <summary>Kardex de ventas, anulaciones y cambios: siempre por <see cref="IInventoryPosting"/>, en la transacción del documento.</summary>
public sealed class SaleInventory(IInventoryPosting posting, IWarehouseDirectory warehouses)
{
    public const string SaleSource = "SALE";
    public const string SaleVoidSource = "SALE_VOID";
    public const string ReturnSource = "CUSTOMER_RETURN";

    /// <summary>Salida de la venta (FEFO, sin saldo negativo) y costo de cada línea desde el kardex (D7-06).</summary>
    public async Task<Result> PostSaleAsync(Sale sale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sale);
        var lines = sale.ActiveLines.Where(l => l.IsStockable).Select(l => new PostingLine(
            sale.WarehouseId, l.ProductId, MovementType.Sale, l.BaseQuantity, SourceLineId: l.Id, PackagingId: l.PackagingId,
            PackagingQuantity: l.PackagingId is null ? null : l.Quantity)).ToList();
        var posted = await posting.PostAsync(
            new InventoryPosting(SaleSource, sale.Id, sale.Number, sale.BranchId, sale.BusinessDate, lines, AllowNegativeStock: false), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error.Code == "INVENTORY.INSUFFICIENT_STOCK"
                ? Error.BusinessRule(SalesErrors.InsufficientStock.Code, posted.Error.Message)
                : posted.Error;
        }

        foreach (var group in posted.Value.GroupBy(m => m.SourceLineId!.Value))
        {
            var quantity = group.Sum(m => m.Quantity);
            var cost = group.Sum(m => m.TotalCost);
            sale.SetLineCost(group.Key, quantity == 0m ? 0m : decimal.Round(cost / quantity, 4, MidpointRounding.AwayFromZero), cost,
                group.OrderByDescending(m => m.Quantity).First().LotId);
        }

        return Result.Success();
    }

    /// <summary>Anulación: movimientos inversos al costo y lote originales (D7-10).</summary>
    public async Task<Result> ReverseSaleAsync(Sale sale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sale);
        if (!sale.ActiveLines.Any(l => l.IsStockable))
        {
            return Result.Success();
        }

        var reversed = await posting.ReverseAsync(
            new InventoryReversal(SaleSource, sale.Id, SaleVoidSource, sale.Id, sale.Number, sale.BranchId, sale.BusinessDate), cancellationToken);
        return reversed.IsSuccess ? Result.Success() : reversed.Error;
    }

    /// <summary>
    /// Entrada de un cambio o reintegro al costo con que salió (D7-11): a la bodega de la caja, a averías, o a averías y de
    /// ahí como daño (descarte).
    /// </summary>
    public async Task<Result> PostReturnAsync(CustomerReturn customerReturn, Sale original, Guid warehouseId, DateOnly businessDate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customerReturn);
        ArgumentNullException.ThrowIfNull(original);
        var stockable = customerReturn.Lines.Where(l => original.Lines.Single(s => s.Id == l.SaleLineId).IsStockable).ToList();
        if (stockable.Count == 0)
        {
            return Result.Success();
        }

        Guid? damaged = null;
        if (stockable.Any(l => l.Destination != ReturnDestination.ReturnToStock))
        {
            damaged = (await warehouses.ListByBranchAsync(customerReturn.BranchId, cancellationToken)).FirstOrDefault(w => w.Kind == "DAMAGED" && w.IsActive)?.Id;
            if (damaged is null)
            {
                return SalesAppErrors.DamagedWarehouseMissing;
            }
        }

        var lines = new List<PostingLine>();
        foreach (var line in stockable)
        {
            if (line.Destination == ReturnDestination.ReturnToStock)
            {
                lines.Add(new PostingLine(warehouseId, line.ProductId, MovementType.CustomerReturn, line.BaseQuantity, line.UnitCost, line.Id, LotId: line.LotId));
                continue;
            }

            lines.Add(new PostingLine(damaged!.Value, line.ProductId, MovementType.CustomerReturnDamaged, line.BaseQuantity, line.UnitCost, line.Id, LotId: line.LotId));
            if (line.Destination == ReturnDestination.Discard)
            {
                lines.Add(new PostingLine(damaged.Value, line.ProductId, MovementType.Damage, line.BaseQuantity, SourceLineId: line.Id, LotId: line.LotId));
            }
        }

        var posted = await posting.PostAsync(
            new InventoryPosting(ReturnSource, customerReturn.Id, customerReturn.Number, customerReturn.BranchId, businessDate, lines), cancellationToken);
        return posted.IsSuccess ? Result.Success() : posted.Error;
    }
}

/// <summary>Tiquete de una venta con el encabezado de la empresa.</summary>
public sealed class SaleReceipts(ISalesReadModel readModel, IBillingService billing)
{
    public async Task<SaleReceiptDto> BuildAsync(Sale sale, bool copy, bool openDrawer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sale);
        var header = await readModel.GetTicketHeaderAsync(sale.PosTerminalId, sale.CashierId, cancellationToken)
            ?? new TicketHeader(string.Empty, string.Empty, string.Empty, null, null, string.Empty, null, string.Empty, string.Empty);
        var document = await billing.GetForSourceAsync(sale.Id, cancellationToken);
        var ticket = SaleTicketBuilder.Build(sale, header, copy, openDrawer, document?.DocumentType);
        return new SaleReceiptDto(sale.ToDto(), ticket, TicketLayout.ToText(ticket, TicketLayout.Columns80Mm), openDrawer, document?.DocumentType, document?.Status);
    }
}

public sealed record PaymentRequest(Guid PaymentMethodId, decimal Amount, string? Reference = null, string? CardFranchise = null, string? CardLast4 = null);

// ─────────────────────────────── Cobrar ───────────────────────────────

/// <summary>
/// Cobra y completa la venta en UNA transacción (D7-04, RN-SAL-11): recalcula con las promociones vigentes, asigna los pagos
/// (primero los que no dan cambio; el efectivo redondeado da el cambio), número de la serie de la caja, kardex (FEFO, sin
/// saldo negativo) con el costo de cada línea, movimientos de caja por medio, comprobante, cambio de mercancía si lo hay y
/// evento de sincronización. Idempotente: repetir con la misma clave devuelve la venta ya completada.
/// </summary>
public sealed record CompleteSaleCommand(Guid SaleId, IReadOnlyList<PaymentRequest> Payments, string? IdempotencyKey) : ICommand<SaleReceiptDto>, IAllowedWhenRestricted;

internal sealed class CompleteSaleHandler(
    ISalesStore store,
    TerminalResolver terminals,
    PromotionRules promotions,
    ExchangeCompletion exchanges,
    SaleInventory inventory,
    SaleReceipts receipts,
    IPaymentMethodDirectory methods,
    ICashRegister cash,
    IBillingService billing,
    IDocumentNumberAllocator numbers,
    ISettingsReader settings,
    CustomerResolver customers,
    ICustomerCreditGate credit,
    ILoyaltyProgram loyalty,
    IOutbox outbox,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<CompleteSaleCommand, SaleReceiptDto>
{
    public const string CompletedEvent = "sales.sale_completed.v1";

    public async Task<Result<SaleReceiptDto>> Handle(CompleteSaleCommand request, CancellationToken cancellationToken)
    {
        var scope = await terminals.ResolveAsync(requireOpenSession: false, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? request.SaleId.ToString() : request.IdempotencyKey.Trim();
        if (key.Length > 100)
        {
            return Error.Validation("SALES.INVALID_IDEMPOTENCY_KEY", "La clave de idempotencia tiene hasta 100 caracteres.");
        }

        var sale = await store.LockSaleAsync(request.SaleId, cancellationToken);
        if (sale is null)
        {
            return SalesErrors.SaleNotFound;
        }

        var same = TerminalResolver.EnsureSameTerminal(sale, scope.Value);
        if (same.IsFailure)
        {
            return same.Error;
        }

        if (sale.Status == SaleStatus.Completed)
        {
            return sale.CompletionKey == key ? await receipts.BuildAsync(sale, copy: false, OpensDrawer(sale), cancellationToken) : SalesErrors.CompletionKeyMismatch;
        }

        if (sale.Status != SaleStatus.Open)
        {
            return SalesErrors.NotOpen;
        }

        if (scope.Value.Session is not { } session || session.Id != sale.CashSessionId)
        {
            return SalesErrors.NoOpenCashSession;
        }

        if (!sale.ActiveLines.Any())
        {
            return SalesErrors.EmptySale;
        }

        sale.Recalculate(await promotions.ActiveAsync(sale.BranchId, cancellationToken));
        var context = new SettingContext(sale.CompanyId, sale.BranchId, sale.PosTerminalId);
        var limit = await settings.GetAsync(SalesSettings.AnonymousSaleLimit, new SettingContext(sale.CompanyId), cancellationToken);
        if (limit > 0m && sale.CustomerId is null && sale.Total > limit)
        {
            return SalesErrors.CustomerRequired;
        }

        // RN-SAL-19: el snapshot del comprador es el del momento del cobro; si pide factura, sus datos deben estar completos.
        if (sale.CustomerId is { } customerId && await customers.ProfileAsync(customerId, cancellationToken) is { } buyer)
        {
            sale.RefreshCustomer(CustomerResolver.Snapshot(buyer, sale.CustomerFiscal?.ConsentPolicyVersion));
            if (sale.InvoiceRequested && CustomerResolver.MissingInvoiceData(buyer) is { Count: > 0 } missing)
            {
                return Error.BusinessRule(
                    SalesErrors.CustomerFiscalDataIncomplete.Code, $"{SalesErrors.CustomerFiscalDataIncomplete.Message} Faltan: {string.Join(", ", missing)}.");
            }
        }

        var tenders = await TendersAsync(sale, request.Payments ?? [], cancellationToken);
        if (tenders.IsFailure)
        {
            return tenders.Error;
        }

        var allocation = PaymentAllocator.Allocate(
            sale.Total, tenders.Value, await settings.GetAsync(SalesSettings.CashRoundingIncrement, context, cancellationToken));
        if (allocation.IsFailure)
        {
            return allocation.Error;
        }

        var number = await numbers.NextForTerminalAsync("SALE", sale.PosTerminalId, cancellationToken);
        var now = clock.UtcNow;
        var completed = sale.Complete(number.Number, allocation.Value, key, now, ids.NewId);
        if (completed.IsFailure)
        {
            return completed.Error;
        }

        // El cambio de mercancía entra primero: lo recibido vuelve al inventario antes de la salida de la venta nueva.
        if (sale.ExchangeId is { } exchangeId)
        {
            var exchanged = await exchanges.CompleteAsync(exchangeId, sale, now, cancellationToken);
            if (exchanged.IsFailure)
            {
                return exchanged.Error;
            }
        }

        var posted = await inventory.PostSaleAsync(sale, cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        var cashed = await cash.RecordSaleMovementsAsync(
            new SaleCashRequest(
                sale.CashSessionId, "SALE", "SALE", sale.Id, sale.Number!,
                [.. sale.Payments.Where(p => p.MethodKind != "EXCHANGE_CREDIT").Select(p => new SaleCashAmount(p.PaymentMethodId, p.Applied))], null, null),
            cancellationToken);
        if (cashed.IsFailure)
        {
            return cashed.Error;
        }

        var issued = await billing.IssueAsync(
            new FiscalIssueRequest(
                "SALE", sale.Id, sale.Number!, sale.BranchId, sale.PosTerminalId, sale.BusinessDate, sale.CustomerName, sale.CustomerIdentificationType,
                sale.CustomerIdentification, sale.CustomerEmail, sale.Subtotal, sale.TaxTotal, sale.Total),
            cancellationToken);
        if (issued.IsFailure)
        {
            return issued.Error;
        }

        outbox.Enqueue(
            CompletedEvent,
            new
            {
                SaleId = sale.Id, sale.Number, sale.BranchId, sale.PosTerminalId, sale.CashSessionId, sale.BusinessDate, sale.CustomerId, sale.Subtotal, sale.TaxTotal,
                sale.PromotionTotal, sale.DiscountTotal, sale.RoundingAdjustment, sale.Total, sale.ExchangeId, CompletedAt = now,
                Lines = sale.ActiveLines.Select(l => new { l.Id, l.ProductId, l.Quantity, l.BaseQuantity, l.UnitPrice, l.PromotionId, l.Total, l.UnitCost }),
                Payments = sale.Payments.Select(p => new { p.PaymentMethodId, p.MethodKind, p.Applied }),
            },
            OutboxDestination.Sync);
        return await receipts.BuildAsync(sale, copy: false, OpensDrawer(sale), cancellationToken);
    }

    private static bool OpensDrawer(Sale sale) => sale.Payments.Any(p => p.AffectsCashDrawer);

    private async Task<Result<IReadOnlyList<Tender>>> TendersAsync(Sale sale, IReadOnlyList<PaymentRequest> payments, CancellationToken cancellationToken)
    {
        var tenders = new List<Tender>();
        foreach (var payment in payments)
        {
            var method = await methods.GetAsync(payment.PaymentMethodId, cancellationToken);
            if (method is not { IsActive: true })
            {
                return SalesErrors.InvalidPayment;
            }

            if (method.Kind == "EXCHANGE_CREDIT")
            {
                return SalesErrors.ExchangeCreditNotAllowed;
            }

            // Crédito y puntos (D8-15, D8-16): reservados; la implementación nula los rechaza hasta la Fase 8-B.
            if (method.Kind is "CUSTOMER_CREDIT" or "LOYALTY_POINTS")
            {
                if (sale.CustomerId is not { } holder)
                {
                    return SalesErrors.PaymentKindNotAvailable;
                }

                var allowed = method.Kind == "CUSTOMER_CREDIT"
                    ? await credit.AuthorizeAsync(holder, payment.Amount, cancellationToken)
                    : await loyalty.RedeemAsync(holder, payment.Amount, cancellationToken);
                if (allowed.IsFailure)
                {
                    return allowed.Error;
                }
            }

            var reference = string.IsNullOrWhiteSpace(payment.Reference) ? null : payment.Reference.Trim();
            if ((method.RequiresReference && reference is null) || reference is { Length: > 60 })
            {
                return SalesErrors.ReferenceRequired;
            }

            var last4 = string.IsNullOrWhiteSpace(payment.CardLast4) ? null : payment.CardLast4.Trim();
            if (last4 is not null && (last4.Length != 4 || !last4.All(char.IsAsciiDigit)))
            {
                return Error.Validation("SALES.INVALID_CARD", "De la tarjeta solo se guardan los últimos 4 dígitos (nunca el número completo).");
            }

            var franchise = string.IsNullOrWhiteSpace(payment.CardFranchise) ? null : payment.CardFranchise.Trim();
            tenders.Add(new Tender(
                method.Id, method.Code, method.Kind, method.AffectsCashDrawer, method.AffectsCashDrawer, payment.Amount, reference,
                franchise is { Length: > 20 } ? franchise[..20] : franchise, last4));
        }

        if (sale.ExchangeId is not null)
        {
            if (sale.ExchangeCredit > sale.Total)
            {
                return Error.BusinessRule(
                    SalesErrors.ExchangeBelowCredit.Code, $"{SalesErrors.ExchangeBelowCredit.Message} Crédito {sale.ExchangeCredit:N2}, venta {sale.Total:N2}.");
            }

            var credit = await methods.GetByCodeAsync("CAMBIO", cancellationToken);
            if (credit is not { IsActive: true })
            {
                return SalesErrors.InvalidPayment;
            }

            tenders.Insert(0, new Tender(credit.Id, credit.Code, credit.Kind, GivesChange: false, AffectsCashDrawer: false, sale.ExchangeCredit));
        }

        return tenders.Count == 0 && sale.Total > 0m ? SalesErrors.InsufficientPayment : tenders;
    }
}

// ─────────────────────────────── Anular ───────────────────────────────

/// <summary>
/// Anula una venta completada por error (D7-10, §5.3): solo con la jornada de la venta abierta, desde su caja y con
/// autorización. Kardex inverso al costo y lote originales, salida de caja por medio (el efectivo debe alcanzar) y
/// comprobante anulado. Una venta con cambios de mercancía, o pagada con un crédito de cambio, no se anula.
/// </summary>
public sealed record VoidSaleCommand(Guid SaleId, string Reason) : ICommand<SaleReceiptDto>, IAllowedWhenRestricted;

internal sealed class VoidSaleHandler(
    ISalesStore store,
    TerminalResolver terminals,
    SaleInventory inventory,
    SaleReceipts receipts,
    ICashRegister cash,
    IBillingService billing,
    IAuthorizationScope authorization,
    IActorContext actor,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock) : ICommandHandler<VoidSaleCommand, SaleReceiptDto>
{
    public async Task<Result<SaleReceiptDto>> Handle(VoidSaleCommand request, CancellationToken cancellationToken)
    {
        var scope = await terminals.ResolveAsync(requireOpenSession: true, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var sale = await store.LockSaleAsync(request.SaleId, cancellationToken);
        if (sale is null)
        {
            return SalesErrors.SaleNotFound;
        }

        var same = TerminalResolver.EnsureSameTerminal(sale, scope.Value);
        if (same.IsFailure)
        {
            return same.Error;
        }

        if (sale.ExchangeId is not null)
        {
            return SalesAppErrors.ExchangeSaleNotVoidable;
        }

        if (scope.Value.Session!.Id != sale.CashSessionId)
        {
            return Error.BusinessRule(SalesErrors.VoidNotAllowed.Code, $"{SalesErrors.VoidNotAllowed.Message} La jornada de la venta ya se cerró: haga un cambio de mercancía.");
        }

        var voided = sale.Void(request.Reason, actor.ActorId!.Value, authorization.Current?.AuthorizedBy, clock.UtcNow);
        if (voided.IsFailure)
        {
            return voided.Error;
        }

        var reversed = await inventory.ReverseSaleAsync(sale, cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error;
        }

        var cashed = await cash.RecordSaleMovementsAsync(
            new SaleCashRequest(
                sale.CashSessionId, "SALE_VOID", "SALE_VOID", sale.Id, sale.Number!,
                [.. sale.Payments.Where(p => p.MethodKind != "EXCHANGE_CREDIT").Select(p => new SaleCashAmount(p.PaymentMethodId, p.Applied))], sale.VoidReason,
                authorization.Current?.AuthorizedBy ?? actor.ActorId),
            cancellationToken);
        if (cashed.IsFailure)
        {
            return cashed.Error;
        }

        var document = await billing.VoidForSourceAsync(sale.Id, sale.VoidReason!, cancellationToken);
        if (document.IsFailure)
        {
            return document.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("sales", "SALE_VOIDED", nameof(Sale), sale.Id, sale.AuditLabel, $"Venta {sale.Number} de {sale.Total:N2} anulada: {sale.VoidReason}",
                AuthorizedBy: authorization.Current?.AuthorizedBy, Severity: AuditSeverity.Critical),
            cancellationToken);
        outbox.Enqueue("sales.sale_voided.v1", new { SaleId = sale.Id, sale.Number, sale.VoidReason, sale.VoidedAt, sale.VoidedBy }, OutboxDestination.Sync);
        return await receipts.BuildAsync(sale, copy: false, openDrawer: sale.Payments.Any(p => p.AffectsCashDrawer), cancellationToken);
    }
}

// ─────────────────────────────── Reimprimir y consultar ───────────────────────────────

/// <summary>Reimpresión (RN-SAL-14): el tiquete sale marcado "COPIA" y queda auditada.</summary>
public sealed record ReprintSaleCommand(Guid SaleId) : ICommand<SaleReceiptDto>, IAllowedWhenRestricted;

internal sealed class ReprintSaleHandler(ISalesStore store, SaleReceipts receipts, IAuditWriter audit) : ICommandHandler<ReprintSaleCommand, SaleReceiptDto>
{
    public async Task<Result<SaleReceiptDto>> Handle(ReprintSaleCommand request, CancellationToken cancellationToken)
    {
        var sale = await store.GetSaleAsync(request.SaleId, cancellationToken);
        if (sale is null)
        {
            return SalesErrors.SaleNotFound;
        }

        if (sale.Status is not (SaleStatus.Completed or SaleStatus.Voided))
        {
            return SalesErrors.InvalidStatus;
        }

        await audit.WriteAsync(new AuditEntry("sales", "SALE_REPRINTED", nameof(Sale), sale.Id, sale.AuditLabel, $"Copia del tiquete de la venta {sale.Number}."), cancellationToken);
        return await receipts.BuildAsync(sale, copy: true, openDrawer: false, cancellationToken);
    }
}

public sealed record GetSaleQuery(Guid SaleId) : IQuery<SaleDto>;

internal sealed class GetSaleHandler(ISalesStore store) : IQueryHandler<GetSaleQuery, SaleDto>
{
    public async Task<Result<SaleDto>> Handle(GetSaleQuery request, CancellationToken cancellationToken) =>
        await store.GetSaleAsync(request.SaleId, cancellationToken) is { } sale ? sale.ToDto() : SalesErrors.SaleNotFound;
}

/// <summary>Ventas de la sucursal con filtros (fechas de negocio, estado, número, caja, jornada).</summary>
public sealed record ListSalesQuery(
    DateOnly? From, DateOnly? To, string? Status, string? Number, Guid? PosTerminalId, Guid? CashSessionId, int? Limit, Guid? CustomerId = null)
    : IQuery<IReadOnlyList<SaleSummaryDto>>;

internal sealed class ListSalesHandler(Pos.Application.Abstractions.Installation.IInstallationContext installation, ISalesReadModel readModel)
    : IQueryHandler<ListSalesQuery, IReadOnlyList<SaleSummaryDto>>
{
    public async Task<Result<IReadOnlyList<SaleSummaryDto>>> Handle(ListSalesQuery request, CancellationToken cancellationToken) =>
        installation.BranchId is { } branch
            ? Result.Success(await readModel.ListSalesAsync(
                new SaleFilter(branch, request.From, request.To, request.Status?.ToUpperInvariant(), request.Number?.Trim(), request.PosTerminalId, request.CashSessionId,
                    Math.Clamp(request.Limit ?? 200, 1, 1000), request.CustomerId),
                cancellationToken))
            : SalesAppErrors.SetupRequired;
}

/// <summary>Ventas suspendidas de esta caja.</summary>
public sealed record ListHeldSalesQuery : IQuery<IReadOnlyList<SaleSummaryDto>>;

internal sealed class ListHeldSalesHandler(TerminalResolver terminals, ISalesReadModel readModel) : IQueryHandler<ListHeldSalesQuery, IReadOnlyList<SaleSummaryDto>>
{
    public async Task<Result<IReadOnlyList<SaleSummaryDto>>> Handle(ListHeldSalesQuery request, CancellationToken cancellationToken)
    {
        var scope = await terminals.ResolveAsync(requireOpenSession: false, cancellationToken);
        return scope.IsSuccess ? Result.Success(await readModel.ListHeldAsync(scope.Value.PosTerminalId, cancellationToken)) : scope.Error;
    }
}
