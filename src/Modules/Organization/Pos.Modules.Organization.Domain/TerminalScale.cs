using System.Text.RegularExpressions;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Organization.Domain;

/// <summary>Configuración de la báscula de una caja recibida por la API (Fase 7-C). Lo vacío toma el valor de fábrica del driver.</summary>
public sealed record ScaleConfiguration(
    bool Enabled,
    string Driver,
    string? Port,
    int? BaudRate,
    int? DataBits,
    string? Parity,
    int? StopBits,
    string? ReadMode,
    string? RequestCommand,
    int? TimeoutMs,
    int? Decimals,
    string? Brand,
    string? Model,
    string? CatalogCode,
    int StableWaitSeconds,
    int EmptyWaitSeconds,
    bool AllowManualWeight,
    int? MinWeightGrams = null);

/// <summary>
/// Báscula conectada a una caja (tabla <c>org.terminal_scales</c>, Fase 7-C parte B). El agente de la caja la recibe de la interfaz de
/// caja (como la impresora) y la guarda en su equipo. Marca, modelo y código de catálogo quedan listos para el catálogo de básculas
/// compatibles (parte D): al elegir un modelo, el catálogo llenará el driver y el puerto serie. Los tiempos de espera y el permiso de
/// digitar el peso son de la caja (⚙️ de la decisión del propietario).
/// </summary>
[Audited("organization")]
public sealed partial class TerminalScale : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    public const string Toledo8217 = "TOLEDO_8217";
    public const string Cas = "CAS";
    public const string Generic = "GENERIC";
    public const string Simulated = "SIMULATED";

    /// <summary>Peso mínimo por defecto (20 g ⚙️).</summary>
    public const int DefaultMinWeightGrams = 20;

    public static readonly string[] Drivers = [Toledo8217, Cas, Generic, Simulated];

    private static readonly int[] BaudRates = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

    private TerminalScale(Guid id, Guid companyId, Guid posTerminalId)
        : base(id)
    {
        CompanyId = companyId;
        PosTerminalId = posTerminalId;
    }

    public Guid CompanyId { get; private set; }

    public Guid PosTerminalId { get; private set; }

    public bool Enabled { get; private set; }

    public string Driver { get; private set; } = Toledo8217;

    public string? Port { get; private set; }

    public int? BaudRate { get; private set; }

    public int? DataBits { get; private set; }

    /// <summary>NONE, EVEN u ODD (vacío = el del driver).</summary>
    public string? Parity { get; private set; }

    public int? StopBits { get; private set; }

    /// <summary>REQUEST o CONTINUOUS (vacío = el del driver).</summary>
    public string? ReadMode { get; private set; }

    public string? RequestCommand { get; private set; }

    public int? TimeoutMs { get; private set; }

    public int? Decimals { get; private set; }

    public string? Brand { get; private set; }

    public string? Model { get; private set; }

    public string? CatalogCode { get; private set; }

    /// <summary>Segundos que la caja espera a que el peso se quede quieto (por defecto 3).</summary>
    public int StableWaitSeconds { get; private set; } = 3;

    /// <summary>Segundos que la caja espera a que pongan el producto en la báscula (por defecto 5).</summary>
    public int EmptyWaitSeconds { get; private set; } = 5;

    /// <summary>Si la báscula no responde, el cajero puede digitar el peso (queda marcado como "peso digitado").</summary>
    public bool AllowManualWeight { get; private set; } = true;

    /// <summary>
    /// Peso mínimo en gramos (banda de cero ⚙️, por defecto 20 g): por debajo la báscula cuenta como vacía y no se cobra (la deriva de
    /// unos gramos no es un producto). El agente lo recibe con la configuración.
    /// </summary>
    public int MinWeightGrams { get; private set; } = DefaultMinWeightGrams;

    public string AuditLabel => $"Báscula de la caja {PosTerminalId}";

    public static TerminalScale Defaults(Guid id, Guid companyId, Guid posTerminalId) => new(id, companyId, posTerminalId);

    public Result Configure(ScaleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var driver = (configuration.Driver ?? string.Empty).Trim().ToUpperInvariant();
        var port = Clean(configuration.Port)?.ToUpperInvariant();
        var parity = Clean(configuration.Parity)?.ToUpperInvariant();
        var mode = Clean(configuration.ReadMode)?.ToUpperInvariant();
        var command = string.IsNullOrEmpty(configuration.RequestCommand) ? null : configuration.RequestCommand;
        var valid = Drivers.Contains(driver)
            && (port is null ? driver == Simulated || !configuration.Enabled : PortPattern().IsMatch(port))
            && (configuration.BaudRate is null || BaudRates.Contains(configuration.BaudRate.Value))
            && configuration.DataBits is null or 7 or 8
            && parity is null or "NONE" or "EVEN" or "ODD"
            && configuration.StopBits is null or 1 or 2
            && mode is null or "REQUEST" or "CONTINUOUS"
            && command is not { Length: > 40 }
            && configuration.TimeoutMs is null or (>= 200 and <= 5000)
            && configuration.Decimals is null or (>= 0 and <= 4)
            && configuration.StableWaitSeconds is >= 1 and <= 15
            && configuration.EmptyWaitSeconds is >= 1 and <= 30
            && configuration.MinWeightGrams is null or (>= 0 and <= 1000)
            && Clean(configuration.Brand) is not { Length: > 60 } && Clean(configuration.Model) is not { Length: > 60 }
            && Clean(configuration.CatalogCode) is not { Length: > 40 };
        if (!valid)
        {
            return Error.Validation(
                "ORGANIZATION.INVALID_SCALE",
                "Báscula inválida: marca o protocolo (Toledo 8217, CAS o genérica), puerto COMx, velocidad, 7 u 8 bits, paridad NONE/EVEN/ODD, " +
                "1 o 2 bits de parada, espera del peso estable de 1 a 15 s y del producto de 1 a 30 s, peso mínimo de 0 a 1000 g.");
        }

        Enabled = configuration.Enabled;
        Driver = driver;
        Port = port;
        BaudRate = configuration.BaudRate;
        DataBits = configuration.DataBits;
        Parity = parity;
        StopBits = configuration.StopBits;
        ReadMode = mode;
        RequestCommand = command;
        TimeoutMs = configuration.TimeoutMs;
        Decimals = configuration.Decimals;
        Brand = Clean(configuration.Brand);
        Model = Clean(configuration.Model);
        CatalogCode = Clean(configuration.CatalogCode);
        StableWaitSeconds = configuration.StableWaitSeconds;
        EmptyWaitSeconds = configuration.EmptyWaitSeconds;
        AllowManualWeight = configuration.AllowManualWeight;
        MinWeightGrams = configuration.MinWeightGrams ?? DefaultMinWeightGrams;
        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^COM([1-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-6])$", RegexOptions.CultureInvariant)]
    private static partial Regex PortPattern();
}
