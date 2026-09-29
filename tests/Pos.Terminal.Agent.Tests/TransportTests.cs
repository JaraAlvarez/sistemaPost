using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pos.Printing;
using Pos.Terminal.Agent.Printing;
using Pos.Terminal.Agent.Transports;
using static Pos.Terminal.Agent.Tests.AgentFactory;

namespace Pos.Terminal.Agent.Tests;

public class TransportTests
{
    private static PrinterTarget Resolve(ReceiptPrinterSettings settings, AgentOptions? options = null) =>
        PrinterSettingsResolver.TryResolve(settings, options ?? new AgentOptions { FileOutputDirectory = TempDirectory() }, out var error)
        ?? throw new InvalidOperationException(error);

    [Fact]
    public async Task El_transporte_de_archivo_crea_un_archivo_nuevo_por_trabajo()
    {
        var directory = TempDirectory();
        var transport = new FilePrinterTransport(directory);
        var first = EscPosEncoder.Encode(AgentApiTests.Sale(), new PrinterOptions());
        var second = EscPosEncoder.OpenDrawer(DrawerPin.Pin2);

        var firstPath = await transport.SendAsync(first, Ct);
        var secondPath = await transport.SendAsync(second, Ct);

        firstPath.ShouldNotBe(secondPath);
        Path.GetDirectoryName(firstPath).ShouldBe(Path.GetFullPath(directory));
        Path.GetFileName(firstPath).ShouldMatch(@"^tiquete-\d{8}-\d{6}-\d{3}-\d{4}\.escpos$");
        (await File.ReadAllBytesAsync(firstPath, Ct)).ShouldBe(first);
        (await File.ReadAllBytesAsync(secondPath, Ct)).ShouldBe(second);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task El_transporte_de_red_entrega_los_bytes_exactos_por_TCP()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var bytes = EscPosEncoder.Encode(AgentApiTests.Sale(), new PrinterOptions());
        var received = ReceiveAsync(listener);

        var destination = await new NetworkPrinterTransport("127.0.0.1", port, TimeSpan.FromSeconds(5)).SendAsync(bytes, Ct);

        destination.ShouldBe($"127.0.0.1:{port}");
        (await received).ShouldBe(bytes);
    }

    [Fact]
    public async Task Una_impresora_de_red_que_no_responde_falla()
    {
        var closed = new NetworkPrinterTransport("127.0.0.1", AgentApiTests.ClosedPort(), TimeSpan.FromSeconds(2));

        // Windows reintenta la conexión rechazada unos 2 s: según la máquina llega el rechazo o vence el tiempo.
        var error = await Should.ThrowAsync<Exception>(() => closed.SendAsync(new byte[] { 1 }, Ct));
        (error is SocketException or TimeoutException).ShouldBeTrue(error.ToString());

        // Dirección que no responde (TEST-NET-1): vence el tiempo de conexión (o la red la rechaza de inmediato).
        var silent = await Should.ThrowAsync<Exception>(
            () => new NetworkPrinterTransport("192.0.2.1", 9100, TimeSpan.FromMilliseconds(200)).SendAsync(new byte[] { 1 }, Ct));
        (silent is SocketException or TimeoutException).ShouldBeTrue(silent.ToString());
    }

    [Fact]
    public async Task Puerto_serie_inexistente()
    {
        var transport = new SerialPrinterTransport("COM987", 9600);

        (await Should.ThrowAsync<Exception>(() => transport.SendAsync(new byte[] { 1 }, Ct))).ShouldBeAssignableTo<IOException>();
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Impresora_de_Windows_inexistente()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Solo en Windows.");
        var transport = new WindowsSpoolerTransport("Impresora que no existe " + Guid.NewGuid());
        transport.Exists().ShouldBeFalse();
        (await Should.ThrowAsync<IOException>(() => transport.SendAsync(new byte[] { 1 }, Ct))).Message.ShouldContain("Windows no encuentra la impresora");
    }

    [Fact]
    public void La_fabrica_elige_el_transporte_de_la_impresora()
    {
        var options = new AgentOptions { FileOutputDirectory = TempDirectory() };
        var factory = new PrinterTransportFactory(Options.Create(options));

        factory.Create(Resolve(new ReceiptPrinterSettings { Connection = "NETWORK", Address = "10.0.0.5" })).ShouldBeOfType<NetworkPrinterTransport>();
        factory.Create(Resolve(new ReceiptPrinterSettings { Connection = "SERIAL", Address = "COM3" })).ShouldBeOfType<SerialPrinterTransport>();
        factory.Create(Resolve(new ReceiptPrinterSettings(), options)).ShouldBeOfType<FilePrinterTransport>()
            .Directory.ShouldBe(options.ResolveFileOutputDirectory());
        if (OperatingSystem.IsWindows())
        {
            factory.Create(Resolve(new ReceiptPrinterSettings { Connection = "WINDOWS_SPOOLER", Address = "EPSON TM-T20" })).ShouldBeOfType<WindowsSpoolerTransport>();
        }

        Should.Throw<ArgumentNullException>(() => factory.Create(null!));
    }

    [Fact]
    public async Task Un_fallo_de_la_impresora_se_informa_sin_perder_el_servicio()
    {
        var options = Options.Create(new AgentOptions());
        using var printing = new PrintService(new PrinterTransportFactory(options), TimeProvider.System, options, NullLogger<PrintService>.Instance);

        var outcome = await printing.PrintAsync(AgentApiTests.Sale(), Resolve(new ReceiptPrinterSettings { Connection = "SERIAL", Address = "COM986" }), Ct);

        outcome.Succeeded.ShouldBeFalse();
        outcome.ErrorCode.ShouldBe(PrintOutcome.PrinterUnavailable);
        outcome.Error!.ShouldContain("COM986");
        printing.LastJob.ShouldBe(outcome);
    }

    internal static async Task<byte[]> ReceiveAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync(Ct);
        await using var stream = client.GetStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }
}
