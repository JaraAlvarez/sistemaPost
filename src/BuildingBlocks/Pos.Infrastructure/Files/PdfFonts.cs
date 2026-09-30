using System.Globalization;
using PdfSharp.Fonts;

namespace Pos.Infrastructure.Files;

/// <summary>
/// Fuentes del PDF. PDFsharp 6 (build Core) no usa fuentes del sistema por defecto: en Windows se habilitan las de Windows; en Linux
/// (la nube) se resuelve una familia sans instalada (DejaVu o Liberation).
/// </summary>
public static class PdfFonts
{
    public const string Family = "Arial";
    private static readonly Lock Gate = new();
    private static bool _configured;

    public static void EnsureConfigured()
    {
        lock (Gate)
        {
            if (_configured)
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                GlobalFontSettings.UseWindowsFontsUnderWindows = true;
            }
            else if (GlobalFontSettings.FontResolver is null)
            {
                GlobalFontSettings.FontResolver = new UnixFontResolver();
            }

            _configured = true;
        }
    }

    private sealed class UnixFontResolver : IFontResolver
    {
        private static readonly string[] Candidates =
        [
            "/usr/share/fonts/truetype/dejavu/DejaVuSans{0}.ttf",
            "/usr/share/fonts/dejavu/DejaVuSans{0}.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-{1}.ttf",
            "/usr/share/fonts/liberation/LiberationSans-{1}.ttf",
        ];

        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
            new(bold && italic ? "BoldItalic" : bold ? "Bold" : italic ? "Italic" : "Regular");

        public byte[]? GetFont(string faceName)
        {
            var dejavu = faceName switch { "Bold" => "-Bold", "Italic" => "-Oblique", "BoldItalic" => "-BoldOblique", _ => string.Empty };
            foreach (var candidate in Candidates)
            {
                var path = string.Format(CultureInfo.InvariantCulture, candidate, dejavu, faceName);
                if (File.Exists(path))
                {
                    return File.ReadAllBytes(path);
                }
            }

            return null;
        }
    }
}
