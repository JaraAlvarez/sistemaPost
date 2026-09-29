using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Modules.Purchasing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Purchasing.Application;

public sealed record ReturnLineRequest(Guid PurchaseLineId, decimal BaseQuantity);

internal static class ReturnMapping
{
    public static async Task<SupplierReturnDto> ToDtoAsync(this SupplierReturn r, ICatalogReader catalog, IPurchasingQueries queries, CancellationToken cancellationToken)
    {
        var products = await catalog.GetProductsAsync([.. r.Lines.Select(l => l.ProductId)], cancellationToken);
        var names = await queries.SupplierNamesAsync([r.SupplierId], cancellationToken);
        return new SupplierReturnDto(
            r.Id, r.Number, r.PurchaseId, r.SupplierId, names.GetValueOrDefault(r.SupplierId) ?? string.Empty, r.WarehouseId, r.BusinessDate, r.Reason, r.Status.Db(),
            r.Total, r.CreditTotal, r.Settlement?.Db(), r.SettlementReference, r.PostedAt, r.SettledAt,
            [.. r.Lines.OrderBy(l => l.LineNumber).Select(l => new SupplierReturnLineDto(
                l.Id, l.PurchaseLineId, l.ProductId, products.GetValueOrDefault(l.ProductId)?.Sku ?? string.Empty,
                products.GetValueOrDefault(l.ProductId)?.Name ?? string.Empty, l.BaseQuantity, l.UnitCost, l.Total, l.CreditAmount, l.LotId))]);
    }
}

/// <summary>Devolución en borrador contra una compra contabilizada: al costo neto de la compra y del lote original (D5-07).</summary>
public sealed record CreateReturnCommand(Guid PurchaseId, DateOnly? BusinessDate, string Reason, IReadOnlyList<ReturnLineRequest> Lines) : ICommand<SupplierReturnDto>;

internal sealed class CreateReturnHandler(
    IInstallationContext installation, IPurchasingStore store, IPurchasingQueries queries, ICatalogReader catalog, IDocumentNumberAllocator numbers,
    IIdGenerator ids, IClock clock) : ICommandHandler<CreateReturnCommand, SupplierReturnDto>
{
    public async Task<Result<SupplierReturnDto>> Handle(CreateReturnCommand request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        if (await store.GetPurchaseAsync(request.PurchaseId, cancellationToken) is not { } purchase)
        {
            return PurchasingErrors.PurchaseNotFound;
        }

        if (purchase.BranchId != local.Value.BranchId)
        {
            return Error.BusinessRule("INVENTORY.WAREHOUSE_NOT_LOCAL", "La compra es de otra sucursal: la devolución la registra la sucursal que compró.");
        }

        var lines = new List<ReturnLineInput>();
        foreach (var line in request.Lines ?? [])
        {
            if (purchase.Lines.SingleOrDefault(l => l.Id == line.PurchaseLineId) is not { } purchased)
            {
                return PurchasingErrors.InvalidLine;
            }

            lines.Add(new ReturnLineInput(purchased.Id, purchased.ProductId, line.BaseQuantity, purchased.NetUnitCost, purchased.LotId));
        }

        var number = await numbers.NextForBranchAsync("SUPPLIER_RETURN", local.Value.BranchId, cancellationToken);
        var created = SupplierReturn.Create(ids.NewId(), purchase, number.Number, request.BusinessDate ?? clock.Today, request.Reason, lines, ids.NewId);
        if (created.IsFailure)
        {
            return created.Error;
        }

        store.Add(created.Value);
        return await created.Value.ToDtoAsync(catalog, queries, cancellationToken);
    }
}

/// <summary>
/// Contabiliza la devolución: descuenta lo devuelto de la compra (RN-PUR-06), salida SUPPLIER_RETURN del kardex al costo
/// de la compra y del lote original (sin dejar negativos) y asiento RETURN que reduce la cuenta por pagar.
/// </summary>
public sealed record PostReturnCommand(Guid ReturnId) : ICommand<SupplierReturnDto>;

