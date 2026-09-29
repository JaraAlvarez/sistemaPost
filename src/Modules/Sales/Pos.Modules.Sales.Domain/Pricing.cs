using Pos.SharedKernel.Finance;

namespace Pos.Modules.Sales.Domain;

/// <summary>Impuesto de una línea: porcentual (<c>Rate</c>, p. ej. 19) o fijo por unidad base (<c>FixedAmount</c>).</summary>
public sealed record PricingTax(Guid TaxId, string Code, string Kind, decimal? Rate, decimal? FixedAmount);

/// <summary>Descuento manual (siempre autorizado): porcentaje o valor, nunca ambos.</summary>
public sealed record ManualDiscount(decimal? Percent, decimal? Amount);

/// <summary>
/// Línea que entra al motor. <c>Quantity</c> en la unidad de venta (presentación o kilos); <c>Factor</c> = unidades base por
/// unidad de venta; <c>UnitPrice</c> por unidad de venta, con impuestos si <c>PriceIncludesTax</c>. Una línea con precio
/// modificado a mano no recibe promociones (<c>PromotionsAllowed</c>).
/// </summary>
public sealed record PricingLine(
    Guid Key,
    Guid ProductId,
    Guid? PackagingId,
    Guid CategoryId,
    Guid? BrandId,
    decimal Quantity,
    decimal Factor,
    decimal UnitPrice,
    bool PriceIncludesTax,
    IReadOnlyList<PricingTax> Taxes,
    ManualDiscount? Discount = null,
    bool PromotionsAllowed = true)
{
    public decimal BaseQuantity => Quantity * Factor;
}

public enum PromotionKind
{
    /// <summary>Lleve N pague M: por cada grupo de N unidades salen gratis las N − M de menor precio.</summary>
    MultiBuy,

    /// <summary>Precio especial en la vigencia.</summary>
    SpecialPrice,

    /// <summary>Porcentaje de descuento sobre el precio de lista.</summary>
    PercentOff,

    /// <summary>Precio por cantidad: desde X unidades base, cada una a un precio.</summary>
    QuantityPrice,

    /// <summary>Combo: un conjunto de productos a un precio fijo.</summary>
    Combo,
}

/// <summary>
/// A qué aplica una promoción: un producto (y opcionalmente una presentación), un conjunto de categorías (la categoría y sus
/// subcategorías, ya expandidas) o una marca. En los combos, <c>Quantity</c> es la cantidad del componente en unidades de venta.
/// Si hay presentación, los precios de la regla son por esa presentación; si no, por unidad base.
/// </summary>
public sealed record PromotionTarget(
    Guid? ProductId = null, Guid? PackagingId = null, IReadOnlySet<Guid>? CategoryIds = null, Guid? BrandId = null, decimal Quantity = 1m)
{
    public bool Matches(PricingLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (ProductId is { } product)
        {
            return line.ProductId == product && (PackagingId is null || line.PackagingId == PackagingId);
        }

        if (CategoryIds is { Count: > 0 } categories)
        {
            return categories.Contains(line.CategoryId);
        }

        return BrandId is { } brand && line.BrandId == brand;
    }

    /// <summary>Precio de la regla por unidad de venta de la línea.</summary>
    public decimal PerSaleUnit(decimal price, PricingLine line) => PackagingId is not null ? price : price * line.Factor;
}

/// <summary>Regla de una promoción activa (la entrega el módulo Promotions ya filtrada por sucursal, fecha y horario).</summary>
public sealed record PromotionRule(
    Guid Id,
    string Name,
    PromotionKind Kind,
    IReadOnlyList<PromotionTarget> Targets,
    int? BuyQuantity = null,
    int? PayQuantity = null,
    decimal? Price = null,
    decimal? Percent = null,
    decimal? MinQuantity = null,
    int? MaxApplications = null,
    string? TicketText = null);

public sealed record PricedTax(Guid TaxId, string Code, string Kind, decimal? Rate, decimal? FixedAmount, decimal Base, decimal Amount);

/// <summary>Resultado de una línea: <c>Gross</c> − promoción − descuentos = neto (= <c>Total</c> si el precio incluye impuestos).</summary>
public sealed record PricedLine(
    Guid Key,
    decimal Gross,
    Guid? PromotionId,
    string? PromotionName,
    decimal PromotionDiscount,
    decimal LineDiscount,
    decimal GlobalDiscountShare,
    decimal Base,
    decimal TaxTotal,
    decimal Total,
    IReadOnlyList<PricedTax> Taxes)
{
    public decimal Net => Gross - PromotionDiscount - LineDiscount - GlobalDiscountShare;
}

