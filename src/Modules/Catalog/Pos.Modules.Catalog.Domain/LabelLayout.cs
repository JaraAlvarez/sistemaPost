namespace Pos.Modules.Catalog.Domain;

/// <summary>Plantillas de etiqueta: hojas A4 para góndola (habladores) y adhesivos sueltos para impresoras de etiquetas térmicas.</summary>
public enum LabelTemplateKind
{
    /// <summary>Hoja A4 con 24 etiquetas (3 columnas × 8 filas) de 66 × 35 mm.</summary>
    Shelf3x8,

    /// <summary>Hoja A4 con 14 etiquetas (2 columnas × 7 filas) de 100 × 40 mm: precio más grande.</summary>
    Shelf2x7,

    /// <summary>Adhesivo de 40 × 25 mm (una etiqueta por página, al tamaño exacto del rollo).</summary>
    Sticker40x25,

    /// <summary>Adhesivo de 50 × 30 mm.</summary>
    Sticker50x30,
}

/// <summary>
/// Medidas de una plantilla en milímetros: tamaño de la página, columnas × filas, tamaño de cada etiqueta, márgenes y separación entre
/// etiquetas. Los adhesivos son una etiqueta por página (la impresora térmica corta o separa cada una).
/// </summary>
public sealed record LabelTemplate(
    LabelTemplateKind Kind,
    string Name,
    decimal PageWidth,
    decimal PageHeight,
    int Columns,
    int Rows,
    decimal LabelWidth,
    decimal LabelHeight,
    decimal MarginLeft,
    decimal MarginTop,
    decimal GapX,
    decimal GapY)
{
    public int PerPage => Columns * Rows;

    public bool IsSticker => Kind is LabelTemplateKind.Sticker40x25 or LabelTemplateKind.Sticker50x30;

    /// <summary>¿Caben todas las etiquetas dentro de la página con los márgenes y separaciones?</summary>
    public bool Fits =>
        MarginLeft + (Columns * LabelWidth) + ((Columns - 1) * GapX) <= PageWidth
        && MarginTop + (Rows * LabelHeight) + ((Rows - 1) * GapY) <= PageHeight;
}

/// <summary>Lugar de una etiqueta: página (desde 0), columna y fila, y la esquina superior izquierda en milímetros.</summary>
public sealed record LabelSlot(int Page, int Column, int Row, decimal X, decimal Y);

/// <summary>Cómo se dibuja un código: ancho del módulo y del área (mm) y si ocupa todo el ancho de la etiqueta.</summary>
public sealed record BarcodeFit(decimal Module, decimal AreaWidth, bool FullWidth, bool BelowNominal = false);

/// <summary>Reparte etiquetas en páginas: llena cada hoja por filas (izquierda a derecha, arriba a abajo) y pasa a la siguiente.</summary>
public static class LabelLayout
{
    /// <summary>Máximo de etiquetas por documento (evita PDF gigantes por error en una cantidad).</summary>
    public const int MaxLabels = 2000;

    /// <summary>Máximo de copias de un mismo producto.</summary>
    public const int MaxCopies = 500;

    public static IReadOnlyList<LabelTemplate> Templates { get; } =
    [
        new(LabelTemplateKind.Shelf3x8, "Hoja A4 · 24 etiquetas de góndola (66 × 35 mm)", 210m, 297m, 3, 8, 66m, 35m, 6m, 8.5m, 0m, 0m),
        new(LabelTemplateKind.Shelf2x7, "Hoja A4 · 14 etiquetas grandes (100 × 40 mm)", 210m, 297m, 2, 7, 100m, 40m, 5m, 8.5m, 0m, 0m),
        new(LabelTemplateKind.Sticker40x25, "Adhesivo 40 × 25 mm (impresora de etiquetas)", 40m, 25m, 1, 1, 40m, 25m, 0m, 0m, 0m, 0m),
        new(LabelTemplateKind.Sticker50x30, "Adhesivo 50 × 30 mm (impresora de etiquetas)", 50m, 30m, 1, 1, 50m, 30m, 0m, 0m, 0m, 0m),
    ];

    /// <summary>Módulo (barra más delgada) mínimo legible: 0,25 mm = 2 puntos de una impresora térmica de 203 ppp.</summary>
    public const decimal MinModule = 0.25m;

    /// <summary>Módulo nominal mínimo de EAN-13/EAN-8 (80 % de 0,33 mm, norma GS1).</summary>
    public const decimal MinEanModule = 0.264m;

