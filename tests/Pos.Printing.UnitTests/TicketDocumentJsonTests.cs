using System.Text.Json;

namespace Pos.Printing.UnitTests;

public class TicketDocumentJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ida_y_vuelta_conserva_cada_elemento(bool web)
    {
        var options = web ? Web : JsonSerializerOptions.Default;
        var ticket = TicketSamples.Sale();

        var json = JsonSerializer.Serialize(ticket, options);
        var copy = JsonSerializer.Deserialize<TicketDocument>(json, options).ShouldNotBeNull();

        copy.Title.ShouldBe(ticket.Title);
        copy.Cut.ShouldBe(ticket.Cut);
        copy.OpenDrawer.ShouldBe(ticket.OpenDrawer);
        copy.Elements.ShouldBe(ticket.Elements);
        copy.Elements.Select(e => e.GetType()).ShouldBe(ticket.Elements.Select(e => e.GetType()));
        EscPosEncoder.Encode(copy, new PrinterOptions()).ShouldBe(EscPosEncoder.Encode(ticket, new PrinterOptions()));
    }

    [Fact]
    public void Discriminador_type_por_elemento()
    {
        TicketElement[] elements =
        [
            new TextLine("a"), new ColumnsLine("a", "b"), new SeparatorLine(), new BarcodeElement("1"), new QrElement("q"), new FeedElement(),
        ];
        var json = JsonSerializer.Serialize(new TicketDocument("t", elements), Web);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("type").GetString())
            .ShouldBe(["text", "columns", "separator", "barcode", "qr", "feed"]);
    }

    [Fact]
    public void Lee_el_json_que_envia_el_servidor()
    {
        const string json = """
            {
              "title": "Reimpresión",
              "elements": [
                { "type": "text", "text": "Mercado", "align": 1, "bold": true, "doubleSize": true },
                { "type": "columns", "left": "Total", "right": "12.500", "bold": true },
                { "type": "separator", "character": "=" },
                { "type": "barcode", "data": "V-1" },
                { "type": "qr", "data": "https://x.test" },
                { "type": "feed", "lines": 3 }
              ],
              "cut": false,
              "openDrawer": true
            }
            """;

        var ticket = JsonSerializer.Deserialize<TicketDocument>(json, Web).ShouldNotBeNull();

        ticket.Title.ShouldBe("Reimpresión");
        ticket.Cut.ShouldBeFalse();
        ticket.OpenDrawer.ShouldBeTrue();
        ticket.Elements.ShouldBe(
        [
            new TextLine("Mercado", TicketAlign.Center, Bold: true, DoubleSize: true),
            new ColumnsLine("Total", "12.500", Bold: true),
            new SeparatorLine('='),
            new BarcodeElement("V-1"),
            new QrElement("https://x.test"),
            new FeedElement(3),
        ]);
    }
}
