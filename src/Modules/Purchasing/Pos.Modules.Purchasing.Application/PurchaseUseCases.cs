using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Modules.Purchasing.Domain;
using Pos.SharedKernel.Finance;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Purchasing.Application;

/// <summary>Impuesto digitado para una línea (el de la factura). Sin él, la línea toma los impuestos del producto en la fecha de la factura.</summary>
public sealed record TaxOverride(string Code, decimal? Rate, decimal? FixedAmount, bool? IsVat = null);

/// <summary>Línea de compra: producto por Id, por código del proveedor o por SKU/código de barras; costo por presentación.</summary>
public sealed record PurchaseLineRequest(
    Guid? ProductId,
    string? Code,
    Guid? PackagingId,
    decimal Quantity,
    decimal? UnitCost,
    decimal Discount = 0m,
    IReadOnlyList<TaxOverride>? Taxes = null,
    decimal? ManualCharges = null,
    string? LotNumber = null,
    DateOnly? ExpiryDate = null,
    Guid? OrderLineId = null);

/// <summary>Retención digitada; sin <c>Amount</c> se calcula como base × tarifa.</summary>
public sealed record WithholdingRequest(WithholdingKind Kind, decimal Base, decimal? Rate, decimal? Amount);

/// <summary>
/// Compra (factura del proveedor). En una modificación no cambian proveedor, bodega ni orden. Con orden y sin líneas, la
/// compra propone lo pendiente de la orden.
/// </summary>
public sealed record PurchaseRequest(
    Guid SupplierId,
    Guid WarehouseId,
    Guid? PurchaseOrderId,
    string SupplierInvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly? BusinessDate,
    DateOnly? DueDate,
    PaymentMode PaymentMode,
    Guid? PaymentMethodId,
    string? PaymentReference,
    decimal? InvoiceTotal,
    ProrationMethod Proration,
    decimal ChargesTotal,
    string? ChargesNotes,
    string? Notes,
    IReadOnlyList<PurchaseLineRequest>? Lines,
    IReadOnlyList<WithholdingRequest>? Withholdings);

internal static class PurchaseMapping
{
    public static async Task<PurchaseDto> ToDtoAsync(
        this Purchase p, ICatalogReader catalog, IPurchasingQueries queries, CancellationToken cancellationToken, IReadOnlyList<PurchaseAlertDto>? alerts = null)
    {
        var products = await catalog.GetProductsAsync([.. p.Lines.Select(l => l.ProductId)], cancellationToken);
        var names = await queries.SupplierNamesAsync([p.SupplierId], cancellationToken);
        return new PurchaseDto(
            p.Id, p.Number, p.SupplierId, names.GetValueOrDefault(p.SupplierId) ?? string.Empty, p.WarehouseId, p.PurchaseOrderId, p.SupplierInvoiceNumber,
            p.InvoiceDate, p.BusinessDate, p.DueDate, p.PaymentMode.Db(), p.PaymentMethodId, p.PaymentReference, p.RequiresSupportDocument, p.InvoiceTotal,
            p.ProrationMethod.Db(), p.ChargesTotal, p.ChargesNotes, p.Subtotal, p.DiscountTotal, p.TaxTotal, p.DeductibleTaxTotal, p.WithholdingTotal, p.Total,
            p.PayableTotal, p.Status.Db(), p.Notes, p.PostedAt, p.VoidedAt, p.VoidReason,
            [.. p.Lines.OrderBy(l => l.LineNumber).Select(l => new PurchaseLineDto(
                l.Id, l.LineNumber, l.ProductId, products.GetValueOrDefault(l.ProductId)?.Sku ?? string.Empty, products.GetValueOrDefault(l.ProductId)?.Name ?? string.Empty,
                l.PackagingId, l.Factor, l.Quantity, l.BaseQuantity, l.UnitCost, l.GrossAmount, l.DiscountAmount, l.ChargesAmount, l.TaxAmount, l.NonDeductibleTax,
                l.LineTotal, l.NetUnitCost, l.LotNumber, l.ExpiryDate, l.OrderLineId, l.ReturnedBaseQuantity,
                [.. l.Taxes.OrderBy(t => t.TaxCode, StringComparer.Ordinal)
                    .Select(t => new PurchaseLineTaxDto(t.TaxCode, t.IsVat, t.Rate, t.FixedAmount, t.Base, t.Amount, t.IsDeductible))]))],
            [.. p.Withholdings.OrderBy(w => w.Kind).Select(w => new WithholdingDto(w.Kind.Db(), w.Base, w.Rate, w.Amount))],
            alerts ?? []);
    }
}

