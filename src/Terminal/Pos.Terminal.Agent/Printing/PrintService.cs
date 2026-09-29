using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Pos.Printing;
using Pos.Terminal.Agent.Transports;

namespace Pos.Terminal.Agent.Printing;

/// <summary>Resultado de un trabajo (el agente no guarda el contenido del tiquete: solo este resumen para <c>/status</c>).</summary>
public sealed record PrintOutcome(string Kind, bool Succeeded, DateTimeOffset At, int Bytes, string? Printer, string? Destination, string? ErrorCode, string? Error)
{
    public const string NoDrawer = "AGENT.NO_DRAWER";
    public const string PrinterUnavailable = "AGENT.PRINTER_UNAVAILABLE";
}

/// <summary>
/// Convierte el tiquete neutro a ESC/POS con <see cref="EscPosEncoder"/> y lo envía por el transporte de la impresora recibida. Un
/// trabajo a la vez: los bytes de dos tiquetes nunca se mezclan. Si la impresora falla, la venta ya está guardada en el servidor y
/// la caja puede reimprimir.
/// </summary>
public sealed class PrintService(IPrinterTransportFactory transports, TimeProvider time, IOptions<AgentOptions> options, ILogger<PrintService> logger)
    : IDisposable
{
    private readonly SemaphoreSlim _queue = new(1, 1);

    public PrintOutcome? LastJob { get; private set; }

    public void Dispose() => _queue.Dispose();

    /// <summary>Tiquete; el pulso del cajón que pida el tiquete se omite si la impresora no tiene cajón conectado.</summary>
    public Task<PrintOutcome> PrintAsync(TicketDocument ticket, PrinterTarget printer, CancellationToken cancellationToken) =>
        SendAsync("print", printer, Encode(ticket, printer), cancellationToken);

    /// <summary>Pulso del cajón por la impresora (solo por instrucción de la caja, D7-14).</summary>
    public Task<PrintOutcome> OpenDrawerAsync(PrinterTarget printer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(printer);
        return printer.DrawerConnected
            ? SendAsync("drawer", printer, [.. EscPosEncoder.Initialize, .. EscPosEncoder.OpenDrawer(printer.Options.DrawerPin)], cancellationToken)
            : Task.FromResult(Remember(Failure("drawer", printer, PrintOutcome.NoDrawer, "La impresora de esta caja no tiene un cajón conectado.")));
    }

    /// <summary>Página de prueba del día de la instalación: tildes, estilos, código de barras, QR, corte y pulso del cajón.</summary>
    public Task<PrintOutcome> PrintTestPageAsync(PrinterTarget printer, string? terminalName, CancellationToken cancellationToken) =>
        SendAsync("test-page", printer, Encode(TestPage.Build(printer, terminalName, time.GetLocalNow()), printer), cancellationToken);

    public static byte[] Encode(TicketDocument ticket, PrinterTarget printer)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(printer);
        return EscPosEncoder.Encode(printer.DrawerConnected ? ticket : ticket with { OpenDrawer = false }, printer.Options);
    }

    private async Task<PrintOutcome> SendAsync(string kind, PrinterTarget printer, byte[] bytes, CancellationToken cancellationToken)
    {
        await _queue.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.JobTimeout);
            var destination = await transports.Create(printer).SendAsync(bytes, timeout.Token);
            AgentLog.JobSent(logger, kind, printer.Description, bytes.Length, destination);
            return Remember(new PrintOutcome(kind, true, time.GetUtcNow(), bytes.Length, printer.Description, destination, null, null));
        }
        catch (Exception ex) when (IsPrinterFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            AgentLog.JobFailed(logger, ex, printer.Description, kind);
            return Remember(Failure(kind, printer, PrintOutcome.PrinterUnavailable, $"La impresora ({printer.Description}) no está disponible: {ex.Message}"));
        }
        finally
        {
            _queue.Release();
        }
    }

    private static bool IsPrinterFailure(Exception ex) =>
        ex is IOException or SocketException or TimeoutException or UnauthorizedAccessException or OperationCanceledException
            or Win32Exception or InvalidOperationException or ArgumentException or PlatformNotSupportedException;

    private PrintOutcome Failure(string kind, PrinterTarget printer, string code, string message) =>
        new(kind, false, time.GetUtcNow(), 0, printer.Description, null, code, message);

    private PrintOutcome Remember(PrintOutcome outcome)
    {
        LastJob = outcome;
        return outcome;
    }
}

/// <summary>Página de prueba para validar la impresora el día de la instalación.</summary>
public static class TestPage
{
    public static TicketDocument Build(PrinterTarget printer, string? terminalName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(printer);
        TicketElement[] elements =
        [
            new TextLine("PÁGINA DE PRUEBA", TicketAlign.Center, Bold: true, DoubleSize: true),
            new TextLine($"Agente de caja {AgentInfo.Version}", TicketAlign.Center),
            new TextLine(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), TicketAlign.Center),
            new SeparatorLine(),
            new ColumnsLine("Caja", string.IsNullOrWhiteSpace(terminalName) ? "-" : terminalName.Trim()),
            new ColumnsLine("Conexión", printer.Description),
            new ColumnsLine("Papel", $"{printer.PaperWidthMm} mm ({printer.Options.Columns} columnas)"),
            new ColumnsLine("Página de códigos", printer.Options.CodePage.ToString().ToUpperInvariant()),
            new ColumnsLine("Corte automático", printer.Options.AutoCut ? "sí" : "no"),
            new ColumnsLine("Cajón", printer.DrawerConnected ? (printer.Options.DrawerPin == DrawerPin.Pin5 ? "pin 5" : "pin 2") : "no"),
            new SeparatorLine(),
            new TextLine("Español: ñ Ñ á é í ó ú Á É Í Ó Ú ü ¡ ¿"),
            new ColumnsLine("Producto de ejemplo", TicketLayout.Money(1_234_567m)),
            new TextLine("Negrita", Bold: true),
            new TextLine("Doble tamaño", DoubleSize: true),
            new TextLine("Centrado", TicketAlign.Center),
            new TextLine("Derecha", TicketAlign.Right),
            new SeparatorLine('='),
            new BarcodeElement("PRUEBA-123"),
            new QrElement("https://www.dian.gov.co"),
            new FeedElement(),
            new TextLine("Si lee bien todo, la impresora está lista.", TicketAlign.Center),
        ];
        return new TicketDocument("Página de prueba", elements, Cut: true, OpenDrawer: printer.DrawerConnected);
    }
}
