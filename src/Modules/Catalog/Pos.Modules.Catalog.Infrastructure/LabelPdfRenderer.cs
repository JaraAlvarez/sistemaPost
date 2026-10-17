using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Pos.Infrastructure.Files;
using Pos.Modules.Catalog.Application;
using Pos.Modules.Catalog.Domain;

namespace Pos.Modules.Catalog.Infrastructure;

/// <summary>
/// PDF de etiquetas con PDFsharp (MIT, el mismo de los reportes). Cada página mide exactamente lo de la plantilla: hoja A4 con líneas
/// grises suaves para cortar, o el adhesivo de la impresora térmica (40 × 25 o 50 × 30 mm, una etiqueta por página). Las barras se dibujan
/// como rectángulos vectoriales: se ven nítidas a cualquier resolución.
/// </summary>
internal sealed class LabelPdfRenderer : ILabelRenderer
{
    private const double PointsPerMillimeter = 72d / 25.4d;

    public byte[] RenderPdf(LabelTemplate template, IReadOnlyList<LabelContent> labels, string title)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(labels);
        PdfFonts.EnsureConfigured();
        using var document = new PdfDocument();
        document.Info.Title = title;
        document.Info.Creator = "BusinessPost";
        var pages = LabelLayout.PageCount(template, labels.Count);
        for (var p = 0; p < pages; p++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromMillimeter((double)template.PageWidth);
            page.Height = XUnit.FromMillimeter((double)template.PageHeight);
            using var gfx = XGraphics.FromPdfPage(page);
            for (var i = p * template.PerPage; i < Math.Min(labels.Count, (p + 1) * template.PerPage); i++)
            {
                var slot = LabelLayout.Slot(template, i);
                var box = new XRect(Mm(slot.X), Mm(slot.Y), Mm(template.LabelWidth), Mm(template.LabelHeight));
                if (template.IsSticker)
                {
                    DrawSticker(gfx, box, labels[i], template);
                }
                else
                {
                    gfx.DrawRectangle(new XPen(XColor.FromArgb(200, 200, 200), 0.3), box);
                    DrawShelf(gfx, box, labels[i], template);
                }
            }
        }

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    /// <summary>Etiqueta de góndola: nombre, precio grande (oferta con el precio normal tachado), código de barras y datos menores.</summary>
    private static void DrawShelf(XGraphics gfx, XRect box, LabelContent label, LabelTemplate template)
    {
        var scale = box.Height / Mm(35m);
        var pad = Mm(LabelLayout.Padding(template));
        var inner = new XRect(box.X + pad, box.Y + pad, box.Width - (2 * pad), box.Height - (2 * pad));

        // Nombre: hasta dos líneas.
        var nameFont = Font(8.5 * scale, bold: true);
        var nameHeight = nameFont.GetHeight() * 2;
        DrawLines(gfx, Wrap(gfx, label.Name, nameFont, inner.Width, 2), nameFont, new XRect(inner.X, inner.Y, inner.Width, nameHeight), XStringAlignment.Near);

        // Código largo (p. ej. un SKU en CODE128): usa todo el ancho y los datos menores suben a la derecha del precio.
        var fullWidth = label.Barcode is { } code && LabelLayout.FitBarcode(template, code) is { FullWidth: true };

        // Precio grande (y la oferta).
        var priceTop = inner.Y + nameHeight + (Mm(0.5m) * scale);
        var priceHeight = inner.Height * 0.36;
        if (label.OfferText is { } offer)
        {
            var offerFont = Font(6.5 * scale, bold: true);
            var offerBox = new XRect(inner.X, priceTop, inner.Width, offerFont.GetHeight());
            var text = label.RegularPriceText is { } regular ? $"{offer} · Antes " : offer;
            gfx.DrawString(text, offerFont, XBrushes.Black, offerBox, XStringFormats.TopLeft);
            if (label.RegularPriceText is { } struck)
            {
                var x = inner.X + gfx.MeasureString(text, offerFont).Width;
                var width = gfx.MeasureString(struck, offerFont).Width;
                gfx.DrawString(struck, offerFont, XBrushes.Black, new XRect(x, priceTop, width, offerFont.GetHeight()), XStringFormats.TopLeft);
                var middle = priceTop + (offerFont.GetHeight() * 0.55);
                gfx.DrawLine(new XPen(XColors.Black, 0.6 * scale), x, middle, x + width, middle);
            }

            priceTop += offerFont.GetHeight();
            priceHeight -= offerFont.GetHeight() * 0.6;
        }

        if (label.PriceText is { } price)
        {
            var unit = label.PriceUnit is { } u ? $" /{u}" : string.Empty;
            var priceFont = FitFont(gfx, price, inner.Width * (fullWidth ? 0.6 : 0.85), priceHeight, bold: true);
            var unitFont = Font(Math.Max(6d, priceFont.Size * 0.4), bold: true);
            var priceWidth = gfx.MeasureString(price, priceFont).Width;
            gfx.DrawString(price, priceFont, XBrushes.Black, new XRect(inner.X, priceTop, priceWidth, priceHeight), XStringFormats.CenterLeft);
            if (unit.Length > 0)
            {
                gfx.DrawString(unit, unitFont, XBrushes.Black, new XRect(inner.X + priceWidth, priceTop, inner.Width - priceWidth, priceHeight * 0.85),
                    XStringFormats.BottomLeft);
            }
        }
        else
        {
            gfx.DrawString("Sin precio", Font(9 * scale, bold: true), XBrushes.Black, new XRect(inner.X, priceTop, inner.Width, priceHeight), XStringFormats.CenterLeft);
        }

        // Fila de abajo: código de barras a la izquierda, datos menores a la derecha.
        var bottomTop = priceTop + priceHeight + (Mm(0.5m) * scale);
        var bottomHeight = inner.Bottom - bottomTop;
        if (bottomHeight <= 0)
        {
            return;
        }

        var barcodeWidth = fullWidth ? inner.Width : Mm(LabelLayout.BarcodeAreaWidth(template));
        if (label.Barcode is { } barcode)
        {
            DrawBarcode(gfx, barcode, new XRect(inner.X, bottomTop, barcodeWidth, bottomHeight), Font(5.5 * scale, bold: false), template, center: false);
        }

        var smallFont = Font(5.5 * scale, bold: false);
        var lines = new List<string>();
        if (label.UnitMeasurePrice is { } perUnit)
        {
            lines.AddRange(Wrap(gfx, perUnit, smallFont, fullWidth ? inner.Width * 0.38 : inner.Width - barcodeWidth - Mm(1m), 2));
        }

        lines.Add($"SKU {label.Sku}");
        if (label.DateText is { } date)
        {
            lines.Add(date);
        }

        var side = fullWidth
            ? new XRect(inner.X + (inner.Width * 0.62), priceTop, inner.Width * 0.38, priceHeight)
            : new XRect(inner.X + barcodeWidth + Mm(1m), bottomTop, inner.Width - barcodeWidth - Mm(1m), bottomHeight);
        DrawLines(gfx, lines, smallFont, side, XStringAlignment.Far);
    }