internal sealed class PostReturnHandler(
    IPurchasingStore store, IPurchasingQueries queries, ICatalogReader catalog, IInventoryPosting posting, IActorContext actor, IAuditWriter audit,
    IIdGenerator ids, IClock clock) : ICommandHandler<PostReturnCommand, SupplierReturnDto>
{
    public async Task<Result<SupplierReturnDto>> Handle(PostReturnCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetReturnAsync(request.ReturnId, cancellationToken) is not { } supplierReturn)
        {
            return PurchasingErrors.ReturnNotFound;
        }

        if (supplierReturn.Status != SupplierReturnStatus.Draft)
        {
            return PurchasingErrors.InvalidStatus;
        }

        var purchase = (await store.GetPurchaseAsync(supplierReturn.PurchaseId, cancellationToken))!;
        foreach (var line in supplierReturn.Lines)
        {
            if (purchase.RegisterReturn(line.PurchaseLineId, line.BaseQuantity) is { IsFailure: true } exceeded)
            {
                return exceeded.Error;
            }
        }

        var posted = await posting.PostAsync(
            new InventoryPosting("SUPPLIER_RETURN", supplierReturn.Id, supplierReturn.Number, supplierReturn.BranchId, supplierReturn.BusinessDate,
                [.. supplierReturn.Lines.Select(l => new PostingLine(
                    supplierReturn.WarehouseId, l.ProductId, MovementType.SupplierReturn, l.BaseQuantity, l.UnitCost, l.Id, LotId: l.LotId))]),
            cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        var userId = actor.ActorId!.Value;
        var now = clock.UtcNow;
        supplierReturn.MarkPosted(userId, now);
        var account = await store.GetPayableByPurchaseAsync(purchase.Id, cancellationToken);
        if (account is not null
            && account.Adjust(ids.NewId(), PayableEntryType.Return, supplierReturn.CreditTotal, "SUPPLIER_RETURN", supplierReturn.Id, supplierReturn.Number, userId, now)
                is { IsFailure: true } adjusted)
        {
            return adjusted.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("purchasing", "SUPPLIER_RETURN_POSTED", nameof(SupplierReturn), supplierReturn.Id, supplierReturn.AuditLabel,
                $"Devolución {supplierReturn.Number} de la compra {purchase.Number}: costo {supplierReturn.Total:N2}, crédito esperado {supplierReturn.CreditTotal:N2}. "
                + supplierReturn.Reason,
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return await supplierReturn.ToDtoAsync(catalog, queries, cancellationToken);
    }
}

/// <summary>
/// Liquida la devolución: nota crédito (queda la reducción de la cartera), reintegro (asiento REFUND que cancela el saldo
/// a favor) o reposición (la mercancía vuelve a entrar al costo de la compra, en el lote original, y la deuda se restablece).
/// </summary>
public sealed record SettleReturnCommand(Guid ReturnId, ReturnSettlement Settlement, string Reference) : ICommand<SupplierReturnDto>;

internal sealed class SettleReturnHandler(
    IPurchasingStore store, IPurchasingQueries queries, ICatalogReader catalog, IInventoryPosting posting, IActorContext actor, IIdGenerator ids, IClock clock)
    : ICommandHandler<SettleReturnCommand, SupplierReturnDto>
{
    public async Task<Result<SupplierReturnDto>> Handle(SettleReturnCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetReturnAsync(request.ReturnId, cancellationToken) is not { } supplierReturn)
        {
            return PurchasingErrors.ReturnNotFound;
        }

        var userId = actor.ActorId!.Value;
        var now = clock.UtcNow;
        var settled = supplierReturn.Settle(request.Settlement, request.Reference, userId, now);
        if (settled.IsFailure)
        {
            return settled.Error;
        }

        var account = await store.GetPayableByPurchaseAsync(supplierReturn.PurchaseId, cancellationToken);
        if (request.Settlement == ReturnSettlement.Replacement)
        {
            var posted = await posting.PostAsync(
                new InventoryPosting("SUPPLIER_REPLACEMENT", supplierReturn.Id, supplierReturn.Number, supplierReturn.BranchId, clock.Today,
                    [.. supplierReturn.Lines.Select(l => new PostingLine(
                        supplierReturn.WarehouseId, l.ProductId, MovementType.PurchaseReceipt, l.BaseQuantity, l.UnitCost, l.Id, LotId: l.LotId))]),
                cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error;
            }
        }

        if (request.Settlement != ReturnSettlement.CreditNote && account is not null)
        {
            var type = request.Settlement == ReturnSettlement.Refund ? PayableEntryType.Refund : PayableEntryType.Replacement;
            if (account.Adjust(ids.NewId(), type, supplierReturn.CreditTotal, "SUPPLIER_RETURN_SETTLEMENT", supplierReturn.Id, supplierReturn.Number, userId, now)
                is { IsFailure: true } adjusted)
            {
                return adjusted.Error;
            }
        }

        return await supplierReturn.ToDtoAsync(catalog, queries, cancellationToken);
    }
}

public sealed record CancelReturnCommand(Guid ReturnId) : ICommand<SupplierReturnDto>;

internal sealed class CancelReturnHandler(IPurchasingStore store, IPurchasingQueries queries, ICatalogReader catalog, IClock clock)
    : ICommandHandler<CancelReturnCommand, SupplierReturnDto>
{
    public async Task<Result<SupplierReturnDto>> Handle(CancelReturnCommand request, CancellationToken cancellationToken)
    {
        if (await store.GetReturnAsync(request.ReturnId, cancellationToken) is not { } supplierReturn)
        {
            return PurchasingErrors.ReturnNotFound;
        }

        var cancelled = supplierReturn.Cancel(clock.UtcNow);
        return cancelled.IsSuccess ? await supplierReturn.ToDtoAsync(catalog, queries, cancellationToken) : cancelled.Error;
    }
}

public sealed record GetReturnQuery(Guid ReturnId) : IQuery<SupplierReturnDto>;

internal sealed class GetReturnHandler(IPurchasingStore store, IPurchasingQueries queries, ICatalogReader catalog) : IQueryHandler<GetReturnQuery, SupplierReturnDto>
{
    public async Task<Result<SupplierReturnDto>> Handle(GetReturnQuery request, CancellationToken cancellationToken) =>
        await store.GetReturnAsync(request.ReturnId, cancellationToken) is { } supplierReturn
            ? await supplierReturn.ToDtoAsync(catalog, queries, cancellationToken)
            : PurchasingErrors.ReturnNotFound;
}
