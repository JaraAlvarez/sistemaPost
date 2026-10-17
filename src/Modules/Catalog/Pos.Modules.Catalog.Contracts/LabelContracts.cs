namespace Pos.Modules.Catalog.Contracts;

/// <summary>De dónde salen los productos de las etiquetas.</summary>
public static class LabelSources
{
    /// <summary>Productos elegidos uno por uno (búsqueda).</summary>
    public const string Products = "PRODUCTS";

    /// <summary>Productos activos de una categoría y sus subcategorías.</summary>
    public const string Category = "CATEGORY";

    /// <summary>Productos cuyo precio de venta empezó a regir desde una fecha (historial de precios).</summary>
    public const string PriceChanges = "PRICE_CHANGES";

    /// <summary>Productos recibidos en una compra contabilizada (cantidad = unidades recibidas).</summary>
    public const string Purchase = "PURCHASE";

    /// <summary>Productos activos sin código de barras propio.</summary>
    public const string WithoutBarcode = "WITHOUT_BARCODE";
}

/// <summary>
/// Una etiqueta lista para revisar antes de imprimir. <c>Price</c>: precio al público vigente, igual al que cobra la caja (por kilo si se
/// vende por peso); <c>PromoPrice</c>/<c>PromoText</c>: la promoción que rige hoy en la sucursal (sin las de días u horario) y
/// <c>PromoValidUntil</c> su último día; <c>UnitMeasurePrice</c>: precio por kilo o litro (contenido neto); <c>Barcode</c> null = el
/// producto no tiene código y la etiqueta lleva el SKU en CODE128; <c>IsActive</c> false = producto inactivo (se avisa).
/// </summary>
public sealed record LabelItemDto(
    Guid ProductId,
    string Sku,
    string Name,
    string? Barcode,
    bool HasBarcode,
    decimal? Price,
    string? PriceUnit,
    decimal? PromoPrice,
    string? PromoText,
    string? UnitMeasurePrice,
    bool IsWeighed,
    int Quantity,
    DateOnly? PromoValidUntil = null,
    bool IsActive = true);

/// <summary>Resultado de armar la lista de etiquetas. <c>Reference</c>: número de la compra o fecha desde la que se buscaron cambios.</summary>
public sealed record LabelPreviewDto(string Source, string? Reference, IReadOnlyList<LabelItemDto> Items, IReadOnlyList<string> Warnings, bool Truncated);

/// <summary>Plantilla disponible (medidas en milímetros).</summary>
public sealed record LabelTemplateDto(string Code, string Name, int PerPage, bool IsSticker, decimal LabelWidth, decimal LabelHeight);

/// <summary>Producto y número de copias a imprimir.</summary>
public sealed record LabelPrintItem(Guid ProductId, int Quantity);

/// <summary>
/// Resultado por producto al generar códigos internos: <c>Generated</c> true con el código nuevo; false con <c>Message</c> (ya tenía código,
/// descontinuado, no existe…) y, si ya tenía, su código en <c>Barcode</c>.
/// </summary>
public sealed record GeneratedLabelCodeDto(Guid ProductId, string Sku, string Name, string? Barcode, bool Generated, string? Message = null);