    /// <summary>Un punto de una impresora térmica de 203 ppp: el módulo se ajusta a puntos enteros para que todas las barras salgan iguales.</summary>
    public const decimal Dot = 0.125m;

    /// <summary>Módulo máximo (más ancho no mejora la lectura y se sale de la etiqueta).</summary>
    public const decimal MaxModule = 0.5m;

    public static LabelTemplate Get(LabelTemplateKind kind) => Templates.Single(t => t.Kind == kind);

    /// <summary>Margen interior de la etiqueta en milímetros (crece con el alto de la etiqueta).</summary>
    public static decimal Padding(LabelTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.IsSticker ? 1.2m * template.LabelHeight / 25m : 2m * template.LabelHeight / 35m;
    }

    /// <summary>Ancho interior de la etiqueta (sin márgenes).</summary>
    public static decimal InnerWidth(LabelTemplate template) => template.LabelWidth - (2 * Padding(template));

    /// <summary>Ancho normal del código en góndola (58 %, con los datos menores a la derecha); en adhesivos, todo el ancho.</summary>
    public static decimal BarcodeAreaWidth(LabelTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.IsSticker ? InnerWidth(template) : decimal.Round(InnerWidth(template) * 0.58m, 3);
    }

    /// <summary>
    /// Cómo cabe un código de <paramref name="totalModules"/> módulos (con zonas en blanco): módulo = mayor múltiplo de un punto que cabe, hasta
    /// 0,5 mm. Primero se busca el tamaño nominal mínimo (0,264 mm = 80 % para EAN; 0,25 mm para CODE128) en el ancho normal y luego en todo el
    /// ancho de la etiqueta (<c>FullWidth</c>; en góndola los datos menores suben a la fila del precio). Un EAN que solo cabe a 0,25 mm se dibuja
    /// así con <c>BelowNominal</c> (se avisa: algunos lectores lo leen mal). Null si ni así cabe: el código es demasiado largo para la etiqueta.
    /// </summary>
    public static BarcodeFit? FitBarcode(LabelTemplate template, int totalModules, bool ean = false)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentOutOfRangeException.ThrowIfLessThan(totalModules, 1);
        (decimal Area, bool Full)[] areas = [(BarcodeAreaWidth(template), template.IsSticker), (InnerWidth(template), true)];
        foreach (var minimum in ean ? new[] { MinEanModule, MinModule } : [MinModule])
        {
            foreach (var (area, full) in areas)
            {
                var snapped = decimal.Floor(area / totalModules / Dot) * Dot;
                if (snapped >= minimum)
                {
                    return new BarcodeFit(Math.Min(snapped, MaxModule), area, full, BelowNominal: ean && snapped < MinEanModule);
                }
            }
        }

        return null;
    }

    /// <summary>Cómo cabe este código en la etiqueta (EAN-13/EAN-8 con su mínimo nominal).</summary>
    public static BarcodeFit? FitBarcode(LabelTemplate template, BarcodeDrawing barcode)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        return FitBarcode(template, barcode.TotalModules, barcode.Symbol != BarcodeSymbol.Code128);
    }

    /// <summary>Ancho del módulo en milímetros, o null si el código no cabe legible (ver <see cref="FitBarcode(LabelTemplate, int, bool)"/>).</summary>
    public static decimal? ModuleWidth(LabelTemplate template, int totalModules, bool ean = false) => FitBarcode(template, totalModules, ean)?.Module;

    /// <summary>Número de páginas para <paramref name="labels"/> etiquetas (al menos una).</summary>
    public static int PageCount(LabelTemplate template, int labels)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentOutOfRangeException.ThrowIfNegative(labels);
        return Math.Max(1, (labels + template.PerPage - 1) / template.PerPage);
    }

    /// <summary>Lugar de la etiqueta número <paramref name="index"/> (desde 0).</summary>
    public static LabelSlot Slot(LabelTemplate template, int index)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var page = index / template.PerPage;
        var inPage = index % template.PerPage;
        var row = inPage / template.Columns;
        var column = inPage % template.Columns;
        return new LabelSlot(
            page,
            column,
            row,
            template.MarginLeft + (column * (template.LabelWidth + template.GapX)),
            template.MarginTop + (row * (template.LabelHeight + template.GapY)));
    }

    /// <summary>Lugares de <paramref name="labels"/> etiquetas seguidas.</summary>
    public static IReadOnlyList<LabelSlot> Slots(LabelTemplate template, int labels) =>
        [.. Enumerable.Range(0, Math.Max(0, labels)).Select(i => Slot(template, i))];
}
