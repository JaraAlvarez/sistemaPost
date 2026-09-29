namespace Pos.Printing.UnitTests;

public class TicketLayoutTests
{
    [Theory]
    [InlineData(58, 32)]
    [InlineData(57, 32)]
    [InlineData(76, 42)]
    [InlineData(80, 42)]
    public void Columnas_por_ancho_de_papel(int millimeters, int columns)
    {
        TicketLayout.Columns80Mm.ShouldBe(42);
        TicketLayout.Columns58Mm.ShouldBe(32);
        TicketLayout.ColumnsFor(millimeters).ShouldBe(columns);
    }

    [Theory]
    [InlineData(12_500, "12.500")]
    [InlineData(1_234_567, "1.234.567")]
    [InlineData(0, "0")]
    [InlineData(6_225.5, "6.225,50")]
    [InlineData(99.99, "99,99")]
    public void Dinero_en_pesos(double value, string expected) => TicketLayout.Money((decimal)value).ShouldBe(expected);

    [Fact]
    public void Cantidades()
    {
        TicketLayout.Quantity(3m).ShouldBe("3");
        TicketLayout.Quantity(3.000m).ShouldBe("3");
        TicketLayout.Quantity(2.500m).ShouldBe("2,5");
        TicketLayout.Quantity(0.125m).ShouldBe("0,125");
    }

    [Fact]
    public void Ajuste_por_palabras()
    {
        TicketLayout.Wrap("Arroz Diana premium bolsa", 10).ShouldBe(["Arroz", "Diana", "premium", "bolsa"]);
        TicketLayout.Wrap("abcde fghij", 11).ShouldBe(["abcde fghij"]);
        TicketLayout.Wrap("abcde fghij", 10).ShouldBe(["abcde", "fghij"]);
        TicketLayout.Wrap("  hola   mundo  ", 42).ShouldBe(["hola mundo"]);
    }

    [Fact]
    public void Palabra_larga_se_corta()
    {
        TicketLayout.Wrap("abc defghijklmnop", 5).ShouldBe(["abc", "defgh", "ijklm", "nop"]);
        TicketLayout.Wrap("abcdefghij", 5).ShouldBe(["abcde", "fghij"]);
    }

    [Fact]
    public void Saltos_de_linea_y_vacios()
    {
        TicketLayout.Wrap("a\r\nb\n\nc", 10).ShouldBe(["a", "b", string.Empty, "c"]);
        TicketLayout.Wrap(string.Empty, 42).ShouldBe([string.Empty]);
        TicketLayout.Wrap(null!, 42).ShouldBe([string.Empty]);
        Should.Throw<ArgumentOutOfRangeException>(() => TicketLayout.Wrap("x", 0));
    }

    [Fact]
    public void Izquierda_y_derecha()
    {
        var line = TicketLayout.Columns("Total", "12.500", 42).ShouldHaveSingleItem();
        line.ShouldBe("Total" + new string(' ', 31) + "12.500");
        line.Length.ShouldBe(42);

        TicketLayout.Columns("Arroz Diana premium", "25.900", 20).ShouldBe(["Arroz Diana", "premium       25.900"]);
        TicketLayout.Columns("x", null!, 10).ShouldBe(["x" + new string(' ', 9)]);
        TicketLayout.Columns("ab", "123456789", 10).ShouldBe(["a", "b123456789"]);
    }

    [Fact]
    public void Derecha_mas_ancha_que_la_linea_se_recorta()
    {
        TicketLayout.Columns("Detergente", "Precio especial promocional", 20).ShouldBe(["Detergente", "Precio especial prom"]);
        TicketLayout.Columns("A", "1234567890", 10).ShouldBe(["A", "1234567890"]);
    }

    [Theory]
    [InlineData(TicketAlign.Left, "abc")]
    [InlineData(TicketAlign.Center, "   abc")]
    [InlineData(TicketAlign.Right, "       abc")]
    public void Alineacion(TicketAlign align, string expected) => TicketLayout.Align("abc", align, 10).ShouldBe(expected);

    [Fact]
    public void Alineacion_de_texto_que_llena_el_ancho_o_nulo()
    {
        TicketLayout.Align("abcdefghij", TicketAlign.Right, 10).ShouldBe("abcdefghij");
        TicketLayout.Align("abcdefghijk", TicketAlign.Center, 10).ShouldBe("abcdefghijk");
        TicketLayout.Align(null!, TicketAlign.Right, 3).ShouldBe("   ");
    }

    [Fact]
    public void Texto_plano_exacto()
    {
        var ticket = new TicketDocument(
            "Prueba",
            [
                new TextLine("Hola", TicketAlign.Center),
                new ColumnsLine("A", "1"),
                new SeparatorLine('*'),
                new FeedElement(1),
                new BarcodeElement("123"),
                new QrElement("x"),
                new TextLine("Doble", TicketAlign.Right, DoubleSize: true),
                new TextLine("Fin", TicketAlign.Right),
                new FeedElement(-1),
            ]);

        TicketLayout.ToText(ticket, 10).ShouldBe("   Hola\nA        1\n**********\n\n|| 123 ||\n   [QR]\nDoble\n       Fin\n");
    }

    [Fact]
    public void Avance_limitado_a_diez_lineas() =>
        TicketLayout.Lines(new TicketDocument("t", [new FeedElement(25)]), 42).Count().ShouldBe(10);

    [Theory]
    [InlineData(TicketLayout.Columns80Mm)]
    [InlineData(TicketLayout.Columns58Mm)]
    public void Ninguna_linea_supera_el_ancho(int columns)
    {
        var ticket = TicketSamples.Sale();
        var lines = TicketLayout.Lines(ticket, columns).ToList();

        lines.Count.ShouldBeGreaterThan(ticket.Elements.Count);
        foreach (var line in lines)
        {
            line.Length.ShouldBeLessThanOrEqualTo(columns, line);
        }

        // Las líneas de doble tamaño ocupan el doble: su texto no pasa de la mitad del ancho.
        var header = (TextLine)ticket.Elements[0];
        foreach (var line in TicketLayout.Wrap(header.Text, columns / 2))
        {
            line.Length.ShouldBeLessThanOrEqualTo(columns / 2);
        }

        lines.ShouldContain(new string('=', columns));
        lines.ShouldContain(l => l.StartsWith("TOTAL", StringComparison.Ordinal) && l.EndsWith("1.234.567", StringComparison.Ordinal) && l.Length == columns);
        TicketLayout.ToText(ticket, columns).ShouldBe(string.Join('\n', lines) + "\n");
    }

    [Fact]
    public void Argumentos_nulos()
    {
        Should.Throw<ArgumentNullException>(() => TicketLayout.ToText(null!, 42));
        Should.Throw<ArgumentNullException>(() => TicketLayout.Lines(null!, 42).ToList());
    }
}