    /// <summary>Adhesivo pequeño: nombre (dos líneas), precio y el código de barras a todo el ancho.</summary>
    private static void DrawSticker(XGraphics gfx, XRect box, LabelContent label, LabelTemplate template)
    {
        var scale = box.Height / Mm(25m);
        var pad = Mm(LabelLayout.Padding(template));
        var inner = new XRect(box.X + pad, box.Y + pad, box.Width - (2 * pad), box.Height - (2 * pad));
        var nameFont = Font(6.5 * scale, bold: true);
        var nameHeight = nameFont.GetHeight() * 2;
        DrawLines(gfx, Wrap(gfx, label.Name, nameFont, inner.Width, 2), nameFont, new XRect(inner.X, inner.Y, inner.Width, nameHeight), XStringAlignment.Center);

        var priceHeight = inner.Height * 0.26;
        var top = inner.Y + nameHeight;
        if (label.PriceText is { } price)
        {
            var text = label.PriceUnit is { } unit ? $"{price} /{unit}" : price;
            var font = FitFont(gfx, text, inner.Width, priceHeight, bold: true);
            gfx.DrawString(text, font, XBrushes.Black, new XRect(inner.X, top, inner.Width, priceHeight), XStringFormats.Center);
            top += priceHeight;
        }

        if (label.Barcode is { } barcode && inner.Bottom - top > Mm(4m))
        {
            DrawBarcode(gfx, barcode, new XRect(inner.X, top + Mm(0.3m), inner.Width, inner.Bottom - top - Mm(0.3m)), Font(5 * scale, bold: false), template, center: true);
        }
    }

