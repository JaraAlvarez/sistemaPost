using System.Globalization;
using System.IO.Ports;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pos.SharedKernel.Measurement;
using Pos.Terminal.Agent.Api;

namespace Pos.Terminal.Agent.Scales;

/// <summary>
/// Peso tal como lo ve la caja. <c>Status</c>: OK (estable y por encima del peso mínimo), UNSTABLE, ZERO (vacía: dentro de la banda de
/// cero ⚙️), NEGATIVE, OVERLOAD o ERROR (incluye un peso fuera de rango).
/// <para>Fórmula de la tara (Fase 7-C, una sola regla): <c>GrossKg</c> es lo que marca la báscula; <c>TareKg</c> es la tara manual
/// del agente (tecla Ins), que solo vale para la siguiente pesada: la caja la quita al agregar la línea o al cancelar con Esc (D2);
/// <c>WeightKg = GrossKg − TareKg</c>. <c>ScaleNet</c> = la propia báscula ya descontó una tara (trama neta). La tara del producto
/// (bolsa o bandeja configurada en el catálogo) la descuenta UNA vez el servidor, y solo si no hubo otra tara (ni manual ni de la
/// báscula): <c>neto = WeightKg − (TareKg = 0 y no ScaleNet ? taraProducto : 0)</c>.</para>
/// <para>Retorno a cero (D1): <c>EmptiedSinceLastAccepted</c> dice si el agente vio la báscula vacía después de la última pesada que la
/// caja agregó; <c>SignedReading</c> es la lectura firmada que la caja reenvía al servidor (solo la báscula en uso y con la caja vinculada).</para>
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
    bool ScaleNet = false,
    Guid? ReadingId = null,
    bool EmptiedSinceLastAccepted = true,
    SignedScaleReading? SignedReading = null,
    string? SigningProblem = null)
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
    IReadOnlyList<string> SerialPorts,
    Guid? PairedTerminalId = null,
    bool Testing = false,
    string? SigningProblem = null,
    Guid? KeyId = null,
    Guid? PendingKeyId = null);

/// <summary><c>POST /scale/accepted</c>: la caja agregó la línea con esta lectura (o, con <c>Release</c>, eliminó la última línea pesada).</summary>
public sealed record ScaleAcceptedRequest(Guid? ReadingId = null, bool Release = false);

/// <summary>Resultado de una operación con la báscula: el peso o un error con su código (como los ProblemDetails del agente).</summary>
public sealed record ScaleOutcome(ScaleWeightDto? Weight, string? ErrorCode, string? Error)
{
    public const string NotConfigured = "SCALE.NOT_CONFIGURED";
    public const string Unavailable = "SCALE.UNAVAILABLE";
    public const string TareUnstable = "SCALE.TARE_UNSTABLE";
    public const string TestNotStarted = "SCALE.TEST_NOT_STARTED";
    public const string InTest = "SCALE.IN_TEST";

    public static ScaleOutcome Of(ScaleWeightDto weight) => new(weight, null, null);

    public static ScaleOutcome Fail(string code, string message) => new(null, code, message);
}

/// <summary>
/// La báscula de esta caja (Fase 7-C parte B): una por equipo. Guarda la configuración en
/// <c>C:\ProgramData\{Producto}\agent\scale.json</c> (sobrevive a reinicios y actualizaciones), mantiene el puerto abierto entre lecturas,
/// lee una trama a la vez (Toledo 8217 pide al menos 200 ms entre comandos) y aplica la tara manual por software.
/// <para>"Probar báscula" usa una sesión de prueba aparte (<see cref="TestStartAsync"/>): su propia configuración y tara, sin tocar la
/// báscula en uso. Si la prueba usa el mismo puerto COM con los mismos parámetros, lee por la conexión de la báscula en uso (no se
/// cierra ni se reabre el puerto); con otros parámetros la prueba toma el puerto y la venta espera a que termine. La prueba se cierra
/// sola tras <see cref="AgentOptions.ScaleTestIdleTimeout"/> sin lecturas (con un temporizador).</para>
/// <para>Retorno a cero (D1): el agente anota la última vez que vio la báscula vacía; después de una pesada aceptada
/// (<see cref="MarkAccepted"/>) un vigilante lee la báscula hasta verla vacía (<see cref="ScaleEmptyMonitor"/>). Vacía = el BRUTO dentro de
/// la banda de cero: con tara manual o de la báscula el neto en cero no cuenta (BB1).</para>
/// </summary>
public sealed class ScaleService : IDisposable
{
    /// <summary>Formato genérico: el peso cuenta como estable si no cambia durante este tiempo.</summary>
    public static readonly TimeSpan SoftwareStableWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>Peso máximo que se acepta de una trama (más es un error de decimales o de protocolo, nunca un peso real).</summary>
    public const decimal MaxReadableKg = 99_999m;

