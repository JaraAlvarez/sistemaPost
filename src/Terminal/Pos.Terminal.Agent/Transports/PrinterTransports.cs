using System.Globalization;
using System.IO.Ports;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Pos.Terminal.Agent.Printing;

namespace Pos.Terminal.Agent.Transports;

/// <summary>Camino de los bytes hasta la impresora. Cada envío es un trabajo completo (tiquete, pulso del cajón o página de prueba).</summary>
public interface IPrinterTransport
{
    /// <summary>Envía los bytes; devuelve el destino concreto (p. ej. el archivo creado).</summary>
    Task<string> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

public interface IPrinterTransportFactory
{
    IPrinterTransport Create(PrinterTarget printer);
}

/// <summary>Crea el transporte de la impresora recibida: archivo, red, spooler de Windows o puerto serie.</summary>
public sealed class PrinterTransportFactory(IOptions<AgentOptions> options) : IPrinterTransportFactory
{
    public IPrinterTransport Create(PrinterTarget printer)
    {
        ArgumentNullException.ThrowIfNull(printer);
        switch (printer.Connection)
        {
            case PrinterConnectionKind.Network:
                return new NetworkPrinterTransport(printer.Destination, printer.Port, options.Value.NetworkConnectTimeout);
            case PrinterConnectionKind.WindowsSpooler:
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("El spooler de Windows solo está disponible en Windows.");
                }

                return new WindowsSpoolerTransport(printer.Destination);
            case PrinterConnectionKind.Serial:
                return new SerialPrinterTransport(printer.Destination, printer.Port);
            default:
                return new FilePrinterTransport(printer.Destination);
        }
    }
}

/// <summary>
/// Un archivo NUEVO por trabajo (<c>tiquete-AAAAMMDD-HHMMSS-fff-NNNN.escpos</c>) en la carpeta indicada: nunca sobrescribe. Sirve
/// para probar sin hardware y comparar los bytes ESC/POS.
/// </summary>
public sealed class FilePrinterTransport(string directory) : IPrinterTransport
{
    public const string Extension = ".escpos";

    private static int _sequence;

    public string Directory { get; } = Path.GetFullPath(directory);

    public async Task<string> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var sequence = Interlocked.Increment(ref _sequence) % 10_000;
        var name = string.Create(CultureInfo.InvariantCulture, $"tiquete-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{sequence:D4}{Extension}");
        var path = Path.Combine(Directory, name);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        await file.WriteAsync(data, cancellationToken);
        return path;
    }
}

/// <summary>Impresora de red: TCP "RAW" (puerto 9100 por defecto).</summary>
public sealed class NetworkPrinterTransport(string host, int port, TimeSpan connectTimeout) : IPrinterTransport
{
    public async Task<string> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(connectTimeout);
            try
            {
                await client.ConnectAsync(host, port, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"La impresora {host}:{port} no respondió en {connectTimeout.TotalSeconds:0.#} s.");
            }
        }

        await using var stream = client.GetStream();
        await stream.WriteAsync(data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        client.Client.Shutdown(SocketShutdown.Send);
        return $"{host}:{port}";
    }
}

/// <summary>Puerto serie o USB virtual (<c>COMx</c>): 8 bits, sin paridad, 1 bit de parada, sin control de flujo.</summary>
public sealed class SerialPrinterTransport(string portName, int baudRate) : IPrinterTransport
{
    public Task<string> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                using var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One) { WriteTimeout = 10_000, Handshake = Handshake.None };
                port.Open();
                port.Write(data.ToArray(), 0, data.Length);
                port.BaseStream.Flush();
                return $"{portName} ({baudRate} baudios)";
            },
            cancellationToken);
}
