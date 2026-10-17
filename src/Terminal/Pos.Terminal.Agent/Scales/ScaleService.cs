using System.Globalization;
using System.IO.Ports;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pos.Terminal.Agent.Api;

namespace Pos.Terminal.Agent.Scales;

/// <summary>
/// Peso tal como lo ve la caja. <c>Status</c>: OK (estable y mayor que cero), UNSTABLE, ZERO, NEGATIVE, OVERLOAD o ERROR. El peso
/// siempre en kilogramos (3 decimales) ya descontada la tara del agente; <c>Grams</c> y <c>Display</c> para mostrarlo.
/// </summary>
public sealed record ScaleWeightDto(
    string Status,
    string Message,
    decimal? WeightKg,
    decimal? GrossKg,
    decimal TareKg,
    bool Stable,
    bool Zero,
    bool Negative,
    bool Overload,
    int? Grams,
    string Display,
    DateTimeOffset At)
{
    public const string Ok = "OK";
    public const string Unstable = "UNSTABLE";
    public const string ZeroWeight = "ZERO";
    public const string NegativeWeight = "NEGATIVE";
    public const string OverloadWeight = "OVERLOAD";
    public const string Error = "ERROR";
}

/// <summary>Valores de fábrica de un driver (para que la pantalla de configuración los proponga).</summary>
public sealed record ScaleDriverInfo(string Code, string Name, int BaudRate, int DataBits, string Parity, int StopBits, string ReadMode, string? RequestCommand);

public sealed record ScaleStatusDto(
    bool Configured,
    string? Driver,
    string? Description,
    bool Simulated,
    bool SimulatorAvailable,
    decimal TareKg,
    ScaleWeightDto? LastReading,
    string? LastError,
    ScaleSettings? Settings,
    IReadOnlyList<ScaleDriverInfo> Drivers,
    IReadOnlyList<string> SerialPorts);

/// <summary>Resultado de una operación con la báscula: el peso o un error con su código (como los ProblemDetails del agente).</summary>
public sealed record ScaleOutcome(ScaleWeightDto? Weight, string? ErrorCode, string? Error)
{
    public const string NotConfigured = "SCALE.NOT_CONFIGURED";
    public const string Unavailable = "SCALE.UNAVAILABLE";
    public const string TareUnstable = "SCALE.TARE_UNSTABLE";

    public static ScaleOutcome Of(ScaleWeightDto weight) => new(weight, null, null);

    public static ScaleOutcome Fail(string code, string message) => new(null, code, message);
}

/// <summary>
/// La báscula de esta caja (Fase 7-C parte B): una por equipo. Guarda la configuración en
/// <c>C:\ProgramData\{Producto}\agent\scale.json</c> (sobrevive a reinicios y actualizaciones), mantiene el puerto abierto entre lecturas,
/// lee una trama a la vez (Toledo 8217 pide al menos 200 ms entre comandos) y aplica la tara por software.
/// </summary>
public sealed class ScaleService : IDisposable
{
    /// <summary>Formato genérico: el peso cuenta como estable si no cambia durante este tiempo.</summary>
    public static readonly TimeSpan SoftwareStableWindow = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(200);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IScaleConnectionFactory _connections;
    private readonly TimeProvider _time;
    private readonly ILogger<ScaleService> _logger;
    private readonly string _file;
    private IScaleConnection? _connection;
    private ScaleSettings? _settings;
    private ScaleTarget? _target;
    private decimal _tareKg;
    private decimal? _lastGross;
    private DateTimeOffset _sameSince;
    private DateTimeOffset _lastRequest;

    public ScaleService(IScaleConnectionFactory connections, TimeProvider time, IOptions<AgentOptions> options, IHostEnvironment environment, ILogger<ScaleService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        _connections = connections;
        _time = time;
        _logger = logger;
        _file = options.Value.ResolveScaleConfigFile();
        SimulatorAvailable = options.Value.EnableScaleSimulator || environment.IsDevelopment();
        Load();
    }

