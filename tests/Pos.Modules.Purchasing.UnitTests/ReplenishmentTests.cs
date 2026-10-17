using Pos.Modules.Purchasing.Application;
using Pos.Modules.Purchasing.Domain;

namespace Pos.Modules.Purchasing.UnitTests;

/// <summary>Reabastecimiento: cuentas del pedido sugerido, redondeo a la presentación de compra y agrupación por proveedor.</summary>
public class ReplenishmentMathTests
{
    [Theory]
    [InlineData(5, 0, 10, 40, 35)] // Con máximo: 40 − 5.
    [InlineData(5, 12, 10, 40, 23)] // Lo pendiente por recibir se descuenta: 40 − (5 + 12).
    [InlineData(0, 0, 10, 40, 40)] // Sin existencias: hasta el máximo.
    [InlineData(-3, 0, 10, 40, 43)] // Existencia negativa (se vendió sin existencias registradas): pide más.
    [InlineData(45, 0, 10, 40, 0)] // Por encima del máximo: nada (nunca negativo).
    [InlineData(5, 50, 10, 40, 0)] // El pendiente ya cubre: nada.
    public void Con_maximo_pide_hasta_el_maximo(decimal stock, decimal pending, decimal min, decimal max, decimal expected) =>
        ReplenishmentMath.SuggestedBaseQuantity(stock, pending, min, max, null).ShouldBe(expected);

    [Fact]
    public void Sin_maximo_usa_la_cantidad_a_pedir_o_completa_el_minimo()
    {
        ReplenishmentMath.SuggestedBaseQuantity(2m, 0m, 10m, null, 24m).ShouldBe(24m);
        ReplenishmentMath.SuggestedBaseQuantity(2m, 6m, 10m, null, 24m).ShouldBe(18m);
        ReplenishmentMath.SuggestedBaseQuantity(2m, 0m, 10m, null, null).ShouldBe(8m);
        ReplenishmentMath.SuggestedBaseQuantity(-4m, 0m, 10m, null, null).ShouldBe(14m);
        ReplenishmentMath.SuggestedBaseQuantity(12m, 0m, 10m, null, null).ShouldBe(0m);
    }

    [Fact]
    public void Con_punto_de_pedido_y_sin_maximo_ni_cantidad_pide_hasta_el_punto()
    {
        // Mínimo 5, punto de pedido 8: con 3 en existencia se piden 5 (hasta el punto), no 2 (hasta el mínimo).
        ReplenishmentMath.SuggestedBaseQuantity(3m, 0m, 5m, null, null, 8m).ShouldBe(5m);
        ReplenishmentMath.SuggestedBaseQuantity(3m, 2m, 5m, null, null, 8m).ShouldBe(3m);
        // Justo en el punto da cero: por eso la ficha pide el máximo o la cantidad a pedir cuando hay punto de pedido.
        ReplenishmentMath.SuggestedBaseQuantity(8m, 0m, 5m, null, null, 8m).ShouldBe(0m);
        // Un punto menor que el mínimo no baja el objetivo; con máximo o cantidad a pedir, el punto no cambia la cuenta.
        ReplenishmentMath.SuggestedBaseQuantity(1m, 0m, 5m, null, null, 2m).ShouldBe(4m);
        ReplenishmentMath.SuggestedBaseQuantity(3m, 0m, 5m, 30m, null, 8m).ShouldBe(27m);
        ReplenishmentMath.SuggestedBaseQuantity(3m, 0m, 5m, null, 12m, 8m).ShouldBe(12m);
    }

    [Theory]
    [InlineData(30, 24, false, 2, 48)] // 30 unidades en cajas x24 → 2 cajas.
    [InlineData(24, 24, false, 1, 24)] // Justo una caja.
    [InlineData(1, 12, false, 1, 12)] // Una unidad pide una caja completa.
    [InlineData(0, 24, false, 0, 0)]
    [InlineData(7.2, 1, false, 8, 8)] // Sin presentación: unidades enteras hacia arriba.
    [InlineData(7.2501, 1, true, 7.251, 7.251)] // Por peso: 3 decimales hacia arriba.
    [InlineData(5, 0, false, 5, 5)] // Factor inválido = sin presentación.
    public void Redondea_a_la_presentacion_de_compra(decimal baseQuantity, decimal factor, bool allowsDecimal, decimal packages, decimal roundedBase)
    {
        var (p, b) = ReplenishmentMath.RoundToPackaging(baseQuantity, factor, allowsDecimal);
        p.ShouldBe(packages);
        b.ShouldBe(roundedBase);
    }

