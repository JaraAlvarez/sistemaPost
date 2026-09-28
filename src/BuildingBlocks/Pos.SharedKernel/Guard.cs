using System.Runtime.CompilerServices;

namespace Pos.SharedKernel;

/// <summary>
/// Validaciones de invariantes. Una violación indica un error de programación
/// (los datos del usuario se validan antes, en la capa de aplicación).
/// </summary>
public static class Guard
{
    public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value;
    }

    public static string NotNullOrWhiteSpace(string? value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value;
    }

    public static decimal NotNegative(decimal value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, paramName);
        return value;
    }

    public static decimal MaxDecimals(decimal value, int decimals, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (!HasAtMostDecimals(value, decimals))
        {
            throw new ArgumentException($"El valor {value} tiene más de {decimals} decimales.", paramName);
        }

        return value;
    }

    public static bool HasAtMostDecimals(decimal value, int decimals) =>
        decimal.Round(value, decimals) == value;
}
