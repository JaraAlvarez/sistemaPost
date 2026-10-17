using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pos.Licensing.Contracts;
using Pos.Updates.Contracts;

namespace Pos.Server.Updater;

/// <summary>
/// Configuración del actualizador: <c>{DataRoot}\config\server.json</c> (servidor) o <c>updater.json</c> (caja), escritos por el instalador.
/// R5: <see cref="InstallRoot"/> NO sale de la configuración (un usuario que pudiera escribirla haría que SYSTEM instale en otra carpeta):
/// la resuelve <see cref="UpdaterSecurity.ResolveInstallRoot"/> a partir de la ubicación del programa y de la clave de Inno.
/// </summary>
public sealed record UpdaterSettings(
    string Product,
    UpdaterMode Mode,
    string DataRoot,
    string InstallRoot,
    string? ManifestUrl,
    string Channel,
    string? ServerUrl,
    string? ServerCertificateThumbprint,
    int CheckIntervalHours,
    int WindowHour,
    int ServerPort,
    int AgentPort,
    int HealthTimeoutSeconds,
    string? ConnectionString,
    string? PgBinPath,
    IReadOnlyList<string> ServiceNames,
    IReadOnlyList<string> DevelopmentTrustedKeys)
{
    public static UpdaterSettings From(IConfiguration configuration, string product, string dataRoot, string installRoot, bool production)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var updates = configuration.GetSection("Pos:Updates");
        var mode = string.Equals(updates["Mode"], nameof(UpdaterMode.Terminal), StringComparison.OrdinalIgnoreCase) ? UpdaterMode.Terminal : UpdaterMode.Server;
        return new UpdaterSettings(
            product,
            mode,
            dataRoot,
            installRoot,
            updates["ManifestUrl"],
            UpdateChannels.IsKnown(updates["Channel"]) ? updates["Channel"]! : UpdateChannels.Stable,
            updates["ServerUrl"],
            updates["ServerCertificateThumbprint"],
            Int(updates["CheckIntervalHours"], 6, 1, 168),
            Int(updates["WindowHour"], 2, 0, 23),
            Int(configuration["Pos:Server:Port"], 5480, 1, 65535),
            Int(configuration["Agent:Port"], 5490, 1, 65535),
            Int(updates["HealthTimeoutSeconds"], 120, 10, 900),
            configuration["Pos:Database:ConnectionString"],
            configuration["Pos:Backup:PgBinPath"],
            mode == UpdaterMode.Terminal ? [$"{product}-TerminalAgent"] : [$"{product}-Server", $"{product}-TerminalAgent"],
            DevelopmentKeys(updates, DevelopmentKeysCompiledIn && !production));
    }

    /// <summary>
    /// Claves de desarrollo de los manifiestos (<c>Pos:Updates:DevelopmentTrustedKeys</c>): solo existen en compilación Debug y fuera de
    /// producción (R1). El entorno lo cambia cualquier administrador de Windows, así que en Release nunca se leen.
    /// </summary>
#if DEBUG
    internal const bool DevelopmentKeysCompiledIn = true;
#else
    internal const bool DevelopmentKeysCompiledIn = false;
#endif

    internal static IReadOnlyList<string> DevelopmentKeys(IConfiguration updates, bool enabled)
    {
#if DEBUG
        return enabled ? [.. updates.GetSection("DevelopmentTrustedKeys").GetChildren().Select(c => c.Value).OfType<string>()] : [];
#else
        _ = updates;
        _ = enabled;
        return [];
#endif
    }

    private static int Int(string? text, int fallback, int min, int max) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max ? value : fallback;
}

/// <summary>Claves públicas de confianza para los manifiestos: embebidas (<c>update-keys.json</c>) y, solo en Debug fuera de producción, las de la configuración.</summary>
public static class UpdateKeys
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static LicenseKeyRing Load(IEnumerable<string> developmentKeys)
    {
        using var stream = typeof(UpdateKeys).Assembly.GetManifestResourceStream("Pos.Updates.TrustedKeys.json");
        using var reader = stream is null ? null : new StreamReader(stream);
        return Parse(reader?.ReadToEnd(), developmentKeys);
    }

    /// <summary>Las claves con <c>status: REVOKED</c> no se cargan (una clave filtrada se revoca publicando la lista sin ella o marcada).</summary>
    public static LicenseKeyRing Parse(string? keysJson, IEnumerable<string> developmentKeys)
    {
        ArgumentNullException.ThrowIfNull(developmentKeys);
        var keys = new List<LicensePublicKey>();
        if (keysJson is { Length: > 0 } && JsonSerializer.Deserialize<KeysFile>(keysJson, Json) is { } file)
        {
            keys.AddRange(file.Keys
                .Where(k => !string.Equals(k.Status, "REVOKED", StringComparison.OrdinalIgnoreCase))
                .Select(k => LicensePublicKey.TryParse(k.X, out var key) && key.Kid == k.Kid ? key : null)
                .OfType<LicensePublicKey>());
        }

        keys.AddRange(developmentKeys.Select(x => LicensePublicKey.TryParse(x, out var key) ? key : null).OfType<LicensePublicKey>());
        return new LicenseKeyRing(keys.DistinctBy(k => k.Kid));
    }

    private sealed record KeysFile(IReadOnlyList<PublicKeyDto> Keys);
}