    [Fact]
    public void La_fila_del_reabastecimiento_trae_el_sugerido_en_cajas_y_oculta_el_costo_sin_permiso()
    {
        var box = Guid.NewGuid();
        var row = new ReplenishmentRow(
            Build.Warehouse, "SALA", Guid.NewGuid(), "YOG-1", "Yogurt", Guid.NewGuid(), "Lácteos", "UND", false, Quantity: 3m, MinQuantity: 10m,
            MaxQuantity: 50m, ReorderPoint: null, ReorderQuantity: null, PendingQuantity: 0m, Guid.NewGuid(), "Distribuidora", LastCost: 1_500m, null, box,
            "Caja x24", 24m);

        var dto = GetReplenishmentHandler.ToDto(row, includeCosts: false);

        dto.SuggestedQuantity.ShouldBe(2m); // 47 unidades → 2 cajas.
        dto.SuggestedBaseQuantity.ShouldBe(48m);
        dto.PackagingId.ShouldBe(box);
        dto.LastCost.ShouldBeNull();
        GetReplenishmentHandler.ToDto(row, includeCosts: true).LastCost.ShouldBe(1_500m);

        // Con más filas que el máximo se marcan como truncadas y se devuelven solo las primeras.
        var truncated = GetReplenishmentHandler.Build([row, row, row], includeCosts: false, maxRows: 2);
        truncated.Truncated.ShouldBeTrue();
        truncated.Lines.Count.ShouldBe(2);
        GetReplenishmentHandler.Build([row, row], includeCosts: false, maxRows: 2).Truncated.ShouldBeFalse();

        var unpacked = GetReplenishmentHandler.ToDto(row with { PackagingId = null, PackagingName = null, PackagingFactor = null }, includeCosts: true);
        unpacked.SuggestedQuantity.ShouldBe(47m);
        unpacked.PackagingFactor.ShouldBe(1m);
        unpacked.PackagingName.ShouldBeNull();
    }
}

public class ReplenishmentGroupingTests
{
    private static readonly Guid SupplierA = Guid.NewGuid();
    private static readonly Guid SupplierB = Guid.NewGuid();
    private static readonly Guid OtherWarehouse = Guid.NewGuid();

    private static SuggestedLine Line(Guid? supplier, Guid? product = null, decimal quantity = 2m, Guid? warehouse = null, Guid? packaging = null) =>
        new(warehouse ?? Build.Warehouse, supplier, product ?? Guid.NewGuid(), packaging, quantity, null);

    [Fact]
    public void Una_orden_por_proveedor_y_bodega()
    {
        var groups = ReplenishmentMath.GroupBySupplier([
            Line(SupplierA), Line(SupplierB), Line(SupplierA), Line(SupplierA, warehouse: OtherWarehouse), Line(SupplierB),
        ]).Value;

        groups.Count.ShouldBe(3);
        groups.Single(g => g.SupplierId == SupplierA && g.WarehouseId == Build.Warehouse).Lines.Count.ShouldBe(2);
        groups.Single(g => g.SupplierId == SupplierA && g.WarehouseId == OtherWarehouse).Lines.Count.ShouldBe(1);
        groups.Single(g => g.SupplierId == SupplierB).Lines.Count.ShouldBe(2);
    }

    [Fact]
    public void Sin_proveedor_sin_lineas_cantidad_cero_o_producto_repetido_no_se_crea_nada()
    {
        ReplenishmentMath.GroupBySupplier([]).Error.Code.ShouldBe("PURCHASING.NOTHING_SELECTED");
        ReplenishmentMath.GroupBySupplier([Line(SupplierA), Line(null)]).Error.Code.ShouldBe("PURCHASING.SUPPLIER_REQUIRED");
        ReplenishmentMath.GroupBySupplier([Line(SupplierA, quantity: 0m)]).Error.Code.ShouldBe("PURCHASING.INVALID_LINE");

        var product = Guid.NewGuid();
        ReplenishmentMath.GroupBySupplier([Line(SupplierA, product), Line(SupplierA, product)]).Error.Code.ShouldBe("PURCHASING.DUPLICATED_LINE");

        // El mismo producto para dos proveedores distintos sí se puede (son dos órdenes).
        ReplenishmentMath.GroupBySupplier([Line(SupplierA, product), Line(SupplierB, product)]).Value.Count.ShouldBe(2);
    }
}
