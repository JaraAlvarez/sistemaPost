using Pos.Modules.Catalog.Domain;
using Pos.Modules.Sales.Domain;

namespace Pos.Modules.Sales.UnitTests;

/// <summary>
/// La etiqueta de góndola (LabelPricing, módulo Catalog) dice exactamente lo que cobra la caja (SaleCalculator) por una unidad (o un kilo): lista
/// con y sin impuestos, IVA, impuestos fijos y porcentuales juntos, y cada tipo de promoción, solas o varias a la vez, por producto, categoría o
/// marca. En peso fraccionario (0,750 kg) lo cobrado es el precio por kilo de la etiqueta por los kilos (a lo sumo un centavo por impuesto).
/// </summary>
public class LabelCheckoutParityTests
{
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Category = Guid.NewGuid();
    private static readonly Guid Brand = Guid.NewGuid();

    /// <summary>Promoción de la prueba: tipo, a qué aplica (P = producto, C = categoría, B = marca), precio o porcentaje, mínimo y lleve/pague.</summary>
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
    private static readonly Tax[] Iva5 = [new(5m, null)];
    private static readonly Tax[] IvaIbua = [new(19m, null), new(null, 150m)];
    private static readonly Tax[] IvaIcui = [new(19m, null), new(20m, null)];