    /// <summary>
    /// Barras como rectángulos (un rectángulo por cada tramo negro) y el texto centrado debajo. El módulo es un número entero de puntos de
    /// 203 ppp y nunca menor de 0,25 mm (LabelLayout.ModuleWidth); si el código no cabe, se imprime solo su texto.
    /// </summary>
    private static void DrawBarcode(XGraphics gfx, BarcodeDrawing barcode, XRect area, XFont textFont, LabelTemplate template, bool center)
    {
        var textHeight = textFont.GetHeight();
        var barsHeight = Math.Max(area.Height - textHeight, area.Height * 0.6);
        if (LabelLayout.FitBarcode(template, barcode)?.Module is not { } moduleMm)
        {
            gfx.DrawString(barcode.Text, textFont, XBrushes.Black, area, XStringFormats.Center);
            return;
        }

        var module = Mm(moduleMm);
        var width = module * barcode.TotalModules;
        var left = center ? area.X + ((area.Width - width) / 2) : area.X;
        var start = left + (barcode.QuietZone * module);
        var i = 0;
        while (i < barcode.Modules.Count)
        {
            if (!barcode.Modules[i])
            {
                i++;
                continue;
            }

            var run = i;
            while (run < barcode.Modules.Count && barcode.Modules[run])
            {
                run++;
            }

            gfx.DrawRectangle(XBrushes.Black, start + (i * module), area.Y, (run - i) * module, barsHeight);
            i = run;
        }

        if (area.Height - barsHeight >= textHeight * 0.8)
        {
            gfx.DrawString(barcode.Text, textFont, XBrushes.Black, new XRect(left, area.Y + barsHeight, width, textHeight), XStringFormats.TopCenter);
        }
    }

    private static void DrawLines(XGraphics gfx, List<string> lines, XFont font, XRect area, XStringAlignment alignment)
    {
        var height = font.GetHeight();
        var format = alignment switch
        {
            XStringAlignment.Center => XStringFormats.TopCenter,
            XStringAlignment.Far => XStringFormats.TopRight,
            _ => XStringFormats.TopLeft,
        };
        for (var i = 0; i < lines.Count && (i + 1) * height <= area.Height + 0.01; i++)
        {
            gfx.DrawString(lines[i], font, XBrushes.Black, new XRect(area.X, area.Y + (i * height), area.Width, height), format);
        }
    }

    /// <summary>Parte el texto por palabras en máximo <paramref name="maxLines"/> líneas; la última termina en "…" si no cabe.</summary>
    private static List<string> Wrap(XGraphics gfx, string text, XFont font, double width, int maxLines)
    {
        var lines = new List<string>();
        var current = string.Empty;
        var words = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var w = 0; w < words.Length; w++)
        {
            var candidate = current.Length == 0 ? words[w] : $"{current} {words[w]}";
            if (gfx.MeasureString(candidate, font).Width <= width || current.Length == 0)
            {
                current = candidate;
                continue;
            }

            lines.Add(current);
            current = words[w];
            if (lines.Count == maxLines - 1)
            {
                current = string.Join(' ', words.Skip(w));
                break;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        for (var l = 0; l < lines.Count; l++)
        {
            if (gfx.MeasureString(lines[l], font).Width > width)
            {
                var cut = lines[l];
                while (cut.Length > 1 && gfx.MeasureString(cut + "…", font).Width > width)
                {
                    cut = cut[..^1];
                }

                lines[l] = cut.TrimEnd() + "…";
            }
        }

        return lines;
    }

    /// <summary>Fuente negrita del mayor tamaño que cabe en el ancho y el alto indicados.</summary>
    private static XFont FitFont(XGraphics gfx, string text, double width, double height, bool bold)
    {
        var size = Math.Max(6d, height / 1.2);
        var font = Font(size, bold);
        while (size > 6d && (gfx.MeasureString(text, font).Width > width || font.GetHeight() > height * 1.05))
        {
            size -= 0.5;
            font = Font(size, bold);
        }

        return font;
    }

    private static XFont Font(double size, bool bold) => new(PdfFonts.Family, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

    private static double Mm(decimal millimeters) => (double)millimeters * PointsPerMillimeter;
}
