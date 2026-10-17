using System.Globalization;
using Pos.SharedKernel.Finance;

namespace Pos.Modules.Catalog.Domain;

/// <summary>A qué aplica una promoción vista desde la etiqueta: un producto (sin presentación), un conjunto de categorías o una marca.</summary>
public sealed record LabelPromotionTarget(Guid? ProductId, Guid? PackagingId, IReadOnlySet<Guid>? CategoryIds, Guid? BrandId);

/// <summary>
/// Promoción que rige hoy (tipos del módulo de promociones: MULTI_BUY, SPECIAL_PRICE, PERCENT_OFF, QUANTITY_PRICE o COMBO). Los precios de la
/// regla están en los términos de la lista (con o sin impuestos, igual que el precio de lista). <c>ValidTo</c>: fin de la vigencia
/// (exclusivo); <c>HasSchedule</c>: solo rige algunos días u horas.
/// </summary>
public sealed record LabelPromotion(
    string Name,
    string Kind,
    IReadOnlyList<LabelPromotionTarget> Targets,
    int? BuyQuantity = null,
    int? PayQuantity = null,
    decimal? Price = null,
    decimal? Percent = null,
    decimal? MinQuantity = null,
    DateTimeOffset? ValidTo = null,
    bool HasSchedule = false);

/// <summary>Impuesto del producto: porcentual (<c>Rate</c>, p. ej. 19) o valor fijo por unidad base (<c>FixedAmount</c>, p. ej. IBUA).</summary>
public sealed record LabelTax(decimal? Rate, decimal? FixedAmount);

/// <summary>
/// Oferta que se imprime: precio de oferta al público por unidad base (null si la oferta es solo un texto, p. ej. "Lleve 3 pague 2"), el texto
/// corto y el fin de la vigencia.
/// </summary>
public sealed record LabelOffer(decimal? Price, string Text, DateTimeOffset? ValidTo = null);

/// <summary>
/// Precios de la etiqueta calculados EXACTAMENTE como los cobra la caja para una unidad base (motor <c>SaleCalculator</c> del módulo de
/// ventas: bruto redondeado → promoción en términos de la lista → impuestos sobre el neto). Si la lista no incluye impuestos, el IVA se
/// calcula sobre el neto ya descontado y el impuesto fijo (IBUA, ICUI) se suma sin descuento. Una prueba compara contra el motor real.
/// Las promociones con días u horario no van en la etiqueta (la góndola no puede decir "solo martes de 2 a 5").
/// </summary>
public static class LabelPricing
{
    private static readonly CultureInfo Colombia = CultureInfo.GetCultureInfo("es-CO");
    private static readonly RoundingPolicy Rounding = RoundingPolicy.Colombia;

    /// <summary>Lo que paga el cliente por una unidad base con el neto <paramref name="net"/> en términos de la lista.</summary>
    public static decimal CheckoutPrice(decimal net, bool includesTax, IReadOnlyList<LabelTax> taxes)
    {
        ArgumentNullException.ThrowIfNull(taxes);
        if (includesTax)
        {
            return net;
        }

        var fixedTotal = taxes.Where(t => t.Rate is null && t.FixedAmount is not null).Sum(t => Rounding.RoundMoney(t.FixedAmount!.Value));
        var percentTotal = taxes.Where(t => t.Rate is not null).Sum(t => Rounding.RoundMoney(net * t.Rate!.Value / 100m));
        return net + fixedTotal + percentTotal;
    }

    /// <summary>Precio normal al público por unidad base.</summary>
    public static decimal RegularPrice(decimal listPrice, bool includesTax, IReadOnlyList<LabelTax> taxes) =>
        CheckoutPrice(Rounding.RoundMoney(listPrice), includesTax, taxes);

