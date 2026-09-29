using Pos.Modules.Sales.Domain;
using static Pos.Modules.Sales.UnitTests.Fx;

namespace Pos.Modules.Sales.UnitTests;

public class SaleCalculatorTests
{
    private static readonly Guid A = K(1);
    private static readonly Guid B = K(2);
    private static readonly Guid C = K(3);
    private static readonly Guid Categoria = K(50);
    private static readonly Guid OtraCategoria = K(51);
    private static readonly Guid Marca = K(60);
    private static readonly Guid Sixpack = K(70);
    private static readonly Guid Unidad = K(71);

    private static PromotionRule Rule(
        PromotionKind kind, PromotionTarget[] targets, int? buy = null, int? pay = null, decimal? price = null, decimal? percent = null,
        decimal? min = null, int? max = null, string? ticket = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), $"Promo {kind}", kind, targets, buy, pay, price, percent, min, max, ticket);

    private static PromotionTarget Product(Guid product, Guid? packaging = null, decimal quantity = 1m) => new(product, packaging, Quantity: quantity);

    private static PromotionTarget Category(params Guid[] categories) => new(CategoryIds: categories.ToHashSet());

    // ─────────────────────────────── Impuestos y redondeo ───────────────────────────────

    [Fact]
    public void Precio_con_IVA_incluido_el_total_es_el_precio_exhibido()
    {
        var sale = SaleCalculator.Calculate([Line(1, 11_900m, taxes: [Iva19])], []);

        var line = sale.Lines.Single();
        line.Gross.ShouldBe(11_900m);
        line.Base.ShouldBe(10_000m);
        line.TaxTotal.ShouldBe(1_900m);
        line.Total.ShouldBe(11_900m);
        line.Net.ShouldBe(11_900m);
        line.PromotionId.ShouldBeNull();
        var tax = line.Taxes.Single();
        (tax.Code, tax.Rate, tax.FixedAmount, tax.Base, tax.Amount).ShouldBe(("IVA19", (decimal?)19m, (decimal?)null, 10_000m, 1_900m));
        (sale.Gross, sale.PromotionTotal, sale.DiscountTotal, sale.Subtotal, sale.TaxTotal, sale.Total).ShouldBe((11_900m, 0m, 0m, 10_000m, 1_900m, 11_900m));
    }

    [Fact]
    public void Precio_sin_IVA_el_impuesto_se_suma()
    {
        var line = SaleCalculator.Calculate([Line(2, 10_000m, includesTax: false, taxes: [Iva19])], []).Lines.Single();

        line.Base.ShouldBe(20_000m);
        line.TaxTotal.ShouldBe(3_800m);
        line.Total.ShouldBe(23_800m);
    }

    [Fact]
    public void Impuesto_fijo_por_unidad_base_y_porcentual_con_precio_incluido()
    {
        var line = SaleCalculator.Calculate([Line(3, 1_000m, taxes: [Iva19, Bolsa])], []).Lines.Single();

        // (3.000 − 3 × 66) / 1,19 = 2.354,62 → IVA 447,38.
        line.TaxTotal.ShouldBe(645.38m);
        line.Base.ShouldBe(2_354.62m);
        line.Total.ShouldBe(3_000m);
        line.Taxes.Count.ShouldBe(2);
        line.Taxes[0].ShouldBe(new PricedTax(Iva19.TaxId, "IVA19", "IVA", 19m, null, 2_354.62m, 447.38m));
        line.Taxes[1].ShouldBe(new PricedTax(Bolsa.TaxId, "INC_BOLSA", "INC_BOLSAS", null, 66m, 3m, 198m));
    }

    [Fact]
    public void Impuesto_fijo_con_presentacion_usa_las_unidades_base()
    {
        var line = SaleCalculator.Calculate([Line(2, 6_000m, packaging: Sixpack, factor: 6m, includesTax: false, taxes: [Bolsa])], []).Lines.Single();

        line.TaxTotal.ShouldBe(792m);
        line.Base.ShouldBe(12_000m);
        line.Total.ShouldBe(12_792m);
    }

    [Fact]
    public void Un_impuesto_sin_tarifa_ni_valor_se_ignora()
    {
        var line = SaleCalculator.Calculate([Line(1, 1_000m, taxes: [new PricingTax(K(999), "EXCL", "EXCLUIDO", null, null)])], []).Lines.Single();

        line.Taxes.ShouldBeEmpty();
        (line.Base, line.TaxTotal, line.Total).ShouldBe((1_000m, 0m, 1_000m));
    }

    [Fact]
    public void Peso_con_fracciones_redondea_a_dos_decimales_lejos_de_cero()
    {
        // 0,125 kg × 4.999 = 624,875 → 624,88; con IVA 5 % incluido la base absorbe el redondeo.
        var line = SaleCalculator.Calculate([Line(0.125m, 4_999m, taxes: [Iva5])], []).Lines.Single();

        line.Gross.ShouldBe(624.88m);
        line.TaxTotal.ShouldBe(29.76m);
        line.Base.ShouldBe(595.12m);
        line.Total.ShouldBe(624.88m);
    }

    // ─────────────────────────────── Promociones ───────────────────────────────

    [Fact]
    public void Lleve_3_pague_2_el_de_menor_precio_sale_gratis()
    {
        var rule = Rule(PromotionKind.MultiBuy, [Product(A)], buy: 3, pay: 2);
        var line = SaleCalculator.Calculate([Line(3, 1_000m, product: A)], [rule]).Lines.Single();

        line.PromotionId.ShouldBe(rule.Id);
        line.PromotionName.ShouldBe("Promo MultiBuy");
        line.PromotionDiscount.ShouldBe(1_000m);
        line.Total.ShouldBe(2_000m);
    }

    [Fact]
    public void Lleve_3_pague_2_mezclando_productos_de_la_categoria()
    {
        var rule = Rule(PromotionKind.MultiBuy, [Category(Categoria)], buy: 3, pay: 2);
        var caro = Line(2, 1_200m, category: Categoria);
        var barato = Line(1, 1_000m, category: Categoria);

        var sale = SaleCalculator.Calculate([caro, barato], [rule]);

        var byKey = sale.Lines.ToDictionary(l => l.Key);
        byKey[barato.Key].PromotionDiscount.ShouldBe(1_000m);
        byKey[caro.Key].PromotionDiscount.ShouldBe(0m);
        byKey[caro.Key].PromotionId.ShouldBe(rule.Id);
        sale.PromotionTotal.ShouldBe(1_000m);
        sale.Total.ShouldBe(2_400m);
    }

    [Fact]
    public void Lleve_3_pague_2_por_grupos_completos_y_con_tope_de_aplicaciones()
    {
        SaleCalculator.Calculate([Line(7, 1_000m, product: A)], [Rule(PromotionKind.MultiBuy, [Product(A)], buy: 3, pay: 2)])
            .PromotionTotal.ShouldBe(2_000m);
        SaleCalculator.Calculate([Line(7, 1_000m, product: A)], [Rule(PromotionKind.MultiBuy, [Product(A)], buy: 3, pay: 2, max: 1)])
            .PromotionTotal.ShouldBe(1_000m);
    }

    [Fact]
    public void Lleve_N_pague_M_no_aplica_a_peso_ni_con_regla_invalida()
    {
        SaleCalculator.Calculate([Line(3.5m, 1_000m, product: A)], [Rule(PromotionKind.MultiBuy, [Product(A)], buy: 3, pay: 2)])
            .PromotionTotal.ShouldBe(0m);
        SaleCalculator.Calculate([Line(3, 1_000m, product: A)], [Rule(PromotionKind.MultiBuy, [Product(A)], buy: 2, pay: 2)])
            .PromotionTotal.ShouldBe(0m);
        SaleCalculator.Calculate([Line(3, 1_000m, product: A)], [Rule(PromotionKind.MultiBuy, [Product(A)], pay: 2)])
            .PromotionTotal.ShouldBe(0m);
        SaleCalculator.Calculate([Line(2, 1_000m, product: A)], [Rule(PromotionKind.MultiBuy, [Product(A)], buy: 3, pay: 2)])
            .Lines.Single().PromotionId.ShouldBeNull();
    }

    [Fact]
    public void Precio_especial_por_unidad_base_o_por_presentacion()
    {
        // Sin presentación en la regla: el precio es por unidad base.
        SaleCalculator.Calculate([Line(2, 5_000m, product: A)], [Rule(PromotionKind.SpecialPrice, [Product(A)], price: 4_000m)])
            .PromotionTotal.ShouldBe(2_000m);
        SaleCalculator.Calculate(
                [Line(1, 30_000m, product: A, packaging: Sixpack, factor: 6m)], [Rule(PromotionKind.SpecialPrice, [Product(A)], price: 4_800m)])
            .PromotionTotal.ShouldBe(1_200m);

        // Con presentación: el precio es por esa presentación y no aplica a otra.
        var porSixpack = Rule(PromotionKind.SpecialPrice, [Product(A, Sixpack)], price: 27_000m);
        SaleCalculator.Calculate([Line(1, 30_000m, product: A, packaging: Sixpack, factor: 6m)], [porSixpack]).PromotionTotal.ShouldBe(3_000m);
        SaleCalculator.Calculate([Line(1, 5_000m, product: A, packaging: Unidad)], [porSixpack]).PromotionTotal.ShouldBe(0m);
    }

    [Fact]
    public void Precio_especial_mayor_que_el_de_lista_o_sin_precio_no_descuenta()
    {
        SaleCalculator.Calculate([Line(1, 5_000m, product: A)], [Rule(PromotionKind.SpecialPrice, [Product(A)], price: 6_000m)])
            .Lines.Single().PromotionId.ShouldBeNull();
        SaleCalculator.Calculate([Line(1, 5_000m, product: A)], [Rule(PromotionKind.SpecialPrice, [Product(A)])])
            .PromotionTotal.ShouldBe(0m);
    }

    [Fact]
    public void Porcentaje_por_categoria_con_texto_del_tiquete()
    {
        var rule = Rule(PromotionKind.PercentOff, [Category(Categoria)], percent: 10m, ticket: "Semana del aseo -10 %");
        var aseo = Line(1, 12_345m, category: Categoria);
        var otra = Line(1, 5_000m, category: OtraCategoria);

        var sale = SaleCalculator.Calculate([aseo, otra], [rule]);

        var byKey = sale.Lines.ToDictionary(l => l.Key);
        byKey[aseo.Key].PromotionDiscount.ShouldBe(1_234.50m);
        byKey[aseo.Key].PromotionName.ShouldBe("Semana del aseo -10 %");
        byKey[otra.Key].PromotionId.ShouldBeNull();
        SaleCalculator.Calculate([aseo], [Rule(PromotionKind.PercentOff, [Category(Categoria)])]).PromotionTotal.ShouldBe(0m);
    }

    [Fact]
    public void Precio_por_cantidad_desde_un_minimo_de_unidades_base()
    {
        var rule = Rule(PromotionKind.QuantityPrice, [Product(A)], price: 900m, min: 3m);

        SaleCalculator.Calculate([Line(2, 1_000m, product: A)], [rule]).PromotionTotal.ShouldBe(0m);
        SaleCalculator.Calculate([Line(3, 1_000m, product: A)], [rule]).PromotionTotal.ShouldBe(300m);
        SaleCalculator.Calculate([Line(1, 6_000m, product: A, packaging: Sixpack, factor: 6m)], [rule]).PromotionTotal.ShouldBe(600m);
        SaleCalculator.Calculate([Line(10, 1_000m, product: A)], [Rule(PromotionKind.QuantityPrice, [Product(A)], price: 900m)])
            .PromotionTotal.ShouldBe(0m);
    }

    [Fact]
    public void Combo_a_precio_fijo_reparte_el_descuento_entre_los_componentes()
    {
        var rule = Rule(PromotionKind.Combo, [Product(A), Product(B)], price: 5_000m);
        var arroz = Line(2, 3_000m, product: A);
        var queso = Line(1, 2_500m, product: B);

        var sale = SaleCalculator.Calculate([arroz, queso], [rule]);

        var byKey = sale.Lines.ToDictionary(l => l.Key);
        byKey[arroz.Key].PromotionDiscount.ShouldBe(272.73m);
        byKey[queso.Key].PromotionDiscount.ShouldBe(227.27m);
        byKey[arroz.Key].PromotionId.ShouldBe(rule.Id);
        sale.PromotionTotal.ShouldBe(500m);
        sale.Total.ShouldBe(8_000m);
    }

    [Fact]
    public void Combo_con_tope_y_componente_con_cantidad()
    {
        var dosPorUno = Rule(PromotionKind.Combo, [Product(A, quantity: 2m), Product(B)], price: 7_000m);
        SaleCalculator.Calculate([Line(4, 3_000m, product: A), Line(2, 2_500m, product: B)], [dosPorUno]).PromotionTotal.ShouldBe(3_000m);

        var conTope = Rule(PromotionKind.Combo, [Product(A, quantity: 2m), Product(B)], price: 7_000m, max: 1);
        SaleCalculator.Calculate([Line(4, 3_000m, product: A), Line(2, 2_500m, product: B)], [conTope]).PromotionTotal.ShouldBe(1_500m);
    }

    [Fact]
    public void Combo_incompleto_caro_o_mal_definido_no_aplica()
    {
        var lines = new[] { Line(1, 3_000m, product: A), Line(1, 2_500m, product: B) };

        SaleCalculator.Calculate([lines[0]], [Rule(PromotionKind.Combo, [Product(A), Product(B)], price: 5_000m)]).PromotionTotal.ShouldBe(0m);
        SaleCalculator.Calculate(lines, [Rule(PromotionKind.Combo, [Product(A), Product(B)], price: 5_500m)]).PromotionTotal.ShouldBe(0m);
        SaleCalculator.Calculate(lines, [Rule(PromotionKind.Combo, [], price: 1_000m)]).PromotionTotal.ShouldBe(0m);
        SaleCalculator.Calculate(lines, [Rule(PromotionKind.Combo, [Product(A, quantity: 0m), Product(B)], price: 1_000m)]).PromotionTotal.ShouldBe(0m);
        SaleCalculator.Calculate(lines, [Rule(PromotionKind.Combo, [Product(A), Product(B)])]).PromotionTotal.ShouldBe(0m);
    }

    [Fact]
    public void Combo_no_usa_dos_veces_la_misma_unidad()
    {
        // El arroz es de la marca: el primer componente lo toma y el segundo sigue con el queso.
        var rule = Rule(PromotionKind.Combo, [Product(A), new PromotionTarget(BrandId: Marca)], price: 5_000m);
        var arroz = Line(1, 3_000m, product: A, brand: Marca, key: K(11));
        var queso = Line(1, 2_500m, product: B, brand: Marca, key: K(12));

        var sale = SaleCalculator.Calculate([arroz, queso], [rule]);

        sale.PromotionTotal.ShouldBe(500m);
        sale.Lines.ShouldAllBe(l => l.PromotionId == rule.Id);
    }

    [Fact]
    public void Gana_la_promocion_que_mas_descuenta_sin_acumular()
    {
        var especial = Rule(PromotionKind.SpecialPrice, [Product(A)], price: 9_500m);
        var porcentaje = Rule(PromotionKind.PercentOff, [Product(A)], percent: 10m);

        var line = SaleCalculator.Calculate([Line(1, 10_000m, product: A)], [especial, porcentaje]).Lines.Single();

        line.PromotionId.ShouldBe(porcentaje.Id);
        line.PromotionDiscount.ShouldBe(1_000m);
    }

    [Fact]
    public void Una_promocion_por_linea_y_la_siguiente_ronda_aplica_a_las_libres()
    {
        var soloArroz = Rule(PromotionKind.PercentOff, [Product(A)], percent: 30m);
        var categoria = Rule(PromotionKind.PercentOff, [Category(Categoria)], percent: 10m);
        var arroz = Line(1, 10_000m, product: A, category: Categoria);
        var queso = Line(1, 15_000m, product: B, category: Categoria);

        var sale = SaleCalculator.Calculate([arroz, queso], [categoria, soloArroz]);

        var byKey = sale.Lines.ToDictionary(l => l.Key);
        (byKey[arroz.Key].PromotionId, byKey[arroz.Key].PromotionDiscount).ShouldBe((soloArroz.Id, 3_000m));
        (byKey[queso.Key].PromotionId, byKey[queso.Key].PromotionDiscount).ShouldBe((categoria.Id, 1_500m));
        sale.PromotionTotal.ShouldBe(4_500m);
    }

    [Fact]
    public void En_empate_gana_la_de_menor_id()
    {
        var segunda = Rule(PromotionKind.PercentOff, [Product(A)], percent: 10m, id: K(502));
        var primera = Rule(PromotionKind.PercentOff, [Product(A)], percent: 10m, id: K(501));

        SaleCalculator.Calculate([Line(1, 10_000m, product: A)], [segunda, primera]).Lines.Single().PromotionId.ShouldBe(primera.Id);
    }

    [Fact]
    public void Lineas_con_precio_manual_sin_precio_o_sin_cantidad_no_reciben_promociones()
    {
        var rule = Rule(PromotionKind.PercentOff, [Product(A)], percent: 10m);

        SaleCalculator.Calculate([Line(1, 10_000m, product: A, promotionsAllowed: false)], [rule]).PromotionTotal.ShouldBe(0m);
        SaleCalculator.Calculate([Line(1, 0m, product: A)], [rule]).PromotionTotal.ShouldBe(0m);
        SaleCalculator.Calculate([Line(0m, 10_000m, product: A)], [rule]).PromotionTotal.ShouldBe(0m);
        SaleCalculator.Calculate([Line(1, 10_000m, product: A)], [Rule((PromotionKind)99, [Product(A)], percent: 10m)]).PromotionTotal.ShouldBe(0m);
    }

    [Fact]
    public void Destino_de_una_promocion()
    {
        var sixpack = Line(1, 30_000m, product: A, category: Categoria, brand: Marca, packaging: Sixpack, factor: 6m);

        Product(A).Matches(sixpack).ShouldBeTrue();
        Product(A, Sixpack).Matches(sixpack).ShouldBeTrue();
        Product(A, Unidad).Matches(sixpack).ShouldBeFalse();
        Product(B).Matches(sixpack).ShouldBeFalse();
        Category(OtraCategoria, Categoria).Matches(sixpack).ShouldBeTrue();
        Category(OtraCategoria).Matches(sixpack).ShouldBeFalse();
        new PromotionTarget(BrandId: Marca).Matches(sixpack).ShouldBeTrue();
        new PromotionTarget(BrandId: C).Matches(sixpack).ShouldBeFalse();
        new PromotionTarget(CategoryIds: new HashSet<Guid>()).Matches(sixpack).ShouldBeFalse();
        new PromotionTarget().Matches(sixpack).ShouldBeFalse();
        Should.Throw<ArgumentNullException>(() => new PromotionTarget().Matches(null!));

        Product(A, Sixpack).PerSaleUnit(27_000m, sixpack).ShouldBe(27_000m);
        Product(A).PerSaleUnit(4_500m, sixpack).ShouldBe(27_000m);
    }

    // ─────────────────────────────── Descuentos manuales ───────────────────────────────

    [Fact]
    public void Descuento_de_linea_despues_de_la_promocion_y_global_al_final()
    {
        var rule = Rule(PromotionKind.PercentOff, [Product(A)], percent: 10m);
        var line = SaleCalculator.Calculate([Line(1, 10_000m, product: A, discount: new ManualDiscount(null, 500m))], [rule], new ManualDiscount(10m, null))
            .Lines.Single();

        // 10.000 − 1.000 (promoción) − 500 (línea) = 8.500; − 10 % global = 7.650.
        line.PromotionDiscount.ShouldBe(1_000m);
        line.LineDiscount.ShouldBe(500m);
        line.GlobalDiscountShare.ShouldBe(850m);
        line.Net.ShouldBe(7_650m);
        line.Total.ShouldBe(7_650m);
    }

    [Fact]
    public void Descuento_de_linea_porcentual_o_por_valor_nunca_mayor_que_el_neto()
    {
        SaleCalculator.Calculate([Line(1, 10_000m, discount: new ManualDiscount(10m, null))], []).Lines.Single().LineDiscount.ShouldBe(1_000m);
        var total = SaleCalculator.Calculate([Line(1, 10_000m, discount: new ManualDiscount(null, 20_000m))], []);
        total.Lines.Single().LineDiscount.ShouldBe(10_000m);
        total.Total.ShouldBe(0m);
        total.DiscountTotal.ShouldBe(10_000m);
    }

    [Fact]
    public void Descuento_global_se_prorratea_por_el_neto_de_cada_linea()
    {
        var uno = Line(1, 10_000m);
        var dos = Line(1, 5_000m);

        var sale = SaleCalculator.Calculate([uno, dos], [], new ManualDiscount(10m, null));

        var byKey = sale.Lines.ToDictionary(l => l.Key);
        byKey[uno.Key].GlobalDiscountShare.ShouldBe(1_000m);
        byKey[dos.Key].GlobalDiscountShare.ShouldBe(500m);
        sale.DiscountTotal.ShouldBe(1_500m);
        sale.Total.ShouldBe(13_500m);
    }

    [Fact]
    public void El_residuo_del_prorrateo_va_a_la_linea_de_mayor_peso()
    {
        var lines = new[] { Line(1, 1_000m, key: K(23)), Line(1, 1_000m, key: K(21)), Line(1, 1_000m, key: K(22)) };

        var sale = SaleCalculator.Calculate(lines, [], new ManualDiscount(null, 100m));

        var byKey = sale.Lines.ToDictionary(l => l.Key, l => l.GlobalDiscountShare);
        byKey[K(21)].ShouldBe(33.34m);
        byKey[K(22)].ShouldBe(33.33m);
        byKey[K(23)].ShouldBe(33.33m);
        sale.Total.ShouldBe(2_900m);
    }

    [Fact]
    public void Valor_de_un_descuento_manual()
    {
        SaleCalculator.DiscountValue(null, 1_000m).ShouldBe(0m);
        SaleCalculator.DiscountValue(new ManualDiscount(10m, null), 0m).ShouldBe(0m);
        SaleCalculator.DiscountValue(new ManualDiscount(12.5m, null), 999m).ShouldBe(124.88m);
        SaleCalculator.DiscountValue(new ManualDiscount(null, 300.555m), 1_000m).ShouldBe(300.56m);
        SaleCalculator.DiscountValue(new ManualDiscount(null, null), 1_000m).ShouldBe(0m);
        SaleCalculator.DiscountValue(new ManualDiscount(null, -5m), 1_000m).ShouldBe(0m);
    }

    [Fact]
    public void Prorrateo_sin_valor_o_sin_peso_no_reparte()
    {
        SaleCalculator.Prorate(0m, [(K(1), 10m)]).ShouldBeEmpty();
        SaleCalculator.Prorate(10m, [(K(1), 0m)]).ShouldBeEmpty();
        SaleCalculator.Prorate(10m, [(K(1), 1m), (K(2), 2m)]).ShouldBe(new Dictionary<Guid, decimal> { [K(1)] = 3.33m, [K(2)] = 6.67m });
        Should.Throw<ArgumentNullException>(() => SaleCalculator.Prorate(1m, null!));
        Should.Throw<ArgumentNullException>(() => SaleCalculator.Calculate(null!, []));
        Should.Throw<ArgumentNullException>(() => SaleCalculator.Calculate([], null!));
    }

    [Fact]
    public void Venta_vacia_totaliza_cero()
    {
        var sale = SaleCalculator.Calculate([], [Rule(PromotionKind.PercentOff, [Product(A)], percent: 10m)], new ManualDiscount(10m, null));

        sale.Lines.ShouldBeEmpty();
        sale.Total.ShouldBe(0m);
    }

    // ─────────────────────────────── Propiedad ───────────────────────────────

    [Fact]
    public void Propiedad_la_suma_de_las_lineas_es_el_total_en_10000_ventas_aleatorias()
    {
        var random = new Random(20260929);
        var products = Enumerable.Range(1, 12).Select(i => K(100 + i)).ToArray();
        var categories = new[] { K(201), K(202), K(203) };
        var brands = new[] { K(301), K(302) };
        IReadOnlyList<PricingTax>[] taxSets = [[], [Iva19], [Iva5], [Bolsa], [Iva19, Bolsa]];
        var pool = new List<PromotionRule>
        {
            Rule(PromotionKind.MultiBuy, [Product(products[0])], buy: 3, pay: 2, id: K(401)),
            Rule(PromotionKind.MultiBuy, [Category(categories[1])], buy: 2, pay: 1, max: 2, id: K(402)),
            Rule(PromotionKind.SpecialPrice, [Product(products[1])], price: 900m, id: K(403)),
            Rule(PromotionKind.SpecialPrice, [Product(products[2], K(70))], price: 5_000m, id: K(404)),
            Rule(PromotionKind.PercentOff, [Category(categories[0], categories[2])], percent: 15m, id: K(405)),
            Rule(PromotionKind.PercentOff, [new PromotionTarget(BrandId: brands[0])], percent: 7.5m, id: K(406)),
            Rule(PromotionKind.QuantityPrice, [Product(products[3])], price: 800m, min: 3m, id: K(407)),
            Rule(PromotionKind.Combo, [Product(products[4]), Product(products[5])], price: 1_500m, id: K(408)),
            Rule(PromotionKind.Combo, [Product(products[6], quantity: 2m), Category(categories[2])], price: 3_000m, max: 3, id: K(409)),
        };

        for (var i = 0; i < 10_000; i++)
        {
            var lines = new List<PricingLine>();
            for (var j = random.Next(1, 9); j > 0; j--)
            {
                var p = random.Next(products.Length);
                var packaged = random.Next(6) == 0;
                var weighed = !packaged && random.Next(5) == 0;
                var quantity = weighed ? Math.Round((decimal)random.NextDouble() * 3m, 3) + 0.001m : random.Next(1, 8);
                var price = random.Next(3) == 0 ? random.Next(100, 5_000_000) / 100m : random.Next(1, 2_000) * 50m;
                ManualDiscount? discount = random.Next(8) switch
                {
                    0 => new ManualDiscount(random.Next(1, 10_001) / 100m, null),
                    1 => new ManualDiscount(null, random.Next(1, 5_000_000) / 100m),
                    _ => null,
                };
                lines.Add(Line(
                    quantity, price, product: products[p], category: categories[p % 3], brand: p % 2 == 0 ? brands[p % 4 / 2] : null,
                    packaging: packaged ? K(70) : null, factor: packaged ? 6m : 1m, includesTax: random.Next(4) != 0,
                    taxes: taxSets[random.Next(taxSets.Length)], discount: discount, promotionsAllowed: random.Next(10) != 0));
            }

            var promotions = pool.Where(_ => random.Next(2) == 0).ToList();
            ManualDiscount? global = random.Next(5) switch
            {
                0 => new ManualDiscount(random.Next(1, 10_001) / 100m, null),
                1 => new ManualDiscount(null, random.Next(1, 20_000_000) / 100m),
                _ => null,
            };

            var sale = SaleCalculator.Calculate(lines, promotions, global);

            sale.Lines.Count.ShouldBe(lines.Count);
            sale.Lines.Sum(l => l.Total).ShouldBe(sale.Total, $"venta {i}");
            sale.Lines.Sum(l => l.Net).ShouldBe(sale.Gross - sale.PromotionTotal - sale.DiscountTotal, $"venta {i}");
            sale.Lines.Sum(l => l.TaxTotal).ShouldBe(sale.TaxTotal, $"venta {i}");
            sale.Lines.Sum(l => l.Base).ShouldBe(sale.Subtotal, $"venta {i}");
            var beforeGlobal = sale.Lines.Sum(l => l.Gross - l.PromotionDiscount - l.LineDiscount);
            sale.Lines.Sum(l => l.GlobalDiscountShare).ShouldBe(SaleCalculator.DiscountValue(global, beforeGlobal), $"venta {i}");
            foreach (var (priced, input) in sale.Lines.Zip(lines))
            {
                priced.Key.ShouldBe(input.Key);
                priced.PromotionDiscount.ShouldBeGreaterThanOrEqualTo(0m);
                priced.LineDiscount.ShouldBeGreaterThanOrEqualTo(0m);
                priced.GlobalDiscountShare.ShouldBeGreaterThanOrEqualTo(0m);
                priced.Net.ShouldBeGreaterThanOrEqualTo(0m, $"venta {i}");
                priced.Total.ShouldBe(input.PriceIncludesTax ? priced.Net : priced.Net + priced.TaxTotal, $"venta {i}");
                (priced.Base + priced.TaxTotal).ShouldBe(priced.Total, $"venta {i}");
                priced.TaxTotal.ShouldBe(priced.Taxes.Sum(t => t.Amount));
                decimal.Round(priced.Total, 2).ShouldBe(priced.Total);
                if (!input.PromotionsAllowed)
                {
                    priced.PromotionId.ShouldBeNull();
                }
            }
        }
    }
}
