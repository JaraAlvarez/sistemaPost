using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Modules.Sales.Domain;
using Pos.Printing;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Sales.Application;

internal static class ExchangeMapping
{
    public static ExchangeDto ToDto(this CustomerReturn r) => new(
        r.Id, r.Number, r.Kind.Db(), r.Status.Db(), r.OriginalSaleId, r.OriginalSaleNumber, r.ReplacementSaleId, r.CreditTotal, r.Reason, r.BusinessDate, r.ReceivedAt,
        r.CompletedAt, [.. r.Lines.Select(l => new ExchangeLineDto(l.SaleLineId, l.ProductId, l.Sku, l.Name, l.Quantity, l.CreditAmount, l.Destination.Db()))]);
}

public sealed record ExchangeStartedDto(ExchangeDto Exchange, SaleDto Sale);

/// <summary>Reintegro por garantía: el documento y su tiquete (el cajón se abre para entregar el dinero).</summary>
public sealed record RefundReceiptDto(ExchangeDto Refund, TicketDocument Ticket, string TicketText, bool OpenDrawer);

/// <summary>
/// Registra lo recibido de la venta original (con la venta bloqueada: dos cambios no toman las mismas unidades), el kardex de
/// entrada, el número y el comprobante del cambio.
/// </summary>
public sealed class ExchangeCompletion(ISalesStore store, SaleInventory inventory, IBillingService billing, IDocumentNumberAllocator numbers)
{
    public async Task<Result> CompleteAsync(Guid exchangeId, Sale replacement, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var exchange = await store.GetReturnAsync(exchangeId, cancellationToken);
        if (exchange is not { Status: ReturnStatus.Draft })
        {
            return SalesErrors.ExchangeNotFound;
        }

        return await ApplyAsync(exchange, replacement.PosTerminalId, replacement.CashSessionId, replacement.WarehouseId, replacement.BusinessDate, null, now,
            cancellationToken);
    }