public sealed record PricedSale(
    IReadOnlyList<PricedLine> Lines, decimal Gross, decimal PromotionTotal, decimal DiscountTotal, decimal Subtotal, decimal TaxTotal, decimal Total);

/// <summary>
/// Motor de cálculo de la venta (doc 08 §M, D7-02, D7-16): puro y determinista, reutilizable por la futura caja autónoma.
/// Orden: bruto → promoción (una por línea, la que más descuenta, sin acumular) → descuento de línea → descuento global
/// prorrateado → impuestos. Con precio que incluye impuestos el total de la línea es exactamente el neto y la base absorbe
/// el redondeo (el cliente paga el precio exhibido).
/// </summary>
public static class SaleCalculator
{
    private static readonly RoundingPolicy Rounding = RoundingPolicy.Colombia;

    public static PricedSale Calculate(IReadOnlyList<PricingLine> lines, IReadOnlyList<PromotionRule> promotions, ManualDiscount? globalDiscount = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(promotions);
        var gross = lines.ToDictionary(l => l.Key, l => Rounding.RoundMoney(l.UnitPrice * l.Quantity));
        var promotion = ApplyPromotions(lines, promotions, gross);

        var afterLine = new Dictionary<Guid, decimal>();
        var lineDiscounts = new Dictionary<Guid, decimal>();
        foreach (var line in lines)
        {
            var net = gross[line.Key] - promotion.GetValueOrDefault(line.Key).Discount;
            var discount = DiscountValue(line.Discount, net);
            lineDiscounts[line.Key] = discount;
            afterLine[line.Key] = net - discount;
        }

        var globalTotal = DiscountValue(globalDiscount, afterLine.Values.Sum());
        var shares = Prorate(globalTotal, lines.Select(l => (l.Key, afterLine[l.Key])).ToList());

        var priced = lines.Select(line =>
        {
            var net = afterLine[line.Key] - shares.GetValueOrDefault(line.Key);
            var (baseAmount, taxes, total) = Taxes(line, net);
            var (promoId, promoName, promoDiscount) = promotion.GetValueOrDefault(line.Key);
            return new PricedLine(
                line.Key, gross[line.Key], promoId, promoName, promoDiscount, lineDiscounts[line.Key], shares.GetValueOrDefault(line.Key), baseAmount,
                taxes.Sum(t => t.Amount), total, taxes);
        }).ToList();

        return new PricedSale(
            priced, priced.Sum(l => l.Gross), priced.Sum(l => l.PromotionDiscount), priced.Sum(l => l.LineDiscount + l.GlobalDiscountShare),
            priced.Sum(l => l.Base), priced.Sum(l => l.TaxTotal), priced.Sum(l => l.Total));
    }

    /// <summary>Valor de un descuento manual sobre un neto: porcentaje redondeado o valor, nunca mayor que el neto.</summary>
    public static decimal DiscountValue(ManualDiscount? discount, decimal net)
    {
        if (discount is null || net <= 0m)
        {
            return 0m;
        }

        var value = discount.Percent is { } percent ? Rounding.RoundMoney(net * percent / 100m) : Rounding.RoundMoney(discount.Amount ?? 0m);
        return Math.Clamp(value, 0m, net);
    }

    /// <summary>Reparte un valor entre las líneas en proporción a su peso; el residuo del redondeo va a la línea de mayor peso.</summary>
    public static IReadOnlyDictionary<Guid, decimal> Prorate(decimal amount, IReadOnlyList<(Guid Key, decimal Weight)> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var result = new Dictionary<Guid, decimal>();
        var total = weights.Sum(w => w.Weight);
        if (amount == 0m || total <= 0m)
        {
            return result;
        }

        foreach (var (key, weight) in weights)
        {
            result[key] = Rounding.RoundMoney(amount * weight / total);
        }

        var largest = weights.OrderByDescending(w => w.Weight).ThenBy(w => w.Key).First().Key;
        result[largest] += amount - result.Values.Sum();
        return result;
    }

