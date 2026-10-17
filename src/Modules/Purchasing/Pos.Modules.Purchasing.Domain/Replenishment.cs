using Pos.SharedKernel.Results;

namespace Pos.Modules.Purchasing.Domain;

/// <summary>
/// Pedido sugerido (Reabastecimiento). Cuánto pedir de un producto en una bodega:
/// <list type="bullet">
/// <item>Con máximo: máximo − (existencia + pedido pendiente).</item>
/// <item>Sin máximo y con cantidad a pedir: esa cantidad − pedido pendiente.</item>
/// <item>Sin ninguno de los dos: hasta el punto de pedido si es mayor que el mínimo, o hasta el mínimo:
/// mayor(mínimo, punto de pedido) − (existencia + pedido pendiente). Si la existencia está justo en el punto de pedido esto da cero:
/// por eso la ficha del producto pide el máximo o la cantidad a pedir cuando se usa punto de pedido.</item>
/// </list>
/// Nunca es negativo. Una existencia negativa (se vendió sin existencias registradas) aumenta el sugerido. Después se redondea hacia arriba
/// a la presentación de compra (cajas completas): 30 unidades en cajas x24 → 2 cajas (48 unidades).
/// </summary>
public static class ReplenishmentMath
{
    /// <summary>Sugerido en unidades base, sin redondear.</summary>
    public static decimal SuggestedBaseQuantity(
        decimal stock, decimal pending, decimal min, decimal? max, decimal? reorderQuantity, decimal? reorderPoint = null)
    {
        var available = stock + Math.Max(0m, pending);
        var need = max is { } top ? top - available
            : reorderQuantity is { } lot ? lot - Math.Max(0m, pending)
            : Math.Max(min, reorderPoint ?? 0m) - available;
        return Math.Max(0m, need);
    }

    /// <summary>
    /// Redondea el sugerido a la presentación de compra: devuelve cuántas presentaciones pedir y a cuántas unidades base equivalen. Sin
    /// presentación (factor 1) se redondea a unidades enteras, o a 3 decimales si el producto admite decimales (por peso).
    /// </summary>
    public static (decimal Packages, decimal BaseQuantity) RoundToPackaging(decimal baseQuantity, decimal factor, bool allowsDecimal)
    {
        if (baseQuantity <= 0m)
        {
            return (0m, 0m);
        }

        if (factor <= 0m || factor == 1m)
        {
            var units = allowsDecimal ? decimal.Ceiling(baseQuantity * 1000m) / 1000m : decimal.Ceiling(baseQuantity);
            return (units, units);
        }

        var packages = decimal.Ceiling(baseQuantity / factor);
        return (packages, packages * factor);
    }

    /// <summary>
    /// Agrupa las líneas elegidas por proveedor y bodega: cada grupo será UNA orden de compra en borrador. Todas las líneas deben tener
    /// proveedor (las "Sin proveedor" se asignan antes) y cantidad mayor que cero; un producto no se repite en la misma orden.
    /// </summary>
    public static Result<IReadOnlyList<SuggestedOrderGroup>> GroupBySupplier(IReadOnlyList<SuggestedLine> lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return PurchasingErrors.NothingSelected;
        }

        if (lines.Any(l => l.SupplierId is null))
        {
            return PurchasingErrors.SupplierRequiredForSuggestion;
        }

        if (lines.Any(l => l.Quantity <= 0m))
        {
            return PurchasingErrors.InvalidLine;
        }

        var groups = lines
            .GroupBy(l => (Supplier: l.SupplierId!.Value, l.WarehouseId))
            .Select(g => new SuggestedOrderGroup(g.Key.Supplier, g.Key.WarehouseId, [.. g]))
            .ToList();
        if (groups.Any(g => g.Lines.GroupBy(l => (l.ProductId, l.PackagingId)).Any(d => d.Count() > 1)))
        {
            return PurchasingErrors.DuplicatedLine;
        }

        return groups;
    }
}

/// <summary>Línea elegida en la pantalla de Reabastecimiento: cantidad en la presentación indicada (o en unidades base sin presentación).</summary>
public sealed record SuggestedLine(Guid WarehouseId, Guid? SupplierId, Guid ProductId, Guid? PackagingId, decimal Quantity, decimal? UnitCost);

/// <summary>Una orden de compra por crear: proveedor, bodega y sus líneas.</summary>
public sealed record SuggestedOrderGroup(Guid SupplierId, Guid WarehouseId, IReadOnlyList<SuggestedLine> Lines);
