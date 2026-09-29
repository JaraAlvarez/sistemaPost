using System.Globalization;
using System.Text.RegularExpressions;
using Pos.Printing;

namespace Pos.Terminal.Agent.Printing;

/// <summary>
/// Impresora de tiquetes TAL COMO la devuelve el servidor en <c>GET /organization/terminals/{id}/receipt-printer</c>
/// (<c>ReceiptPrinterDto</c> del módulo Organization): la interfaz de caja la lee y la entrega al agente en cada petición. Los valores
/// por defecto son los mismos del servidor para una caja sin configurar (archivo, 80 mm, PC850, corte, cajón en el pin 2).
/// </summary>
public sealed record ReceiptPrinterSettings
{
    public Guid? PosTerminalId { get; init; }

    public bool Configured { get; init; }

    /// <summary>FILE, NETWORK, WINDOWS_SPOOLER o SERIAL.</summary>
    public string? Connection { get; init; } = "FILE";

    /// <summary>Carpeta (FILE, opcional), <c>IP[:puerto]</c> (NETWORK), nombre de la impresora (WINDOWS_SPOOLER) o <c>COMx[:baudios]</c> (SERIAL).</summary>
    public string? Address { get; init; }

    public int PaperWidthMm { get; init; } = 80;

    /// <summary>PC850 o ASCII.</summary>
    public string? CodePage { get; init; } = "PC850";

    public bool AutoCut { get; init; } = true;

    public bool DrawerConnected { get; init; } = true;

    /// <summary>PIN2 o PIN5.</summary>
    public string? DrawerPin { get; init; } = "PIN2";
}

public enum PrinterConnectionKind
{
    File,
    Network,
    WindowsSpooler,
    Serial,
}

/// <summary>
/// Impresora validada y lista para usar. <c>Destination</c>: carpeta absoluta (FILE), host (NETWORK), nombre de Windows
/// (WINDOWS_SPOOLER) o puerto <c>COMx</c> (SERIAL); <c>Port</c> es el puerto TCP o la velocidad del puerto serie.
/// </summary>
public sealed record PrinterTarget(
    PrinterConnectionKind Connection, string Destination, int Port, int PaperWidthMm, PrinterOptions Options, bool DrawerConnected)
{
    /// <summary>Descripción legible para el estado, los registros y la página de prueba.</summary>
    public string Description => Connection switch
    {
        PrinterConnectionKind.Network => $"Red {(Destination.Contains(':', StringComparison.Ordinal) ? $"[{Destination}]" : Destination)}:{Port}",
        PrinterConnectionKind.WindowsSpooler => $"Windows \"{Destination}\"",
        PrinterConnectionKind.Serial => $"{Destination} ({Port} baudios)",
        _ => $"Archivo {Destination}",
    };
}

/// <summary>Valida la configuración recibida y la convierte en <see cref="PrinterTarget"/>. Los errores se devuelven en español.</summary>
public static partial class PrinterSettingsResolver
{
    public const int MaxAddressLength = 200;

    private static readonly int[] BaudRates = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

