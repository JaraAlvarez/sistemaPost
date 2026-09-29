using System.Text;
using Pos.Printing;

namespace Pos.Terminal.Agent.Printing;

/// <summary>
/// Límites del tiquete que recibe el agente: evita trabajos absurdos (miles de líneas, textos enormes) y datos que la impresora
/// no puede representar (código de barras fuera de ASCII imprimible, QR demasiado grande).
/// </summary>
public static class TicketValidator
{
    public const int MaxElements = 2_000;
    public const int MaxTextLength = 2_000;
    public const int MaxBarcodeLength = 80;
    public const int MaxQrBytes = 2_000;

    /// <summary>Mensaje del primer problema encontrado, o <c>null</c> si el tiquete es válido.</summary>
    public static string? Validate(TicketDocument? ticket)
    {
        if (ticket is null)
        {
            return "Falta el tiquete (\"ticket\").";
        }

        if (ticket.Elements is null || ticket.Elements.Count == 0)
        {
            return "El tiquete no tiene elementos.";
        }

        if (ticket.Elements.Count > MaxElements)
        {
            return $"El tiquete supera {MaxElements} elementos.";
        }

        if (ticket.Title is { Length: > MaxTextLength })
        {
            return "El título del tiquete es demasiado largo.";
        }

        for (var i = 0; i < ticket.Elements.Count; i++)
        {
            if (Check(ticket.Elements[i]) is { } problem)
            {
                return $"Elemento {i + 1}: {problem}";
            }
        }

        return null;
    }

    private static string? Check(TicketElement? element) => element switch
    {
        null => "vacío.",
        TextLine t when t.Text is null => "texto sin contenido.",
        TextLine t when t.Text.Length > MaxTextLength => "texto demasiado largo.",
        TextLine t when !Enum.IsDefined(t.Align) => "alineación inválida.",
        ColumnsLine c when (c.Left?.Length ?? 0) + (c.Right?.Length ?? 0) > MaxTextLength => "columnas demasiado largas.",
        SeparatorLine s when char.IsControl(s.Character) || (char.IsWhiteSpace(s.Character) && s.Character != ' ') => "carácter de separación inválido.",
        BarcodeElement b when string.IsNullOrEmpty(b.Data) || b.Data.Length > MaxBarcodeLength || b.Data.Any(c => c is < ' ' or > '~') =>
            $"el código de barras debe tener de 1 a {MaxBarcodeLength} caracteres ASCII imprimibles.",
        QrElement q when string.IsNullOrEmpty(q.Data) || Encoding.UTF8.GetByteCount(q.Data) > MaxQrBytes => $"el QR debe tener de 1 a {MaxQrBytes} bytes.",
        FeedElement f when f.Lines is < 0 or > 10 => "el avance de papel debe ser de 0 a 10 líneas.",
        _ => null,
    };
}
