using System.Globalization;
using System.Text;

namespace Pos.SharedKernel.Text;

/// <summary>
/// Formato único de los estados y tipos en las respuestas de la API (D17E-02): MAYÚSCULAS_CON_GUION (<c>ACTIVE</c>, <c>SALES_FLOOR</c>,
/// <c>IN_TRANSIT</c>), el mismo que guarda la base de datos. Las peticiones siguen aceptando el nombre de la enumeración (<c>Active</c>).
/// La interfaz usa <see cref="Normalize"/> para comparar sin importar en cuál de los dos formatos llegó un valor.
/// </summary>
public static class EnumText
{
    /// <summary><c>SalesFloor</c> → <c>SALES_FLOOR</c>.</summary>
    public static string UpperSnake<TEnum>(this TEnum value)
        where TEnum : struct, Enum => Normalize(value.ToString());

    /// <summary>
    /// Lleva cualquiera de los dos formatos a MAYÚSCULAS_CON_GUION: <c>SalesFloor</c>, <c>salesFloor</c> y <c>SALES_FLOOR</c> dan
    /// <c>SALES_FLOOR</c>. Vacío o nulo da vacío.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim();
        if (text.Contains('_', StringComparison.Ordinal) || !text.Any(char.IsLower))
        {
            return text.ToUpperInvariant();
        }

        var builder = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(text[i - 1]) || (i + 1 < text.Length && char.IsLower(text[i + 1]) && char.IsUpper(text[i - 1]))))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>¿Representan el mismo estado? (<c>Active</c> = <c>ACTIVE</c>, <c>InTransit</c> = <c>IN_TRANSIT</c>).</summary>
    public static bool Same(string? a, string? b) => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    /// <summary><c>SALES_FLOOR</c> → <c>SalesFloor</c> (el nombre que esperan las peticiones).</summary>
    public static string ToEnumName(string? value) =>
        string.Concat(Normalize(value).Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part[..1] + part[1..].ToLower(CultureInfo.InvariantCulture)));
}
