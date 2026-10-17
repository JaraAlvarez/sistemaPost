using System.Globalization;

namespace Pos.Modules.Catalog.Domain;

/// <summary>A qué aplica una promoción vista desde la etiqueta: un producto (sin presentación), un conjunto de categorías o una marca.</summary>
public sealed record LabelPromotionTarget(Guid? ProductId, Guid? PackagingId, IReadOnlySet<Guid>? CategoryIds, Guid? BrandId);

/// <summary>Promoción que rige hoy (tipos del módulo de promociones: MULTI_BUY, SPECIAL_PRICE, PERCENT_OFF, QUANTITY_PRICE o COMBO).</summary>
public sealed record LabelPromotion(
    string Name,
    string Kind,
    IReadOnlyList<LabelPromotionTarget> Targets,
    int? BuyQuantity = null,
    int? PayQuantity = null,
    decimal? Price = null,
    decimal? Percent = null,
    decimal? MinQuantity = null);

/// <summary>Oferta que se imprime: precio de oferta por unidad base (null si la oferta es solo un texto, p. ej. "Lleve 3 pague 2") y el texto corto.</summary>
public sealed record LabelOffer(decimal? Price, string Text);

/// <summary>
/// Precios de la etiqueta: precio al público con impuestos, oferta vigente y precio por unidad de medida (kilo o litro, según el contenido
/// neto del empaque). Puro y determinista para probarlo sin base de datos.
/// </summary>
public static class LabelPricing
{
    private static readonly CultureInfo Colombia = CultureInfo.GetCultureInfo("es-CO");

    /// <summary>Precio al público: si la lista no incluye impuestos se suman los porcentuales y los valores fijos por unidad.</summary>
    public static decimal ConsumerPrice(decimal listPrice, bool includesTax, decimal ratePercent, decimal fixedPerUnit) =>
        includesTax ? listPrice : RoundPeso((listPrice * (1m + (ratePercent / 100m))) + fixedPerUnit);

    /// <summary>
    /// Mejor oferta para el producto: entre las promociones de precio especial y de porcentaje gana la de menor precio (solo si queda por debajo
    /// del precio normal); si no hay, se informa la de cantidad ("Desde 3 und") o la de lleve N pague M. Los combos no van en la etiqueta.
    /// </summary>
    public static LabelOffer? BestOffer(Guid productId, Guid categoryId, Guid? brandId, decimal? regularPrice, IEnumerable<LabelPromotion> promotions)
    {
        ArgumentNullException.ThrowIfNull(promotions);
        var applicable = promotions.Where(p => p.Targets.Any(t => Matches(t, productId, categoryId, brandId))).ToList();
        var priced = new List<(decimal Price, string Text)>();
        foreach (var promotion in applicable)
        {
            switch (promotion.Kind)
            {
                case "SPECIAL_PRICE" when promotion.Price is { } price && price > 0m:
                    priced.Add((price, "Precio especial"));
                    break;
                case "PERCENT_OFF" when promotion.Percent is { } percent && regularPrice is { } regular && percent > 0m:
                    priced.Add((RoundPeso(regular * (1m - (Math.Min(percent, 100m) / 100m))), $"Ahorre {percent.ToString("0.##", Colombia)} %"));
                    break;
                default:
                    break;
            }
        }

        if (priced.Where(p => regularPrice is null || p.Price < regularPrice).OrderBy(p => p.Price).ThenBy(p => p.Text, StringComparer.Ordinal).FirstOrDefault()
            is { Text: not null } best)
        {
            return new LabelOffer(best.Price, best.Text);
        }

        var quantity = applicable.Where(p => p is { Kind: "QUANTITY_PRICE", Price: > 0m, MinQuantity: > 0m })
            .OrderBy(p => p.Price).FirstOrDefault();
        if (quantity is not null && (regularPrice is null || quantity.Price < regularPrice))
        {
            return new LabelOffer(null, $"Desde {quantity.MinQuantity!.Value.ToString("0.###", Colombia)} und: {Money(quantity.Price!.Value)} c/u");
        }

        var multi = applicable.Where(p => p is { Kind: "MULTI_BUY", BuyQuantity: > 0, PayQuantity: >= 0 } && p.BuyQuantity > p.PayQuantity)
            .OrderByDescending(p => (decimal)(p.BuyQuantity!.Value - p.PayQuantity!.Value) / p.BuyQuantity.Value).FirstOrDefault();
        return multi is null
            ? null
            : new LabelOffer(null, string.Create(Colombia, $"Lleve {multi.BuyQuantity} pague {multi.PayQuantity}"));
    }

    /// <summary>
    /// Precio por kilo o por litro según el contenido neto (G, KG, ML, L); null si el producto no declara contenido o se vende por peso
    /// (en ese caso el precio ya es por kilo).
    /// </summary>
    public static string? UnitMeasurePrice(decimal? price, decimal? netContent, string? netContentUnit)
    {
        if (price is not { } value || netContent is not > 0m || string.IsNullOrWhiteSpace(netContentUnit))
        {
            return null;
        }

        var (factor, unit) = netContentUnit.Trim().ToUpperInvariant() switch
        {
            "G" => (1000m, "kg"),
            "KG" => (1m, "kg"),
            "ML" => (1000m, "litro"),
            "L" => (1m, "litro"),
            _ => (0m, string.Empty),
        };
        return factor == 0m ? null : $"{Money(RoundPeso(value / netContent.Value * factor))} por {unit}";
    }

    /// <summary>Dinero en pesos colombianos sin decimales: <c>$ 12.500</c>.</summary>
    public static string Money(decimal value) => "$ " + RoundPeso(value).ToString("N0", Colombia);

    public static decimal RoundPeso(decimal value) => decimal.Round(value, 0, MidpointRounding.AwayFromZero);

    private static bool Matches(LabelPromotionTarget target, Guid productId, Guid categoryId, Guid? brandId)
    {
        if (target.ProductId is { } product)
        {
            return product == productId && target.PackagingId is null;
        }

        if (target.CategoryIds is { Count: > 0 } categories)
        {
            return categories.Contains(categoryId);
        }

        return target.BrandId is { } brand && brand == brandId;
    }
}
