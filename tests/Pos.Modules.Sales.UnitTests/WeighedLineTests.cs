using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Sales.Application;
using Pos.Modules.Sales.Contracts;
using Pos.Modules.Sales.Domain;
using static Pos.Modules.Sales.UnitTests.Fx;

namespace Pos.Modules.Sales.UnitTests;

/// <summary>Fase 7-C: línea pesada en la caja (báscula conectada o peso digitado), tara del producto, unidades y tiquete.</summary>
public class WeighedLineTests
{
    private static CatalogSaleItem Papa(string unit = "KG", int tareGrams = 0, string saleMode = "WEIGHT", Guid? packaging = null) => new(
        Guid.NewGuid(), "PAPA", "Papa pastusa", "Papa", packaging, packaging is null ? null : "Bulto", 1m, unit, saleMode, "BARCODE", 1m, 3_000m, 3_000m, true,
        [], Guid.NewGuid(), null, true, true, false, true, [], IsSoldByScale: true, TareGrams: tareGrams);

    /// <summary>Lectura del agente: bruto, tara manual, trama neta de la báscula y estable.</summary>
    private static ScaleReadingInfo Reading(decimal grossKg, decimal tareKg = 0m, bool net = false, bool stable = true, bool emptied = false) =>
        new(grossKg, tareKg, net, stable, Now, emptied);

    private static Pos.SharedKernel.Results.Result<(decimal Quantity, string Source)> Scale(CatalogSaleItem item, decimal grossKg, decimal tareKg = 0m, bool net = false) =>
        AddLineHandler.WeighedQuantity(item, grossKg - tareKg, WeightSources.Scale, manualAuthorized: false, scaleLabel: false, Reading(grossKg, tareKg, net));

    [Theory]
    [InlineData("KG", 0, 0.700, 0.700)]
    [InlineData("KG", 20, 0.720, 0.700)]
    [InlineData("LB500", 0, 0.700, 1.400)]
    [InlineData("G", 0, 0.700, 700)]
    public void Peso_de_la_bascula_menos_la_tara_en_la_unidad_del_producto(string unit, int tare, decimal kg, decimal expected)
    {
        var weighed = Scale(Papa(unit, tare), kg);

        weighed.IsSuccess.ShouldBeTrue(weighed.IsFailure ? weighed.Error.Message : null);
        weighed.Value.ShouldBe((expected, "SCALE_LIVE"));
    }

