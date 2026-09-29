using Pos.Modules.Organization.Domain;

namespace Pos.Modules.Organization.UnitTests;

public class TerminalDeviceTests
{
    private static TerminalDevice Printer(Guid? terminal = null) => TerminalDevice.Defaults(Guid.CreateVersion7(), Guid.CreateVersion7(), terminal ?? Guid.CreateVersion7());

    [Fact]
    public void Sin_configurar_usa_archivo_80_mm_PC850_corte_y_cajon_en_el_pin_2()
    {
        var terminal = Guid.CreateVersion7();
        var printer = Printer(terminal);

        printer.Kind.ShouldBe(TerminalDevice.ReceiptPrinterKind);
        (printer.Connection, printer.Address, printer.PaperWidthMm, printer.CodePage, printer.AutoCut, printer.DrawerConnected, printer.DrawerPin)
            .ShouldBe((PrinterConnection.File, (string?)null, 80, PrinterCodePageSetting.Pc850, true, true, DrawerPinSetting.Pin2));
        printer.PosTerminalId.ShouldBe(terminal);
        printer.AuditLabel.ShouldBe($"Impresora de la caja {terminal}");
    }

    [Fact]
    public void Configura_una_impresora_de_red_y_recorta_la_direccion()
    {
        var printer = Printer();

        printer.Configure(PrinterConnection.Network, "  192.168.1.50:9100 ", 58, PrinterCodePageSetting.Ascii, false, false, DrawerPinSetting.Pin5).IsSuccess.ShouldBeTrue();

        (printer.Connection, printer.Address, printer.PaperWidthMm, printer.CodePage, printer.AutoCut, printer.DrawerConnected, printer.DrawerPin)
            .ShouldBe((PrinterConnection.Network, (string?)"192.168.1.50:9100", 58, PrinterCodePageSetting.Ascii, false, false, DrawerPinSetting.Pin5));
    }

    [Fact]
    public void El_archivo_no_exige_direccion_y_una_en_blanco_queda_nula()
    {
        var printer = Printer();

        printer.Configure(PrinterConnection.File, "   ", 80, PrinterCodePageSetting.Pc850, true, true, DrawerPinSetting.Pin2).IsSuccess.ShouldBeTrue();
        printer.Address.ShouldBeNull();
    }

    [Theory]
    [InlineData(PrinterConnection.Network, null, 80)]
    [InlineData(PrinterConnection.WindowsSpooler, " ", 80)]
    [InlineData(PrinterConnection.Serial, null, 80)]
    [InlineData(PrinterConnection.File, null, 76)]
    [InlineData((PrinterConnection)99, "x", 80)]
    public void Rechaza_papel_invalido_conexion_desconocida_o_direccion_faltante(PrinterConnection connection, string? address, int paper)
    {
        var printer = Printer();

        var result = printer.Configure(connection, address, paper, PrinterCodePageSetting.Pc850, true, true, DrawerPinSetting.Pin2);

        result.Error.Code.ShouldBe("ORGANIZATION.INVALID_PRINTER");
        printer.Connection.ShouldBe(PrinterConnection.File);
    }

    [Fact]
    public void Rechaza_una_direccion_de_mas_de_200_caracteres() =>
        Printer().Configure(PrinterConnection.WindowsSpooler, new string('x', 201), 80, PrinterCodePageSetting.Pc850, true, true, DrawerPinSetting.Pin2)
            .Error.Code.ShouldBe("ORGANIZATION.INVALID_PRINTER");
}