/// <summary>
/// Ciclo del actualizador (D13-08): consulta el manifiesto cada ⚙️ 6 h, descarga y verifica el paquete, y lo aplica en la ventana segura
/// (⚙️ 02:00, sin jornadas abiertas) o cuando el propietario pide "instalar ahora". Publica su estado para el servidor.
/// Revisión del actualizador: manifiesto solo por https (servidor), con caducidad y número de secuencia creciente por canal (el último visto
/// se guarda); la firma y la huella se vuelven a revisar justo antes de instalar; la descarga va a un archivo nuevo; si la instalación
/// falla, se reintenta con espera creciente sin hacer un backup nuevo cada minuto; la caja instala de madrugada, al encender el equipo o
/// cuando el servidor propaga "instalar ahora" (no se detiene el agente en plena venta); si quedó una actualización a medias, al arrancar
/// se vuelve atrás y, mientras no se resuelva, no se empieza otra.
/// </summary>
public sealed partial class UpdateWorker(
    UpdaterSettings settings,
    IUpdateHost host,
    LicenseKeyRing trustedKeys,
    TimeProvider time,
    ILogger<UpdateWorker> logger,
    HttpMessageHandler? handler = null) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Espera máxima entre reintentos de una instalación que falló.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(6);

    /// <summary>ALTA 4: la caja instala lo descargado en los primeros minutos después de encender el equipo, antes de empezar a vender.</summary>
    public static readonly TimeSpan BootWindow = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http = new(handler ?? CreateHandler(settings)) { Timeout = TimeSpan.FromMinutes(10) };
    private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private int _failures;
    private bool _serverAsksInstallNow;
    private (UpdateManifest Manifest, string Package, SignedUpdateManifest Signed)? _ready;
    private string? _lastError;

    /// <summary>Error que se publica en el estado (para pruebas y para el servidor).</summary>
    internal string? LastError => _lastError;

    internal UpdateManifest? ReadyManifest => _ready?.Manifest;

    /// <summary>Si la instalación vigente no pasa la revisión de seguridad (permisos), no se instala ni se deshace nada (R5, B1).</summary>
    public string? SecurityProblem { get; init; }

    /// <summary>ALTA 1: ¿el archivo de control es de SYSTEM o de los Administradores? (registro en curso, secuencias, versión fallida).</summary>
    public Func<string, bool>? IsTrustedFile { get; init; }

    /// <summary>Tiempo desde que se encendió el equipo (ALTA 4, la caja instala al arrancar).</summary>
    public Func<TimeSpan> Uptime { get; init; } = () => TimeSpan.FromMilliseconds(Environment.TickCount64);

    private UpdateOrchestrator Orchestrator() => new(host, settings.Mode, settings.DataRoot, time, IsTrustedFile);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAtStartupAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // M2: un error al CONSULTAR (red, servidor) no alarga la espera de las instalaciones.
                _lastError = ex.Message;
                LogFailed(logger, ex);
            }

            Publish();
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
        }
    }

    /// <summary>U4: una actualización que quedó a medias (corte de luz) se deshace antes de cualquier otra cosa (salvo con un problema de seguridad).</summary>
    internal async Task RecoverAtStartupAsync(CancellationToken cancellationToken)
    {
        if (SecurityProblem is not null)
        {
            return;
        }

        try
        {
            var orchestrator = Orchestrator();
            var recovered = await orchestrator.RecoverAsync(cancellationToken);
            if (orchestrator.JournalProblem is { } problem)
            {
                _lastError = problem;
            }

            if (recovered is { } result)
            {
                LogRecovered(logger, result);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = $"No se pudo deshacer una actualización a medias: {ex.Message}";
            LogFailed(logger, ex);
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        if (SecurityProblem is { } problem)
        {
            _lastError = problem;
            return;
        }

        var now = time.GetUtcNow();
        if (now >= _nextCheck)
        {
            _nextCheck = now.AddHours(settings.CheckIntervalHours);
            await CheckAsync(cancellationToken);
        }

        if (_ready is not { } ready)
        {
            return;
        }

        var request = Path.Combine(UpdateFiles.Directory(settings.DataRoot), UpdateFiles.InstallNowRequest);
        var forced = File.Exists(request) || _serverAsksInstallNow;
        var inWindow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Hour == settings.WindowHour;

        // También en la caja: el agente no se detiene en plena venta; se espera la madrugada, el arranque del equipo (antes de vender) o
        // "instalar ahora". M2: "instalar ahora" se atiende aunque haya una espera por un fallo anterior.
        var atBoot = settings.Mode == UpdaterMode.Terminal && Uptime() < BootWindow;
        if (!forced && (!(inWindow || atBoot) || now < _retryAt))
        {
            return;
        }

        if (File.Exists(request))
        {
            File.Delete(request);
        }

        _serverAsksInstallNow = false;

        // U2: justo antes de instalar se vuelven a revisar la firma del manifiesto y la huella del paquete (pudo cambiar en el disco).
        if (UpdateSigning.Verify(ready.Signed, trustedKeys, settings.Product, out var again) != ManifestStatus.Valid
            || again is null || again != ready.Manifest || !await MatchesAsync(ready.Package, ready.Manifest, cancellationToken))
        {
            _lastError = "El manifiesto o el paquete cambió desde que se verificó: se descarta y se vuelve a descargar.";
            _ready = null;
            _nextCheck = now;
            return;
        }

        UpdateResult result;
        var orchestrator = Orchestrator();
        try
        {
            result = await orchestrator.ApplyAsync(ready.Manifest, ready.Package, forced, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // M2: la espera creciente es para los fallos de INSTALACIÓN.
            _lastError = ex.Message;
            Backoff();
            LogFailed(logger, ex);
            return;
        }

        LogResult(logger, ready.Manifest.Version, result);
        switch (result)
        {
            case UpdateResult.Deferred:
                return;
            case UpdateResult.Pending:
                _lastError = orchestrator.JournalProblem
                    ?? "Hay una actualización anterior a medias que no se pudo deshacer: requiere soporte (no se instala otra hasta resolverla).";
                return;
            case UpdateResult.FailedNoChanges:
                // Nada cambió (backup, servicios o migración): se reintenta más tarde, cada vez con más espera.
                Backoff();
                return;
            case UpdateResult.RolledBack or UpdateResult.RollbackFailed:
                // Esa versión no sirve en este equipo: no se reintenta hasta que haya una más nueva (M2: se guarda en el disco).
                SaveFailedVersion(ready.Manifest.Version);
                _ready = null;
                return;
        }

        _failures = 0;
        _retryAt = DateTimeOffset.MinValue;
        if (settings.Mode == UpdaterMode.Server)
        {
            // Las cajas se actualizan desde aquí (D13-09): el servidor ofrece el manifiesto firmado y el paquete de la versión instalada.
            // El paquete primero: una caja que ve el manifiesto nuevo ya encuentra su paquete.
            var folder = UpdateFiles.Directory(settings.DataRoot);
            File.Copy(ready.Package, Path.Combine(folder, UpdateFiles.InstalledPackage), overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(folder, UpdateFiles.InstalledManifest), UpdateSigning.Serialize(ready.Signed), cancellationToken);
        }

        _ready = null;
    }

    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        var manifestUrl = ManifestUri();
        if (manifestUrl is null)
        {
            return;
        }

        if (!SemanticVersion.TryParse(host.CurrentVersion, out var current))
        {
            // Bajas: current.json dañado no es "0.0.0" (cualquier versión parecería nueva): no se actualiza y se avisa.
            _lastError = "No se sabe qué versión está instalada (app\\current.json falta o está dañado): no se actualiza. Reinstale o llame a soporte.";
            _ready = null;
            return;
        }

        using var response = await _http.GetAsync(manifestUrl, cancellationToken);
        if (settings.Mode == UpdaterMode.Terminal && response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // B3: el equipo principal todavía no tiene manifiesto (instalación sin versión firmada): no hay nada que instalar, no es un error.
            _lastError = null;
            _ready = null;
            return;
        }

        response.EnsureSuccessStatusCode();
        _serverAsksInstallNow = settings.Mode == UpdaterMode.Terminal
            && response.Headers.TryGetValues(TerminalInstallNowHeader, out var values) && values.Contains("1");
        var signed = await response.Content.ReadFromJsonAsync<SignedUpdateManifest>(Json, cancellationToken);
        var status = UpdateSigning.Verify(signed, trustedKeys, settings.Product, out var manifest);
        if (status == ManifestStatus.Valid && manifest is not null && settings.Mode == UpdaterMode.Server && manifest.Channel != settings.Channel)
        {
            // M4: el canal se valida ANTES de mirar o guardar la secuencia.
            _lastError = null;
            _ready = null;
            return;
        }

        if (status == ManifestStatus.Valid && manifest is not null)
        {
            status = ManifestFreshness.Check(manifest, time.GetUtcNow(), LastSequence(manifest.Channel), requireNotExpired: settings.Mode == UpdaterMode.Server);
        }

        if (status == ManifestStatus.MissingFreshness && settings.Mode == UpdaterMode.Terminal)
        {
            // B3: el equipo principal ofrece el manifiesto de una versión firmada antes de esta revisión: no se instala, sin error permanente.
            _lastError = null;
            _ready = null;
            return;
        }

        if (status != ManifestStatus.Valid || manifest is null)
        {
            _lastError = $"Manifiesto rechazado ({status}).";
            _ready = null;
            return;
        }

        SaveSequence(manifest.Channel, manifest.Sequence);
        _failures = 0;
        if (!SemanticVersion.TryParse(manifest.Version, out var available)
            || available <= current
            || string.Equals(manifest.Version, FailedVersion(), StringComparison.Ordinal))
        {
            _lastError = null;
            _ready = null;
            return;
        }

        var package = Path.Combine(UpdateFiles.Directory(settings.DataRoot), $"{manifest.Version}.zip");
        if (!File.Exists(package) || !await MatchesAsync(package, manifest, cancellationToken))
        {
            var packageUrl = settings.Mode == UpdaterMode.Terminal
                ? new Uri(new Uri(settings.ServerUrl!), "api/v1/system/updates/package")
                : new Uri(manifestUrl, manifest.PackageUrl);
            if (settings.Mode == UpdaterMode.Server && packageUrl.Scheme != Uri.UriSchemeHttps)
            {
                _lastError = "El paquete debe descargarse por https.";
                return;
            }

            await DownloadAsync(packageUrl, package, cancellationToken);
            if (!await MatchesAsync(package, manifest, cancellationToken))
            {
                File.Delete(package);
                _lastError = "El paquete descargado no coincide con la huella del manifiesto.";
                return;
            }

            UpdateFiles.AppendHistory(settings.DataRoot, new UpdateHistoryEntry(
                Guid.CreateVersion7(), time.GetUtcNow(), UpdateOutcomes.Downloaded, host.CurrentVersion, manifest.Version,
                $"Versión {manifest.Version} descargada y verificada; se instalará a las {settings.WindowHour:00}:00 sin jornadas abiertas."));
        }

        _ready = (manifest, package, signed!);
        _lastError = null;
    }

    /// <summary>Cabecera con la que el equipo principal propaga "instalar ahora" a sus cajas (UpdatesEndpoints del servidor).</summary>
    public const string TerminalInstallNowHeader = "X-Pos-Install-Now";

    /// <summary>
    /// R5 (TOCTOU): la descarga va a un archivo NUEVO (se borra el que hubiera y se crea con <see cref="FileMode.CreateNew"/>, en exclusiva)
    /// dentro de la carpeta de datos con ACL; la huella se revisa después y otra vez al instalar con un handle exclusivo.
    /// </summary>
    private async Task DownloadAsync(Uri packageUrl, string package, CancellationToken cancellationToken)
    {
        var part = package + ".part";
        foreach (var path in new[] { part, package })
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                if (UpdaterSecurity.IsReparsePoint(path) && Directory.Exists(path))
                {
                    Directory.Delete(path);
                }
                else
                {
                    File.Delete(path);
                }
            }
        }

        await using (var source = await _http.GetStreamAsync(packageUrl, cancellationToken))
        await using (var target = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        File.Move(part, package, overwrite: false);
    }

    private Uri? ManifestUri()
    {
        if (settings.Mode == UpdaterMode.Terminal)
        {
            return settings.ServerUrl is { Length: > 0 } server ? new Uri(new Uri(server), "api/v1/system/updates/manifest") : null;
        }

        if (!Uri.TryCreate(settings.ManifestUrl, UriKind.Absolute, out var url))
        {
            return null;
        }

        if (url.Scheme != Uri.UriSchemeHttps)
        {
            // Bajas: el manifiesto viaja firmado, pero por http se podría ocultar una actualización de seguridad (o servir uno viejo).
            _lastError = "La dirección del manifiesto de actualizaciones debe ser https.";
            return null;
        }

        return url;
    }

    private void Backoff()
    {
        _failures++;
        var delay = TimeSpan.FromMinutes(5 * Math.Pow(2, Math.Min(_failures - 1, 10)));
        _retryAt = time.GetUtcNow() + (delay > MaxRetryDelay ? MaxRetryDelay : delay);
    }

    internal DateTimeOffset RetryAt => _retryAt;

    private string ControlFile(string name) => Path.Combine(UpdateFiles.Directory(settings.DataRoot), name);

    /// <summary>ALTA 1: un archivo de control que no es de SYSTEM ni de los Administradores se borra y no se usa.</summary>
    private JsonDocument? ReadControl(string name)
    {
        var path = ControlFile(name);
        if (!File.Exists(path))
        {
            return null;
        }

        if (IsTrustedFile is not null && !IsTrustedFile(path))
        {
            File.Delete(path);
            return null;
        }

        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private void WriteControl(string name, object content)
    {
        Directory.CreateDirectory(UpdateFiles.Directory(settings.DataRoot));
        var path = ControlFile(name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(content));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>M4: última secuencia aceptada, por canal (la caja usa el canal del manifiesto que le sirve el equipo principal).</summary>
    private long LastSequence(string channel)
    {
        using var document = ReadControl(UpdateFiles.LastSequence);
        return document is not null && document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(channel, out var value) && value.TryGetInt64(out var sequence)
                ? sequence
                : 0;
    }

    private void SaveSequence(string channel, long sequence)
    {
        if (sequence <= LastSequence(channel))
        {
            return;
        }

        var all = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var document = ReadControl(UpdateFiles.LastSequence))
        {
            if (document is not null && document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.TryGetInt64(out var value))
                    {
                        all[property.Name] = value;
                    }
                }
            }
        }

        all[channel] = sequence;
        WriteControl(UpdateFiles.LastSequence, all);
    }

    private string? FailedVersion()
    {
        using var document = ReadControl(UpdateFiles.FailedVersion);
        return document is not null && document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("version", out var value) ? value.GetString() : null;
    }

    private void SaveFailedVersion(string version) => WriteControl(UpdateFiles.FailedVersion, new { version });

    private static async Task<bool> MatchesAsync(string path, UpdateManifest manifest, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || UpdaterSecurity.IsReparsePoint(path))
        {
            return false;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != manifest.PackageSize)
        {
            return false;
        }

        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(hash, manifest.PackageSha256, StringComparison.OrdinalIgnoreCase);
    }

    private void Publish()
    {
        try
        {
            var today = TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZoneInfo.Local);
            var window = new DateTimeOffset(today.Date.AddHours(settings.WindowHour), today.Offset);
            UpdateFiles.WriteState(settings.DataRoot, new UpdaterState(
                host.CurrentVersion, _ready?.Manifest.Version, _ready?.Manifest.Notes, _ready is not null, time.GetUtcNow(), _lastError,
                _ready is null ? null : window < today ? window.AddDays(1) : window));
        }
        catch (IOException ex)
        {
            LogFailed(logger, ex);
        }
    }

    private static HttpClientHandler CreateHandler(UpdaterSettings settings)
    {
        var handler = new HttpClientHandler();
        if (settings.Mode == UpdaterMode.Terminal && settings.ServerCertificateThumbprint is { Length: > 0 } pinned)
        {
            // El servidor de la tienda usa un certificado propio: se confía solo en la huella vista al emparejar (ADR-0018).
#pragma warning disable CA5359 // La validación es por huella fijada, no se desactiva.
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), pinned.Replace(":", string.Empty, StringComparison.Ordinal),
                    StringComparison.OrdinalIgnoreCase);
#pragma warning restore CA5359
        }

        return handler;
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló el ciclo del actualizador.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Actualización a {Version}: {Result}")]
    private static partial void LogResult(ILogger logger, string version, UpdateResult result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Había una actualización a medias; al arrancar: {Result}")]
    private static partial void LogRecovered(ILogger logger, UpdateResult result);
}
