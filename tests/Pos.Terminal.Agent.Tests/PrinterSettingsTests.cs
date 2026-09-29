using System.Globalization;
using Pos.Printing;
using Pos.Terminal.Agent.Printing;

namespace Pos.Terminal.Agent.Tests;

/// <summary>Conversión de la impresora del servidor (<c>ReceiptPrinterDto</c>) a la del agente, y límites del tiquete.</summary>
public class PrinterSettingsTests
{
    private static readonly AgentOptions Options = new() { FileOutputDirectory = AgentFactory.TempDirectory() };

    private static PrinterTarget? Resolve(ReceiptPrinterSettings? settings) => PrinterSettingsResolver.TryResolve(settings, Options, out _);

    [Fact]
    public void Sin_configurar_es_archivo_80_mm_PC850_con_corte_y_cajon_en_el_pin_2()
    {
        var target = Resolve(new ReceiptPrinterSettings())!;

        target.Connection.ShouldBe(PrinterConnectionKind.File);
        target.Destination.ShouldBe(Options.ResolveFileOutputDirectory());
        target.Options.ShouldBe(new PrinterOptions(42, PrinterCodePage.Pc850, AutoCut: true, DrawerPin.Pin2));
        target.DrawerConnected.ShouldBeTrue();
        target.Description.ShouldStartWith("Archivo ");
    }

    [Theory]
    [InlineData("192.168.1.50", "192.168.1.50", 9100, "Red 192.168.1.50:9100")]
    [InlineData("192.168.1.50:9101", "192.168.1.50", 9101, "Red 192.168.1.50:9101")]
    [InlineData("impresora-caja1", "impresora-caja1", 9100, "Red impresora-caja1:9100")]
    [InlineData("[fe80::1]:9100", "fe80::1", 9100, "Red [fe80::1]:9100")]
    [InlineData("::1", "::1", 9100, "Red [::1]:9100")]
    public void Direcciones_de_red(string address, string host, int port, string description)
    {
        var target = Resolve(new ReceiptPrinterSettings { Connection = "NETWORK", Address = $"  {address} " })!;

        target.Connection.ShouldBe(PrinterConnectionKind.Network);
        target.Destination.ShouldBe(host);
        target.Port.ShouldBe(port);
        target.Description.ShouldBe(description);
    }

    [Theory]
    [InlineData("192.168.1.50:")]
    [InlineData("192.168.1.50:0")]
    [InlineData("192.168.1.50:70000")]
    [InlineData("192.168.1.50:+80")]
    [InlineData("[fe80::1")]
    [InlineData("[fe80::1]x")]
    [InlineData("mal nombre")]
    public void Direcciones_de_red_invalidas(string address) =>
        Resolve(new ReceiptPrinterSettings { Connection = "NETWORK", Address = address }).ShouldBeNull();

    [Theory]
    [InlineData("COM3", "COM3", 9600)]
    [InlineData("com12:19200", "COM12", 19200)]
    [InlineData("COM1:115200", "COM1", 115200)]
    public void Puertos_serie(string address, string port, int baud)
    {
        var target = Resolve(new ReceiptPrinterSettings { Connection = "serial", Address = address })!;

        target.Connection.ShouldBe(PrinterConnectionKind.Serial);
        target.Destination.ShouldBe(port);
        target.Port.ShouldBe(baud);
        target.Description.ShouldBe(string.Create(CultureInfo.InvariantCulture, $"{port} ({baud} baudios)"));
    }

    [Fact]
    public void Spooler_papel_de_58_mm_ASCII_y_pin_5()
    {
        var target = Resolve(new ReceiptPrinterSettings
        {
            Connection = "WINDOWS_SPOOLER", Address = "EPSON TM-T20II Receipt", PaperWidthMm = 58, CodePage = "ascii", AutoCut = false, DrawerPin = "pin5",
        })!;

        target.Connection.ShouldBe(PrinterConnectionKind.WindowsSpooler);
        target.Destination.ShouldBe("EPSON TM-T20II Receipt");
        target.Options.ShouldBe(new PrinterOptions(32, PrinterCodePage.Ascii, AutoCut: false, DrawerPin.Pin5));
        target.Description.ShouldBe("Windows \"EPSON TM-T20II Receipt\"");
        Resolve(new ReceiptPrinterSettings { Connection = "WindowsSpooler", Address = "X" })!.Connection.ShouldBe(PrinterConnectionKind.WindowsSpooler);
        Resolve(new ReceiptPrinterSettings { Connection = "WINDOWS_SPOOLER", Address = "X\tY" }).ShouldBeNull();
    }

