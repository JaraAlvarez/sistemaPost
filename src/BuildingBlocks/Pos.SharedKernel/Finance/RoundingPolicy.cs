namespace Pos.SharedKernel.Finance;

/// <summary>
/// Punto único de redondeo del sistema (RN-GEN-07). Ningún módulo redondea por su cuenta:
/// todos usan esta política, configurada por empresa.
/// </summary>
public sealed record RoundingPolicy
{
    /// <summary>Decimales internos de cantidades (kg con gramos, L con ml).</summary>
    public const int QuantityDecimals = 4;

    /// <summary>Decimales internos de costos unitarios.</summary>
    public const int UnitCostDecimals = 4;

    /// <summary>Política por defecto para Colombia: 2 decimales y efectivo redondeado a $50 (moneda más pequeña en circulación).</summary>
    public static readonly RoundingPolicy Colombia = new(moneyDecimals: 2, cashIncrement: 50m);

    public RoundingPolicy(int moneyDecimals, decimal cashIncrement, MidpointRounding mode = MidpointRounding.AwayFromZero)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(moneyDecimals);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(moneyDecimals, 4);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cashIncrement);
        MoneyDecimals = moneyDecimals;
        CashIncrement = cashIncrement;
        Mode = mode;
    }

    /// <summary>Decimales de importes (totales de línea, impuestos, totales de documento).</summary>
    public int MoneyDecimals { get; }

    /// <summary>Múltiplo al que se redondea el cobro en efectivo (p. ej. 50 en Colombia).</summary>
    public decimal CashIncrement { get; }

    /// <summary>Regla para valores en el punto medio. Por defecto "lejos de cero" (0,5 → 1), la usada en comercio.</summary>
    public MidpointRounding Mode { get; }

    public decimal RoundMoney(decimal value) => decimal.Round(value, MoneyDecimals, Mode);

    public decimal RoundQuantity(decimal value) => decimal.Round(value, QuantityDecimals, Mode);

    public decimal RoundUnitCost(decimal value) => decimal.Round(value, UnitCostDecimals, Mode);

    /// <summary>Redondea al múltiplo de efectivo más cercano (p. ej. 4.820 → 4.800; 4.825 → 4.850).</summary>
    public decimal RoundToCash(decimal value) =>
        decimal.Round(value / CashIncrement, 0, Mode) * CashIncrement;
}
