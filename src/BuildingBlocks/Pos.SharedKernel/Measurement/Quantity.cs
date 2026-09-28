using System.Globalization;
using Pos.SharedKernel.Finance;
using Pos.SharedKernel.Results;

namespace Pos.SharedKernel.Measurement;

/// <summary>
/// Cantidad no negativa en una unidad de medida (UND, KG, L…), con hasta 4 decimales.
/// El signo de los movimientos (entrada/salida) se modela aparte, nunca con cantidades negativas.
/// </summary>
public sealed record Quantity
{
    private Quantity(decimal value, string unitCode)
    {
        Value = value;
        UnitCode = unitCode;
    }

    public decimal Value { get; }

    public string UnitCode { get; }

    public bool IsZero => Value == 0m;

    /// <summary>Crea una cantidad validando datos provenientes del usuario.</summary>
    public static Result<Quantity> Create(decimal value, string unitCode)
    {
        if (string.IsNullOrWhiteSpace(unitCode))
        {
            return Error.Validation("QUANTITY.UNIT_REQUIRED", "La unidad de medida es obligatoria.");
        }

        if (value < 0m)
        {
            return Error.Validation("QUANTITY.NEGATIVE", "La cantidad no puede ser negativa.");
        }

        if (!Guard.HasAtMostDecimals(value, RoundingPolicy.QuantityDecimals))
        {
            return Error.Validation(
                "QUANTITY.TOO_MANY_DECIMALS",
                $"La cantidad admite como máximo {RoundingPolicy.QuantityDecimals} decimales.");
        }

        return new Quantity(value, unitCode.Trim().ToUpperInvariant());
    }

    /// <summary>Crea una cantidad a partir de un valor ya validado (lanza excepción si es inválido).</summary>
    public static Quantity Of(decimal value, string unitCode)
    {
        var result = Create(value, unitCode);
        return result.IsSuccess
            ? result.Value
            : throw new ArgumentException(result.Error.Message, nameof(value));
    }

    public static Quantity Zero(string unitCode) => Of(0m, unitCode);

    public static Quantity operator +(Quantity left, Quantity right) => Add(left, right);

    public static Quantity operator -(Quantity left, Quantity right) => Subtract(left, right);

    public static Quantity Add(Quantity left, Quantity right)
    {
        EnsureSameUnit(left, right);
        return new Quantity(left.Value + right.Value, left.UnitCode);
    }

    public static Quantity Subtract(Quantity left, Quantity right)
    {
        EnsureSameUnit(left, right);
        if (right.Value > left.Value)
        {
            throw new InvalidOperationException("El resultado de la resta sería una cantidad negativa.");
        }

        return new Quantity(left.Value - right.Value, left.UnitCode);
    }

    /// <summary>Convierte a otra unidad con un factor (p. ej. "Paquete x6" → UND con factor 6).</summary>
    public Quantity ConvertTo(string targetUnitCode, decimal factor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        return Of(decimal.Round(Value * factor, RoundingPolicy.QuantityDecimals, MidpointRounding.AwayFromZero), targetUnitCode);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Value} {UnitCode}");

    private static void EnsureSameUnit(Quantity left, Quantity right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.UnitCode != right.UnitCode)
        {
            throw new InvalidOperationException(
                $"No se pueden operar cantidades de unidades distintas ({left.UnitCode} y {right.UnitCode}).");
        }
    }
}