    [Fact]
    public void Configuracion_invalida_con_mensaje()
    {
        PrinterSettingsResolver.TryResolve(null, Options, out var missing).ShouldBeNull();
        missing.ShouldContain("printer");
        PrinterSettingsResolver.TryResolve(new ReceiptPrinterSettings { Address = new string('a', 201) }, Options, out var longAddress).ShouldBeNull();
        longAddress.ShouldContain("200");
        PrinterSettingsResolver.TryResolve(new ReceiptPrinterSettings { Connection = null }, Options, out var connection).ShouldBeNull();
        connection.ShouldContain("FILE, NETWORK, WINDOWS_SPOOLER o SERIAL");
        PrinterSettingsResolver.TryResolve(new ReceiptPrinterSettings { Address = "C:\\Windows\\System32" }, Options, out var folder).ShouldBeNull();
        folder.ShouldContain("AllowedFileDirectories");
        PrinterSettingsResolver.TryResolve(new ReceiptPrinterSettings { Address = "\0" }, Options, out var badFolder).ShouldBeNull();
        badFolder.ShouldNotBeNullOrEmpty();
        Should.Throw<ArgumentNullException>(() => PrinterSettingsResolver.TryResolve(new ReceiptPrinterSettings(), null!, out _));
    }

    [Fact]
    public void Limites_del_tiquete()
    {
        TicketValidator.Validate(AgentApiTests.Sale()).ShouldBeNull();
        TicketValidator.Validate(new TicketDocument("x", null!)).ShouldNotBeNull();
        TicketValidator.Validate(new TicketDocument(new string('t', 2001), [new FeedElement()])).ShouldNotBeNull();
        TicketValidator.Validate(new TicketDocument("x", [.. Enumerable.Repeat<TicketElement>(new FeedElement(), 2001)])).ShouldNotBeNull();
        TicketValidator.Validate(new TicketDocument("x", [null!])).ShouldBe("Elemento 1: vacío.");
        TicketValidator.Validate(new TicketDocument("x", [new TextLine(null!)])).ShouldNotBeNull();
        TicketValidator.Validate(new TicketDocument("x", [new TextLine(new string('a', 2001))])).ShouldNotBeNull();
        TicketValidator.Validate(new TicketDocument("x", [new ColumnsLine(new string('a', 1500), new string('b', 600))])).ShouldNotBeNull();
        TicketValidator.Validate(new TicketDocument("x", [new SeparatorLine(' ')])).ShouldBeNull();
        TicketValidator.Validate(new TicketDocument("x", [new SeparatorLine('\t')])).ShouldNotBeNull();
        TicketValidator.Validate(new TicketDocument("x", [new BarcodeElement(new string('1', 81))])).ShouldNotBeNull();
        TicketValidator.Validate(new TicketDocument("x", [new QrElement("")])).ShouldNotBeNull();
        TicketValidator.Validate(new TicketDocument("x", [new QrElement(new string('ñ', 1001))])).ShouldNotBeNull();
        TicketValidator.Validate(new TicketDocument("x", [new FeedElement(-1)])).ShouldNotBeNull();
    }

    [Fact]
    public void La_pagina_de_prueba_describe_la_impresora()
    {
        var target = Resolve(new ReceiptPrinterSettings { Connection = "NETWORK", Address = "10.0.0.5", PaperWidthMm = 58, DrawerPin = "PIN5" })!;
        var page = TestPage.Build(target, "Caja 2", DateTimeOffset.Parse("2026-10-01T10:00:00-05:00", CultureInfo.InvariantCulture));
        var text = TicketLayout.ToText(page, target.Options.Columns);

        text.ShouldContain("PÁGINA DE PRUEBA");
        text.ShouldContain("Caja 2");
        text.ShouldContain("pin 5");
        text.ShouldContain("Red 10.0.0.5:9100");
        text.ShouldContain("2026-10-01 10:00:00");
        page.OpenDrawer.ShouldBeTrue();
        TicketValidator.Validate(page).ShouldBeNull();

        var noDrawer = TestPage.Build(target with { DrawerConnected = false }, null, DateTimeOffset.UnixEpoch);
        noDrawer.OpenDrawer.ShouldBeFalse();
        TicketLayout.ToText(noDrawer, 32).ShouldContain("Caja" + new string(' ', 27) + "-");
        Should.Throw<ArgumentNullException>(() => TestPage.Build(null!, null, DateTimeOffset.UnixEpoch));
    }
}
