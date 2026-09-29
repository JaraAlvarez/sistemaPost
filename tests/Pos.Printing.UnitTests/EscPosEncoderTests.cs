using System.Text;

namespace Pos.Printing.UnitTests;

public class EscPosEncoderTests
{
    private static readonly PrinterOptions Default = new();

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>Bytes del documento sin el encabezado (ESC @ y ESC t 2) ni el cierre.</summary>
    private static byte[] Body(TicketElement element, PrinterOptions? options = null)
    {
        var bytes = EscPosEncoder.Encode(new TicketDocument("t", [element], Cut: false), options ?? Default);
        bytes[..5].ShouldBe([0x1B, 0x40, 0x1B, 0x74, 0x02]);
        return bytes[5..];
    }

    [Fact]
    public void Comandos_basicos()
    {
        EscPosEncoder.Esc.ShouldBe((byte)0x1B);
        EscPosEncoder.Gs.ShouldBe((byte)0x1D);
        EscPosEncoder.Lf.ShouldBe((byte)0x0A);
        EscPosEncoder.Initialize.ShouldBe([0x1B, 0x40]);
        EscPosEncoder.Cut.ShouldBe([0x1B, 0x64, 0x04, 0x1D, 0x56, 0x42, 0x00]);
        EscPosEncoder.OpenDrawer(DrawerPin.Pin2).ShouldBe([0x1B, 0x70, 0x00, 0x19, 0xFA]);
        EscPosEncoder.OpenDrawer(DrawerPin.Pin5).ShouldBe([0x1B, 0x70, 0x01, 0x19, 0xFA]);
    }

    [Fact]
    public void Opciones_por_defecto()
    {
        Default.Columns.ShouldBe(42);
        Default.CodePage.ShouldBe(PrinterCodePage.Pc850);
        Default.AutoCut.ShouldBeTrue();
        Default.DrawerPin.ShouldBe(DrawerPin.Pin2);
    }

    [Fact]
    public void Tiquete_completo_byte_a_byte()
    {
        var ticket = new TicketDocument(
            "Venta",
            [new TextLine("Mercado", TicketAlign.Center, Bold: true, DoubleSize: true), new ColumnsLine("Total", "12.500", Bold: true), new FeedElement(2)],
            Cut: true,
            OpenDrawer: true);

        var expected = Concat(
            [0x1B, 0x40],
            [0x1B, 0x74, 0x02],
            [0x1B, 0x61, 0x01, 0x1B, 0x45, 0x01, 0x1D, 0x21, 0x11],
            Ascii("Mercado"),
            [0x0A],
            [0x1D, 0x21, 0x00, 0x1B, 0x45, 0x00, 0x1B, 0x61, 0x00],
            [0x1B, 0x45, 0x01],
            Ascii("Total" + new string(' ', 31) + "12.500"),
            [0x0A],
            [0x1B, 0x45, 0x00],
            [0x1B, 0x64, 0x02],
            [0x1B, 0x64, 0x04, 0x1D, 0x56, 0x42, 0x00],
            [0x1B, 0x70, 0x00, 0x19, 0xFA]);

        EscPosEncoder.Encode(ticket, Default).ShouldBe(expected);
    }

    [Fact]
    public void Es_determinista()
    {
        var ticket = TicketSamples.Sale();
        EscPosEncoder.Encode(ticket, Default).ShouldBe(EscPosEncoder.Encode(ticket, Default));
    }

    [Theory]
    [InlineData(TicketAlign.Left, 0)]
    [InlineData(TicketAlign.Center, 1)]
    [InlineData(TicketAlign.Right, 2)]
    public void Alineacion_y_texto_normal(TicketAlign align, byte code) =>
        Body(new TextLine("Hola", align)).ShouldBe(Concat(
            [0x1B, 0x61, code, 0x1B, 0x45, 0x00, 0x1D, 0x21, 0x00],
            Ascii("Hola"),
            [0x0A, 0x1D, 0x21, 0x00, 0x1B, 0x45, 0x00, 0x1B, 0x61, 0x00]));

