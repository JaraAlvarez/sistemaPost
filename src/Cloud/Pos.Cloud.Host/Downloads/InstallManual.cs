using System.Text;
using System.Text.RegularExpressions;

namespace Pos.Cloud.Host.Downloads;

/// <summary>
/// Manual de instalación (D13B-06): <c>docs/manual-instalacion.md</c> va incrustado en el ensamblado al compilar (una sola fuente) y
/// se muestra como página del portal con <see cref="SafeMarkdown"/>.
/// </summary>
internal static class InstallManual
{
    public const string ResourceName = "Pos.Cloud.Host.Docs.manual-instalacion.md";

    /// <summary>Prefijo de los recursos incrustados de las capturas (<c>docs/imagenes/instalacion/*.png</c>).</summary>
    public const string ImageResourcePrefix = "Pos.Cloud.Host.Docs.imagenes.";

    /// <summary>Ruta del portal (relativa a su dirección base) donde se sirven las capturas del manual.</summary>
    public const string ImageRoute = "descargas/manual/imagenes/";

    private static readonly Lazy<string> Markdown = new(() =>
    {
        using var stream = typeof(InstallManual).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Falta el recurso incrustado {ResourceName} (docs/manual-instalacion.md).");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    // Un nivel menos: el título de la página (h1) lo pone el encabezado del portal.
    private static readonly Lazy<string> Html = new(() => SafeMarkdown.ToHtml(Markdown.Value, headingOffset: 1, imageBaseUrl: ImageRoute));

    /// <summary>Texto original del manual.</summary>
    public static string Source => Markdown.Value;

    /// <summary>El manual en HTML (todo el texto codificado; ver <see cref="SafeMarkdown"/>).</summary>
    public static string ToHtml() => Html.Value;

    /// <summary>
    /// Captura del manual por su nombre (<c>01-bienvenida.png</c>), o <c>null</c> si el nombre no es válido o no existe. Solo se leen
    /// los recursos incrustados al compilar: el nombre nunca se usa como ruta de disco.
    /// </summary>
    public static Stream? OpenImage(string? name) =>
        name is not null && SafeMarkdown.IsImageFileName(name)
            ? typeof(InstallManual).Assembly.GetManifestResourceStream(ImageResourcePrefix + name)
            : null;
}

/// <summary>
/// Convierte a HTML solo el pequeño subconjunto de Markdown que usa el manual: títulos (<c>#</c> a <c>####</c>), párrafos, listas
/// (<c>-</c> y <c>1.</c>), citas (<c>&gt;</c>), negrita (<c>**…**</c>), código (<c>`…`</c>), enlaces <c>[texto](https://…)</c> y
/// capturas en su propia línea <c>![pie](imagenes/instalacion/nombre.png)</c>.
/// TODO el texto se codifica (nunca se copia HTML del archivo), los enlaces solo pueden ser http(s) y las imágenes solo pueden ser
/// capturas <c>.png</c> de <c>imagenes/instalacion/</c> con un nombre sencillo (servidas desde el mismo sitio): así el resultado es
/// seguro aunque el archivo traiga etiquetas, <c>javascript:</c> o rutas raras. Lo demás se muestra como texto.
/// </summary>
internal static partial class SafeMarkdown
{
    /// <summary>Carpeta de las capturas, relativa a <c>docs/</c> (así también se ven en GitHub y en el PDF).</summary>
    public const string ImageFolder = "imagenes/instalacion/";

    /// <param name="markdown">Texto del manual.</param>
    /// <param name="headingOffset">Niveles que se bajan los títulos (1: <c>#</c> → <c>h2</c>), hasta <c>h6</c>.</param>
    /// <param name="imageBaseUrl">Ruta donde se sirven las capturas; <c>null</c> = las imágenes se muestran solo como su pie de foto.</param>
    public static string ToHtml(string markdown, int headingOffset = 0, string? imageBaseUrl = null)
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

            if (Image(trimmed) is { } image)
            {
                FlushParagraph();
                CloseList();
                html.Append("<figure class=\"bp-manual-figura\">");
                if (imageBaseUrl is not null)
                {
                    html.Append("<img src=\"").Append(Encode(imageBaseUrl + image.FileName)).Append("\" alt=\"").Append(Encode(image.Caption))
                        .Append("\" loading=\"lazy\">");
                }

                html.Append("<figcaption>").Append(Encode(image.Caption)).Append("</figcaption></figure>\n");
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

    /// <summary>Nombre de captura aceptado: minúsculas, números y guiones, terminado en <c>.png</c> (sin carpetas ni puntos extra).</summary>
    public static bool IsImageFileName(string name) => ImageName().IsMatch(name);

    /// <summary><c>![pie](imagenes/instalacion/nombre.png)</c> sola en la línea; cualquier otra forma no es imagen (queda como texto).</summary>
    private static (string Caption, string FileName)? Image(string line)
    {
        if (!line.StartsWith("![", StringComparison.Ordinal) || !line.EndsWith(')') || Link(line, 1) is not { } link || link.End != line.Length)
        {
            return null;
        }

        var path = link.Url;
        return path.StartsWith(ImageFolder, StringComparison.Ordinal) && IsImageFileName(path[ImageFolder.Length..])
            ? (link.Text.Trim(), path[ImageFolder.Length..])
            : null;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,80}\.png\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ImageName();

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

/// <summary>Capturas del manual (<c>GET /descargas/manual/imagenes/{nombre}.png</c>), con el mismo permiso que la página del manual.</summary>
internal static class InstallManualEndpoints
{
    public static IEndpointRouteBuilder MapInstallManualImages(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/" + InstallManual.ImageRoute + "{name}", (string name, HttpContext http) =>
            {
                if (InstallManual.OpenImage(name) is not { } image)
                {
                    return Results.NotFound();
                }

                http.Response.Headers.CacheControl = "private, max-age=86400";
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.Stream(image, "image/png");
            })
            .RequireAuthorization(Pos.Cloud.Abstractions.CloudPermissions.DownloadView)
            .ExcludeFromDescription();
        return endpoints;
    }
}
