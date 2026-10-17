using Pos.Modules.Catalog.Domain;
using Pos.Modules.Sales.Domain;

namespace Pos.Modules.Sales.UnitTests;

/// <summary>
/// La etiqueta de góndola (LabelPricing, módulo Catalog) dice exactamente lo que cobra la caja (SaleCalculator) por una unidad (o un kilo): lista
/// con y sin impuestos, IVA, impuestos fijos y porcentuales juntos, y cada tipo de promoción, solas o varias a la vez, por producto o categoría.
/// </summary>
public class LabelCheckoutParityTests
{
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Category = Guid.NewGuid();

    /// <summary>Promoción de la prueba: tipo, a qué aplica (P = producto, C = categoría), precio o porcentaje, mínimo y lleve/pague.</summary>
    public sealed record Promo(string Kind, char Target = 'P', decimal? Price = null, decimal? Percent = null, decimal? Min = null, int? Buy = null, int? Pay = null)
    {
        public override string ToString() => $"{Kind}/{Target}/{Price}/{Percent}/{Min}/{Buy}-{Pay}";
    }

    /// <summary>Impuesto de la prueba: porcentual (IVA, ICUI) o fijo por unidad (IBUA, bolsa).</summary>
    public sealed record Tax(decimal? Rate, decimal? Fixed)
    {
        public override string ToString() => Rate is { } r ? $"{r}%" : $"${Fixed}";
    }

    private static readonly Tax[] Iva19 = [new(19m, null)];
    private static readonly Tax[] IvaIbua = [new(19m, null), new(null, 150m)];
    private static readonly Tax[] IvaIcui = [new(19m, null), new(20m, null)];

    public static TheoryData<string, decimal, bool, Tax[], Promo[]> Cases => new()
    {
        { "sin promoción, IVA incluido", 10_000m, true, Iva19, [] },
        { "sin promoción, lista sin IVA", 10_000m, false, Iva19, [] },
        { "precio especial, lista sin IVA", 10_000m, false, Iva19, [new("SPECIAL_PRICE", Price: 9_000m)] },
        { "precio especial, IVA incluido", 10_000m, true, Iva19, [new("SPECIAL_PRICE", Price: 9_000m)] },
        { "porcentaje, lista sin IVA", 10_000m, false, Iva19, [new("PERCENT_OFF", Percent: 10m)] },
        { "porcentaje con IBUA", 2_000m, false, IvaIbua, [new("PERCENT_OFF", Percent: 10m)] },
        { "especial con IBUA", 2_000m, false, IvaIbua, [new("SPECIAL_PRICE", Price: 1_500m)] },
        { "porcentaje con IBUA incluido", 2_000m, true, IvaIbua, [new("PERCENT_OFF", Percent: 10m)] },
        { "porcentaje con centavos", 3_333m, true, [new(5m, null)], [new("PERCENT_OFF", Percent: 15m)] },
        { "centavos sin impuestos incluidos", 3_333.33m, false, [new(5m, null), new(null, 37.5m)], [new("PERCENT_OFF", Percent: 12.5m)] },
        { "solo impuesto fijo", 1_234.56m, false, [new(null, 120m)], [new("SPECIAL_PRICE", Price: 999.99m)] },
        { "IVA 0", 24_900m, false, [new(0m, null)], [new("PERCENT_OFF", Percent: 33m)] },
        { "por cantidad desde 1", 10_000m, false, Iva19, [new("QUANTITY_PRICE", Price: 8_000m, Min: 1m)] },
        { "por cantidad desde 1 y precio especial", 10_000m, false, Iva19, [new("QUANTITY_PRICE", Price: 8_000m, Min: 1m), new("SPECIAL_PRICE", Price: 8_500m)] },
        { "por cantidad desde 3 no aplica a una", 10_000m, true, Iva19, [new("QUANTITY_PRICE", Price: 8_000m, Min: 3m)] },
        { "lleve 1 pague 0, IVA incluido", 5_000m, true, Iva19, [new("MULTI_BUY", Buy: 1, Pay: 0)] },
        { "lleve 1 pague 0 con IBUA sin incluir", 5_000m, false, IvaIbua, [new("MULTI_BUY", Buy: 1, Pay: 0)] },
        { "lleve 3 pague 2 no aplica a una", 5_000m, true, Iva19, [new("MULTI_BUY", Buy: 3, Pay: 2)] },
        { "por categoría", 10_000m, false, Iva19, [new("PERCENT_OFF", 'C', Percent: 20m)] },
        { "varias promociones", 10_000m, false, IvaIbua,
            [new("PERCENT_OFF", 'C', Percent: 15m), new("SPECIAL_PRICE", Price: 8_400m), new("MULTI_BUY", Buy: 3, Pay: 2), new("COMBO", Price: 1m)] }, // el combo (con otro producto) no aplica a una sola unidad
        { "IVA e ICUI juntos", 4_000m, false, IvaIcui, [new("PERCENT_OFF", Percent: 10m)] },
        { "IVA e ICUI incluidos", 4_000m, true, IvaIcui, [new("SPECIAL_PRICE", Price: 3_500m)] },
        { "producto por peso (precio por kilo)", 24_900m, false, [new(5m, null)], [new("SPECIAL_PRICE", 'C', Price: 22_900m)] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void La_etiqueta_muestra_lo_que_cobra_la_caja(string caso, decimal list, bool includesTax, Tax[] taxes, Promo[] promos)
    {
        var line = new PricingLine(Guid.NewGuid(), Product, null, Category, null, 1m, 1m, list, includesTax,
            [.. taxes.Select((t, i) => new PricingTax(Guid.NewGuid(), $"T{i}", t.Rate is null ? "OTHER" : "VAT", t.Rate, t.Fixed))]);
        var rules = promos.Select(p => new PromotionRule(
            Guid.NewGuid(), "x", p.Kind switch
            {
                "SPECIAL_PRICE" => PromotionKind.SpecialPrice,
                "PERCENT_OFF" => PromotionKind.PercentOff,
                "QUANTITY_PRICE" => PromotionKind.QuantityPrice,
                "MULTI_BUY" => PromotionKind.MultiBuy,
                _ => PromotionKind.Combo,
            },
            p.Kind == "COMBO"
                ? [new PromotionTarget(Product), new PromotionTarget(Guid.NewGuid())]
                : [p.Target == 'C' ? new PromotionTarget(CategoryIds: new HashSet<Guid> { Category }) : new PromotionTarget(Product)],
            p.Buy, p.Pay, p.Price, p.Percent, p.Min)).ToList();
        var labelPromotions = promos.Select(p => new LabelPromotion(
            "x", p.Kind,
            [p.Target == 'C' ? new LabelPromotionTarget(null, null, new HashSet<Guid> { Category }, null) : new LabelPromotionTarget(Product, null, null, null)],
            p.Buy, p.Pay, p.Price, p.Percent, p.Min)).ToList();
        var labelTaxes = taxes.Select(t => new LabelTax(t.Rate, t.Fixed)).ToList();

        var charged = SaleCalculator.Calculate([line], rules).Total;
        var offer = LabelPricing.BestOffer(Product, Category, null, list, includesTax, labelTaxes, labelPromotions);
        var shown = offer?.Price ?? LabelPricing.RegularPrice(list, includesTax, labelTaxes);

        shown.ShouldBe(charged, caso);
        decimal.Parse(LabelPricing.Money(shown)[2..].Replace(".", string.Empty, StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBeGreaterThanOrEqualTo(charged, "el precio exhibido nunca es menor que el cobrado");
    }
}