    private static (decimal Base, IReadOnlyList<PricedTax> Taxes, decimal Total) Taxes(PricingLine line, decimal net)
    {
        var baseQuantity = line.BaseQuantity;
        var fixedTaxes = line.Taxes.Where(t => t.Rate is null && t.FixedAmount is not null)
            .Select(t => (Tax: t, Amount: Rounding.RoundMoney(t.FixedAmount!.Value * baseQuantity))).ToList();
        var percentTaxes = line.Taxes.Where(t => t.Rate is not null).ToList();
        var fixedTotal = fixedTaxes.Sum(t => t.Amount);
        var rateTotal = percentTaxes.Sum(t => t.Rate!.Value);

        decimal taxable;
        if (line.PriceIncludesTax)
        {
            taxable = Math.Max(0m, Rounding.RoundMoney((net - fixedTotal) / (1m + (rateTotal / 100m))));
        }
        else
        {
            taxable = net;
        }

        var percentAmounts = percentTaxes.Select(t => (Tax: t, Amount: Rounding.RoundMoney(taxable * t.Rate!.Value / 100m))).ToList();
        var taxTotal = fixedTotal + percentAmounts.Sum(t => t.Amount);
        var baseAmount = line.PriceIncludesTax ? net - taxTotal : net;
        var total = line.PriceIncludesTax ? net : net + taxTotal;
        IReadOnlyList<PricedTax> taxes =
        [
            .. percentAmounts.Select(t => new PricedTax(t.Tax.TaxId, t.Tax.Code, t.Tax.Kind, t.Tax.Rate, null, baseAmount, t.Amount)),
            .. fixedTaxes.Select(t => new PricedTax(t.Tax.TaxId, t.Tax.Code, t.Tax.Kind, null, t.Tax.FixedAmount, baseQuantity, t.Amount)),
        ];
        return (baseAmount, taxes, total);
    }

    /// <summary>
    /// Asigna promociones (D7-16): en cada ronda gana la promoción que más descuenta sobre las líneas aún libres y sus líneas
    /// participantes quedan tomadas (una promoción por línea, sin acumular). Empates: la de menor Id (determinista).
    /// </summary>
    private static Dictionary<Guid, (Guid? Id, string? Name, decimal Discount)> ApplyPromotions(
        IReadOnlyList<PricingLine> lines, IReadOnlyList<PromotionRule> promotions, IReadOnlyDictionary<Guid, decimal> gross)
    {
        var assigned = new Dictionary<Guid, (Guid? Id, string? Name, decimal Discount)>();
        var free = lines.Where(l => l.PromotionsAllowed && l.Quantity > 0m && l.UnitPrice > 0m).ToList();
        var pending = promotions.OrderBy(p => p.Id).ToList();
        while (free.Count > 0 && pending.Count > 0)
        {
            (PromotionRule Rule, Evaluation Result)? best = null;
            foreach (var rule in pending)
            {
                var evaluation = Evaluate(rule, free, gross);
                if (evaluation.Total > 0m && (best is null || evaluation.Total > best.Value.Result.Total))
                {
                    best = (rule, evaluation);
                }
            }

            if (best is not { } winner)
            {
                break;
            }

            foreach (var key in winner.Result.Participants)
            {
                assigned[key] = (winner.Rule.Id, winner.Rule.TicketText ?? winner.Rule.Name, winner.Result.Discounts.GetValueOrDefault(key));
            }

            free.RemoveAll(l => winner.Result.Participants.Contains(l.Key));
            pending.Remove(winner.Rule);
        }

        return assigned;
    }

    private sealed record Evaluation(IReadOnlyDictionary<Guid, decimal> Discounts, IReadOnlySet<Guid> Participants)
    {
        public static readonly Evaluation None = new(new Dictionary<Guid, decimal>(), new HashSet<Guid>());

        public decimal Total => Discounts.Values.Sum();
    }

