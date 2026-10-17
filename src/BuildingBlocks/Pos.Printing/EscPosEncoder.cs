using System.Text;

namespace Pos.Printing;

/// <summary>Página de códigos de la impresora para los caracteres del español (tildes, ñ, ¿, ¡).</summary>
public enum PrinterCodePage
{
    /// <summary>PC850 (Multilingual Latin I): ESC t 2. La soportan casi todas las impresoras ESC/POS.</summary>
    Pc850,

    /// <summary>Solo ASCII: las tildes se reemplazan (á → a, ñ → n) para impresoras sin página de códigos.</summary>
    Ascii,
}

/// <summary>Pin del conector RJ-11 del cajón en la impresora.</summary>
public enum DrawerPin
{
    Pin2,
    Pin5,
}

/// <summary>Opciones de la impresora de la caja (de <c>org.terminal_devices</c>).</summary>
public sealed record PrinterOptions(int Columns = TicketLayout.Columns80Mm, PrinterCodePage CodePage = PrinterCodePage.Pc850, bool AutoCut = true, DrawerPin DrawerPin = DrawerPin.Pin2);

/// <summary>
/// Convierte el tiquete neutro a comandos ESC/POS estándar (Epson y compatibles: Bixolon, 3nStar, Digital POS, Xprinter).
/// Determinista: el mismo tiquete produce los mismos bytes (se prueba byte a byte).
/// </summary>
public static class EscPosEncoder
{
    public const byte Esc = 0x1B;
    public const byte Gs = 0x1D;
    public const byte Lf = 0x0A;