    /// <summary>Aplica un cambio o un reintegro: cantidades en la venta original, kardex, número y comprobante.</summary>
    public async Task<Result> ApplyAsync(
        CustomerReturn exchange, Guid posTerminalId, Guid cashSessionId, Guid warehouseId, DateOnly businessDate, Guid? refundMethodId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        var original = await store.LockSaleAsync(exchange.OriginalSaleId, cancellationToken);
        if (original is null)
        {
            return SalesErrors.SaleNotFound;
        }

        foreach (var line in exchange.Lines)
        {
            var registered = original.RegisterReturned(line.SaleLineId, line.Quantity);
            if (registered.IsFailure)
            {
                return registered.Error;
            }
        }

        var number = await numbers.NextForTerminalAsync("CUSTOMER_RETURN", posTerminalId, cancellationToken);
        var completed = exchange.Complete(number.Number, cashSessionId, refundMethodId, now);
        if (completed.IsFailure)
        {
            return completed.Error;
        }

        var posted = await inventory.PostReturnAsync(exchange, original, warehouseId, businessDate, cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        var issued = await billing.IssueAsync(
            new FiscalIssueRequest(
                "CUSTOMER_RETURN", exchange.Id, exchange.Number!, exchange.BranchId, posTerminalId, businessDate, original.CustomerName, original.CustomerIdentificationType,
                original.CustomerIdentification, original.CustomerEmail, 0m, 0m, exchange.CreditTotal, RelatedSourceId: original.Id),
            cancellationToken);
        return issued.IsSuccess ? Result.Success() : issued.Error;
    }
}

/// <summary>
/// Inicia un cambio de mercancía (D7-11, §5.4): calcula el crédito al precio que el cliente pagó y abre una venta nueva que
/// lo usa. El cliente lleva productos por igual o mayor valor y paga la diferencia; nunca sale dinero del cajón. Cancelar la
/// venta nueva deja el cambio sin efecto. El endpoint exige el permiso o la autorización de supervisor.
/// </summary>
public sealed record StartExchangeCommand(Guid OriginalSaleId, IReadOnlyList<ReturnLineRequest> Lines, string Reason) : ICommand<ExchangeStartedDto>, IAllowedWhenRestricted;

internal sealed class StartExchangeHandler(
    ISalesStore store,
    TerminalResolver terminals,
    ISettingsReader settings,
    IAuthorizationScope authorization,
    IActorContext actor,
    IAuditWriter audit,
    CustomerResolver customers,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<StartExchangeCommand, ExchangeStartedDto>
{
    public async Task<Result<ExchangeStartedDto>> Handle(StartExchangeCommand request, CancellationToken cancellationToken)
    {
        var scope = await terminals.ResolveAsync(requireOpenSession: true, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        if (await store.GetOpenSaleAsync(scope.Value.PosTerminalId, cancellationToken) is { } open)
        {
            return Error.Conflict(SalesErrors.OpenSaleExists.Code, $"{SalesErrors.OpenSaleExists.Message} Venta en curso: {open.Id}.");
        }

        var original = await store.LockSaleAsync(request.OriginalSaleId, cancellationToken);
        if (original is null)
        {
            return SalesErrors.SaleNotFound;
        }

        if (original.BranchId != scope.Value.BranchId)
        {
            return SalesErrors.ExchangeNotAllowed;
        }

        if (await store.HasDraftReturnAsync(original.Id, cancellationToken))
        {
            return SalesAppErrors.ExchangeInProgress;
        }

        var session = scope.Value.Session!;
        var days = await settings.GetAsync(SalesSettings.ExchangeDays, new SettingContext(original.CompanyId, original.BranchId), cancellationToken);
        var now = clock.UtcNow;
        var exchange = CustomerReturn.Create(
            ids.NewId(), ReturnKind.Exchange, original, request.Lines ?? [], request.Reason, scope.Value.PosTerminalId, session.BusinessDate, days, actor.ActorId!.Value,
            authorization.Current?.AuthorizedBy ?? actor.ActorId, now, ids.NewId);
        if (exchange.IsFailure)
        {
            return exchange.Error;
        }

        var sale = Sale.Start(
            ids.NewId(), scope.Value.CompanyId, scope.Value.BranchId, scope.Value.PosTerminalId, scope.Value.WarehouseId, session.Id, scope.Value.UserId,
            session.BusinessDate, now,
            new CustomerSnapshot(original.CustomerId, original.CustomerName, original.CustomerIdentificationType, original.CustomerIdentification, original.CustomerEmail,
                original.CustomerFiscal),
            exchange.Value.Id, exchange.Value.CreditTotal);

        // La venta nueva usa la lista de precio del cliente de la venta original (D8-09).
        if (original.CustomerId is { } customerId)
        {
            var customer = await customers.ResolveAsync(customerId, original.BranchId, cancellationToken);
            if (customer.IsFailure)
            {
                return customer.Error;
            }

            sale.SetCustomer(customer.Value.Snapshot, customer.Value.Pricing, invoiceRequested: original.InvoiceRequested);
        }

        exchange.Value.LinkReplacementSale(sale.Id);
        store.Add(exchange.Value);
        store.Add(sale);
        await audit.WriteAsync(
            new AuditEntry("sales", "EXCHANGE_STARTED", nameof(CustomerReturn), exchange.Value.Id, exchange.Value.AuditLabel,
                $"Cambio de la venta {original.Number}: crédito {exchange.Value.CreditTotal:N2} ({exchange.Value.Lines.Count} productos). Motivo: {exchange.Value.Reason}",
                AuthorizedBy: authorization.Current?.AuthorizedBy, Severity: AuditSeverity.Warning),
            cancellationToken);
        return new ExchangeStartedDto(exchange.Value.ToDto(), sale.ToDto());
    }
}

/// <summary>
/// Reintegro por garantía (excepción legal, Ley 1480 de 2011; Fase 7, pregunta final 2): solo el propietario
/// (<c>sales.refund.warranty</c>, sin autorización de supervisor), con motivo; el dinero sale en efectivo del cajón de la
/// jornada abierta (RN-CSH-06) y queda auditado como crítico.
/// </summary>
public sealed record WarrantyRefundCommand(Guid OriginalSaleId, IReadOnlyList<ReturnLineRequest> Lines, string Reason) : ICommand<RefundReceiptDto>, IAllowedWhenRestricted;

internal sealed class WarrantyRefundHandler(
    ISalesStore store,
    TerminalResolver terminals,
    ExchangeCompletion completion,
    ISalesReadModel readModel,
    IPaymentMethodDirectory methods,
    ICashRegister cash,
    ISettingsReader settings,
    IActorContext actor,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<WarrantyRefundCommand, RefundReceiptDto>
{
    public async Task<Result<RefundReceiptDto>> Handle(WarrantyRefundCommand request, CancellationToken cancellationToken)
    {
        var scope = await terminals.ResolveAsync(requireOpenSession: true, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var original = await store.LockSaleAsync(request.OriginalSaleId, cancellationToken);
        if (original is null)
        {
            return SalesErrors.SaleNotFound;
        }

        if (original.BranchId != scope.Value.BranchId)
        {
            return SalesErrors.ExchangeNotAllowed;
        }

        if (await store.HasDraftReturnAsync(original.Id, cancellationToken))
        {
            return SalesAppErrors.ExchangeInProgress;
        }

        var session = scope.Value.Session!;
        var days = await settings.GetAsync(SalesSettings.ExchangeDays, new SettingContext(original.CompanyId, original.BranchId), cancellationToken);
        var now = clock.UtcNow;
        var refund = CustomerReturn.Create(
            ids.NewId(), ReturnKind.WarrantyRefund, original, request.Lines ?? [], request.Reason, scope.Value.PosTerminalId, session.BusinessDate, days,
            actor.ActorId!.Value, actor.ActorId, now, ids.NewId);
        if (refund.IsFailure)
        {
            return refund.Error;
        }

        var cashMethod = await methods.GetByCodeAsync("EFECTIVO", cancellationToken);
        store.Add(refund.Value);
        var applied = await completion.ApplyAsync(refund.Value, scope.Value.PosTerminalId, session.Id, scope.Value.WarehouseId, session.BusinessDate, cashMethod!.Id, now,
            cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        var cashed = await cash.RecordSaleMovementsAsync(
            new SaleCashRequest(session.Id, "CUSTOMER_REFUND", "CUSTOMER_RETURN", refund.Value.Id, refund.Value.Number!, [new SaleCashAmount(cashMethod.Id, refund.Value.CreditTotal)],
                $"Garantía de la venta {original.Number}: {refund.Value.Reason}", actor.ActorId),
            cancellationToken);
        if (cashed.IsFailure)
        {
            return cashed.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("sales", "WARRANTY_REFUND", nameof(CustomerReturn), refund.Value.Id, refund.Value.AuditLabel,
                $"Reintegro por garantía de {refund.Value.CreditTotal:N2} en efectivo (venta {original.Number}): {refund.Value.Reason}", Severity: AuditSeverity.Critical),
            cancellationToken);
        var header = await readModel.GetTicketHeaderAsync(scope.Value.PosTerminalId, scope.Value.UserId, cancellationToken)
            ?? new TicketHeader(string.Empty, string.Empty, string.Empty, null, null, string.Empty, null, string.Empty, string.Empty);
        var ticket = SaleTicketBuilder.BuildReturn(refund.Value, header, openDrawer: true);
        return new RefundReceiptDto(refund.Value.ToDto(), ticket, TicketLayout.ToText(ticket, TicketLayout.Columns80Mm), OpenDrawer: true);
    }
}

public sealed record GetExchangeQuery(Guid ExchangeId) : IQuery<ExchangeDto>;

internal sealed class GetExchangeHandler(ISalesStore store) : IQueryHandler<GetExchangeQuery, ExchangeDto>
{
    public async Task<Result<ExchangeDto>> Handle(GetExchangeQuery request, CancellationToken cancellationToken) =>
        await store.GetReturnAsync(request.ExchangeId, cancellationToken) is { } exchange ? exchange.ToDto() : SalesErrors.ExchangeNotFound;
}