    [Fact]
    public void Doble_tamano_parte_a_la_mitad_del_ancho()
    {
        // A 32 columnas el doble tamaño deja 16: "Gracias por su" (14) y "compra" en otra línea.
        var bytes = Body(new TextLine("Gracias por su compra", DoubleSize: true), Default with { Columns = 32 });
        bytes.ShouldBe(Concat(
            [0x1B, 0x61, 0x00, 0x1B, 0x45, 0x00, 0x1D, 0x21, 0x11],
            Ascii("Gracias por su"),
            [0x0A],
            Ascii("compra"),
            [0x0A, 0x1D, 0x21, 0x00, 0x1B, 0x45, 0x00, 0x1B, 0x61, 0x00]));

        // Con una sola columna el ancho doble no baja de 1.
        var narrow = Body(new TextLine("ab", DoubleSize: true), Default with { Columns = 1 });
        narrow.ShouldBe(Concat(
            [0x1B, 0x61, 0x00, 0x1B, 0x45, 0x00, 0x1D, 0x21, 0x11],
            Ascii("a"),
            [0x0A],
            Ascii("b"),
            [0x0A, 0x1D, 0x21, 0x00, 0x1B, 0x45, 0x00, 0x1B, 0x61, 0x00]));
    }

    [Fact]
    public void Columnas_sin_negrita_y_separador()
    {
        Body(new ColumnsLine("Efectivo", "20.000")).ShouldBe(Concat(
            [0x1B, 0x45, 0x00],
            Ascii("Efectivo" + new string(' ', 28) + "20.000"),
            [0x0A, 0x1B, 0x45, 0x00]));

        Body(new SeparatorLine('='), Default with { Columns = 32 }).ShouldBe(Concat(Ascii(new string('=', 32)), [0x0A]));
        Body(new SeparatorLine()).ShouldBe(Concat(Ascii(new string('-', 42)), [0x0A]));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(10, 10)]
    [InlineData(25, 10)]
    public void Avance_de_papel_limitado(int lines, byte expected) =>
        Body(new FeedElement(lines)).ShouldBe([0x1B, 0x64, expected]);

    [Fact]
    public void Codigo_de_barras_code128()
    {
        Body(new BarcodeElement("V-000123")).ShouldBe(Concat(
            [0x1B, 0x61, 0x01],
            [0x1D, 0x68, 80, 0x1D, 0x77, 0x02, 0x1D, 0x48, 0x02],
            [0x1D, 0x6B, 73, 10],
            Ascii("{BV-000123"),
            [0x0A, 0x1B, 0x61, 0x00]));
    }

    [Fact]
    public void Codigo_de_barras_largo_o_con_tildes()
    {
        var bytes = Body(new BarcodeElement(new string('9', 300)));
        bytes[12..15].ShouldBe([0x1D, 0x6B, 73]);
        bytes[15].ShouldBe((byte)255);
        bytes.Length.ShouldBe(16 + 255 + 4);
        bytes[16..18].ShouldBe(Ascii("{B"));

        var accents = Body(new BarcodeElement("Ñandú"));
        accents[15].ShouldBe((byte)7);
        accents[16..23].ShouldBe(Ascii("{BNandu"));

        var empty = Body(new BarcodeElement(null!));
        empty[15].ShouldBe((byte)2);
        empty[16..18].ShouldBe(Ascii("{B"));
    }

    [Fact]
    public void Codigo_qr()
    {
        Body(new QrElement("abc")).ShouldBe(Concat(
            [0x1B, 0x61, 0x01],
            [0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00],
            [0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 0x06],
            [0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31],
            [0x1D, 0x28, 0x6B, 0x06, 0x00, 0x31, 0x50, 0x30],
            Ascii("abc"),
            [0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30],
            [0x0A, 0x1B, 0x61, 0x00]));
    }

    [Fact]
    public void Codigo_qr_largo_usa_dos_bytes_de_longitud_y_utf8()
    {
        var data = "https://catalogo-vpfe.dian.gov.co/document/searchqr?documentkey=" + new string('f', 300);
        var bytes = Body(new QrElement(data));
        var total = data.Length + 3;
        total.ShouldBeGreaterThan(255);
        bytes[28..36].ShouldBe([0x1D, 0x28, 0x6B, (byte)(total % 256), (byte)(total / 256), 0x31, 0x50, 0x30]);
        bytes[36..(36 + data.Length)].ShouldBe(Ascii(data));

        var utf8 = Body(new QrElement("ñ"));
        utf8[31..33].ShouldBe([0x05, 0x00]);
        utf8[36..38].ShouldBe([0xC3, 0xB1]);

        Body(new QrElement(null!))[31..33].ShouldBe([0x03, 0x00]);
    }