    private static Evaluation Evaluate(PromotionRule rule, List<PricingLine> free, IReadOnlyDictionary<Guid, decimal> gross) => rule.Kind switch
    {
        PromotionKind.SpecialPrice or PromotionKind.QuantityPrice when rule.Price is { } price => PerLine(rule, free, gross, (line, target) =>
            rule.Kind == PromotionKind.QuantityPrice && line.BaseQuantity < (rule.MinQuantity ?? decimal.MaxValue)
                ? 0m
                : gross[line.Key] - Rounding.RoundMoney(target.PerSaleUnit(price, line) * line.Quantity)),
        PromotionKind.PercentOff when rule.Percent is { } percent => PerLine(rule, free, gross, (line, _) => Rounding.RoundMoney(gross[line.Key] * percent / 100m)),
        PromotionKind.MultiBuy when rule is { BuyQuantity: { } buy, PayQuantity: { } pay } && buy > pay && pay >= 0 => MultiBuy(rule, free, buy, pay),
        PromotionKind.Combo when rule.Price is { } comboPrice => Combo(rule, free, comboPrice),
        _ => Evaluation.None,
    };

    private static Evaluation PerLine(
        PromotionRule rule, List<PricingLine> free, IReadOnlyDictionary<Guid, decimal> gross, Func<PricingLine, PromotionTarget, decimal> discountOf)
    {
        var discounts = new Dictionary<Guid, decimal>();
        foreach (var line in free)
        {
            if (rule.Targets.FirstOrDefault(t => t.Matches(line)) is { } target
                && Math.Clamp(discountOf(line, target), 0m, gross[line.Key]) is var discount and > 0m)
            {
                discounts[line.Key] = discount;
            }
        }

        return new Evaluation(discounts, discounts.Keys.ToHashSet());
    }

    private static Evaluation MultiBuy(PromotionRule rule, List<PricingLine> free, int buy, int pay)
    {
        // Unidades enteras de las líneas que cumplen (mezcla de productos permitida), de mayor a menor precio.
        var units = free.Where(l => rule.Targets.Any(t => t.Matches(l)) && decimal.Truncate(l.Quantity) == l.Quantity)
            .SelectMany(l => Enumerable.Repeat((l.Key, l.UnitPrice), (int)Math.Min(l.Quantity, 10_000m)))
            .OrderByDescending(u => u.UnitPrice).ThenBy(u => u.Key)
            .ToList();
        var groups = units.Count / buy;
        if (rule.MaxApplications is { } max)
        {
            groups = Math.Min(groups, max);
        }

        var discounts = new Dictionary<Guid, decimal>();
        var participants = new HashSet<Guid>();
        for (var g = 0; g < groups; g++)
        {
            var group = units.GetRange(g * buy, buy);
            participants.UnionWith(group.Select(u => u.Key));
            foreach (var (key, price) in group.Skip(pay))
            {
                discounts[key] = discounts.GetValueOrDefault(key) + price;
            }
        }

        return new Evaluation(discounts, participants);
    }

    private static Evaluation Combo(PromotionRule rule, List<PricingLine> free, decimal comboPrice)
    {
        if (rule.Targets.Count == 0 || rule.Targets.Any(t => t.Quantity <= 0m))
        {
            return Evaluation.None;
        }

        // Cantidad de combos completos: el componente más escaso manda.
        var available = rule.Targets.Select(t => free.Where(t.Matches).Sum(l => l.Quantity)).ToList();
        var combos = (int)rule.Targets.Select((t, i) => decimal.Floor(available[i] / t.Quantity)).Min();
        if (rule.MaxApplications is { } max)
        {
            combos = Math.Min(combos, max);
        }

        if (combos <= 0)
        {
            return Evaluation.None;
        }

        // Toma las unidades de cada componente de las líneas en orden y calcula su valor de lista.
        var taken = new Dictionary<Guid, decimal>();
        var used = new Dictionary<Guid, decimal>();
        foreach (var target in rule.Targets)
        {
            var needed = target.Quantity * combos;
            foreach (var line in free.Where(target.Matches).OrderBy(l => l.Key))
            {
                var take = Math.Min(needed, line.Quantity - used.GetValueOrDefault(line.Key));
                if (take <= 0m)
                {
                    continue;
                }

                used[line.Key] = used.GetValueOrDefault(line.Key) + take;
                taken[line.Key] = taken.GetValueOrDefault(line.Key) + Rounding.RoundMoney(take * line.UnitPrice);
                needed -= take;
                if (needed == 0m)
                {
                    break;
                }
            }
        }

        var discount = taken.Values.Sum() - Rounding.RoundMoney(comboPrice * combos);
        if (discount <= 0m)
        {
            return Evaluation.None;
        }

        var shares = Prorate(discount, [.. taken.Select(t => (t.Key, t.Value))]);
        return new Evaluation(shares, taken.Keys.ToHashSet());
    }
}
