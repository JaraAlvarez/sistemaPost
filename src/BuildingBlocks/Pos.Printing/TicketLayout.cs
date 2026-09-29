using System.Globalization;
using System.Text;

namespace Pos.Printing;

/// <summary>Distribución del tiquete en columnas de ancho fijo (80 mm = 42 columnas, 58 mm = 32) y su vista en texto plano.</summary>
public static class TicketLayout
{
    public const int Columns80Mm = 42;

    public const int Columns58Mm = 32;

    private static readonly CultureInfo Colombia = CultureInfo.GetCultureInfo("es-CO");

    public static int ColumnsFor(int paperWidthMm) => paperWidthMm <= 58 ? Columns58Mm : Columns80Mm;

    /// <summary>Valor en pesos sin decimales si son cero (12.500 · 6.225,50).</summary>
    public static string Money(decimal value) =>
        decimal.Truncate(value) == value ? value.ToString("N0", Colombia) : value.ToString("N2", Colombia);

    public static string Quantity(decimal value) => value.ToString(decimal.Truncate(value) == value ? "0" : "0.###", Colombia);

    /// <summary>Parte un texto en líneas de hasta <paramref name="width"/> columnas, por palabras (una palabra larga se corta).</summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        var lines = new List<string>();
        foreach (var paragraph in (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var current = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var remaining = word;
                while (remaining.Length > width)
                {
                    if (current.Length > 0)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                    }

                    lines.Add(remaining[..width]);
                    remaining = remaining[width..];
                }

                if (current.Length > 0 && current.Length + 1 + remaining.Length > width)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(remaining);
            }

            lines.Add(current.ToString());
        }

        return lines;
    }

    /// <summary>Izquierda y derecha en una línea; si no caben, la izquierda se parte y la derecha queda en la última línea.</summary>
    public static IReadOnlyList<string> Columns(string left, string right, int width)
    {
        right ??= string.Empty;
        if (right.Length >= width)
        {
            return [.. Wrap(left, width), right[..width]];
        }

        var available = width - right.Length - 1;
        var wrapped = Wrap(left, Math.Max(1, available)).ToList();
        var last = wrapped[^1];
        wrapped[^1] = last + new string(' ', width - last.Length - right.Length) + right;
        return wrapped;
    }

    public static string Align(string text, TicketAlign align, int width)
    {
        text ??= string.Empty;
        if (text.Length >= width)
        {
            return text;
        }

        return align switch
        {
            TicketAlign.Center => new string(' ', (width - text.Length) / 2) + text,
            TicketAlign.Right => new string(' ', width - text.Length) + text,
            _ => text,
        };
    }

    /// <summary>Vista del tiquete en texto plano (pruebas, vista previa y transporte "archivo" legible).</summary>
    public static string ToText(TicketDocument ticket, int columns)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var builder = new StringBuilder();
        foreach (var line in Lines(ticket, columns))
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    public static IEnumerable<string> Lines(TicketDocument ticket, int columns)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        foreach (var element in ticket.Elements)
        {
            switch (element)
            {
                case TextLine text:
                    var width = text.DoubleSize ? Math.Max(1, columns / 2) : columns;
                    foreach (var line in Wrap(text.Text, width))
                    {
                        yield return Align(line, text.Align, width).TrimEnd();
                    }

                    break;
                case ColumnsLine pair:
                    foreach (var line in Columns(pair.Left, pair.Right, columns))
                    {
                        yield return line.TrimEnd();
                    }

                    break;
                case SeparatorLine separator:
                    yield return new string(separator.Character, columns);
                    break;
                case BarcodeElement barcode:
                    yield return Align($"|| {barcode.Data} ||", TicketAlign.Center, columns);
                    break;
                case QrElement:
                    yield return Align("[QR]", TicketAlign.Center, columns);
                    break;
                case FeedElement feed:
                    for (var i = 0; i < Math.Clamp(feed.Lines, 0, 10); i++)
                    {
                        yield return string.Empty;
                    }

                    break;
            }
        }
    }
}
