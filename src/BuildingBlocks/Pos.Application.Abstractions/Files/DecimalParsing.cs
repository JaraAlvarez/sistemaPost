using System.Globalization;

namespace Pos.Application.Abstractions.Files;

/// <summary>
/// Números escritos por personas en Colombia: "4.980" es cuatro mil novecientos ochenta (punto de miles), "1,25" es uno
/// coma veinticinco. Reglas:
/// <list type="bullet">
/// <item>Con punto y coma a la vez, el último que aparece es el separador decimal ("1.234,5" y "1,234.5").</item>
/// <item>Solo comas: decimal si hay una ("1,25"); miles si agrupan de a tres ("1,234,567").</item>
/// <item>Solo puntos: miles si agrupan de a tres ("4.980", "1.250.000"); decimal en los demás casos ("0.5", "12.75").</item>
/// </list>
/// Se ignoran espacios y el signo "$". Las celdas numéricas de Excel no pasan por aquí: llegan como número.
/// </summary>
public static class DecimalParsing
{
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Replace("$", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        if (cleaned.Length == 0)
        {
            return false;
        }

        var lastDot = cleaned.LastIndexOf('.');
        var lastComma = cleaned.LastIndexOf(',');
        string normalized;
        if (lastDot >= 0 && lastComma >= 0)
        {
            var decimalSeparator = lastDot > lastComma ? '.' : ',';
            var thousands = decimalSeparator == '.' ? ',' : '.';
            normalized = cleaned.Replace(thousands.ToString(), string.Empty, StringComparison.Ordinal).Replace(decimalSeparator, '.');
        }
        else if (lastComma >= 0)
        {
            normalized = IsThousandsGrouping(cleaned, ',') ? cleaned.Replace(",", string.Empty, StringComparison.Ordinal) : cleaned.Replace(',', '.');
        }
        else if (lastDot >= 0)
        {
            normalized = IsThousandsGrouping(cleaned, '.') ? cleaned.Replace(".", string.Empty, StringComparison.Ordinal) : cleaned;
        }
        else
        {
            normalized = cleaned;
        }

        return decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    // "1.234", "12.345.678" (grupos de 3 después del primero, que tiene de 1 a 3 dígitos). Una sola coma nunca es miles.
    private static bool IsThousandsGrouping(string text, char separator)
    {
        var body = text.StartsWith('-') ? text[1..] : text;
        var parts = body.Split(separator);
        if (parts.Length < 2 || (separator == ',' && parts.Length == 2))
        {
            return false;
        }

        return parts[0].Length is >= 1 and <= 3 && parts[0].All(char.IsAsciiDigit)
            && parts.Skip(1).All(p => p.Length == 3 && p.All(char.IsAsciiDigit));
    }
}
