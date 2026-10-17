using System.Text;

namespace Pos.Cloud.Host.Downloads;

/// <summary>
/// Manual de instalación (D13B-06): <c>docs/manual-instalacion.md</c> va incrustado en el ensamblado al compilar (una sola fuente) y
/// se muestra como página del portal con <see cref="SafeMarkdown"/>.
/// </summary>
internal static class InstallManual
{
    public const string ResourceName = "Pos.Cloud.Host.Docs.manual-instalacion.md";

    private static readonly Lazy<string> Markdown = new(() =>
    {
        using var stream = typeof(InstallManual).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Falta el recurso incrustado {ResourceName} (docs/manual-instalacion.md).");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    // Un nivel menos: el título de la página (h1) lo pone el encabezado del portal.
    private static readonly Lazy<string> Html = new(() => SafeMarkdown.ToHtml(Markdown.Value, headingOffset: 1));

    /// <summary>Texto original del manual.</summary>
    public static string Source => Markdown.Value;

    /// <summary>El manual en HTML (todo el texto codificado; ver <see cref="SafeMarkdown"/>).</summary>
    public static string ToHtml() => Html.Value;
}

/// <summary>
/// Convierte a HTML solo el pequeño subconjunto de Markdown que usa el manual: títulos (<c>#</c> a <c>####</c>), párrafos, listas
/// (<c>-</c> y <c>1.</c>), citas (<c>&gt;</c>), negrita (<c>**…**</c>), código (<c>`…`</c>) y enlaces <c>[texto](https://…)</c>.
/// TODO el texto se codifica (nunca se copia HTML del archivo) y los enlaces solo pueden ser http(s): así el resultado es seguro
/// aunque el archivo traiga etiquetas o <c>javascript:</c>. Lo demás se muestra como texto.
/// </summary>
internal static class SafeMarkdown
{
    /// <param name="markdown">Texto del manual.</param>
    /// <param name="headingOffset">Niveles que se bajan los títulos (1: <c>#</c> → <c>h2</c>), hasta <c>h6</c>.</param>
    public static string ToHtml(string markdown, int headingOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var html = new StringBuilder();
        var paragraph = new List<string>();
        string? openList = null;

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                html.Append("<p>").Append(Inline(string.Join(' ', paragraph))).Append("</p>\n");
                paragraph.Clear();
            }
        }

        void CloseList()
        {
            if (openList is not null)
            {
                html.Append("</").Append(openList).Append(">\n");
                openList = null;
            }
        }

        foreach (var rawLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                CloseList();
                continue;
            }

            if (Heading(trimmed) is { } heading)
            {
                FlushParagraph();
                CloseList();
                var level = Math.Clamp(heading.Level + headingOffset, 1, 6);
                html.Append("<h").Append(level).Append('>').Append(Inline(heading.Text)).Append("</h").Append(level).Append(">\n");
                continue;
            }

            if (ListItem(trimmed) is { } item)
            {
                FlushParagraph();
                if (openList != item.Tag)
                {
                    CloseList();
                    html.Append('<').Append(item.Tag).Append(item.Start > 1 ? $" start=\"{item.Start}\"" : string.Empty).Append(">\n");
                    openList = item.Tag;
                }

                html.Append("<li>").Append(Inline(item.Text)).Append("</li>\n");
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                CloseList();
                html.Append("<blockquote><p>").Append(Inline(trimmed.TrimStart('>').Trim())).Append("</p></blockquote>\n");
                continue;
            }

            // Línea que continúa el elemento de lista anterior (sangría) o el párrafo.
            if (openList is not null && line.StartsWith("  ", StringComparison.Ordinal))
            {
                var at = html.ToString().LastIndexOf("</li>", StringComparison.Ordinal);
                html.Insert(at, " " + Inline(trimmed));
                continue;
            }

            CloseList();
            paragraph.Add(trimmed);
        }

        FlushParagraph();
        CloseList();
        return html.ToString();
    }

    private static (int Level, string Text)? Heading(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        return level is >= 1 and <= 4 && level < line.Length && line[level] == ' ' ? (level, line[(level + 1)..].Trim()) : null;
    }

    private static (string Tag, string Text, int Start)? ListItem(string line)
    {
        if (line.Length > 2 && (line[0] == '-' || line[0] == '*') && line[1] == ' ')
        {
            return ("ul", line[2..].Trim(), 1);
        }

        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        return digits is > 0 and <= 3 && line.Length > digits + 2 && line[digits] == '.' && line[digits + 1] == ' '
            ? ("ol", line[(digits + 2)..].Trim(), int.Parse(line.AsSpan(0, digits), System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>Negrita, código y enlaces; todo lo demás se codifica como texto.</summary>
    internal static string Inline(string text)
    {
        var html = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`' && text.IndexOf('`', i + 1) is var endCode and > 0)
            {
                html.Append("<code>").Append(Encode(text[(i + 1)..endCode])).Append("</code>");
                i = endCode + 1;
                continue;
            }

            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*' && text.IndexOf("**", i + 2, StringComparison.Ordinal) is var endBold and > 0)
            {
                html.Append("<strong>").Append(Inline(text[(i + 2)..endBold])).Append("</strong>");
                i = endBold + 2;
                continue;
            }

            if (text[i] == '[' && Link(text, i) is { } link)
            {
                if (IsSafeUrl(link.Url))
                {
                    html.Append("<a href=\"").Append(Encode(link.Url)).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">")
                        .Append(Inline(link.Text)).Append("</a>");
                }
                else
                {
                    html.Append(Inline(link.Text));
                }

                i = link.End;
                continue;
            }

            html.Append(Encode(text[i].ToString()));
            i++;
        }

        return html.ToString();
    }

    private static (string Text, string Url, int End)? Link(string text, int start)
    {
        var closeText = text.IndexOf("](", start + 1, StringComparison.Ordinal);
        if (closeText < 0)
        {
            return null;
        }

        var closeUrl = text.IndexOf(')', closeText + 2);
        return closeUrl < 0 ? null : (text[(start + 1)..closeText], text[(closeText + 2)..closeUrl].Trim(), closeUrl + 1);
    }

    private static bool IsSafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    /// <summary>Codifica los caracteres especiales de HTML (deja las tildes y la ñ como están).</summary>
    private static string Encode(string text)
    {
        var encoded = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            encoded.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }

        return encoded.ToString();
    }
}
