using Pos.SharedKernel.Finance;

namespace Pos.Modules.Purchasing.Domain;

/// <summary>Cómo se reparten los cargos (fletes, acarreos) entre las líneas de la compra (D5-03).</summary>
public enum ProrationMethod
{
    /// <summary>En proporción al valor neto de cada línea (por defecto).</summary>
    Value,

    /// <summary>En proporción a la cantidad en unidad base.</summary>
    Quantity,

    /// <summary>Digitado por línea; la suma debe ser igual al total de cargos.</summary>
    Manual,
}

/// <summary>Impuesto de una línea con su tarifa en la fecha de la factura (porcentaje o valor fijo por unidad base).</summary>
public sealed record CostingTax(Guid? TaxId, string Code, bool IsVat, decimal? Rate, decimal? FixedAmount);

/// <summary>Línea a costear: cantidad en la presentación facturada, factor a unidad base y costo por presentación.</summary>
public sealed record CostingLine(decimal Quantity, decimal Factor, decimal UnitCost, decimal Discount, IReadOnlyList<CostingTax> Taxes, decimal? ManualCharges);

public sealed record CostedTax(Guid? TaxId, string Code, bool IsVat, decimal? Rate, decimal? FixedAmount, decimal Base, decimal Amount, bool IsDeductible);

/// <summary>Resultado del costeo de una línea. <c>NetUnitCost</c>: costo por unidad base con que entra al kardex.</summary>
public sealed record CostedLine(
    decimal BaseQuantity,
    decimal Gross,
    decimal Discount,
    decimal Charges,
    decimal TaxAmount,
    decimal NonDeductibleTax,
    decimal LineTotal,
    decimal NetUnitCost,
    IReadOnlyList<CostedTax> Taxes);

public sealed record CostingTotals(
    decimal Subtotal, decimal DiscountTotal, decimal ChargesTotal, decimal TaxTotal, decimal DeductibleTaxTotal, decimal Total);

public sealed record CostingResult(IReadOnlyList<CostedLine> Lines, CostingTotals Totals);

/// <summary>
/// Costo neto de entrada de una compra (D5-03, doc 07):
/// <c>costo neto por unidad base = (cantidad × costo − descuento + cargos prorrateados + impuestos no descontables) ÷ cantidad base</c>.
/// El IVA es descontable (no es costo) si la empresa es responsable de IVA ⚙️; los demás impuestos (INC, bolsa,
/// saludables) siempre son costo. Importes a 2 decimales, costos unitarios a 4; el residuo del prorrateo va a la línea de
/// mayor peso para que la suma cuadre exactamente.
/// Ejemplo: 10 cajas × 24 u a $60.000, descuento $30.000, flete $12.000 → (600.000 − 30.000 + 12.000) ÷ 240 = $2.425.
/// </summary>
public static class PurchaseCosting
{
    private static readonly RoundingPolicy Rounding = RoundingPolicy.Colombia;

    public static CostingResult Calculate(IReadOnlyList<CostingLine> lines, decimal chargesTotal, ProrationMethod proration, bool vatDeductible)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var charges = Prorate(lines, Rounding.RoundMoney(chargesTotal), proration);
        var costed = new List<CostedLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var baseQuantity = Rounding.RoundQuantity(line.Quantity * line.Factor);
            var gross = Rounding.RoundMoney(line.Quantity * line.UnitCost);
            var discount = Rounding.RoundMoney(line.Discount);
            var net = gross - discount;
            var taxes = line.Taxes.Select(t =>
            {
                var amount = t.Rate is { } rate
                    ? Rounding.RoundMoney(net * rate / 100m)
                    : Rounding.RoundMoney((t.FixedAmount ?? 0m) * baseQuantity);
                return new CostedTax(t.TaxId, t.Code, t.IsVat, t.Rate, t.Rate is null ? t.FixedAmount : null, net, amount, t.IsVat && vatDeductible);
            }).ToList();
            var taxAmount = taxes.Sum(t => t.Amount);
            var nonDeductible = taxes.Where(t => !t.IsDeductible).Sum(t => t.Amount);
            var netUnitCost = baseQuantity > 0m ? Rounding.RoundUnitCost((net + charges[i] + nonDeductible) / baseQuantity) : 0m;
            costed.Add(new CostedLine(baseQuantity, gross, discount, charges[i], taxAmount, nonDeductible, net + taxAmount, netUnitCost, taxes));
        }

        var subtotal = costed.Sum(l => l.Gross);
        var discountTotal = costed.Sum(l => l.Discount);
        var chargeTotal = costed.Sum(l => l.Charges);
        var taxTotal = costed.Sum(l => l.TaxAmount);
        var deductible = costed.Sum(l => l.TaxAmount - l.NonDeductibleTax);
        return new CostingResult(costed, new CostingTotals(subtotal, discountTotal, chargeTotal, taxTotal, deductible, subtotal - discountTotal + chargeTotal + taxTotal));
    }

    /// <summary>Reparte los cargos; con MANUAL devuelve los digitados (la validación de la suma la hace la compra).</summary>
    public static IReadOnlyList<decimal> Prorate(IReadOnlyList<CostingLine> lines, decimal chargesTotal, ProrationMethod proration)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (proration == ProrationMethod.Manual)
        {
            return [.. lines.Select(l => Rounding.RoundMoney(l.ManualCharges ?? 0m))];
        }

        var result = new decimal[lines.Count];
        if (chargesTotal == 0m || lines.Count == 0)
        {
            return result;
        }

        var weights = lines.Select(l => proration == ProrationMethod.Quantity
            ? l.Quantity * l.Factor
            : Rounding.RoundMoney(l.Quantity * l.UnitCost) - Rounding.RoundMoney(l.Discount)).ToArray();
        var totalWeight = weights.Sum();
        if (totalWeight <= 0m)
        {
            weights = [.. lines.Select(l => l.Quantity * l.Factor)];
            totalWeight = weights.Sum();
        }

        for (var i = 0; i < lines.Count; i++)
        {
            result[i] = Rounding.RoundMoney(chargesTotal * weights[i] / totalWeight);
        }

        // El residuo del redondeo va a la línea de mayor peso: Σ = total exacto.
        var largest = Array.IndexOf(weights, weights.Max());
        result[largest] += chargesTotal - result.Sum();
        return result;
    }
}
