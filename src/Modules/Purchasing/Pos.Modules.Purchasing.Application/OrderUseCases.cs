using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Modules.Purchasing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Purchasing.Application;

/// <summary>Producto resuelto para una línea: su información de catálogo y el factor de la presentación.</summary>
public sealed record ResolvedProduct(CatalogProductInfo Product, Guid? PackagingId, decimal Factor, SupplierProduct? SupplierProduct);

/// <summary>
/// Resuelve productos de las líneas: por Id, por el código del proveedor o por SKU / código de barras; valida que se
/// puedan comprar (no descontinuados) y que la presentación sea del producto y se compre.
/// </summary>
public sealed class PurchaseLineResolver(ICatalogReader catalog, IPurchasingStore store)
{
    public async Task<Result<IReadOnlyList<ResolvedProduct>>> ResolveAsync(
        Guid supplierId, IReadOnlyList<(Guid? ProductId, string? Code, Guid? PackagingId)> lines, CancellationToken cancellationToken)
    {
        var supplierProducts = await store.GetSupplierProductsAsync(supplierId, cancellationToken);
        var ids = new List<Guid>();
        foreach (var (productId, code, _) in lines)
        {
            Guid? id = productId;
            if (id is null && !string.IsNullOrWhiteSpace(code))
            {
                var normalized = code.Trim().ToUpperInvariant();
                id = supplierProducts.FirstOrDefault(p => p.SupplierCode == normalized)?.ProductId
                    ?? (await catalog.FindByCodeAsync(code, cancellationToken))?.Id;
            }

            if (id is null)
            {
                return Error.NotFound("PURCHASING.PRODUCT_NOT_FOUND", $"No se encontró el producto '{code}'.");
            }

            ids.Add(id.Value);
        }

        var products = await catalog.GetProductsAsync([.. ids.Distinct()], cancellationToken);
        var packagings = (await catalog.GetPackagingsAsync([.. ids.Distinct()], cancellationToken)).ToDictionary(p => p.Id);
        var resolved = new List<ResolvedProduct>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!products.TryGetValue(ids[i], out var product) || product.Status == "DISCONTINUED")
            {
                return PurchasingErrors.ProductNotPurchasable;
            }

            var factor = 1m;
            if (lines[i].PackagingId is { } packagingId)
            {
                if (!packagings.TryGetValue(packagingId, out var packaging) || packaging.ProductId != product.Id || !packaging.IsPurchasable)
                {
                    return PurchasingErrors.ProductNotPurchasable;
                }

                factor = packaging.Factor;
            }

            resolved.Add(new ResolvedProduct(product, lines[i].PackagingId, factor, supplierProducts.FirstOrDefault(p => p.ProductId == product.Id)));
        }

        return resolved;
    }
}

/// <summary>Bodega de este nodo y proveedor que admite documentos nuevos.</summary>
public sealed class PurchasingGuards(IWarehouseDirectory warehouses, IInstallationContext installation, IPurchasingStore store)
{
    public async Task<Result<WarehouseInfo>> LocalWarehouseAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        var warehouse = await warehouses.GetAsync(warehouseId, cancellationToken);
        if (warehouse is null)
        {
            return Error.NotFound("INVENTORY.WAREHOUSE_NOT_FOUND", "La bodega no existe.");
        }

        if (warehouse.BranchId != installation.BranchId)
        {
            return Error.BusinessRule("INVENTORY.WAREHOUSE_NOT_LOCAL", "La bodega pertenece a otra sucursal: cada tienda compra para su propio inventario.");
        }

        return warehouse.IsActive ? warehouse : Error.BusinessRule("INVENTORY.WAREHOUSE_INACTIVE", "La bodega está inactiva.");
    }

    public async Task<Result<Supplier>> ActiveSupplierAsync(Guid supplierId, CancellationToken cancellationToken)
    {
        var supplier = await store.GetSupplierAsync(supplierId, cancellationToken);
        if (supplier is null)
        {
            return PurchasingErrors.SupplierNotFound;
        }

        return supplier.AcceptsNewDocuments ? supplier : PurchasingErrors.SupplierBlocked;
    }
}