    /// <summary>
    /// Mejor oferta para el producto: entre las de precio especial y porcentaje gana la que deja el menor precio al público (solo si baja el
    /// normal); si no hay, se informa la de cantidad ("Desde 3 und: $ X c/u") o la de lleve N pague M. Los combos y las promociones con días u
    /// horario no van en la etiqueta.
    /// </summary>
    public static LabelOffer? BestOffer(
        Guid productId, Guid categoryId, Guid? brandId, decimal? listPrice, bool includesTax, IReadOnlyList<LabelTax> taxes,
        IEnumerable<LabelPromotion> promotions, string quantityUnit = "und")
    {
        ArgumentNullException.ThrowIfNull(promotions);
        ArgumentNullException.ThrowIfNull(taxes);
        var applicable = promotions.Where(p => !p.HasSchedule && p.Targets.Any(t => Matches(t, productId, categoryId, brandId))).ToList();
        if (listPrice is not { } list)
        {
            var onlyText = applicable.Where(p => p is { Kind: "MULTI_BUY", BuyQuantity: > 0, PayQuantity: >= 0 } && p.BuyQuantity > p.PayQuantity).FirstOrDefault();
            return onlyText is null ? null : new LabelOffer(null, MultiBuyText(onlyText), onlyText.ValidTo);
        }

        var gross = Rounding.RoundMoney(list);
        var regular = CheckoutPrice(gross, includesTax, taxes);
        var priced = new List<(decimal Price, string Text, DateTimeOffset? ValidTo)>();
        foreach (var promotion in applicable)
        {
            switch (promotion.Kind)
            {
                case "SPECIAL_PRICE" when promotion.Price is { } price && price > 0m:
                    priced.Add((CheckoutPrice(gross - Math.Clamp(gross - Rounding.RoundMoney(price), 0m, gross), includesTax, taxes), "Precio especial",
                        promotion.ValidTo));
                    break;
                case "QUANTITY_PRICE" when promotion is { Price: > 0m, MinQuantity: <= 1m }:
                    // Desde 1 unidad (o menos) la caja la aplica a la primera unidad: es un precio especial.
                    priced.Add((CheckoutPrice(gross - Math.Clamp(gross - Rounding.RoundMoney(promotion.Price!.Value), 0m, gross), includesTax, taxes),
                        "Precio especial", promotion.ValidTo));
                    break;
                case "MULTI_BUY" when promotion is { BuyQuantity: 1, PayQuantity: 0 }:
                    // Lleve 1 pague 0: la caja descuenta la unidad completa.
                    priced.Add((CheckoutPrice(0m, includesTax, taxes), "Gratis", promotion.ValidTo));
                    break;
                case "PERCENT_OFF" when promotion.Percent is { } percent && percent > 0m:
                    var discount = Math.Clamp(Rounding.RoundMoney(gross * percent / 100m), 0m, gross);
                    priced.Add((CheckoutPrice(gross - discount, includesTax, taxes), $"Ahorre {percent.ToString("0.##", Colombia)} %", promotion.ValidTo));
                    break;
                default:
                    break;
            }
        }

        if (priced.Where(p => p.Price < regular).OrderBy(p => p.Price).ThenBy(p => p.Text, StringComparer.Ordinal).FirstOrDefault() is { Text: not null } best)
        {
            return new LabelOffer(best.Price, best.Text, best.ValidTo);
        }

        var quantity = applicable.Where(p => p is { Kind: "QUANTITY_PRICE", Price: > 0m, MinQuantity: > 1m } && Rounding.RoundMoney(p.Price!.Value) < gross)
            .OrderBy(p => p.Price).FirstOrDefault();
        if (quantity is not null)
        {
            var each = CheckoutPrice(Rounding.RoundMoney(quantity.Price!.Value), includesTax, taxes);
            return new LabelOffer(null, $"Desde {quantity.MinQuantity!.Value.ToString("0.###", Colombia)} {quantityUnit}: {Money(each)} {(quantityUnit == "und" ? "c/u" : "/" + quantityUnit)}", quantity.ValidTo);
        }

        var multi = applicable.Where(p => p is { Kind: "MULTI_BUY", BuyQuantity: > 1, PayQuantity: >= 0 } && p.BuyQuantity > p.PayQuantity)
            .OrderByDescending(p => (decimal)(p.BuyQuantity!.Value - p.PayQuantity!.Value) / p.BuyQuantity.Value).FirstOrDefault();
        return multi is null ? null : new LabelOffer(null, MultiBuyText(multi), multi.ValidTo);
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
        return factor == 0m ? null : $"{Money(value / netContent.Value * factor)} por {unit}";
    }

    /// <summary>
    /// Dinero en pesos sin decimales: <c>$ 12.500</c>. Los centavos se redondean HACIA ARRIBA para que el precio exhibido nunca quede por
    /// debajo de lo que cobra la caja.
    /// </summary>
    public static string Money(decimal value) => "$ " + decimal.Ceiling(value).ToString("N0", Colombia);

    private static string MultiBuyText(LabelPromotion multi) => string.Create(Colombia, $"Lleve {multi.BuyQuantity} pague {multi.PayQuantity}");

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