    private const int RecentReadings = 64;

    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(200);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IScaleConnectionFactory _connections;
    private readonly ScaleSigner _signer;
    private readonly TimeProvider _time;
    private readonly ILogger<ScaleService> _logger;
    private readonly string _file;
    private readonly TimeSpan _testIdle;
    private readonly Queue<(Guid Id, long Seq)> _recent = new();
    private readonly System.Diagnostics.Stopwatch _uptime = System.Diagnostics.Stopwatch.StartNew();
    private ScaleSettings? _settings;
    private ScaleChannel? _live;
    private ScaleChannel? _test;
    private ITimer? _testTimer;
    private long _seq;
    private long? _lastEmptySeq;
    private long? _acceptedSeq;
    private TimeSpan _acceptedTick;
    private long? _lastOkSeq;
    private TimeSpan _lastOkTick;

    public ScaleService(
        IScaleConnectionFactory connections, ScaleSigner signer, TimeProvider time, IOptions<AgentOptions> options, IHostEnvironment environment,
        ILogger<ScaleService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        _connections = connections;
        _signer = signer;
        _time = time;
        _logger = logger;
        _file = options.Value.ResolveScaleConfigFile();
        _testIdle = options.Value.ScaleTestIdleTimeout;
        // El simulador da pesos que el agente firma como si vinieran de una báscula real: solo existe en compilación Debug (como las
        // puertas de desarrollo de la licencia, R1). En Release ni la opción ni el entorno (que cambia cualquier administrador) lo activan.
#if DEBUG
        SimulatorAvailable = options.Value.EnableScaleSimulator || environment.IsDevelopment();
#else
        SimulatorAvailable = false;
#endif
        Load();
    }

    public bool SimulatorAvailable { get; }

    public ScaleWeightDto? LastReading { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Hay una prueba ("Probar báscula") abierta.</summary>
    public bool Testing => _test is not null;

    /// <summary>Arranque del agente: el orden de las lecturas firmadas usa este identificador y la secuencia, nunca la hora (BB4).</summary>
    public Guid BootId { get; } = Guid.NewGuid();

    /// <summary>Tiempo máximo que el vigilante sigue leyendo después de una pesada aceptada (⚙️ 10 min, BB8).</summary>
    public static readonly TimeSpan MonitorAfterAccepted = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Después de cualquier lectura con peso (aun antes de que la caja confirme la línea) el vigilante sigue leyendo un rato: si el cajero
    /// cambia el producto A por B antes de la siguiente lectura de la pantalla, el paso por la báscula vacía igual queda anotado (BB8).
    /// </summary>
    public static readonly TimeSpan MonitorAfterReading = TimeSpan.FromSeconds(15);

    /// <summary>El vigilante debe leer: hay una pesada (aceptada o recién leída) y el agente todavía no ha visto la báscula vacía después.</summary>
    public bool AwaitingEmpty
    {
        get
        {
            if (_live is null)
            {
                return false;
            }

            var now = _uptime.Elapsed;
            var afterAccepted = _acceptedSeq is { } accepted && !EmptiedAfter(accepted) && now - _acceptedTick < MonitorAfterAccepted;
            var afterReading = _lastOkSeq is { } ok && !EmptiedAfter(ok) && now - _lastOkTick < MonitorAfterReading;
            return afterAccepted || afterReading;
        }
    }

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
            LastError, _settings, DriverInfos, ports, _signer.TerminalId, Testing, _signer.SigningProblem, _signer.KeyId, _signer.PendingKeyId);
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
            // La tara manual nunca pasa de una configuración a otra. Si el puerto es el mismo se conserva la conexión abierta.
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

