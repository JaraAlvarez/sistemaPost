namespace Pos.Printing.UnitTests;

/// <summary>Tiquete de venta representativo, con nombres largos para forzar el ajuste de líneas.</summary>
internal static class TicketSamples
{
    public static TicketDocument Sale(bool withCodes = true)
    {
        List<TicketElement> elements =
        [
            new TextLine("Supermercado La Economía del Barrio Central S.A.S.", TicketAlign.Center, Bold: true, DoubleSize: true),
            new TextLine("NIT 900.123.456-7 · Calle 10 # 20-30, Bogotá D.C.", TicketAlign.Center),
            new TextLine("Tiquete de venta V-000123"),
            new SeparatorLine(),
            new ColumnsLine("Arroz Diana premium bolsa x 5.000 g (paquete familiar)", TicketLayout.Money(25_900m)),
            new ColumnsLine($"  {TicketLayout.Quantity(2m)} x {TicketLayout.Money(12_950m)}", string.Empty),
            new ColumnsLine("Aceite", TicketLayout.Money(6_225.5m)),
            new ColumnsLine("Detergente", "Precio especial promocional"),
            new ColumnsLine("Supercalifragilisticoespialidosoextraordinario", "1"),
            new SeparatorLine('='),
            new ColumnsLine("TOTAL", TicketLayout.Money(1_234_567m), Bold: true),
            new TextLine("Lleve 3 pague 2 en gaseosas de 400 ml durante todo el fin de semana", TicketAlign.Right),
            new FeedElement(2),
            new TextLine("Gracias por su compra\n¡Vuelva pronto!", TicketAlign.Center),
        ];

        if (withCodes)
        {
            elements.Add(new BarcodeElement("V-000123"));
            elements.Add(new QrElement("https://example.test/qr?id=123"));
        }

        return new TicketDocument("Venta V-000123", elements, Cut: true, OpenDrawer: true);
    }
}
