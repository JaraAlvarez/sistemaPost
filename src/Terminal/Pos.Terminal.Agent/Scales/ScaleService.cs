using System.Globalization;
using System.IO.Ports;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pos.Terminal.Agent.Api;

namespace Pos.Terminal.Agent.Scales;

/// <summary>
/// Peso tal como lo ve la caja. <c>Status</c>: OK (estable y con al menos el peso mínimo), UNSTABLE, ZERO (vacía: por debajo del peso
/// mínimo ⚙️), NEGATIVE, OVERLOAD o ERROR (incluye un peso fuera de rango).
/// <para>Fórmula de la tara (Fase 7-C, una sola regla): <c>GrossKg</c> es lo que marca la báscula; <c>TareKg</c> es la tara manual
/// del agente (tecla Ins), que solo vale para la siguiente pesada (la caja la quita al agregar la línea y se quita sola cuando la báscula
/// vuelve a quedar vacía); <c>WeightKg = GrossKg − TareKg</c>. <c>ScaleNet</c> = la propia báscula ya descontó una tara (trama neta).
/// La tara del producto (bolsa o bandeja configurada en el catálogo) la descuenta UNA vez el servidor, y solo si no hubo otra tara
/// (ni manual ni de la báscula): <c>neto = WeightKg − (TareKg = 0 y no ScaleNet ? taraProducto : 0)</c>.</para>
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
    DateTimeOffset At,
    bool ScaleNet = false)
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
    public const string TestNotStarted = "SCALE.TEST_NOT_STARTED";

    public static ScaleOutcome Of(ScaleWeightDto weight) => new(weight, null, null);

    public static ScaleOutcome Fail(string code, string message) => new(null, code, message);
}

/// <summary>
/// La báscula de esta caja (Fase 7-C parte B): una por equipo. Guarda la configuración en
/// <c>C:\ProgramData\{Producto}\agent\scale.json</c> (sobrevive a reinicios y actualizaciones), mantiene el puerto abierto entre lecturas,
/// lee una trama a la vez (Toledo 8217 pide al menos 200 ms entre comandos) y aplica la tara manual por software.
/// <para>"Probar báscula" usa una sesión de prueba aparte (<see cref="TestStartAsync"/>): su propia configuración, conexión y tara, sin
/// tocar la báscula en uso. Si la prueba y la báscula en uso comparten el puerto serie, cada lectura cierra el puerto de la otra (un
/// puerto COM no se puede abrir dos veces) y lo vuelve a abrir al leer. La prueba se cierra sola tras
/// <see cref="TestSessionLifetime"/> sin lecturas.</para>
/// </summary>
public sealed class ScaleService : IDisposable
{
    /// <summary>Formato genérico: el peso cuenta como estable si no cambia durante este tiempo.</summary>
    public static readonly TimeSpan SoftwareStableWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>Una prueba sin lecturas durante este tiempo se cierra sola.</summary>
    public static readonly TimeSpan TestSessionLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Peso máximo que se acepta de una trama (más es un error de decimales o de protocolo, nunca un peso real).</summary>
    public const decimal MaxReadableKg = 99_999m;

    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(200);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IScaleConnectionFactory _connections;
    private readonly TimeProvider _time;
    private readonly ILogger<ScaleService> _logger;
    private readonly string _file;
    private ScaleSettings? _settings;
    private ScaleChannel? _live;
    private ScaleChannel? _test;

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

    /// <summary>Hay una prueba ("Probar báscula") abierta.</summary>
    public bool Testing => _test is not null;

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

        var live = _live;
        return new ScaleStatusDto(
            live is not null, live?.Target.DriverCode, live?.Target.Description, live?.Target.Simulated ?? false, SimulatorAvailable, live?.TareKg ?? 0m, LastReading,
            LastError, _settings, DriverInfos, ports);
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
            // La tara manual nunca pasa de una configuración a otra (solo vale para la siguiente pesada). Si el puerto es el mismo se
            // conserva la conexión abierta.
            var keep = _live is { } old && target is not null && SameConnection(old.Target, target) ? old.Connection : null;
            if (keep is null)
            {
                _live?.Close();
            }

