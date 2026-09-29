using System.Globalization;

namespace Pos.Modules.Organization.Domain;

/// <summary>
/// Dígito de verificación (DV) del NIT colombiano: módulo 11 con los pesos oficiales de la DIAN
/// (3, 7, 13, 17, 19, 23, 29, 37, 41, 43, 47, 53, 59, 67, 71), aplicados de derecha a izquierda.
/// Resto 0 o 1 → DV igual al resto; en otro caso DV = 11 − resto.
/// </summary>
public static class Nit
{
    private static readonly int[] Weights = [3, 7, 13, 17, 19, 23, 29, 37, 41, 43, 47, 53, 59, 67, 71];

    public const int MaxDigits = 15;

    public static bool IsWellFormed(string? number) =>
        !string.IsNullOrEmpty(number) && number.Length <= MaxDigits && number.All(char.IsAsciiDigit);

    public static int ComputeCheckDigit(string number)
    {
        if (!IsWellFormed(number))
        {
            throw new ArgumentException("El NIT debe tener solo dígitos (máximo 15), sin DV ni separadores.", nameof(number));
        }

        var sum = 0;
        for (var i = 0; i < number.Length; i++)
        {
            var digit = number[number.Length - 1 - i] - '0';
            sum += digit * Weights[i];
        }

        var remainder = sum % 11;
        return remainder <= 1 ? remainder : 11 - remainder;
    }

    public static bool IsValid(string number, string checkDigit) =>
        IsWellFormed(number)
        && checkDigit is { Length: 1 }
        && ComputeCheckDigit(number).ToString(CultureInfo.InvariantCulture) == checkDigit;
}
