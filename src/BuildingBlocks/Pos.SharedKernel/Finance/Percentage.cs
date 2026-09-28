using System.Globalization;

namespace Pos.SharedKernel.Finance;

/// <summary>Porcentaje o tasa expresada en base 100 (IVA 19 % = 19). Precisión de 4 decimales.</summary>
public readonly record struct Percentage
{
    public const int Decimals = 4;

    public static readonly Percentage Zero = new(0m);

    public static readonly Percentage OneHundred = new(100m);

    private Percentage(decimal value) => Value = value;

    public decimal Value { get; }

    /// <summary>Valor como fracción (19 % → 0,19).</summary>
    public decimal Fraction => Value / 100m;

    public static Percentage Of(decimal value)
    {
        Guard.NotNegative(value);
        Guard.MaxDecimals(value, Decimals);
        return new Percentage(value);
    }

    /// <summary>Aplica el porcentaje sin redondear (19 % de 1.000 = 190).</summary>
    public decimal ApplyTo(decimal amount) => amount * Value / 100m;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Value}%");
}