public sealed record OrderLineRequest(Guid? ProductId, string? Code, Guid? PackagingId, decimal Quantity, decimal? UnitCost);

internal static class OrderMapping
{
    public static async Task<PurchaseOrderDto> ToDtoAsync(this PurchaseOrder o, ICatalogReader catalog, IPurchasingQueries queries, CancellationToken cancellationToken)
    {
        var products = await catalog.GetProductsAsync([.. o.Lines.Select(l => l.ProductId)], cancellationToken);
        var names = await queries.SupplierNamesAsync([o.SupplierId], cancellationToken);
        return new PurchaseOrderDto(
            o.Id, o.Number, o.SupplierId, names.GetValueOrDefault(o.SupplierId) ?? string.Empty, o.WarehouseId, o.OrderDate, o.ExpectedDate, o.Status.Db(), o.Notes,
            o.Total, o.ApprovedAt, o.SentAt,
            [.. o.Lines.OrderBy(l => l.LineNumber).Select(l => new OrderLineDto(
                l.Id, l.LineNumber, l.ProductId, products.GetValueOrDefault(l.ProductId)?.Sku ?? string.Empty, products.GetValueOrDefault(l.ProductId)?.Name ?? string.Empty,
                l.PackagingId, l.Factor, l.Quantity, l.BaseQuantity, l.UnitCost, l.ReceivedBaseQuantity, l.PendingBaseQuantity))]);
    }

    /// <summary>Líneas de la orden: costo digitado o, si falta, el último costo del proveedor × factor.</summary>
    public static async Task<Result<IReadOnlyList<OrderLineInput>>> BuildLinesAsync(
        PurchaseLineResolver resolver, Guid supplierId, IReadOnlyList<OrderLineRequest> lines, CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(supplierId, [.. lines.Select(l => (l.ProductId, l.Code, l.PackagingId))], cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        return Result.Success<IReadOnlyList<OrderLineInput>>([.. lines.Select((l, i) =>
        {
            var r = resolved.Value[i];
            var cost = l.UnitCost ?? decimal.Round((r.SupplierProduct?.LastCost ?? 0m) * r.Factor, 4, MidpointRounding.AwayFromZero);
            return new OrderLineInput(r.Product.Id, r.PackagingId, r.Factor, l.Quantity, cost);
        })]);
    }
}

public sealed record CreateOrderCommand(Guid SupplierId, Guid WarehouseId, DateOnly? OrderDate, DateOnly? ExpectedDate, string? Notes, IReadOnlyList<OrderLineRequest> Lines)
    : ICommand<PurchaseOrderDto>;

internal sealed class CreateOrderHandler(
    IInstallationContext installation, IPurchasingStore store, IPurchasingQueries queries, PurchasingGuards guards, PurchaseLineResolver resolver,
    ICatalogReader catalog, IDocumentNumberAllocator numbers, IIdGenerator ids, IClock clock) : ICommandHandler<CreateOrderCommand, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> Handle(CreateOrderCommand request, CancellationToken cancellationToken)
    {
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

        var lines = await OrderMapping.BuildLinesAsync(resolver, supplier.Value.Id, request.Lines ?? [], cancellationToken);
        if (lines.IsFailure)
        {
            return lines.Error;
        }

        var number = await numbers.NextForBranchAsync("PURCHASE_ORDER", local.Value.BranchId, cancellationToken);
        var order = PurchaseOrder.Create(
            ids.NewId(), local.Value.CompanyId, local.Value.BranchId, request.WarehouseId, supplier.Value.Id, number.Number, request.OrderDate ?? clock.Today,
            request.ExpectedDate, request.Notes, lines.Value, ids.NewId);
        if (order.IsFailure)
        {
            return order.Error;
        }

        store.Add(order.Value);
        return await order.Value.ToDtoAsync(catalog, queries, cancellationToken);
    }
}

