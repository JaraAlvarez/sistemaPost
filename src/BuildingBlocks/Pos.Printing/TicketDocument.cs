using System.Text.Json.Serialization;

namespace Pos.Printing;

public enum TicketAlign
{
    Left,
    Center,
    Right,
}

/// <summary>
/// Elemento del tiquete en un modelo NEUTRO (D7-14): el servidor arma el documento y el agente de caja lo convierte a los
/// comandos de su impresora. Se serializa en JSON con el discriminador <c>type</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextLine), "text")]
[JsonDerivedType(typeof(ColumnsLine), "columns")]
[JsonDerivedType(typeof(SeparatorLine), "separator")]
[JsonDerivedType(typeof(BarcodeElement), "barcode")]
[JsonDerivedType(typeof(QrElement), "qr")]
[JsonDerivedType(typeof(FeedElement), "feed")]
public abstract record TicketElement;

/// <summary>Texto; si no cabe en el ancho se parte en varias líneas por palabras.</summary>
public sealed record TextLine(string Text, TicketAlign Align = TicketAlign.Left, bool Bold = false, bool DoubleSize = false) : TicketElement;

/// <summary>Texto a la izquierda y valor alineado a la derecha (p. ej. "Subtotal ........ 12.500").</summary>
public sealed record ColumnsLine(string Left, string Right, bool Bold = false) : TicketElement;

public sealed record SeparatorLine(char Character = '-') : TicketElement;

/// <summary>Código de barras CODE128 (p. ej. el número de la venta para buscarla en un cambio).</summary>
public sealed record BarcodeElement(string Data) : TicketElement;

/// <summary>Código QR (p. ej. el de la DIAN cuando el documento sea electrónico).</summary>
public sealed record QrElement(string Data) : TicketElement;

public sealed record FeedElement(int Lines = 1) : TicketElement;

/// <summary>
/// Documento a imprimir. <c>Cut</c>: cortar el papel al final; <c>OpenDrawer</c>: pulso al cajón conectado a la impresora
/// (solo en ventas con efectivo, por instrucción del servidor).
/// </summary>
public sealed record TicketDocument(string Title, IReadOnlyList<TicketElement> Elements, bool Cut = true, bool OpenDrawer = false);