    /// <summary>Una lectura de la báscula en uso (con la tara manual aplicada), firmada si la caja está vinculada.</summary>
    public async Task<ScaleOutcome> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_live is not { } live)
            {
                return ScaleOutcome.Fail(ScaleOutcome.NotConfigured, "Esta caja no tiene una báscula configurada.");
            }

            return await ReadLockedAsync(live, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tara manual por software: toma el peso bruto estable actual (bolsa, bandeja) y lo descuenta de la SIGUIENTE pesada. Con
    /// <paramref name="clear"/>, o si la báscula está vacía, se quita la tara. Solo se quita al agregar la línea o al cancelar (D2): la
    /// caja lo pide con <c>clear</c>.
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

            return await TareLockedAsync(live, clear, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// La caja agregó la línea con la lectura <paramref name="readingId"/>: desde esa lectura, la siguiente pesada exige ver la báscula
    /// vacía (D1). Devuelve false si la lectura no es de este agente o ya es muy vieja.
    /// </summary>
    public bool MarkAccepted(Guid readingId)
    {
        lock (_recent)
        {
            foreach (var (id, seq) in _recent)
            {
                if (id == readingId)
                {
                    if (_acceptedSeq is not { } previous || seq > previous)
                    {
                        _acceptedSeq = seq;
                        _acceptedTick = _uptime.Elapsed;
                    }

                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// El servidor liberó el retorno a cero de la caja (se eliminó la última pesada o un supervisor reinició la báscula): la caja lo
    /// confirma con el servidor antes de llamar aquí, así el agente y el servidor quedan de acuerdo.
    /// </summary>
    public void ReleaseAccepted() => _acceptedSeq = null;

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
            CloseTestLocked();
            var test = _test = new ScaleChannel(target) { LastUsed = _time.GetTimestamp() };

            // Mismo puerto con otros parámetros: la prueba toma el puerto (la venta espera a que termine).
            if (_live is { } live && SamePort(live.Target, target) && !SameConnection(live.Target, target))
            {
                live.Close();
            }

            _testTimer = _time.CreateTimer(_ => _ = ExpireTestAsync(), null, _testIdle, Timeout.InfiniteTimeSpan);
            return (null, await ReadLockedAsync(test, cancellationToken));
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
            if (_test is not { } test)
            {
                return TestMissing();
            }

            TouchTest(test);
            return await ReadLockedAsync(test, cancellationToken);
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
            if (_test is not { } test)
            {
                return TestMissing();
            }

            TouchTest(test);
            return await TareLockedAsync(test, clear, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Cierra la prueba: la báscula en uso sigue igual (si la prueba le tomó el puerto, lo vuelve a abrir en la siguiente lectura).</summary>
    public async Task TestStopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CloseTestLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _testTimer?.Dispose();
        _live?.Close();
        _test?.Close();
        _gate.Dispose();
    }

    /// <summary>Báscula vacía (D1): peso dentro de la banda de cero (|peso| ≤ peso mínimo ⚙️). Con banda 0, solo el cero exacto.</summary>
    public static bool IsEmpty(decimal kg, decimal zeroBandKg) => Math.Abs(kg) <= zeroBandKg;

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

    private void TouchTest(ScaleChannel test)
    {
        test.LastUsed = _time.GetTimestamp();
        _testTimer?.Change(_testIdle, Timeout.InfiniteTimeSpan);
    }

    private void CloseTestLocked()
    {
        _testTimer?.Dispose();
        _testTimer = null;
        _test?.Close();
        _test = null;
    }

    /// <summary>El temporizador cierra la prueba sin lecturas aunque nadie vuelva a llamar al agente (el puerto queda libre).</summary>
    private async Task ExpireTestAsync()
    {
        try
        {
            await _gate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_test is { } test && _time.GetElapsedTime(test.LastUsed) >= _testIdle - TimeSpan.FromMilliseconds(50))
            {
                CloseTestLocked();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ScaleOutcome> TareLockedAsync(ScaleChannel channel, bool clear, CancellationToken cancellationToken)
    {
        if (clear)
        {
            channel.TareKg = 0m;
            return await ReadLockedAsync(channel, cancellationToken);
        }

        var current = await ReadLockedAsync(channel, cancellationToken);
        if (current.Weight is not { } weight)
        {
            return current;
        }

        if (!weight.Stable || weight.GrossKg is not { } gross || weight.Overload)
        {
            return ScaleOutcome.Fail(ScaleOutcome.TareUnstable, "Para tomar la tara el peso debe estar quieto: espere un momento y repita.");
        }

        channel.TareKg = gross > 0m && !IsEmpty(gross, channel.Target.ZeroBandKg) ? gross : 0m;
        return await ReadLockedAsync(channel, cancellationToken);
    }

    /// <summary>
    /// Conexión de un canal. La prueba en el mismo puerto y con los mismos parámetros que la báscula en uso lee por la conexión de esta
    /// (sin cerrar ni reabrir el puerto, que en Windows puede quedar ocupado unos instantes).
    /// </summary>
    private (IScaleConnection Connection, ScaleChannel Owner) ConnectionFor(ScaleChannel channel)
    {
        var owner = ReferenceEquals(channel, _test) && _live is { } live && SamePort(live.Target, channel.Target) && SameConnection(live.Target, channel.Target)
            ? live
            : channel;
        owner.Connection ??= Open(owner.Target);
        return (owner.Connection, owner);
    }

    private IScaleConnection Open(ScaleTarget target)
    {
        try
        {
            return _connections.Open(target);
        }
        catch (UnauthorizedAccessException)
        {
            // Windows libera el puerto COM unos instantes después de cerrarlo: un reintento evita el "puerto ocupado" de un cambio rápido.
            Thread.Sleep(300);
            return _connections.Open(target);
        }
    }

    private async Task<ScaleOutcome> ReadLockedAsync(ScaleChannel channel, CancellationToken cancellationToken)
    {
        var target = channel.Target;
        var isLive = ReferenceEquals(channel, _live);
        if (ScaleDrivers.Find(target.DriverCode) is not { } driver)
        {
            return ScaleOutcome.Fail(ScaleOutcome.NotConfigured, "Esta caja no tiene una báscula configurada.");
        }

        if (isLive && _test is { } test && SamePort(test.Target, target) && !SameConnection(test.Target, target))
        {
            return ScaleOutcome.Fail(
                ScaleOutcome.InTest, "La báscula de esta caja está en prueba (Configuración → Cajas e impresoras): detenga la prueba para pesar.");
        }

        ScaleChannel owner = channel;
        try
        {
            var reading = await Task.Run(
                () =>
                {
                    if (target.Mode == ScaleReadMode.Request)
                    {
                        // Tiempos con el reloj monótono: un salto del reloj del equipo no deja al agente esperando (BB4).
                        var wait = MinRequestInterval - _time.GetElapsedTime(channel.LastRequest);
                        if (wait > TimeSpan.Zero && wait <= MinRequestInterval)
                        {
                            Thread.Sleep(wait);
                        }
                    }

                    var (connection, used) = ConnectionFor(channel);
                    owner = used;
                    channel.LastRequest = _time.GetTimestamp();
                    return ScaleProtocolReader.Read(connection, driver, target);
                },
                cancellationToken);

            var now = _time.GetUtcNow();
            var tick = _time.GetTimestamp();
            var stable = reading.Stable && reading.Problem is null;
            if (!driver.ReportsStability && reading.WeightKg is { } gross)
            {
                if (channel.LastGross != gross)
                {
                    channel.LastGross = gross;
                    channel.SameSince = tick;
                }

                stable = stable && _time.GetElapsedTime(channel.SameSince, tick) >= SoftwareStableWindow;
            }

            var dto = Build(reading, stable, channel.TareKg, target.ZeroBandKg, now);
            if (isLive)
            {
                dto = Track(dto);
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
                owner.Close();
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

    /// <summary>
    /// Lectura de la báscula en uso: hora creciente, identificador único, retorno a cero (D1: cualquier lectura dentro de la banda de cero,
    /// estable o no, cuenta como vacía) y firma (D3).
    /// </summary>
    private ScaleWeightDto Track(ScaleWeightDto dto)
    {
        // BB4: la hora es la del reloj (sin "máximo guardado" que deje la caja trabada si el reloj salta); el orden lo da la secuencia.
        var seq = ++_seq;
        var live = _live!.Target;

        // BB1: vacía es el BRUTO dentro de la banda de cero (también con deriva negativa). Con tara (Ins o la tecla de la báscula) el neto da
        // 0 aunque el producto siga encima; si la trama ya viene neta, no trae el bruto real y no se marca vacía.
        if (dto.GrossKg is { } gross0 && !dto.ScaleNet && IsEmpty(gross0, live.ZeroBandKg))
        {
            _lastEmptySeq = seq;
        }
        else if (dto.GrossKg is not null)
        {
            _lastOkSeq = seq;
            _lastOkTick = _uptime.Elapsed;
        }

        var readingId = Guid.CreateVersion7(dto.At);
        var emptied = _acceptedSeq is not { } accepted || EmptiedAfter(accepted);
        lock (_recent)
        {
            _recent.Enqueue((readingId, seq));
            while (_recent.Count > RecentReadings)
            {
                _recent.Dequeue();
            }
        }

        // BB7: la báscula simulada nunca se firma (el servidor no aceptaría un peso inventado).
        SignedScaleReading? signed = null;
        if (!live.Simulated && _signer.TerminalId is { } terminalId && dto.GrossKg is { } gross && dto.WeightKg is { } weight)
        {
            var payload = new ScaleReadingPayload(
                terminalId, readingId, dto.At, BootId, _signer.BootCounter, seq, gross, dto.TareKg, weight, dto.Stable, dto.ScaleNet, dto.Status, _lastEmptySeq, emptied);
            signed = _signer.Sign(payload.Format());
        }

        // BS1/BS3: con clave pero sin un número de arranque confiable el agente no firma y dice por qué (la caja lo muestra).
        var problem = !live.Simulated && _signer.TerminalId is not null ? _signer.SigningProblem : null;
        return dto with { ReadingId = readingId, EmptiedSinceLastAccepted = emptied, SignedReading = signed, SigningProblem = problem };
    }

    private bool EmptiedAfter(long seq) => _lastEmptySeq is { } empty && empty > seq;

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

        public long SameSince { get; set; }

        public long LastRequest { get; set; }

        public long LastUsed { get; set; }

        public void Close()
        {
            Connection?.Dispose();
            Connection = null;
        }
    }
}

/// <summary>
/// Vigilante del retorno a cero (D1): después de una pesada aceptada lee la báscula en uso cada <see cref="Interval"/> hasta verla
/// vacía, aunque la pantalla de la caja no esté leyendo (recargada, en otra pantalla o pasando productos sin peso). Así un paso rápido
/// por cero entre dos lecturas de la caja también cuenta.
/// </summary>
public sealed class ScaleEmptyMonitor(ScaleService scale) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                if (scale.AwaitingEmpty && !scale.Testing)
                {
                    var outcome = await scale.ReadAsync(stoppingToken);
                    if (outcome.Weight is null)
                    {
                        // Báscula sin respuesta: se espera un poco más antes de volver a intentar.
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }
}
