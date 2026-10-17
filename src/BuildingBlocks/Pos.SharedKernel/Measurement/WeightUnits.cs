namespace Pos.SharedKernel.Measurement;

/// <summary>
/// Unidades de peso que puede tener un producto vendido por peso, y la conversión del peso de la báscula (siempre en kilogramos) a la
/// cantidad en la unidad del producto (Fase 7-C parte C). La <b>libra colombiana</b> (<c>LB500</c>) es de 500 g; la libra
/// internacional (<c>LB</c>) de 453,59237 g. Debe coincidir con <c>ref.unit_conversions</c> (R__ref__units_of_measure.sql).
/// </summary>
public static class WeightUnits
{
    public const string Kilogram = "KG";
    public const string Gram = "G";
    public const string Pound = "LB";
    public const string ColombianPound = "LB500";

    /// <summary>Kilogramos que pesa una unidad; <c>null</c> si no es una unidad de peso.</summary>
    public static decimal? KilogramsPerUnit(string? unitCode) => Normalize(unitCode) switch
    {
        Kilogram => 1m,
        Gram => 0.001m,
        Pound => 0.45359237m,
        ColombianPound => 0.5m,
        _ => null,
    };

    public static bool IsWeight(string? unitCode) => KilogramsPerUnit(unitCode) is not null;

    /// <summary>
    /// Cantidad en la unidad del producto para un peso en kg: 0,700 kg → 0,700 KG, 700 G o 1,400 LB500. Gramos sin decimales y las
    /// demás con 3 (lo que admite la tabla de unidades). <c>null</c> si la unidad no es de peso.
    /// </summary>
    public static decimal? QuantityFromKg(decimal kilograms, string? unitCode)
    {
        if (KilogramsPerUnit(unitCode) is not { } perUnit)
        {
            return null;
        }

        var decimals = Normalize(unitCode) == Gram ? 0 : 3;
        return decimal.Round(kilograms / perUnit, decimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>Peso en kg de una cantidad en la unidad del producto (para el tiquete); <c>null</c> si la unidad no es de peso.</summary>
    public static decimal? ToKilograms(decimal quantity, string? unitCode) =>
        KilogramsPerUnit(unitCode) is { } perUnit ? decimal.Round(quantity * perUnit, 3, MidpointRounding.AwayFromZero) : null;

    /// <summary>Abreviatura para pantallas y tiquetes: kg, g, lb (500 g) o lb.</summary>
    public static string Label(string? unitCode) => Normalize(unitCode) switch
    {
        Kilogram => "kg",
        Gram => "g",
        ColombianPound => "lb",
        Pound => "lb int.",
        var other => other.ToLowerInvariant(),
    };

    private static string Normalize(string? unitCode) => (unitCode ?? string.Empty).Trim().ToUpperInvariant();
}