            _live = target is null ? null : new ScaleChannel(target) { Connection = keep };
            _settings = settings;
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

    /// <summary>Una lectura de la báscula en uso (con la tara manual aplicada).</summary>
    public async Task<ScaleOutcome> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_live is not { } live)
            {
                return ScaleOutcome.Fail(ScaleOutcome.NotConfigured, "Esta caja no tiene una báscula configurada.");
            }

            ExpireTest();
            return await ReadLockedAsync(live, other: _test, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tara manual por software: toma el peso bruto estable actual (bolsa, bandeja) y lo descuenta de la SIGUIENTE pesada. Con
    /// <paramref name="clear"/>, o si la báscula está vacía, se quita la tara. También se quita sola cuando la báscula vuelve a quedar
    /// vacía, y la caja la quita al agregar la línea pesada.
    /// </summary>
    public async Task<ScaleOutcome> TareAsync(bool clear, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_live is not { } live)
            {
                return ScaleOutcome.Fail(ScaleOutcome.NotConfigured, "Esta caja no tiene una báscula configurada.");
            }

            ExpireTest();
            return await TareLockedAsync(live, _test, clear, live: true, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// "Probar báscula": abre una prueba con <paramref name="settings"/> (sin guardarla ni cambiar la báscula en uso) y lee una vez.
    /// Devuelve el error de configuración, o la lectura.
    /// </summary>
    public async Task<(string? Error, ScaleOutcome? Outcome)> TestStartAsync(ScaleSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var target = ScaleSettingsResolver.TryResolve(settings with { Enabled = true }, SimulatorAvailable, out var error);
        if (target is null)
        {
            return (error, null);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _test?.Close();
            var test = _test = new ScaleChannel(target) { LastUsed = _time.GetUtcNow() };
            return (null, await ReadLockedAsync(test, other: _live, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Otra lectura de la prueba abierta (peso en vivo de "Probar báscula").</summary>
    public async Task<ScaleOutcome> TestReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ExpireTest();
            if (_test is not { } test)
            {
                return TestMissing();
            }

            test.LastUsed = _time.GetUtcNow();
            return await ReadLockedAsync(test, other: _live, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tara de la prueba (no toca la tara de la báscula en uso).</summary>
    public async Task<ScaleOutcome> TestTareAsync(bool clear, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ExpireTest();
            if (_test is not { } test)
            {
                return TestMissing();
            }

            test.LastUsed = _time.GetUtcNow();
            return await TareLockedAsync(test, _live, clear, live: false, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Cierra la prueba: la báscula en uso sigue igual (vuelve a abrir su puerto en la siguiente lectura).</summary>
    public async Task TestStopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _test?.Close();
            _test = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _live?.Close();
        _test?.Close();
        _gate.Dispose();
    }

    /// <summary>Báscula vacía: por debajo del peso mínimo (banda de cero ⚙️). Con banda 0, solo el cero exacto.</summary>
    public static bool IsEmpty(decimal kg, decimal zeroBandKg) => zeroBandKg > 0m ? Math.Abs(kg) < zeroBandKg : kg == 0m;

    /// <summary>Convierte una lectura del driver en lo que ve la caja (tara, banda de cero, estado y mensaje en español).</summary>
    public static ScaleWeightDto Build(ScaleReading reading, bool stable, decimal tareKg, decimal zeroBandKg, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var raw = reading.WeightKg;
        if (raw is { } big && Math.Abs(big) > MaxReadableKg)
        {
            // Un número absurdo (decimales mal configurados, basura con muchos dígitos): es un error de la trama, nunca un peso.
            return new ScaleWeightDto(
                ScaleWeightDto.Error, "La báscula envió un peso fuera de rango: revise el protocolo y los decimales configurados.", null, null, tareKg, false, false,
                false, reading.Overload, null, Display(null), at, reading.Net);
        }

        var gross = raw is { } g ? decimal.Round(g, 3) : (decimal?)null;
        var net = gross is { } value ? decimal.Round(value - tareKg, 3) : (decimal?)null;
        var empty = net is { } n && IsEmpty(n, zeroBandKg);
        var negative = net is { } m ? m < 0m && !empty : reading.UnderZero;
        var zero = !negative && (empty || (net is null && reading.CenterOfZero && stable && !reading.Overload));
        var (status, message) = reading switch
        {
            { Problem: { } problem } => (ScaleWeightDto.Error, problem),
            { Overload: true } => (ScaleWeightDto.OverloadWeight, "Sobrepeso: el producto supera la capacidad de la báscula."),
            _ when !stable => (ScaleWeightDto.Unstable, "El peso se está moviendo: espere a que quede quieto."),
            _ when negative => (ScaleWeightDto.NegativeWeight, "Peso negativo: retire lo que haya en la báscula o quite la tara."),
            _ when zero => (ScaleWeightDto.ZeroWeight, "La báscula está vacía: ponga el producto."),
            _ when net is null => (ScaleWeightDto.Unstable, "La báscula no envió el peso: espere un momento."),
            _ => (ScaleWeightDto.Ok, "Peso estable."),
        };
        var grams = net is { } k ? (int)decimal.Round(k * 1000m, 0, MidpointRounding.AwayFromZero) : (int?)null;
        return new ScaleWeightDto(
            status, message, net, gross, tareKg, stable && status != ScaleWeightDto.Error, zero, negative, reading.Overload, grams, Display(net), at, reading.Net);
    }

    /// <summary>"0,700 kg" (o "--" sin peso).</summary>
    public static string Display(decimal? kg) =>
        kg is { } value ? value.ToString("0.000", CultureInfo.GetCultureInfo("es-CO")) + " kg" : "--";

    private static bool SameConnection(ScaleTarget a, ScaleTarget b) =>
        a.Simulated == b.Simulated && a.Port == b.Port && a.BaudRate == b.BaudRate && a.DataBits == b.DataBits && a.Parity == b.Parity && a.StopBits == b.StopBits;

    private static bool SamePort(ScaleTarget a, ScaleTarget b) => !a.Simulated && !b.Simulated && a.Port == b.Port;

    private static ScaleOutcome TestMissing() =>
        ScaleOutcome.Fail(ScaleOutcome.TestNotStarted, "La prueba de la báscula terminó: pulse «Probar báscula» otra vez.");

    private void ExpireTest()
    {
        if (_test is { } test && _time.GetUtcNow() - test.LastUsed > TestSessionLifetime)
        {
            test.Close();
            _test = null;
        }
    }

    private async Task<ScaleOutcome> TareLockedAsync(ScaleChannel channel, ScaleChannel? other, bool clear, bool live, CancellationToken cancellationToken)
    {
        if (clear)
        {
            channel.TareKg = 0m;
            return await ReadLockedAsync(channel, other, cancellationToken);
        }

        var current = await ReadLockedAsync(channel, other, cancellationToken);
        if (current.Weight is not { } weight)
        {
            return current;
        }

        if (!weight.Stable || weight.GrossKg is not { } gross || weight.Overload)
        {
            return ScaleOutcome.Fail(ScaleOutcome.TareUnstable, "Para tomar la tara el peso debe estar quieto: espere un momento y repita.");
        }

        channel.TareKg = gross > 0m && !IsEmpty(gross, channel.Target.ZeroBandKg) ? gross : 0m;
        var reading = Build(new ScaleReading(gross, Stable: true), stable: true, channel.TareKg, channel.Target.ZeroBandKg, _time.GetUtcNow());
        channel.LastReading = reading;
        if (live)
        {
            LastReading = reading;
        }

        return ScaleOutcome.Of(reading);
    }

    private async Task<ScaleOutcome> ReadLockedAsync(ScaleChannel channel, ScaleChannel? other, CancellationToken cancellationToken)
    {
        var target = channel.Target;
        var isLive = ReferenceEquals(channel, _live);
        if (ScaleDrivers.Find(target.DriverCode) is not { } driver)
        {
            return ScaleOutcome.Fail(ScaleOutcome.NotConfigured, "Esta caja no tiene una báscula configurada.");
        }

        // Un puerto COM no se abre dos veces: si la otra sesión (prueba o báscula en uso) lo tiene abierto, se cierra; se reabre al leer.
        if (other is not null && other.Connection is not null && SamePort(other.Target, target))
        {
            other.Close();
        }

        try
        {
            var reading = await Task.Run(
                () =>
                {
                    if (target.Mode == ScaleReadMode.Request)
                    {
                        var wait = MinRequestInterval - (_time.GetUtcNow() - channel.LastRequest);
                        if (wait > TimeSpan.Zero)
                        {
                            Thread.Sleep(wait);
                        }
                    }

                    channel.Connection ??= _connections.Open(target);
                    channel.LastRequest = _time.GetUtcNow();
                    return ScaleProtocolReader.Read(channel.Connection, driver, target);
                },
                cancellationToken);

            var now = _time.GetUtcNow();
            var stable = reading.Stable && reading.Problem is null;
            if (!driver.ReportsStability && reading.WeightKg is { } gross)
            {
                if (channel.LastGross != gross)
                {
                    channel.LastGross = gross;
                    channel.SameSince = now;
                }

                stable = stable && now - channel.SameSince >= SoftwareStableWindow;
            }

            // La tara manual solo vale para la pesada en curso: con la báscula vacía (y quieta) se quita sola.
            if (channel.TareKg != 0m && stable && reading.WeightKg is { } empty && Math.Abs(empty) <= MaxReadableKg && IsEmpty(empty, target.ZeroBandKg))
            {
                channel.TareKg = 0m;
            }

            var dto = Build(reading, stable, channel.TareKg, target.ZeroBandKg, now);
            channel.LastReading = dto;
            if (isLive)
            {
                LastError = null;
                LastReading = dto;
            }

            return ScaleOutcome.Of(dto);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or ArgumentException
                                       or OverflowException)
        {
            if (ex is not TimeoutException)
            {
                channel.Close();
            }

            var message = ex switch
            {
                UnauthorizedAccessException => $"El puerto {target.Port} está ocupado por otro programa o no se puede abrir.",
                TimeoutException => ex.Message,
                OverflowException => "La báscula envió un peso fuera de rango: revise el protocolo y los decimales configurados.",
                _ => $"No se pudo leer la báscula ({target.Port}): {ex.Message}",
            };
            AgentLog.ScaleReadFailed(_logger, ex, target.Description);
            if (isLive)
            {
                LastError = message;
            }

            return ScaleOutcome.Fail(ScaleOutcome.Unavailable, message);
        }
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
                _live = new ScaleChannel(target);
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

    /// <summary>Una báscula abierta (la de la caja o la de la prueba): su conexión, su tara manual y su estabilidad por software.</summary>
    private sealed class ScaleChannel(ScaleTarget target)
    {
        public ScaleTarget Target { get; } = target;

        public IScaleConnection? Connection { get; set; }

        public decimal TareKg { get; set; }

        public decimal? LastGross { get; set; }

        public DateTimeOffset SameSince { get; set; }

        public DateTimeOffset LastRequest { get; set; }

        public DateTimeOffset LastUsed { get; set; }

        public ScaleWeightDto? LastReading { get; set; }

        public void Close()
        {
            Connection?.Dispose();
            Connection = null;
        }
    }
}