    public static TheoryData<string, decimal, bool, Tax[], Promo[], decimal, bool> Cases => new()
    {
        { "sin promoción, IVA incluido", 10_000m, true, Iva19, [], 1m, false },
        { "sin promoción, lista sin IVA", 10_000m, false, Iva19, [], 1m, false },
        { "precio especial, lista sin IVA", 10_000m, false, Iva19, [new("SPECIAL_PRICE", Price: 9_000m)], 1m, false },
        { "precio especial, IVA incluido", 10_000m, true, Iva19, [new("SPECIAL_PRICE", Price: 9_000m)], 1m, false },
        { "precio especial mayor que la lista", 10_000m, false, Iva19, [new("SPECIAL_PRICE", Price: 12_000m)], 1m, false },
        { "porcentaje, lista sin IVA", 10_000m, false, Iva19, [new("PERCENT_OFF", Percent: 10m)], 1m, false },
        { "porcentaje con IBUA", 2_000m, false, IvaIbua, [new("PERCENT_OFF", Percent: 10m)], 1m, false },
        { "especial con IBUA", 2_000m, false, IvaIbua, [new("SPECIAL_PRICE", Price: 1_500m)], 1m, false },
        { "porcentaje con IBUA incluido", 2_000m, true, IvaIbua, [new("PERCENT_OFF", Percent: 10m)], 1m, false },
        { "porcentaje con centavos", 3_333m, true, Iva5, [new("PERCENT_OFF", Percent: 15m)], 1m, false },
        { "centavos sin impuestos incluidos", 3_333.33m, false, [new(5m, null), new(null, 37.5m)], [new("PERCENT_OFF", Percent: 12.5m)], 1m, false },
        { "solo impuesto fijo", 1_234.56m, false, [new(null, 120m)], [new("SPECIAL_PRICE", Price: 999.99m)], 1m, false },
        { "IVA 0", 24_900m, false, [new(0m, null)], [new("PERCENT_OFF", Percent: 33m)], 1m, false },
        { "por cantidad desde 1", 10_000m, false, Iva19, [new("QUANTITY_PRICE", Price: 8_000m, Min: 1m)], 1m, false },
        { "por cantidad desde 1 y precio especial", 10_000m, false, Iva19, [new("QUANTITY_PRICE", Price: 8_000m, Min: 1m), new("SPECIAL_PRICE", Price: 8_500m)], 1m, false },
        { "por cantidad desde 3 no aplica a una", 10_000m, true, Iva19, [new("QUANTITY_PRICE", Price: 8_000m, Min: 3m)], 1m, false },
        { "lleve 3 pague 2 no aplica a una", 5_000m, true, Iva19, [new("MULTI_BUY", Buy: 3, Pay: 2)], 1m, false },
        { "por categoría", 10_000m, false, Iva19, [new("PERCENT_OFF", 'C', Percent: 20m)], 1m, false },
        { "por marca", 10_000m, false, Iva19, [new("SPECIAL_PRICE", 'B', Price: 7_777m)], 1m, false },
        { "varias promociones", 10_000m, false, IvaIbua,
            [new("PERCENT_OFF", 'C', Percent: 15m), new("SPECIAL_PRICE", Price: 8_400m), new("MULTI_BUY", Buy: 3, Pay: 2), new("COMBO", Price: 1m)], 1m, false },
        { "empate (gana la de menor Id)", 10_000m, false, Iva19, [new("PERCENT_OFF", Percent: 10m), new("SPECIAL_PRICE", 'B', Price: 9_000m)], 1m, false },
        { "IVA e ICUI juntos", 4_000m, false, IvaIcui, [new("PERCENT_OFF", Percent: 10m)], 1m, false },
        { "IVA e ICUI incluidos", 4_000m, true, IvaIcui, [new("SPECIAL_PRICE", Price: 3_500m)], 1m, false },
        { "peso: un kilo con precio especial", 24_900m, false, Iva5, [new("SPECIAL_PRICE", 'C', Price: 22_900m)], 1m, true },
        { "peso: 0,750 kg con precio especial", 24_900m, false, Iva5, [new("SPECIAL_PRICE", Price: 22_900m)], 0.75m, true },
        { "peso: 0,750 kg y desde 1 kg no aplica", 20_000m, true, Iva5, [new("QUANTITY_PRICE", Price: 18_000m, Min: 1m)], 0.75m, true },
        { "peso: 0,750 kg con porcentaje", 18_990m, true, [], [new("PERCENT_OFF", Percent: 12m)], 0.75m, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void La_etiqueta_muestra_lo_que_cobra_la_caja(
        string caso, decimal list, bool includesTax, Tax[] taxes, Promo[] promos, decimal quantity, bool weighed)
    {
        var line = new PricingLine(Guid.NewGuid(), Product, null, Category, Brand, quantity, 1m, list, includesTax,
            [.. taxes.Select((t, i) => new PricingTax(Guid.NewGuid(), $"T{i}", t.Rate is null ? "OTHER" : "VAT", t.Rate, t.Fixed))]);
        // Ids crecientes en el orden del caso: el primero es el de menor Id (desempate).
        var ids = promos.Select((_, i) => new Guid($"00000000-0000-0000-0000-{i + 1:D12}")).ToArray();
        var rules = promos.Select((p, i) => new PromotionRule(
            ids[i], "x", p.Kind switch
            {
                "SPECIAL_PRICE" => PromotionKind.SpecialPrice,
                "PERCENT_OFF" => PromotionKind.PercentOff,
                "QUANTITY_PRICE" => PromotionKind.QuantityPrice,
                "MULTI_BUY" => PromotionKind.MultiBuy,
                _ => PromotionKind.Combo,
            },
            p.Kind == "COMBO"
                ? [new PromotionTarget(Product), new PromotionTarget(Guid.NewGuid())]
                : [p.Target switch
                {
                    'C' => new PromotionTarget(CategoryIds: new HashSet<Guid> { Category }),
                    'B' => new PromotionTarget(BrandId: Brand),
                    _ => new PromotionTarget(Product),
                }],
            p.Buy, p.Pay, p.Price, p.Percent, p.Min)).ToList();
        var labelPromotions = promos.Select((p, i) => new LabelPromotion(
            "x", p.Kind,
            [p.Target switch
            {
                'C' => new LabelPromotionTarget(null, null, new HashSet<Guid> { Category }, null),
                'B' => new LabelPromotionTarget(null, null, null, Brand),
                _ => new LabelPromotionTarget(Product, null, null, null),
            }],
            p.Buy, p.Pay, p.Price, p.Percent, p.Min, Id: ids[i])).ToList();
        var labelTaxes = taxes.Select(t => new LabelTax(t.Rate, t.Fixed)).ToList();

        var sale = SaleCalculator.Calculate([line], rules);
        var charged = sale.Total;
        var offer = LabelPricing.BestOffer(Product, Category, Brand, list, includesTax, labelTaxes, labelPromotions, weighed ? "kg" : "und", weighed);
        var shown = offer?.Price ?? LabelPricing.RegularPrice(list, includesTax, labelTaxes);

        if (quantity == 1m)
        {
            shown.ShouldBe(charged, caso);
            decimal.Parse(LabelPricing.Money(shown)[2..].Replace(".", string.Empty, StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture)
                .ShouldBeGreaterThanOrEqualTo(charged, "el precio exhibido nunca es menor que el cobrado");
        }
        else
        {
            // Precio por kilo × kilos: la caja redondea cada impuesto al centavo.
            Math.Abs(charged - (shown * quantity)).ShouldBeLessThanOrEqualTo(0.01m * (taxes.Length + 1), caso);
        }

        // La promoción que aplica la caja es la que dice la etiqueta (o ninguna, si la etiqueta no muestra precio de oferta).
        if (offer?.Price is null)
        {
            sale.Lines[0].PromotionId.ShouldBeNull(caso);
        }
        else
        {
            // Empates incluidos: la caja y la etiqueta eligen la misma promoción (la de menor Id).
            var winner = promos[Array.IndexOf(ids, sale.Lines[0].PromotionId!.Value)];
            offer.Text.ShouldStartWith(winner.Kind == "PERCENT_OFF" ? "Ahorre" : "Precio especial", Case.Insensitive, caso);
        }
    }
}