public sealed record UpdateOrderCommand(Guid OrderId, DateOnly OrderDate, DateOnly? ExpectedDate, string? Notes, IReadOnlyList<OrderLineRequest> Lines)
    : ICommand<PurchaseOrderDto>;

internal sealed class UpdateOrderHandler(IPurchasingStore store, IPurchasingQueries queries, PurchaseLineResolver resolver, ICatalogReader catalog, IIdGenerator ids)
    : ICommandHandler<UpdateOrderCommand, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> Handle(UpdateOrderCommand request, CancellationToken cancellationToken)
    {
        var order = await store.GetOrderAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            return PurchasingErrors.OrderNotFound;
        }

        var lines = await OrderMapping.BuildLinesAsync(resolver, order.SupplierId, request.Lines ?? [], cancellationToken);
        if (lines.IsFailure)
        {
            return lines.Error;
        }

        var edited = order.Edit(request.OrderDate, request.ExpectedDate, request.Notes, lines.Value, ids.NewId);
        return edited.IsSuccess ? await order.ToDtoAsync(catalog, queries, cancellationToken) : edited.Error;
    }
}

public enum OrderAction
{
    Approve,
    Send,
    Close,
    Cancel,
}

/// <summary>Aprobar (purchasing.order.approve), enviar, cerrar o cancelar una orden.</summary>
public sealed record ChangeOrderStatusCommand(Guid OrderId, OrderAction Action) : ICommand<PurchaseOrderDto>;

internal sealed class ChangeOrderStatusHandler(
    IPurchasingStore store, IPurchasingQueries queries, PurchasingGuards guards, ICatalogReader catalog, IActorContext actor, IClock clock)
    : ICommandHandler<ChangeOrderStatusCommand, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> Handle(ChangeOrderStatusCommand request, CancellationToken cancellationToken)
    {
        var order = await store.GetOrderAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            return PurchasingErrors.OrderNotFound;
        }

        if (request.Action is OrderAction.Approve or OrderAction.Send && await guards.ActiveSupplierAsync(order.SupplierId, cancellationToken) is { IsFailure: true } blocked)
        {
            return blocked.Error;
        }

        var result = request.Action switch
        {
            OrderAction.Approve => order.Approve(actor.ActorId!.Value, clock.UtcNow),
            OrderAction.Send => order.MarkSent(clock.UtcNow),
            OrderAction.Close => order.Close(clock.UtcNow),
            _ => order.Cancel(clock.UtcNow),
        };
        return result.IsSuccess ? await order.ToDtoAsync(catalog, queries, cancellationToken) : result.Error;
    }
}

public sealed record GetOrderQuery(Guid OrderId) : IQuery<PurchaseOrderDto>;

internal sealed class GetOrderHandler(IPurchasingStore store, IPurchasingQueries queries, ICatalogReader catalog) : IQueryHandler<GetOrderQuery, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> Handle(GetOrderQuery request, CancellationToken cancellationToken) =>
        await store.GetOrderAsync(request.OrderId, cancellationToken) is { } order
            ? await order.ToDtoAsync(catalog, queries, cancellationToken)
            : PurchasingErrors.OrderNotFound;
}

/// <summary>Órdenes, compras, devoluciones o pagos de la sucursal (más recientes primero).</summary>
public sealed record ListDocumentsQuery(string Kind, Guid? SupplierId, string? Status, bool? RequiresSupportDocument = null)
    : IQuery<IReadOnlyList<PurchasingDocumentDto>>;

internal sealed class ListDocumentsHandler(IInstallationContext installation, IPurchasingQueries queries)
    : IQueryHandler<ListDocumentsQuery, IReadOnlyList<PurchasingDocumentDto>>
{
    public async Task<Result<IReadOnlyList<PurchasingDocumentDto>>> Handle(ListDocumentsQuery request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        return local.IsFailure
            ? local.Error
            : Result.Success(await queries.ListDocumentsAsync(
                new DocumentFilter(request.Kind, local.Value.BranchId, request.SupplierId, request.Status, request.RequiresSupportDocument), cancellationToken));
    }
}
