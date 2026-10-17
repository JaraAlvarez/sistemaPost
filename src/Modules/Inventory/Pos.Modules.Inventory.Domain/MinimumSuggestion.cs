namespace Pos.Modules.Inventory.Domain;

/// <summary>
/// "Calcular mínimos sugeridos" (Reabastecimiento): con la venta diaria promedio de los últimos días, el mínimo alcanza para los días
/// que tarda en llegar el pedido y el máximo para esos días más los de cobertura. Se redondea hacia arriba (unidades enteras si el
/// producto no admite decimales).
/// </summary>
public static class MinimumSuggestion
{
    public const int MaxDays = 365;

    public static (decimal AverageDaily, decimal Min, decimal Max) Calculate(
        decimal soldQuantity, int days, int leadTimeDays, int coverageDays, bool allowsDecimal)
    {
        if (days is <= 0 or > MaxDays)
        {
            throw new ArgumentOutOfRangeException(nameof(days), days, "Los días de venta van de 1 a 365.");
        }

        var average = Math.Max(0m, soldQuantity) / days;
        var lead = Math.Max(0, leadTimeDays);
        var min = RoundUp(average * lead, allowsDecimal);
        var max = RoundUp(average * (lead + Math.Max(0, coverageDays)), allowsDecimal);
        return (decimal.Round(average, 4, MidpointRounding.AwayFromZero), min, Math.Max(min, max));
    }

    /// <summary>Hacia arriba: entero o, si el producto admite decimales, a 3 decimales.</summary>
    public static decimal RoundUp(decimal value, bool allowsDecimal) =>
        allowsDecimal ? decimal.Ceiling(value * 1000m) / 1000m : decimal.Ceiling(value);
}