    public static PrinterTarget? TryResolve(ReceiptPrinterSettings? settings, AgentOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = string.Empty;
        if (settings is null)
        {
            error = "Falta la configuración de la impresora (\"printer\").";
            return null;
        }

        var address = string.IsNullOrWhiteSpace(settings.Address) ? null : settings.Address.Trim();
        if (address is { Length: > MaxAddressLength })
        {
            error = $"La dirección de la impresora supera {MaxAddressLength} caracteres.";
            return null;
        }

        if (settings.PaperWidthMm is not (58 or 80))
        {
            error = "El papel debe ser de 58 u 80 mm.";
            return null;
        }

        PrinterCodePage? codePage = Normalize(settings.CodePage) switch
        {
            "PC850" => PrinterCodePage.Pc850,
            "ASCII" => PrinterCodePage.Ascii,
            _ => null,
        };
        DrawerPin? pin = Normalize(settings.DrawerPin) switch
        {
            "PIN2" => Pos.Printing.DrawerPin.Pin2,
            "PIN5" => Pos.Printing.DrawerPin.Pin5,
            _ => null,
        };
        if (codePage is null || pin is null)
        {
            error = "Página de códigos (PC850 o ASCII) o pin del cajón (PIN2 o PIN5) inválidos.";
            return null;
        }

        var printerOptions = new PrinterOptions(TicketLayout.ColumnsFor(settings.PaperWidthMm), codePage.Value, settings.AutoCut, pin.Value);
        PrinterTarget Target(PrinterConnectionKind kind, string destination, int port) =>
            new(kind, destination, port, settings.PaperWidthMm, printerOptions, settings.DrawerConnected);

        switch (Normalize(settings.Connection))
        {
            case "FILE":
                var directory = ResolveFileDirectory(address, options, out error);
                return directory is null ? null : Target(PrinterConnectionKind.File, directory, 0);
            case "NETWORK":
                if (address is null || !TryParseEndpoint(address, out var host, out var port))
                {
                    error = "Impresora de red: la dirección debe ser IP o nombre del equipo, con puerto opcional (192.168.1.50:9100).";
                    return null;
                }

                return Target(PrinterConnectionKind.Network, host, port);
            case "WINDOWS_SPOOLER":
                if (address is null || address.Any(char.IsControl))
                {
                    error = "Impresora de Windows: indique el nombre con que está instalada.";
                    return null;
                }

                return Target(PrinterConnectionKind.WindowsSpooler, address, 0);
            case "SERIAL":
                var match = address is null ? Match.Empty : SerialAddress().Match(address);
                var baud = match.Groups["baud"].Success ? int.Parse(match.Groups["baud"].Value, CultureInfo.InvariantCulture) : options.SerialBaudRate;
                if (!match.Success || !BaudRates.Contains(baud))
                {
                    error = $"Puerto serie: la dirección debe ser COMx con velocidad opcional ({string.Join(", ", BaudRates)}), p. ej. COM3 o COM3:19200.";
                    return null;
                }

                return Target(PrinterConnectionKind.Serial, match.Groups["port"].Value.ToUpperInvariant(), baud);
            default:
                error = "Conexión de la impresora inválida: FILE, NETWORK, WINDOWS_SPOOLER o SERIAL.";
                return null;
        }
    }

    /// <summary><c>host</c>, <c>host:puerto</c>, <c>IPv4:puerto</c> o <c>[IPv6]:puerto</c>; sin puerto se usa 9100.</summary>
    public static bool TryParseEndpoint(string address, out string host, out int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        host = address;
        port = 9100;
        string? portText = null;
        if (address.StartsWith('['))
        {
            var end = address.IndexOf(']', StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }

            host = address[1..end];
            var rest = address[(end + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    return false;
                }

                portText = rest[1..];
            }
        }
        else if (address.Count(c => c == ':') == 1)
        {
            var colon = address.IndexOf(':', StringComparison.Ordinal);
            host = address[..colon];
            portText = address[(colon + 1)..];
        }

        if (portText is not null
            && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            return false;
        }

        return Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    private static string? ResolveFileDirectory(string? address, AgentOptions options, out string error)
    {
        error = string.Empty;
        if (address is null)
        {
            return options.ResolveFileOutputDirectory();
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(address));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Transporte de archivo: la carpeta no es válida.";
            return null;
        }

        var allowed = options.AllowedFileDirectories.Append(options.ResolveFileOutputDirectory())
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d, AppContext.BaseDirectory)));
        if (!allowed.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            error = $"Transporte de archivo: la carpeta {full} no está permitida en la configuración del agente (Agent:AllowedFileDirectories).";
            return null;
        }

        return full;
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().Replace('-', '_').ToUpperInvariant() switch
    {
        "WINDOWSSPOOLER" => "WINDOWS_SPOOLER",
        var v => v,
    };

    [GeneratedRegex(@"^(?<port>COM[1-9][0-9]{0,2})(:(?<baud>[0-9]{1,6}))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SerialAddress();
}
