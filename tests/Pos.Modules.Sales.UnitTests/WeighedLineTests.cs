using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Sales.Application;
using Pos.Modules.Sales.Contracts;
using Pos.Modules.Sales.Domain;
using Pos.SharedKernel.Measurement;
using static Pos.Modules.Sales.UnitTests.Fx;

namespace Pos.Modules.Sales.UnitTests;

/// <summary>Fase 7-C: línea pesada en la caja (báscula conectada o peso digitado), tara del producto, unidades y tiquete.</summary>
public class WeighedLineTests
{
    private static CatalogSaleItem Papa(string unit = "KG", int tareGrams = 0, string saleMode = "WEIGHT", Guid? packaging = null) => new(
        Guid.NewGuid(), "PAPA", "Papa pastusa", "Papa", packaging, packaging is null ? null : "Bulto", 1m, unit, saleMode, "BARCODE", 1m, 3_000m, 3_000m, true,
        [], Guid.NewGuid(), null, true, true, false, true, [], IsSoldByScale: true, TareGrams: tareGrams);

    private static readonly Guid Caja = Guid.NewGuid();
    private static readonly Guid Boot = Guid.NewGuid();
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>Lectura del agente (ya verificada): bruto, tara manual, trama neta de la báscula, estable, orden en el agente y última vacía.</summary>
    private static ScaleReadingPayload Reading(
        decimal grossKg, decimal tareKg = 0m, bool net = false, bool stable = true, DateTimeOffset? at = null, long seq = 1, long? lastEmptySeq = null,
        Guid? boot = null, long bootCounter = 5) =>
        new(Caja, Guid.NewGuid(), at ?? Now, boot ?? Boot, bootCounter, seq, grossKg, tareKg, grossKg - tareKg, stable, net, stable ? "OK" : "UNSTABLE", lastEmptySeq, true);

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
        AddLineHandler.WeighedQuantity(Papa(), 1m, WeightSources.Scale, false, false, Reading(1m) with { Status = "ZERO" }).Error.Code.ShouldBe("SALES.SCALE_WEIGHT_UNSTABLE");
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
    public void Retorno_a_cero_en_el_servidor_exige_ver_la_bascula_vacia_despues_de_cada_pesada()
    {
        var interlock = ScaleInterlock.Start(Caja, Company, Now);
        var line = Guid.NewGuid();
        interlock.Check(Reading(1.2m, seq: 10), Now, Window).IsSuccess.ShouldBeTrue();
        interlock.Accept(Reading(1.2m, seq: 10), Guid.NewGuid(), line, Now);

        // D1: sin ver la báscula vacía se rechaza, pese lo mismo o pese otra cosa (el tomate encima de la carne).
        interlock.Check(Reading(1.2m, seq: 11), Now, Window).Error.ShouldBe(SalesErrors.ScaleNotEmptied);
        interlock.Check(Reading(1.8m, seq: 11), Now, Window).Error.ShouldBe(SalesErrors.ScaleNotEmptied);
        interlock.Check(Reading(0.8m, seq: 12, lastEmptySeq: 9), Now, Window).Error.ShouldBe(SalesErrors.ScaleNotEmptied);

        // Vacía después de la pesada (aunque fuera un paso rápido por cero): vale, también con el mismo peso.
        interlock.Check(Reading(1.2m, seq: 13, lastEmptySeq: 11), Now, Window).IsSuccess.ShouldBeTrue();

        // Una lectura anterior (o la misma) a la aceptada en el agente no vale.
        interlock.Check(Reading(1.2m, seq: 10, lastEmptySeq: 11), Now, Window).Error.ShouldBe(SalesErrors.ScaleReadingStale);
    }

