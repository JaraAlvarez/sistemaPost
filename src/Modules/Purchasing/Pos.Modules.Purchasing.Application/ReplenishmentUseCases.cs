using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Modules.Purchasing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Purchasing.Application;

// ─────────────────────── Reabastecimiento: pedido sugerido y órdenes de compra por proveedor ───────────────────────

/// <summary>Productos bajo el mínimo con el pedido sugerido, filtrables por bodega, categoría (y sus subcategorías) y proveedor.</summary>
public sealed record GetReplenishmentQuery(Guid? WarehouseId, Guid? CategoryId, Guid? SupplierId) : IQuery<IReadOnlyList<ReplenishmentLineDto>>;

internal sealed class GetReplenishmentHandler(IInstallationContext installation, IPurchasingQueries queries, IPermissionChecker permissions)
    : IQueryHandler<GetReplenishmentQuery, IReadOnlyList<ReplenishmentLineDto>>
{
    public async Task<Result<IReadOnlyList<ReplenishmentLineDto>>> Handle(GetReplenishmentQuery request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        var costs = await permissions.HasPermissionAsync(InventoryPermissions.CostView, cancellationToken: cancellationToken);
        var rows = await queries.ListReplenishmentAsync(
            new ReplenishmentFilter(local.Value.BranchId, request.WarehouseId, request.CategoryId, request.SupplierId), cancellationToken);
        return rows.Select(r => ToDto(r, costs)).ToList();
    }

    internal static ReplenishmentLineDto ToDto(ReplenishmentRow r, bool includeCosts)
    {
        var factor = r.PackagingId is not null && r.PackagingFactor is > 0m ? r.PackagingFactor.Value : 1m;
        var baseQuantity = ReplenishmentMath.SuggestedBaseQuantity(r.Quantity, r.PendingQuantity, r.MinQuantity, r.MaxQuantity, r.ReorderQuantity);
        var (packages, roundedBase) = ReplenishmentMath.RoundToPackaging(baseQuantity, factor, r.AllowsDecimalQuantity);
        return new ReplenishmentLineDto(
            r.WarehouseId, r.WarehouseCode, r.ProductId, r.Sku, r.ProductName, r.CategoryId, r.CategoryName, r.BaseUnitCode, r.AllowsDecimalQuantity,
            r.Quantity, r.MinQuantity, r.MaxQuantity, r.ReorderPoint, r.PendingQuantity, roundedBase, r.SupplierId, r.SupplierName,
            includeCosts ? r.LastCost : null, r.LastPurchaseAt, factor == 1m ? null : r.PackagingId, factor == 1m ? null : r.PackagingName, factor, packages);
    }
}

/// <summary>
/// "Crear órdenes de compra" desde el pedido sugerido: agrupa las líneas elegidas por proveedor y bodega y crea una orden en BORRADOR por
/// grupo con el mismo caso de uso de "Nueva orden" (mismas validaciones: proveedor activo, bodega de la sucursal, productos que se compran,
/// costo = último costo del proveedor × presentación si no se indica). Todo o nada: si una orden falla, no se crea ninguna.
/// </summary>
public sealed record CreateSuggestedOrdersCommand(IReadOnlyList<SuggestedOrderLineRequest> Lines, DateOnly? ExpectedDate = null, string? Notes = null)
    : ICommand<IReadOnlyList<SuggestedOrderDto>>;

internal sealed class CreateSuggestedOrdersHandler(IDispatcher sender, IAuditWriter audit, IIdGenerator ids)
    : ICommandHandler<CreateSuggestedOrdersCommand, IReadOnlyList<SuggestedOrderDto>>
{
    public const string DefaultNotes = "Creada desde el pedido sugerido (Reabastecimiento).";

    public async Task<Result<IReadOnlyList<SuggestedOrderDto>>> Handle(CreateSuggestedOrdersCommand request, CancellationToken cancellationToken)
    {
        var groups = ReplenishmentMath.GroupBySupplier(
            [.. (request.Lines ?? []).Select(l => new SuggestedLine(l.WarehouseId, l.SupplierId, l.ProductId, l.PackagingId, l.Quantity, l.UnitCost))]);
        if (groups.IsFailure)
        {
            return groups.Error;
        }

        var notes = string.IsNullOrWhiteSpace(request.Notes) ? DefaultNotes : request.Notes.Trim();
        var created = new List<SuggestedOrderDto>();
        foreach (var group in groups.Value)
        {
            var order = await sender.Send(
                new CreateOrderCommand(group.SupplierId, group.WarehouseId, null, request.ExpectedDate, notes,
                    [.. group.Lines.Select(l => new OrderLineRequest(l.ProductId, null, l.PackagingId, l.Quantity, l.UnitCost))]),
                cancellationToken);
            if (order.IsFailure)
            {
                return order.Error;
            }

            created.Add(new SuggestedOrderDto(
                order.Value.Id, order.Value.Number, order.Value.SupplierId, order.Value.SupplierName, order.Value.WarehouseId, order.Value.Lines.Count,
                order.Value.Total));
        }

        await audit.WriteAsync(
            new AuditEntry("purchasing", "PURCHASE_ORDERS_SUGGESTED", "PurchaseOrder", ids.NewId(), "Pedido sugerido",
                $"Se crearon {created.Count} órdenes de compra en borrador desde el pedido sugerido: "
                + string.Join(", ", created.Select(o => $"{o.Number} ({o.SupplierName}, {o.LineCount} productos)")) + ".",
                NewValues: new Dictionary<string, object?>
                {
                    ["orders"] = created.Select(o => o.Number).ToArray(),
                    ["lines"] = created.Sum(o => o.LineCount),
                    ["total"] = created.Sum(o => o.Total),
                }),
            cancellationToken);
        return created;
    }
}
