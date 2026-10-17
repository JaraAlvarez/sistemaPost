using Pos.Modules.Catalog.Domain;
using Pos.Modules.Sales.Domain;

namespace Pos.Modules.Sales.UnitTests;

/// <summary>
/// La etiqueta de góndola (LabelPricing, módulo Catalog) dice exactamente lo que cobra la caja (SaleCalculator) por una unidad: lista con y
/// sin impuestos, IVA, impuesto fijo por unidad (IBUA/ICUI) y cada tipo de promoción con precio.
/// </summary>
public class LabelCheckoutParityTests
{
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Category = Guid.NewGuid();

    public static TheoryData<decimal, bool, decimal?, decimal?, string?, decimal?> Cases => new()
    {
        // lista, incluye impuestos, IVA %, fijo por unidad, tipo de promoción, valor (precio o %)
        { 10_000m, true, 19m, null, null, null },
        { 10_000m, false, 19m, null, null, null },
        { 10_000m, false, 19m, null, "SPECIAL_PRICE", 9_000m },
        { 10_000m, true, 19m, null, "SPECIAL_PRICE", 9_000m },
        { 10_000m, false, 19m, null, "PERCENT_OFF", 10m },
        { 2_000m, false, 19m, 150m, "PERCENT_OFF", 10m },
        { 2_000m, false, 19m, 150m, "SPECIAL_PRICE", 1_500m },
        { 2_000m, true, 19m, 150m, "PERCENT_OFF", 10m },
        { 3_333m, true, 5m, null, "PERCENT_OFF", 15m },
        { 3_333.33m, false, 5m, 37.5m, "PERCENT_OFF", 12.5m },
        { 1_234.56m, false, null, 120m, "SPECIAL_PRICE", 999.99m },
        { 24_900m, false, 0m, null, "PERCENT_OFF", 33m },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void La_etiqueta_muestra_lo_que_cobra_la_caja(decimal list, bool includesTax, decimal? rate, decimal? fixedAmount, string? kind, decimal? value)
    {
        var labelTaxes = new List<LabelTax>();
        var saleTaxes = new List<PricingTax>();
        if (rate is { } r)
        {
            labelTaxes.Add(new LabelTax(r, null));
            saleTaxes.Add(new PricingTax(Guid.NewGuid(), "IVA", "VAT", r, null));
        }

        if (fixedAmount is { } f)
        {
            labelTaxes.Add(new LabelTax(null, f));
            saleTaxes.Add(new PricingTax(Guid.NewGuid(), "IBUA", "OTHER", null, f));
        }

        var line = new PricingLine(Guid.NewGuid(), Product, null, Category, null, 1m, 1m, list, includesTax, saleTaxes);
        IReadOnlyList<PromotionRule> rules = kind switch
        {
            "SPECIAL_PRICE" => [new PromotionRule(Guid.NewGuid(), "x", PromotionKind.SpecialPrice, [new PromotionTarget(Product)], Price: value)],
            "PERCENT_OFF" => [new PromotionRule(Guid.NewGuid(), "x", PromotionKind.PercentOff, [new PromotionTarget(Product)], Percent: value)],
            _ => [],
        };
        LabelPromotion[] labelPromotions = kind is null
            ? []
            : [new LabelPromotion("x", kind, [new LabelPromotionTarget(Product, null, null, null)], Price: kind == "SPECIAL_PRICE" ? value : null,
                Percent: kind == "PERCENT_OFF" ? value : null)];

        var charged = SaleCalculator.Calculate([line], rules).Total;
        var offer = LabelPricing.BestOffer(Product, Category, null, list, includesTax, labelTaxes, labelPromotions);
        var shown = offer?.Price ?? LabelPricing.RegularPrice(list, includesTax, labelTaxes);

        shown.ShouldBe(charged);
        decimal.Parse(LabelPricing.Money(shown)[2..].Replace(".", string.Empty, StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBeGreaterThanOrEqualTo(charged, "el precio exhibido nunca es menor que el cobrado");
    }
}