    public bool SimulatorAvailable { get; }

    public ScaleWeightDto? LastReading { get; private set; }

    public string? LastError { get; private set; }

    public static IReadOnlyList<ScaleDriverInfo> DriverInfos { get; } = [.. ScaleDrivers.All.Select(d => new ScaleDriverInfo(
        d.Code, d.Name, d.Defaults.BaudRate, d.Defaults.DataBits, d.Defaults.Parity.ToString().ToUpperInvariant(), d.Defaults.StopBits,
        d.Defaults.Mode.ToString().ToUpperInvariant(), d.Defaults.RequestCommand))];

    public ScaleStatusDto Status()
    {
        string[] ports;
        try
        {
            ports = [.. SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            ports = [];
        }

        var target = _target;
        return new ScaleStatusDto(
            target is not null, target?.DriverCode, target?.Description, target?.Simulated ?? false, SimulatorAvailable, _tareKg, LastReading, LastError, _settings,
            DriverInfos, ports);
    }

    /// <summary>Aplica una configuración (vacía o deshabilitada = sin báscula). Con <paramref name="persist"/> la guarda en disco.</summary>
    public async Task<string?> ConfigureAsync(ScaleSettings settings, bool persist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ScaleTarget? target = null;
        if (settings.Enabled)
        {
            target = ScaleSettingsResolver.TryResolve(settings, SimulatorAvailable, out var error);
            if (target is null)
            {
                return error;
            }
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_target is null || target is null || !SameConnection(_target, target))
            {
                CloseConnection();
                _tareKg = 0m;
                _lastGross = null;
            }

            _settings = settings;
            _target = target;
            LastError = null;
            LastReading = null;
            if (persist)
            {
                try
                {
                    Save(settings);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AgentLog.ScaleConfigUnreadable(_logger, ex, _file);
                    return $"La báscula quedó activa, pero no se pudo guardar su configuración en {_file}: {ex.Message}";
                }
            }

            AgentLog.ScaleConfigured(_logger, target?.Description ?? "sin báscula");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Una lectura del peso (con la tara aplicada).</summary>
    public async Task<ScaleOutcome> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tara por software: toma el peso bruto estable actual (bolsa, bandeja) y lo descuenta de las lecturas siguientes. Con
    /// <paramref name="clear"/>, o si la báscula está vacía, se quita la tara.
    /// </summary>
    public async Task<ScaleOutcome> TareAsync(bool clear, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (clear)
            {
                _tareKg = 0m;
                return await ReadLockedAsync(cancellationToken);
            }

            var current = await ReadLockedAsync(cancellationToken);
            if (current.Weight is not { } weight)
            {
                return current;
            }

            if (!weight.Stable || weight.GrossKg is not { } gross || weight.Overload)
            {
                return ScaleOutcome.Fail(ScaleOutcome.TareUnstable, "Para tomar la tara el peso debe estar quieto: espere un momento y repita.");
            }

            _tareKg = gross > 0m ? gross : 0m;
            return ScaleOutcome.Of(LastReading = Build(new ScaleReading(gross, Stable: true), stable: true));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        CloseConnection();
        _gate.Dispose();
    }

    /// <summary>Convierte una lectura del driver en lo que ve la caja (tara, estado y mensaje en español).</summary>
    public ScaleWeightDto Build(ScaleReading reading, bool stable)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var gross = reading.WeightKg is { } g ? decimal.Round(g, 3) : (decimal?)null;
        var net = gross is { } value ? decimal.Round(value - _tareKg, 3) : (decimal?)null;
        var negative = reading.UnderZero || net < 0m;
        var zero = !negative && (net == 0m || (net is null && reading.CenterOfZero && stable && !reading.Overload));
        var (status, message) = reading switch
        {
            { Problem: { } problem } => (ScaleWeightDto.Error, problem),
            { Overload: true } => (ScaleWeightDto.OverloadWeight, "Sobrepeso: el producto supera la capacidad de la báscula."),
            _ when !stable => (ScaleWeightDto.Unstable, "El peso se está moviendo: espere a que quede quieto."),
            _ when negative => (ScaleWeightDto.NegativeWeight, "Peso negativo: retire lo que haya en la báscula o quite la tara."),
            _ when zero => (ScaleWeightDto.ZeroWeight, "La báscula está en cero: ponga el producto."),
            _ when net is null => (ScaleWeightDto.Unstable, "La báscula no envió el peso: espere un momento."),
            _ => (ScaleWeightDto.Ok, "Peso estable."),
        };
        var grams = net is { } n ? (int)decimal.Round(n * 1000m, 0, MidpointRounding.AwayFromZero) : (int?)null;
        return new ScaleWeightDto(
            status, message, net, gross, _tareKg, stable && status != ScaleWeightDto.Error, zero, negative, reading.Overload, grams, Display(net), _time.GetUtcNow());
    }

    /// <summary>"0,700 kg" (o "--" sin peso).</summary>
    public static string Display(decimal? kg) =>
        kg is { } value ? value.ToString("0.000", CultureInfo.GetCultureInfo("es-CO")) + " kg" : "--";

    private static bool SameConnection(ScaleTarget a, ScaleTarget b) =>
        a.Simulated == b.Simulated && a.Port == b.Port && a.BaudRate == b.BaudRate && a.DataBits == b.DataBits && a.Parity == b.Parity && a.StopBits == b.StopBits;

    private async Task<ScaleOutcome> ReadLockedAsync(CancellationToken cancellationToken)
    {
        if (_target is not { } target || ScaleDrivers.Find(target.DriverCode) is not { } driver)
        {
            return ScaleOutcome.Fail(ScaleOutcome.NotConfigured, "Esta caja no tiene una báscula configurada.");
        }

        try
        {
            var reading = await Task.Run(
                () =>
                {
                    if (target.Mode == ScaleReadMode.Request)
                    {
                        var wait = MinRequestInterval - (_time.GetUtcNow() - _lastRequest);
                        if (wait > TimeSpan.Zero)
                        {
                            Thread.Sleep(wait);
                        }
                    }

                    _connection ??= _connections.Open(target);
                    _lastRequest = _time.GetUtcNow();
                    return ScaleProtocolReader.Read(_connection, driver, target);
                },
                cancellationToken);

            var stable = reading.Stable && reading.Problem is null;
            if (!driver.ReportsStability && reading.WeightKg is { } gross)
            {
                var now = _time.GetUtcNow();
                if (_lastGross != gross)
                {
                    _lastGross = gross;
                    _sameSince = now;
                }

                stable = stable && now - _sameSince >= SoftwareStableWindow;
            }

            LastError = null;
            return ScaleOutcome.Of(LastReading = Build(reading, stable));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            if (ex is not TimeoutException)
            {
                CloseConnection();
            }

            var message = ex switch
            {
                UnauthorizedAccessException => $"El puerto {target.Port} está ocupado por otro programa o no se puede abrir.",
                TimeoutException => ex.Message,
                _ => $"No se pudo leer la báscula ({target.Port}): {ex.Message}",
            };
            AgentLog.ScaleReadFailed(_logger, ex, target.Description);
            LastError = message;
            return ScaleOutcome.Fail(ScaleOutcome.Unavailable, message);
        }
    }

    private void CloseConnection()
    {
        _connection?.Dispose();
        _connection = null;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file))
            {
                return;
            }

            var settings = JsonSerializer.Deserialize<ScaleSettings>(File.ReadAllText(_file), AgentEndpoints.Json);
            if (settings is { Enabled: true } && ScaleSettingsResolver.TryResolve(settings, SimulatorAvailable, out _) is { } target)
            {
                _settings = settings;
                _target = target;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AgentLog.ScaleConfigUnreadable(_logger, ex, _file);
        }
    }

    private void Save(ScaleSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, AgentEndpoints.Json));
        File.Move(temporary, _file, overwrite: true);
    }
}
