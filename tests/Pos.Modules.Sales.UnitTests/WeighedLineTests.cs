using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Sales.Application;
using Pos.Modules.Sales.Contracts;
using static Pos.Modules.Sales.UnitTests.Fx;

namespace Pos.Modules.Sales.UnitTests;

/// <summary>Fase 7-C: línea pesada en la caja (báscula conectada o peso digitado), tara del producto, unidades y tiquete.</summary>
public class WeighedLineTests
{
    private static CatalogSaleItem Papa(string unit = "KG", int tareGrams = 0, string saleMode = "WEIGHT", Guid? packaging = null) => new(
        Guid.NewGuid(), "PAPA", "Papa pastusa", "Papa", packaging, packaging is null ? null : "Bulto", 1m, unit, saleMode, "BARCODE", 1m, 3_000m, 3_000m, true,
        [], Guid.NewGuid(), null, true, true, false, true, [], IsSoldByScale: true, TareGrams: tareGrams);

    [Theory]
    [InlineData("KG", 0, 0.700, 0.700)]
    [InlineData("KG", 20, 0.720, 0.700)]
    [InlineData("LB500", 0, 0.700, 1.400)]
    [InlineData("G", 0, 0.700, 700)]
    public void Peso_de_la_bascula_menos_la_tara_en_la_unidad_del_producto(string unit, int tare, decimal kg, decimal expected)
    {
        var weighed = AddLineHandler.WeighedQuantity(Papa(unit, tare), kg, WeightSources.Scale, manualAuthorized: false, scaleLabel: false);

        weighed.IsSuccess.ShouldBeTrue(weighed.IsFailure ? weighed.Error.Message : null);
        weighed.Value.ShouldBe((expected, "SCALE_LIVE"));
    }

    [Fact]
    public void El_peso_digitado_exige_la_ruta_con_permiso_y_no_descuenta_tara()
    {
        AddLineHandler.WeighedQuantity(Papa(tareGrams: 20), 0.5m, WeightSources.Manual, manualAuthorized: false, scaleLabel: false)
            .Error.Code.ShouldBe("SALES.MANUAL_WEIGHT_ROUTE");

        AddLineHandler.WeighedQuantity(Papa(tareGrams: 20), 0.5m, WeightSources.Manual, manualAuthorized: true, scaleLabel: false)
            .Value.ShouldBe((0.5m, "MANUAL_WEIGHT"));
    }

    [Fact]
    public void Rechaza_cero_negativo_menos_que_la_tara_y_productos_que_no_son_de_peso()
    {
        AddLineHandler.WeighedQuantity(Papa(), 0m, null, false, false).Error.Code.ShouldBe("SALES.WEIGHT_TOO_LOW");
        AddLineHandler.WeighedQuantity(Papa(), -0.2m, null, false, false).Error.Code.ShouldBe("SALES.WEIGHT_TOO_LOW");
        AddLineHandler.WeighedQuantity(Papa(tareGrams: 30), 0.025m, null, false, false).Error.Message.ShouldContain("tara");
        AddLineHandler.WeighedQuantity(Papa("UND", saleMode: "UNIT"), 1m, null, false, false).Error.Code.ShouldBe("SALES.NOT_WEIGHABLE");
        AddLineHandler.WeighedQuantity(Papa(packaging: Guid.NewGuid()), 1m, null, false, false).Error.Code.ShouldBe("SALES.NOT_WEIGHABLE");
        AddLineHandler.WeighedQuantity(Papa(), 1m, null, false, scaleLabel: true).Error.Code.ShouldBe("SALES.NOT_WEIGHABLE");
        AddLineHandler.WeighedQuantity(Papa(), 1m, "OTRO", false, false).Error.Code.ShouldBe("SALES.INVALID_WEIGHT_SOURCE");
    }

    [Fact]
    public void Cada_pesada_queda_en_su_linea_aunque_se_junten_los_productos_iguales()
    {
        var sale = NewSale();
        var first = sale.AddLine(Input(quantity: 0.7m, allowsDecimal: true, source: "SCALE_LIVE"), merge: true, Guid.NewGuid).Value;
        var second = sale.AddLine(Input(quantity: 0.5m, allowsDecimal: true, source: "SCALE_LIVE"), merge: true, Guid.NewGuid).Value;
        var typed = sale.AddLine(Input(quantity: 0.4m, allowsDecimal: true, source: "MANUAL_WEIGHT"), merge: true, Guid.NewGuid).Value;

        second.ShouldNotBeSameAs(first);
        typed.ShouldNotBeSameAs(second);
        sale.Lines.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData("SCALE_LIVE", WeightSources.Scale)]
    [InlineData("SCALE_WEIGHT", WeightSources.Label)]
    [InlineData("SCALE_PRICE", WeightSources.Label)]
    [InlineData("MANUAL_WEIGHT", WeightSources.Manual)]
    [InlineData("BARCODE", null)]
    public void Origen_del_peso_de_cada_linea(string source, string? expected) => WeightSources.FromLineSource(source).ShouldBe(expected);

    [Fact]
    public void El_tiquete_muestra_el_peso_y_el_precio_por_kilo()
    {
        SaleTicketBuilder.TicketLineDetail(0.7m, 3_000m, "KG", null, "SCALE_LIVE").ShouldBe("  0,700 kg x $3.000/kg");
        SaleTicketBuilder.TicketLineDetail(1.4m, 1_500m, "LB500", null, "MANUAL_WEIGHT").ShouldBe("  1,400 lb x $1.500/lb (digitado)");
        SaleTicketBuilder.TicketLineDetail(2m, 2_900m, "UND", null, "BARCODE").ShouldBe("  2 UND x 2.900");
    }
}