    [Fact]
    public void Pc850_para_tildes_ene_y_signos()
    {
        EscPosEncoder.Text("áéíóúñÑ¿¡", PrinterCodePage.Pc850).ShouldBe([0xA0, 0x82, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA8, 0xAD]);
        EscPosEncoder.Text("Año", PrinterCodePage.Pc850).ShouldBe([0x41, 0xA4, 0x6F]);
        EscPosEncoder.Text(null!, PrinterCodePage.Pc850).ShouldBeEmpty();
    }

    [Fact]
    public void Ascii_sin_pagina_de_codigos()
    {
        EscPosEncoder.ToAscii("¿Año? ¡Éxito! Café € ü").ShouldBe("?Ano? !Exito! Cafe ? u");
        EscPosEncoder.Text("Señor", PrinterCodePage.Ascii).ShouldBe(Ascii("Senor"));
        EscPosEncoder.Text(null!, PrinterCodePage.Ascii).ShouldBeEmpty();
        Should.Throw<ArgumentNullException>(() => EscPosEncoder.ToAscii(null!));

        var bytes = EscPosEncoder.Encode(new TicketDocument("t", [new SeparatorLine()], Cut: false), Default with { CodePage = PrinterCodePage.Ascii });
        bytes.ShouldBe(Concat([0x1B, 0x40], Ascii(new string('-', 42)), [0x0A]));
    }

    [Fact]
    public void Corte_y_cajon_segun_tiquete_y_opciones()
    {
        var header = new byte[] { 0x1B, 0x40, 0x1B, 0x74, 0x02 };
        var cut = new TicketDocument("t", []);
        EscPosEncoder.Encode(cut, Default).ShouldBe(Concat(header, EscPosEncoder.Cut));
        EscPosEncoder.Encode(cut, Default with { AutoCut = false }).ShouldBe(header);
        EscPosEncoder.Encode(cut with { Cut = false }, Default).ShouldBe(header);

        var drawer = new TicketDocument("t", [], Cut: false, OpenDrawer: true);
        EscPosEncoder.Encode(drawer, Default with { DrawerPin = DrawerPin.Pin5 }).ShouldBe(Concat(header, EscPosEncoder.OpenDrawer(DrawerPin.Pin5)));
    }

    [Fact]
    public void Argumentos_nulos()
    {
        Should.Throw<ArgumentNullException>(() => EscPosEncoder.Encode(null!, Default));
        Should.Throw<ArgumentNullException>(() => EscPosEncoder.Encode(new TicketDocument("t", []), null!));
    }

    [Theory]
    [InlineData(TicketLayout.Columns80Mm)]
    [InlineData(TicketLayout.Columns58Mm)]
    public void Ninguna_linea_impresa_supera_el_ancho(int columns)
    {
        // Sin códigos de barras ni QR (sus comandos llevan datos binarios): solo texto, columnas, separadores y avances.
        var ticket = TicketSamples.Sale(withCodes: false) with { Cut = false, OpenDrawer = false };
        var bytes = EscPosEncoder.Encode(ticket, Default with { Columns = columns, CodePage = PrinterCodePage.Ascii });
        var text = Encoding.ASCII.GetString(bytes);

        // Entre dos saltos de línea solo hay comandos y texto; se quitan los comandos conocidos para medir el texto.
        foreach (var segment in text.Split('\n'))
        {
            var visible = new string([.. StripCommands(segment)]);
            visible.Length.ShouldBeLessThanOrEqualTo(columns, visible);
        }
    }

    private static IEnumerable<char> StripCommands(string segment)
    {
        var i = 0;
        while (i < segment.Length)
        {
            var c = segment[i];
            if (c == '\x1B' || c == '\x1D')
            {
                // Todos los comandos de texto usados (ESC @, ESC a n, ESC E n, ESC t n, GS ! n, ESC d n) tienen a lo sumo 3 bytes.
                i += segment[i + 1] == 0x40 ? 2 : 3;
                continue;
            }

            yield return c;
            i++;
        }
    }
}
