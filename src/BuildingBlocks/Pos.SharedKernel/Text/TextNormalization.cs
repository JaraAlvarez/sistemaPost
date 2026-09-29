namespace Pos.SharedKernel.Text;

/// <summary>Normalización de texto para búsquedas y encabezados de archivos (sin tildes, minúsculas).</summary>
public static class TextNormalization
{
    /// <summary>Minúsculas, sin tildes ni diéresis, espacios colapsados. "Café  ÁGUILA" → "cafe aguila".</summary>
    public static string ForSearch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);
        var previousSpace = false;
        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!previousSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousSpace = true;
                continue;
            }

            builder.Append(char.ToLowerInvariant(c));
            previousSpace = false;
        }

        return builder.ToString().TrimEnd().Normalize(System.Text.NormalizationForm.FormC);
    }

    /// <summary>Encabezado de columna: como <see cref="ForSearch"/> y con los espacios y guiones como "_".</summary>
    public static string ForHeader(string? header) =>
        ForSearch(header).Replace(' ', '_').Replace('-', '_');
}