/// <summary>Arma las líneas y retenciones del dominio desde la petición (productos, factores, impuestos y lo pendiente de la orden).</summary>
public sealed class PurchaseInputBuilder(PurchaseLineResolver resolver, ICatalogReader catalog)
{
    public async Task<Result<(IReadOnlyList<PurchaseLineInput> Lines, IReadOnlyList<WithholdingInput> Withholdings)>> BuildAsync(
        Guid supplierId, PurchaseOrder? order, PurchaseRequest request, CancellationToken cancellationToken)
    {
        var requested = request.Lines is { Count: > 0 } lines ? lines : order is null ? [] : PendingOf(order);
        var resolved = await resolver.ResolveAsync(supplierId, [.. requested.Select(l => (l.ProductId, l.Code, l.PackagingId))], cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        var taxes = await catalog.GetTaxesAsync([.. resolved.Value.Select(r => r.Product.Id).Distinct()], request.InvoiceDate, cancellationToken);
        var result = new List<PurchaseLineInput>();
        for (var i = 0; i < requested.Count; i++)
        {
            var line = requested[i];
            var r = resolved.Value[i];
            if (line.OrderLineId is { } orderLineId
                && (order?.Lines.SingleOrDefault(o => o.Id == orderLineId) is not { } orderLine || orderLine.ProductId != r.Product.Id))
            {
                return PurchasingErrors.OrderLineMismatch;
            }

            var productTaxes = taxes.GetValueOrDefault(r.Product.Id) ?? [];
            IReadOnlyList<CostingTax> lineTaxes = line.Taxes is { } overrides
                ? [.. overrides.Select(o =>
                {
                    var code = (o.Code ?? string.Empty).Trim().ToUpperInvariant();
                    var known = productTaxes.FirstOrDefault(t => t.Code == code);
                    return new CostingTax(known?.TaxId, code, o.IsVat ?? known?.IsVat ?? code.StartsWith("IVA", StringComparison.Ordinal), o.Rate, o.Rate is null ? o.FixedAmount : null);
                })]
                : [.. productTaxes.Select(t => new CostingTax(t.TaxId, t.Code, t.IsVat, t.Rate, t.FixedAmount))];
            if (lineTaxes.Any(t => string.IsNullOrEmpty(t.Code) || t.Code.Length > 20 || (t.Rate is null && t.FixedAmount is null) || t.Rate is < 0m or > 100m
                                   || t.FixedAmount < 0m) || lineTaxes.Select(t => t.Code).Distinct().Count() != lineTaxes.Count)
            {
                return Error.Validation("PURCHASING.INVALID_TAX", "Impuesto inválido: código único por línea, tarifa 0–100 o valor fijo ≥ 0.");
            }

            var unitCost = line.UnitCost ?? decimal.Round((r.SupplierProduct?.LastCost ?? 0m) * r.Factor, 4, MidpointRounding.AwayFromZero);
            result.Add(new PurchaseLineInput(
                r.Product.Id, r.PackagingId, r.Factor, line.Quantity, unitCost, line.Discount, lineTaxes, line.ManualCharges, line.LotNumber, line.ExpiryDate,
                line.OrderLineId));
        }

        var withholdings = (request.Withholdings ?? [])
            .Select(w => new WithholdingInput(w.Kind, w.Base, w.Rate, w.Amount ?? RoundingPolicy.Colombia.RoundMoney(w.Base * (w.Rate ?? 0m) / 100m)))
            .ToList();
        return (result, withholdings);
    }

    /// <summary>Lo pendiente de la orden, en la presentación pedida (cantidad = pendiente base ÷ factor).</summary>
    private static List<PurchaseLineRequest> PendingOf(PurchaseOrder order) =>
    [
        .. order.Lines.Where(l => l.PendingBaseQuantity > 0m).OrderBy(l => l.LineNumber).Select(l => new PurchaseLineRequest(
            l.ProductId, null, l.PackagingId, decimal.Round(l.PendingBaseQuantity / l.Factor, 4, MidpointRounding.AwayFromZero), l.UnitCost, OrderLineId: l.Id)),
    ];
}

public sealed record CreatePurchaseCommand(PurchaseRequest Purchase) : ICommand<PurchaseDto>;

internal sealed class CreatePurchaseHandler(
    IInstallationContext installation,
    IPurchasingStore store,
    IPurchasingQueries queries,
    PurchasingGuards guards,
    PurchaseInputBuilder builder,
    ICatalogReader catalog,
    ISettingsReader settings,
    IDocumentNumberAllocator numbers,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<CreatePurchaseCommand, PurchaseDto>
{
    public async Task<Result<PurchaseDto>> Handle(CreatePurchaseCommand command, CancellationToken cancellationToken)
    {
        var request = command.Purchase;
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        var warehouse = await guards.LocalWarehouseAsync(request.WarehouseId, cancellationToken);
        if (warehouse.IsFailure)
        {
            return warehouse.Error;
        }

        var supplier = await guards.ActiveSupplierAsync(request.SupplierId, cancellationToken);
        if (supplier.IsFailure)
        {
            return supplier.Error;
        }

        PurchaseOrder? order = null;
        if (request.PurchaseOrderId is { } orderId)
        {
            order = await store.GetOrderAsync(orderId, cancellationToken);
            if (order is null)
            {
                return PurchasingErrors.OrderNotFound;
            }

            if (order.SupplierId != supplier.Value.Id || order.WarehouseId != request.WarehouseId)
            {
                return PurchasingErrors.OrderSupplierMismatch;
            }

            if (!order.IsReceivable)
            {
                return PurchasingErrors.OrderNotReceivable;
            }
        }

        if (await store.InvoiceExistsAsync(supplier.Value.Id, Purchase.NormalizeInvoiceNumber(request.SupplierInvoiceNumber), null, cancellationToken))
        {
            return PurchasingErrors.InvoiceDuplicated;
        }

        var inputs = await builder.BuildAsync(supplier.Value.Id, order, request, cancellationToken);
        if (inputs.IsFailure)
        {
            return inputs.Error;
        }

        var vatDeductible = await settings.GetAsync(PurchasingSettings.VatDeductible, new SettingContext(local.Value.CompanyId), cancellationToken);
        var number = await numbers.NextForBranchAsync("PURCHASE", local.Value.BranchId, cancellationToken);
        var purchase = Purchase.Create(
            ids.NewId(), local.Value.CompanyId, local.Value.BranchId, request.WarehouseId, supplier.Value, order?.Id, number.Number, Header(request, clock),
            inputs.Value.Lines, inputs.Value.Withholdings, vatDeductible, ids.NewId);
        if (purchase.IsFailure)
        {
            return purchase.Error;
        }

        store.Add(purchase.Value);
        return await purchase.Value.ToDtoAsync(catalog, queries, cancellationToken);
    }

    internal static PurchaseHeader Header(PurchaseRequest r, IClock clock) => new(
        r.SupplierInvoiceNumber, r.InvoiceDate, r.BusinessDate ?? clock.Today, r.DueDate, r.PaymentMode, r.PaymentMethodId, r.PaymentReference, r.InvoiceTotal,
        r.Proration, r.ChargesTotal, r.ChargesNotes, r.Notes);
}

public sealed record UpdatePurchaseCommand(Guid PurchaseId, PurchaseRequest Purchase) : ICommand<PurchaseDto>;

internal sealed class UpdatePurchaseHandler(
    IPurchasingStore store, IPurchasingQueries queries, PurchaseInputBuilder builder, ICatalogReader catalog, ISettingsReader settings, IIdGenerator ids, IClock clock)
    : ICommandHandler<UpdatePurchaseCommand, PurchaseDto>
{
    public async Task<Result<PurchaseDto>> Handle(UpdatePurchaseCommand command, CancellationToken cancellationToken)
    {
        var purchase = await store.GetPurchaseAsync(command.PurchaseId, cancellationToken);
        if (purchase is null)
        {
            return PurchasingErrors.PurchaseNotFound;
        }

        var supplier = (await store.GetSupplierAsync(purchase.SupplierId, cancellationToken))!;
        var order = purchase.PurchaseOrderId is { } orderId ? await store.GetOrderAsync(orderId, cancellationToken) : null;
        var request = command.Purchase with { SupplierId = purchase.SupplierId, WarehouseId = purchase.WarehouseId, PurchaseOrderId = purchase.PurchaseOrderId };
        if (await store.InvoiceExistsAsync(supplier.Id, Purchase.NormalizeInvoiceNumber(request.SupplierInvoiceNumber), purchase.Id, cancellationToken))
        {
            return PurchasingErrors.InvoiceDuplicated;
        }

        var inputs = await builder.BuildAsync(supplier.Id, order, request, cancellationToken);
        if (inputs.IsFailure)
        {
            return inputs.Error;
        }

        var vatDeductible = await settings.GetAsync(PurchasingSettings.VatDeductible, new SettingContext(purchase.CompanyId), cancellationToken);
        var edited = purchase.Edit(supplier, CreatePurchaseHandler.Header(request, clock), inputs.Value.Lines, inputs.Value.Withholdings, vatDeductible, ids.NewId);
        return edited.IsSuccess ? await purchase.ToDtoAsync(catalog, queries, cancellationToken) : edited.Error;
    }
}

public sealed record GetPurchaseQuery(Guid PurchaseId) : IQuery<PurchaseDto>;

internal sealed class GetPurchaseHandler(IPurchasingStore store, IPurchasingQueries queries, ICatalogReader catalog) : IQueryHandler<GetPurchaseQuery, PurchaseDto>
{
    public async Task<Result<PurchaseDto>> Handle(GetPurchaseQuery request, CancellationToken cancellationToken) =>
        await store.GetPurchaseAsync(request.PurchaseId, cancellationToken) is { } purchase
            ? await purchase.ToDtoAsync(catalog, queries, cancellationToken)
            : PurchasingErrors.PurchaseNotFound;
}

/// <summary>
/// Contabiliza la compra (RN-PUR-01): valida totales, lotes y la orden; registra lotes y el kardex (PURCHASE_RECEIPT al
/// costo neto), la cuenta por pagar (y el pago si es de contado), el último costo del proveedor y devuelve las alertas de
/// precio bajo el costo y de variación de costo. Dos usuarios que contabilizan a la vez: la concurrencia optimista deja
/// pasar a uno solo.
/// </summary>
/// <remarks>Compra de contado desde la caja (Fase 6): con <c>CashSessionId</c> el pago sale de esa jornada.</remarks>
public sealed record PostPurchaseCommand(Guid PurchaseId, Guid? CashSessionId = null) : ICommand<PurchaseDto>;

internal sealed class PostPurchaseHandler(
    IPurchasingStore store,
    IPurchasingQueries queries,
    PurchasingGuards guards,
    PaymentMethodGuard methods,
    ICatalogReader catalog,
    IInventoryPosting posting,
    IInventoryLots lots,
    ISettingsReader settings,
    IDocumentNumberAllocator numbers,
    ICashRegister cash,
    IActorContext actor,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<PostPurchaseCommand, PurchaseDto>
{
    public async Task<Result<PurchaseDto>> Handle(PostPurchaseCommand request, CancellationToken cancellationToken)
    {
        var purchase = await store.GetPurchaseAsync(request.PurchaseId, cancellationToken);
        if (purchase is null)
        {
            return PurchasingErrors.PurchaseNotFound;
        }

        var supplier = await guards.ActiveSupplierAsync(purchase.SupplierId, cancellationToken);
        if (supplier.IsFailure)
        {
            return supplier.Error;
        }

        var warehouse = await guards.LocalWarehouseAsync(purchase.WarehouseId, cancellationToken);
        if (warehouse.IsFailure)
        {
            return warehouse.Error;
        }

        PaymentMethodInfo? method = null;
        if (purchase.PaymentMode == PaymentMode.Cash)
        {
            var active = await methods.ActiveAsync(purchase.PaymentMethodId, cancellationToken);
            if (active.IsFailure)
            {
                return PurchasingErrors.PaymentMethodRequired;
            }

            method = active.Value;
        }

        var context = new SettingContext(purchase.CompanyId, purchase.BranchId);
        var ready = purchase.CanPost(await settings.GetAsync(PurchasingSettings.InvoiceTotalTolerance, context, cancellationToken), method?.RequiresReference == true);
        if (ready.IsFailure)
        {
            return ready.Error;
        }

        var products = await catalog.GetProductsAsync([.. purchase.Lines.Select(l => l.ProductId)], cancellationToken);
        if (purchase.Lines.Any(l => products[l.ProductId] is { TracksLots: true } p && (l.LotNumber is null || (p.TracksExpiry && l.ExpiryDate is null))))
        {
            return PurchasingErrors.LotRequired;
        }

        var receipts = await RegisterOrderReceiptsAsync(purchase, context, cancellationToken);
        if (receipts.IsFailure)
        {
            return receipts.Error;
        }

        var lotsByLine = new Dictionary<Guid, Guid>();
        foreach (var line in purchase.Lines.Where(l => l.LotNumber is not null && products[l.ProductId].TracksLots))
        {
            var lot = await lots.EnsureLotAsync(line.ProductId, line.LotNumber!, line.ExpiryDate, null, cancellationToken);
            if (lot.IsFailure)
            {
                return lot.Error;
            }

            lotsByLine[line.Id] = lot.Value;
        }

        var posted = await posting.PostAsync(
            new InventoryPosting("PURCHASE", purchase.Id, purchase.Number, purchase.BranchId, purchase.BusinessDate,
                [.. purchase.Lines.Select(l => new PostingLine(
                    purchase.WarehouseId, l.ProductId, MovementType.PurchaseReceipt, l.BaseQuantity, l.NetUnitCost, l.Id,
                    PackagingId: l.PackagingId, PackagingQuantity: l.PackagingId is null ? null : l.Quantity, LotId: lotsByLine.TryGetValue(l.Id, out var lot) ? lot : null))]),
            cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        var userId = actor.ActorId!.Value;
        var now = clock.UtcNow;
        purchase.MarkPosted(lotsByLine, userId, now);
        var account = AccountPayable.Open(ids.NewId(), purchase, userId, now, ids.NewId());
        store.Add(account);
        if (method is not null && account.Balance > 0m)
        {
            var number = await numbers.NextForBranchAsync("PAYABLE_PAYMENT", purchase.BranchId, cancellationToken);
            var payment = PayablePayment.Create(
                ids.NewId(), purchase.CompanyId, purchase.BranchId, purchase.SupplierId, number.Number, purchase.InvoiceDate, method.Id, purchase.PaymentReference,
                $"Pago de contado de la compra {purchase.Number}", [new AllocationInput(account.Id, account.Balance)], ids.NewId, request.CashSessionId).Value;
            store.Add(payment);
            account.ApplyPayment(ids.NewId(), payment.Amount, payment.Id, payment.Number, userId, now);
            if (request.CashSessionId is { } sessionId)
            {
                var moved = await cash.RecordOutflowAsync(
                    new CashOutflowRequest(sessionId, "SUPPLIER_PAYMENT", method.Id, payment.Amount, "PAYABLE_PAYMENT", payment.Id, payment.Number,
                        $"Compra de contado {purchase.Number} (factura {purchase.SupplierInvoiceNumber})"),
                    cancellationToken);
                if (moved.IsFailure)
                {
                    return moved.Error;
                }
            }
        }
        else if (request.CashSessionId is not null)
        {
            return Error.Validation("PURCHASING.CASH_SESSION_ONLY_FOR_CASH", "Solo una compra de contado se paga desde la caja.");
        }

        var alerts = await UpdateSupplierCostsAndAlertAsync(purchase, supplier.Value, products, context, now, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("purchasing", "PURCHASE_POSTED", nameof(Purchase), purchase.Id, purchase.AuditLabel,
                $"Compra {purchase.Number} contabilizada: factura {purchase.SupplierInvoiceNumber}, total {purchase.Total:N2}, por pagar {purchase.PayableTotal:N2}"
                + $" ({purchase.PaymentMode.Db()}), {purchase.Lines.Count} líneas{(purchase.RequiresSupportDocument ? ", requiere documento soporte" : string.Empty)}.",
                Severity: AuditSeverity.Info),
            cancellationToken);
        return await purchase.ToDtoAsync(catalog, queries, cancellationToken, alerts);
    }

    private async Task<Result> RegisterOrderReceiptsAsync(Purchase purchase, SettingContext context, CancellationToken cancellationToken)
    {
        if (purchase.PurchaseOrderId is not { } orderId)
        {
            return Result.Success();
        }

        var order = (await store.GetOrderAsync(orderId, cancellationToken))!;
        var tolerance = await settings.GetAsync(PurchasingSettings.ReceiptTolerancePercent, context, cancellationToken);
        foreach (var group in purchase.Lines.Where(l => l.OrderLineId is not null).GroupBy(l => l.OrderLineId!.Value))
        {
            var registered = order.RegisterReceipt(group.Key, group.Sum(l => l.BaseQuantity), tolerance);
            if (registered.IsFailure)
            {
                return registered;
            }
        }

        return Result.Success();
    }

    private async Task<IReadOnlyList<PurchaseAlertDto>> UpdateSupplierCostsAndAlertAsync(
        Purchase purchase, Supplier supplier, IReadOnlyDictionary<Guid, CatalogProductInfo> products, SettingContext context, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var alerts = new List<PurchaseAlertDto>();
        var variation = await settings.GetAsync(PurchasingSettings.CostVariationAlertPercent, context, cancellationToken);
        var supplierProducts = (await store.GetSupplierProductsAsync(supplier.Id, cancellationToken)).ToDictionary(p => p.ProductId);
        var costs = purchase.Lines.GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Max(l => l.NetUnitCost));
        foreach (var line in purchase.Lines)
        {
            var product = products[line.ProductId];
            if (!supplierProducts.TryGetValue(line.ProductId, out var item))
            {
                item = SupplierProduct.Create(ids.NewId(), purchase.CompanyId, supplier.Id, line.ProductId, line.PackagingId, null, null, false).Value;
                store.Add(item);
                supplierProducts[line.ProductId] = item;
            }
            else if (item.LastCost is { } last && last > 0m && Math.Abs(line.NetUnitCost - last) * 100m / last > variation)
            {
                alerts.Add(new PurchaseAlertDto(line.ProductId, product.Sku, product.Name, "COST_VARIATION",
                    $"El costo pasó de {last:N2} a {line.NetUnitCost:N2} por unidad frente a la última compra al proveedor.", line.NetUnitCost, last));
            }

            item.RecordPurchase(line.NetUnitCost, now);
        }

        var prices = await catalog.GetNetSalePricesAsync([.. costs.Keys], purchase.BranchId, now, cancellationToken);
        foreach (var (productId, cost) in costs)
        {
            if (prices.TryGetValue(productId, out var price) && price < cost)
            {
                var product = products[productId];
                alerts.Add(new PurchaseAlertDto(productId, product.Sku, product.Name, "PRICE_BELOW_COST",
                    $"El precio de venta sin impuestos ({price:N2}) quedó por debajo del nuevo costo ({cost:N2}).", cost, price));
            }
        }

        return alerts;
    }
}

/// <summary>
/// Anula una compra contabilizada (D5-08, RN-PUR-05): sin pagos ni devoluciones; movimientos inversos al costo original
/// (si revertir deja saldos o lotes negativos, no se anula); la cuenta por pagar queda anulada y la orden vuelve a pendiente.
/// </summary>
public sealed record VoidPurchaseCommand(Guid PurchaseId, string Reason) : ICommand<PurchaseDto>;

internal sealed class VoidPurchaseHandler(
    IPurchasingStore store, IPurchasingQueries queries, ICatalogReader catalog, IInventoryPosting posting, IActorContext actor, IAuditWriter audit,
    IIdGenerator ids, IClock clock) : ICommandHandler<VoidPurchaseCommand, PurchaseDto>
{
    public async Task<Result<PurchaseDto>> Handle(VoidPurchaseCommand request, CancellationToken cancellationToken)
    {
        var purchase = await store.GetPurchaseAsync(request.PurchaseId, cancellationToken);
        if (purchase is null)
        {
            return PurchasingErrors.PurchaseNotFound;
        }

        var userId = actor.ActorId!.Value;
        var now = clock.UtcNow;
        var voided = purchase.Void(request.Reason, userId, now);
        if (voided.IsFailure)
        {
            return voided.Error;
        }

        var account = await store.GetPayableByPurchaseAsync(purchase.Id, cancellationToken);
        if (account is not null && account.Void(ids.NewId(), purchase.Id, purchase.Number, userId, now) is { IsFailure: true } payable)
        {
            return payable.Error;
        }

        var reversed = await posting.ReverseAsync(
            new InventoryReversal("PURCHASE", purchase.Id, "PURCHASE_VOID", purchase.Id, purchase.Number, purchase.BranchId, clock.Today), cancellationToken);
        if (reversed.IsFailure && reversed.Error.Code != "INVENTORY.NOTHING_TO_REVERSE")
        {
            return reversed.Error;
        }

        if (purchase.PurchaseOrderId is { } orderId && await store.GetOrderAsync(orderId, cancellationToken) is { } order)
        {
            foreach (var group in purchase.Lines.Where(l => l.OrderLineId is not null).GroupBy(l => l.OrderLineId!.Value))
            {
                order.RevertReceipt(group.Key, group.Sum(l => l.BaseQuantity));
            }
        }

        await audit.WriteAsync(
            new AuditEntry("purchasing", "PURCHASE_VOIDED", nameof(Purchase), purchase.Id, purchase.AuditLabel,
                $"Compra {purchase.Number} anulada: {purchase.VoidReason}. Se revirtieron sus movimientos de inventario y su cuenta por pagar.",
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return await purchase.ToDtoAsync(catalog, queries, cancellationToken);
    }
}
