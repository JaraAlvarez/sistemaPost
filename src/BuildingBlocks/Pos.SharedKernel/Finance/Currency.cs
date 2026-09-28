namespace Pos.SharedKernel.Finance;

/// <summary>Moneda ISO 4217 y su cantidad de decimales.</summary>
public sealed record Currency
{
    /// <summary>Peso colombiano. ISO 4217 define 2 decimales (usados en documentos DIAN).</summary>
    public static readonly Currency Cop = new("COP", 2);

    public static readonly Currency Usd = new("USD", 2);

    private Currency(string code, int decimals)
    {
        Code = code;
        Decimals = decimals;
    }

    public string Code { get; }

    public int Decimals { get; }

    public static Currency Create(string code, int decimals)
    {
        Guard.NotNullOrWhiteSpace(code);
        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException("El código de moneda debe tener 3 letras mayúsculas (ISO 4217).", nameof(code));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimals, 4);
        return new Currency(code, decimals);
    }

    public override string ToString() => Code;
}