    private static readonly Lazy<Encoding> Pc850 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(850);
    });

    /// <summary>ESC @: reinicia la impresora.</summary>
    public static byte[] Initialize => [Esc, 0x40];

    /// <summary>ESC p m t1 t2: pulso de 50 ms / 500 ms al cajón por el pin indicado.</summary>
    public static byte[] OpenDrawer(DrawerPin pin) => [Esc, 0x70, pin == DrawerPin.Pin5 ? (byte)1 : (byte)0, 0x19, 0xFA];

    /// <summary>ESC d 4 + GS V 66 0: avanza el papel y hace el corte parcial.</summary>
    public static byte[] Cut => [Esc, 0x64, 0x04, Gs, 0x56, 0x42, 0x00];

    public static byte[] Encode(TicketDocument ticket, PrinterOptions options)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(options);
        var output = new List<byte>(4096);
        output.AddRange(Initialize);
        if (options.CodePage == PrinterCodePage.Pc850)
        {
            output.AddRange([Esc, 0x74, 0x02]);
        }

        foreach (var element in ticket.Elements)
        {
            switch (element)
            {
                case TextLine text:
                    var width = text.DoubleSize ? Math.Max(1, options.Columns / 2) : options.Columns;
                    output.AddRange(AlignCommand(text.Align));
                    output.AddRange(Bold(text.Bold));
                    output.AddRange([Gs, 0x21, text.DoubleSize ? (byte)0x11 : (byte)0x00]);
                    foreach (var line in TicketLayout.Wrap(text.Text, width))
                    {
                        output.AddRange(Text(line.TrimEnd(), options.CodePage));
                        output.Add(Lf);
                    }

                    output.AddRange([Gs, 0x21, 0x00]);
                    output.AddRange(Bold(false));
                    output.AddRange(AlignCommand(TicketAlign.Left));
                    break;
                case ColumnsLine pair:
                    output.AddRange(Bold(pair.Bold));
                    foreach (var line in TicketLayout.Columns(pair.Left, pair.Right, options.Columns))
                    {
                        output.AddRange(Text(line.TrimEnd(), options.CodePage));
                        output.Add(Lf);
                    }

                    output.AddRange(Bold(false));
                    break;
                case SeparatorLine separator:
                    output.AddRange(Text(new string(separator.Character, options.Columns), options.CodePage));
                    output.Add(Lf);
                    break;
                case BarcodeElement { Symbology: null } barcode:
                    output.AddRange(Barcode128(barcode.Data));
                    break;
                case BarcodeElement barcode:
                    output.AddRange(Barcode(barcode, options));
                    break;
                case QrElement qr:
                    output.AddRange(Qr(qr.Data));
                    break;
                case FeedElement feed:
                    output.AddRange([Esc, 0x64, (byte)Math.Clamp(feed.Lines, 0, 10)]);
                    break;
            }
        }

        if (ticket.Cut && options.AutoCut)
        {
            output.AddRange(Cut);
        }

        if (ticket.OpenDrawer)
        {
            output.AddRange(OpenDrawer(options.DrawerPin));
        }

        return [.. output];
    }

    /// <summary>Texto en la página de códigos de la impresora (ASCII: sin tildes; caracteres no representables → '?').</summary>
    public static byte[] Text(string text, PrinterCodePage codePage) =>
        codePage == PrinterCodePage.Pc850 ? Pc850.Value.GetBytes(text ?? string.Empty) : Encoding.ASCII.GetBytes(ToAscii(text ?? string.Empty));

    public static string ToAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(c switch
            {
                '¿' => '?',
                '¡' => '!',
                _ when c > 127 => '?',
                _ => c,
            });
        }

        return builder.ToString();
    }

    private static byte[] AlignCommand(TicketAlign align) => [Esc, 0x61, align switch { TicketAlign.Center => 1, TicketAlign.Right => 2, _ => 0 }];

    private static byte[] Bold(bool on) => [Esc, 0x45, on ? (byte)1 : (byte)0];

    /// <summary>GS k 73: CODE128 subconjunto B, con altura 80 puntos, módulo 2 y el texto debajo.</summary>
    private static byte[] Barcode128(string data)
    {
        var payload = Encoding.ASCII.GetBytes("{B" + ToAscii(data ?? string.Empty));
        var length = (byte)Math.Min(payload.Length, 255);
        return [Esc, 0x61, 1, Gs, 0x68, 80, Gs, 0x77, 2, Gs, 0x48, 2, Gs, 0x6B, 73, length, .. payload.Take(length), Lf, Esc, 0x61, 0];
    }

    /// <summary>Puntos imprimibles del papel: 58 mm = 384 puntos; 80 mm = 576 (203 ppp).</summary>
    public static int PaperDots(PrinterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Columns <= TicketLayout.Columns58Mm ? 384 : 576;
    }

    /// <summary>
    /// Código con simbología explícita: EAN-13 (GS k 67) y EAN-8 (GS k 68) si los dígitos son válidos; si no, CODE128 (subconjunto C cuando
    /// es numérico de largo par). El ancho del módulo (GS w) es el mayor de 3 o 2 puntos que cabe en el papel con las zonas en blanco; si
    /// ni con 2 cabe, se imprime el texto del código (más vale legible a mano que barras cortadas).
    /// </summary>
    private static byte[] Barcode(BarcodeElement barcode, PrinterOptions options)
    {
        var data = ToAscii(barcode.Data ?? string.Empty).Trim();
        var digits = data.Length > 0 && data.All(char.IsAsciiDigit);
        byte system;
        byte[] payload;
        int modules;
        if (barcode.Symbology == BarcodeElement.Ean13 && digits && data.Length == 13 && ValidGtin(data))
        {
            (system, payload, modules) = (67, Encoding.ASCII.GetBytes(data), 95 + 18);
        }
        else if (barcode.Symbology == BarcodeElement.Ean8 && digits && data.Length == 8 && ValidGtin(data))
        {
            (system, payload, modules) = (68, Encoding.ASCII.GetBytes(data), 67 + 14);
        }
        else if (digits && data.Length % 2 == 0)
        {
            var pairs = Enumerable.Range(0, data.Length / 2).Select(i => (byte)(((data[2 * i] - '0') * 10) + (data[(2 * i) + 1] - '0')));
            (system, payload, modules) = (73, [.. Encoding.ASCII.GetBytes("{C"), .. pairs], (11 * (2 + (data.Length / 2))) + 13 + 20);
        }
        else
        {
            (system, payload, modules) = (73, Encoding.ASCII.GetBytes("{B" + data), (11 * (2 + data.Length)) + 13 + 20);
        }

        var dots = PaperDots(options);
        var width = modules * 3 <= dots ? 3 : modules * 2 <= dots ? 2 : 0;
        if (width == 0 || payload.Length > 255 || data.Length == 0)
        {
            return [Esc, 0x61, 1, .. Encoding.ASCII.GetBytes(data), Lf, Esc, 0x61, 0];
        }

        return [Esc, 0x61, 1, Gs, 0x68, 80, Gs, 0x77, (byte)width, Gs, 0x48, 2, Gs, 0x6B, system, (byte)payload.Length, .. payload, Lf, Esc, 0x61, 0];
    }

    private static bool ValidGtin(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length - 1; i++)
        {
            sum += (digits[digits.Length - 2 - i] - '0') * (i % 2 == 0 ? 3 : 1);
        }

        return (10 - (sum % 10)) % 10 == digits[^1] - '0';
    }

    /// <summary>GS ( k: código QR modelo 2, tamaño de módulo 6, corrección de errores M.</summary>
    private static byte[] Qr(string data)
    {
        var payload = Encoding.UTF8.GetBytes(data ?? string.Empty);
        var storeLength = payload.Length + 3;
        return
        [
            Esc, 0x61, 1,
            Gs, 0x28, 0x6B, 4, 0, 0x31, 0x41, 0x32, 0x00,
            Gs, 0x28, 0x6B, 3, 0, 0x31, 0x43, 0x06,
            Gs, 0x28, 0x6B, 3, 0, 0x31, 0x45, 0x31,
            Gs, 0x28, 0x6B, (byte)(storeLength % 256), (byte)(storeLength / 256), 0x31, 0x50, 0x30, .. payload,
            Gs, 0x28, 0x6B, 3, 0, 0x31, 0x51, 0x30,
            Lf, Esc, 0x61, 0,
        ];
    }
}
