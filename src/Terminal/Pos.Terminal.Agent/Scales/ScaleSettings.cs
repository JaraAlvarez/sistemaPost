using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pos.Terminal.Agent.Scales;

/// <summary>
/// Báscula de la caja TAL COMO la guarda el servidor (<c>GET /organization/terminals/{id}/scale</c>) o la arma la pantalla
/// "Probar báscula". Lo que venga vacío toma el valor de fábrica del driver (<see cref="ScaleSerialDefaults"/>). Marca, modelo y código
/// de catálogo son informativos: el catálogo de básculas (Fase 7-C parte D) llenará los demás campos al elegir un modelo.
/// Los campos que no conoce el agente (p. ej. "agregar automáticamente") se ignoran.
/// </summary>
public sealed record ScaleSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>TOLEDO_8217, CAS, GENERIC o SIMULATED.</summary>
    public string? Driver { get; init; }

    /// <summary>Puerto <c>COMx</c> (no se usa con el simulador).</summary>
    public string? Port { get; init; }

    public int? BaudRate { get; init; }

    /// <summary>7 u 8.</summary>
    public int? DataBits { get; init; }

    /// <summary>NONE, EVEN u ODD.</summary>
    public string? Parity { get; init; }

    /// <summary>1 o 2.</summary>
    public int? StopBits { get; init; }

    /// <summary>REQUEST (la caja pide el peso) o CONTINUOUS (la báscula lo envía sola).</summary>
    public string? ReadMode { get; init; }

    /// <summary>Comando para pedir el peso; admite <c>\r</c>, <c>\n</c> y <c>\xHH</c> (p. ej. "W" en Toledo 8217).</summary>
    public string? RequestCommand { get; init; }

    /// <summary>Tiempo máximo de espera de una lectura (200 a 5000 ms).</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Decimales implícitos cuando el peso llega sin punto (0 a 4; por defecto 3 = gramos).</summary>
    public int? Decimals { get; init; }

    public string? Brand { get; init; }

    public string? Model { get; init; }

    /// <summary>Código del modelo en el catálogo de básculas compatibles (parte D); vacío = configuración manual.</summary>
    public string? CatalogCode { get; init; }
}

/// <summary>Báscula validada y lista para abrir.</summary>
public sealed record ScaleTarget(
    string DriverCode,
    bool Simulated,
    string Port,
    int BaudRate,
    int DataBits,
    ScaleParity Parity,
    int StopBits,
    ScaleReadMode Mode,
    byte[] RequestBytes,
    int TimeoutMs,
    int ImpliedDecimals,
    string? Brand,
    string? Model)
{
    /// <summary>Descripción legible: "Toledo 8217 · COM3 9600 7E1".</summary>
    public string Description => Simulated
        ? "Báscula simulada (Toledo 8217)"
        : string.Create(CultureInfo.InvariantCulture, $"{ScaleDrivers.Find(DriverCode)?.Name ?? DriverCode} · {Port} {BaudRate} {DataBits}{Parity.ToString()[0]}{StopBits}");
}

/// <summary>Valida la configuración recibida. Los errores se devuelven en español.</summary>
public static partial class ScaleSettingsResolver
{
    public static readonly int[] BaudRates = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