    [Fact]
    public void La_hora_solo_se_compara_con_el_servidor_y_un_salto_del_reloj_del_agente_no_traba_la_caja()
    {
        var interlock = ScaleInterlock.Start(Caja, Company, Now);
        interlock.Accept(Reading(1.2m, at: Now, seq: 10), Guid.NewGuid(), Guid.NewGuid(), Now);

        // BB4: vieja o del futuro frente al reloj del servidor (⚙️ 5 min): no vale.
        interlock.Check(Reading(0.5m, at: Now.AddMinutes(-6), seq: 12, lastEmptySeq: 11), Now, Window).Error.ShouldBe(SalesErrors.ScaleClockSkew);
        interlock.Check(Reading(0.5m, at: Now.AddMinutes(6), seq: 12, lastEmptySeq: 11), Now, Window).Error.ShouldBe(SalesErrors.ScaleClockSkew);

        // El reloj del agente se atrasó 3 minutos (dentro de la ventana): el orden lo da la secuencia, la caja sigue pesando.
        interlock.Check(Reading(0.5m, at: Now.AddMinutes(-3), seq: 12, lastEmptySeq: 11), Now, Window).IsSuccess.ShouldBeTrue();

        // El agente se reinició (otro arranque): basta que haya visto la báscula vacía desde que arrancó.
        var reboot = Guid.NewGuid();
        interlock.Check(Reading(0.5m, seq: 1, boot: reboot), Now, Window).Error.ShouldBe(SalesErrors.ScaleNotEmptied);
        interlock.Check(Reading(0.5m, seq: 3, lastEmptySeq: 2, boot: reboot), Now, Window).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Una_lectura_de_un_arranque_anterior_del_agente_nunca_vale()
    {
        // BK2: R1 y R2 del arranque A; R1 se acepta. El agente se reinicia (arranque B) y se acepta una pesada de B. R2 (de A) ya no vale,
        // aunque diga que vio la báscula vacía y su hora esté dentro de la ventana.
        var bootA = Guid.NewGuid();
        var bootB = Guid.NewGuid();
        var interlock = ScaleInterlock.Start(Caja, Company, Now);
        var r1 = Reading(1.0m, seq: 1, boot: bootA, bootCounter: 7);
        var r2 = Reading(0.7m, seq: 3, lastEmptySeq: 2, boot: bootA, bootCounter: 7);
        interlock.Accept(r1, Guid.NewGuid(), Guid.NewGuid(), Now);

        var fromB = Reading(0.5m, seq: 2, lastEmptySeq: 1, boot: bootB, bootCounter: 8);
        interlock.Check(fromB, Now, Window).IsSuccess.ShouldBeTrue();
        interlock.Accept(fromB, Guid.NewGuid(), Guid.NewGuid(), Now);
        interlock.RetiredBootIds.ShouldBe([bootA]);

        interlock.Check(r2, Now, Window).Error.ShouldBe(SalesErrors.ScaleReadingStale);
    }

    [Fact]
    public void Un_arranque_anterior_sin_pesadas_aceptadas_tampoco_vale_por_su_numero()
    {
        // BQ3: el arranque 6 nunca tuvo una pesada aceptada (no está entre los retirados), pero su número es menor que el del arranque 8
        // ya aceptado: sus lecturas nunca valen.
        var interlock = ScaleInterlock.Start(Caja, Company, Now);
        interlock.Accept(Reading(1.0m, seq: 1, boot: Guid.NewGuid(), bootCounter: 8), Guid.NewGuid(), Guid.NewGuid(), Now);
        interlock.Reset(Now);

        interlock.Check(Reading(0.7m, seq: 4, lastEmptySeq: 3, boot: Guid.NewGuid(), bootCounter: 6), Now, Window).Error.ShouldBe(SalesErrors.ScaleReadingStale);
        interlock.Check(Reading(0.7m, seq: 4, lastEmptySeq: 3, boot: Guid.NewGuid(), bootCounter: 9), Now, Window).IsSuccess.ShouldBeTrue();
        interlock.LastBootCounter.ShouldBe(8);
    }

    [Fact]
    public void Eliminar_la_ultima_pesada_o_reiniciar_la_bascula_libera_el_retorno_a_cero()
    {
        var interlock = ScaleInterlock.Start(Caja, Company, Now);
        var line = Guid.NewGuid();
        interlock.Accept(Reading(1.2m, seq: 1), Guid.NewGuid(), line, Now);

        interlock.Release(Guid.NewGuid(), Now).ShouldBeFalse();
        interlock.AwaitingEmpty.ShouldBeTrue();
        interlock.Release(line, Now).ShouldBeTrue();
        interlock.Check(Reading(1.2m, seq: 2), Now, Window).IsSuccess.ShouldBeTrue();

        // BB5: "Reiniciar báscula" libera aunque la última pesada no se elimine.
        interlock.Accept(Reading(1.2m, seq: 2), Guid.NewGuid(), Guid.NewGuid(), Now);
        interlock.Reset(Now).ShouldBeTrue();
        interlock.Check(Reading(1.2m, seq: 3), Now, Window).IsSuccess.ShouldBeTrue();

        // BB10: la limpieza de lecturas viejas va una vez al día.
        interlock.PurgeDue(Now).ShouldBeTrue();
        interlock.PurgeDue(Now.AddHours(5)).ShouldBeFalse();
        interlock.PurgeDue(Now.AddDays(1)).ShouldBeTrue();
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