    [Fact]
    public void Una_sola_tara_la_manual_o_la_de_la_bascula_reemplazan_la_del_producto()
    {
        // Bolsa del producto: 20 g. El cajero tomó la tara manual de una bandeja de 50 g: no se descuentan las dos.
        Scale(Papa(tareGrams: 20), grossKg: 0.750m, tareKg: 0.050m).Value.Quantity.ShouldBe(0.700m);

        // La báscula ya envió el peso neto (tara de la propia báscula): tampoco se descuenta la del producto.
        Scale(Papa(tareGrams: 20), grossKg: 0.700m, net: true).Value.Quantity.ShouldBe(0.700m);

        // Sin otra tara, la del producto se descuenta una vez.
        Scale(Papa(tareGrams: 20), grossKg: 0.720m).Value.Quantity.ShouldBe(0.700m);
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
    public void Sin_origen_del_peso_no_se_supone_la_bascula()
    {
        AddLineHandler.WeighedQuantity(Papa(), 1m, null, false, false, Reading(1m)).Error.Code.ShouldBe("SALES.WEIGHT_SOURCE_REQUIRED");
        AddLineHandler.WeighedQuantity(Papa(), 1m, " ", false, false, Reading(1m)).Error.Code.ShouldBe("SALES.WEIGHT_SOURCE_REQUIRED");
        AddLineHandler.WeighedQuantity(Papa(), 1m, "OTRO", false, false).Error.Code.ShouldBe("SALES.INVALID_WEIGHT_SOURCE");
    }

    [Fact]
    public void El_peso_de_bascula_exige_la_lectura_del_agente_estable_y_coherente()
    {
        AddLineHandler.WeighedQuantity(Papa(), 1m, WeightSources.Scale, false, false).Error.Code.ShouldBe("SALES.SCALE_READING_REQUIRED");
        AddLineHandler.WeighedQuantity(Papa(), 1m, WeightSources.Scale, false, false, Reading(1m, stable: false)).Error.Code.ShouldBe("SALES.SCALE_WEIGHT_UNSTABLE");
        AddLineHandler.WeighedQuantity(Papa(), 1m, WeightSources.Scale, false, false, Reading(1.5m)).Error.Code.ShouldBe("SALES.INVALID_SCALE_READING");
        AddLineHandler.WeighedQuantity(Papa(), 1m, WeightSources.Scale, false, false, Reading(0.9m, tareKg: -0.1m)).Error.Code.ShouldBe("SALES.INVALID_SCALE_READING");
    }

    [Fact]
    public void Rechaza_cero_negativo_menos_que_la_tara_y_productos_que_no_son_de_peso()
    {
        Scale(Papa(), 0m).Error.Code.ShouldBe("SALES.WEIGHT_TOO_LOW");
        Scale(Papa(), -0.2m).Error.Code.ShouldBe("SALES.WEIGHT_TOO_LOW");
        Scale(Papa(tareGrams: 30), 0.025m).Error.Message.ShouldContain("tara");
        Scale(Papa("UND", saleMode: "UNIT"), 1m).Error.Code.ShouldBe("SALES.NOT_WEIGHABLE");
        Scale(Papa(packaging: Guid.NewGuid()), 1m).Error.Code.ShouldBe("SALES.NOT_WEIGHABLE");
        AddLineHandler.WeighedQuantity(Papa(), 1m, WeightSources.Scale, false, scaleLabel: true, Reading(1m)).Error.Code.ShouldBe("SALES.NOT_WEIGHABLE");
    }

    [Fact]
    public void El_servidor_rechaza_la_misma_pesada_otra_vez_si_la_bascula_no_quedo_vacia()
    {
        var sale = NewSale();
        sale.AddLine(Input(quantity: 1.2m, allowsDecimal: true, source: "SCALE_LIVE") with { ScaleGrossKg = 1.200m, WeighedAt = Now }, true, Guid.NewGuid);

        // Carne de 1,200 kg y luego el tomate sin retirar la carne: mismo peso bruto.
        AddLineHandler.RepeatedWeight(sale, Reading(1.200m), Now.AddSeconds(20)).ShouldBeTrue();
        AddLineHandler.RepeatedWeight(sale, Reading(1.204m), Now.AddSeconds(20)).ShouldBeTrue();

        // Otro peso, la caja vio la báscula vacía entre las dos, o pasó la ventana: vale.
        AddLineHandler.RepeatedWeight(sale, Reading(0.800m), Now.AddSeconds(20)).ShouldBeFalse();
        AddLineHandler.RepeatedWeight(sale, Reading(1.200m, emptied: true), Now.AddSeconds(20)).ShouldBeFalse();
        AddLineHandler.RepeatedWeight(sale, Reading(1.200m), Now + AddLineHandler.RepeatedWeightWindow + TimeSpan.FromSeconds(1)).ShouldBeFalse();

        // Si eliminan la línea para volver a pesar, el mismo peso vale.
        sale.VoidLine(sale.Lines[0].Id, Cashier, Now);
        AddLineHandler.RepeatedWeight(sale, Reading(1.200m), Now.AddSeconds(20)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("SCALE_LIVE")]
    [InlineData("MANUAL_WEIGHT")]
    [InlineData("SCALE_WEIGHT")]
    [InlineData("SCALE_PRICE")]
    public void La_cantidad_de_una_linea_pesada_no_se_edita(string source)
    {
        var sale = NewSale();
        var line = sale.AddLine(Input(quantity: 0.7m, allowsDecimal: true, source: source), merge: true, Guid.NewGuid).Value;

        sale.ChangeQuantity(line.Id, 5m).Error.ShouldBe(SalesErrors.WeighedLineQuantity);
        line.Quantity.ShouldBe(0.7m);
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