    public static ScaleTarget? TryResolve(ScaleSettings? settings, bool simulatorAllowed, out string error)
    {
        error = string.Empty;
        if (settings is null)
        {
            error = "Falta la configuración de la báscula.";
            return null;
        }

        var code = ScaleDrivers.Normalize(settings.Driver);
        var driver = ScaleDrivers.Find(code);
        if (driver is null)
        {
            error = "Marca o protocolo de báscula inválido: TOLEDO_8217, CAS o GENERIC.";
            return null;
        }

        var simulated = code == ScaleDrivers.SimulatedCode;
        if (simulated && !simulatorAllowed)
        {
            error = "La báscula simulada solo está disponible en el agente de desarrollo.";
            return null;
        }

        var port = (settings.Port ?? string.Empty).Trim().ToUpperInvariant();
        if (!simulated && !SerialPortName().IsMatch(port))
        {
            error = "Indique el puerto de la báscula (COM1 a COM256).";
            return null;
        }

        var defaults = driver.Defaults;
        var baud = settings.BaudRate ?? defaults.BaudRate;
        var dataBits = settings.DataBits ?? defaults.DataBits;
        var stopBits = settings.StopBits ?? defaults.StopBits;
        ScaleParity? parity = string.IsNullOrWhiteSpace(settings.Parity) ? defaults.Parity : settings.Parity.Trim().ToUpperInvariant() switch
        {
            "NONE" or "N" or "NINGUNA" => ScaleParity.None,
            "EVEN" or "E" or "PAR" => ScaleParity.Even,
            "ODD" or "O" or "IMPAR" => ScaleParity.Odd,
            _ => null,
        };
        ScaleReadMode? mode = string.IsNullOrWhiteSpace(settings.ReadMode) ? defaults.Mode : settings.ReadMode.Trim().ToUpperInvariant() switch
        {
            "REQUEST" or "PEDIDO" => ScaleReadMode.Request,
            "CONTINUOUS" or "CONTINUO" => ScaleReadMode.Continuous,
            _ => null,
        };
        var timeout = settings.TimeoutMs ?? 1000;
        var decimals = settings.Decimals ?? 3;
        if (!BaudRates.Contains(baud) || dataBits is not (7 or 8) || stopBits is not (1 or 2) || parity is null || mode is null)
        {
            error = $"Puerto serie inválido: velocidad ({string.Join(", ", BaudRates)}), 7 u 8 bits, paridad NONE/EVEN/ODD, 1 o 2 bits de parada y modo REQUEST o CONTINUOUS.";
            return null;
        }

        if (timeout is < 200 or > 5000 || decimals is < 0 or > 4)
        {
            error = "El tiempo de espera va de 200 a 5000 ms y los decimales de 0 a 4.";
            return null;
        }

        var command = settings.RequestCommand is null ? defaults.RequestCommand ?? string.Empty : settings.RequestCommand;
        var requestBytes = Unescape(command);
        if (requestBytes is null || requestBytes.Length > 16 || (mode == ScaleReadMode.Request && requestBytes.Length == 0))
        {
            error = "Comando de lectura inválido: hasta 16 caracteres (admite \\r, \\n y \\xHH) y obligatorio en modo REQUEST.";
            return null;
        }

        string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 60)];
        return new ScaleTarget(
            simulated ? ScaleDrivers.SimulatedCode : driver.Code, simulated, simulated ? "SIMULADA" : port, baud, dataBits, parity.Value, stopBits, mode.Value,
            requestBytes, timeout, decimals, Text(settings.Brand), Text(settings.Model));
    }

    /// <summary>"W", "\x05", "W\r"… → bytes. <c>null</c> si tiene un escape inválido o caracteres fuera de ASCII.</summary>
    public static byte[]? Unescape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (next is 'r' or 'n' or '\\')
                {
                    bytes.Add(next switch { 'r' => (byte)'\r', 'n' => (byte)'\n', _ => (byte)'\\' });
                    i++;
                    continue;
                }

                if (next == 'x' && i + 3 < text.Length
                    && byte.TryParse(text.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                {
                    bytes.Add(hex);
                    i += 3;
                    continue;
                }

                return null;
            }

            if (c > 0x7F)
            {
                return null;
            }

            bytes.Add((byte)c);
        }

        return [.. bytes];
    }

    /// <summary>Bytes del comando como texto legible (para el estado).</summary>
    public static string Escape(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var text = new StringBuilder();
        foreach (var b in bytes)
        {
            text.Append(b switch
            {
                (byte)'\r' => "\\r",
                (byte)'\n' => "\\n",
                (byte)'\\' => "\\\\",
                < 0x20 or > 0x7E => string.Create(CultureInfo.InvariantCulture, $"\\x{b:X2}"),
                _ => ((char)b).ToString(),
            });
        }

        return text.ToString();
    }

    [GeneratedRegex(@"^COM([1-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-6])$", RegexOptions.CultureInvariant)]
    private static partial Regex SerialPortName();
}
