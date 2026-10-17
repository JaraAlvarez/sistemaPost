namespace Pos.Client.Shared;

/// <summary>Presentación de compra de un producto (empaque con su factor) que se puede elegir en una línea.</summary>
public sealed record EmpaqueProducto(Guid Id, string Nombre);

/// <summary>
/// Línea de producto que se edita con <see cref="LineasProductos"/> en una orden o una factura de compra. Lote, vencimiento y descuento
/// solo se muestran (y se envían) en la factura.
/// </summary>
public sealed class LineaProducto
{
    public Guid ProductId { get; init; }

    public string Sku { get; init; } = "";

    public string Nombre { get; init; } = "";

    public string Unidad { get; init; } = "";

    public IReadOnlyList<EmpaqueProducto> Empaques { get; init; } = [];

    /// <summary>El producto maneja lotes.</summary>
    public bool Lotes { get; init; }

    /// <summary>El producto maneja fecha de vencimiento.</summary>
    public bool Vence { get; init; }

    /// <summary>Presentación elegida; <see cref="Guid.Empty"/> = unidad base.</summary>
    public Guid EmpaqueId { get; set; }

    public decimal Cantidad { get; set; } = 1;

    /// <summary>Costo unitario; sin valor, la API usa el último costo.</summary>
    public decimal? Costo { get; set; }

    public decimal Descuento { get; set; }

    public string? Lote { get; set; }

    public DateTime? Vencimiento { get; set; }
}
