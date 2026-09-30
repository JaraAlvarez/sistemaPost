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
    public static UpdaterSettings From(IConfiguration configuration, string product, string dataRoot, bool production)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var updates = configuration.GetSection("Pos:Updates");
        var mode = string.Equals(updates["Mode"], nameof(UpdaterMode.Terminal), StringComparison.OrdinalIgnoreCase) ? UpdaterMode.Terminal : UpdaterMode.Server;
        var installRoot = updates["InstallRoot"] is { Length: > 0 } root
            ? root
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
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
            production ? [] : [.. updates.GetSection("DevelopmentTrustedKeys").GetChildren().Select(c => c.Value).OfType<string>()]);
    }

    private static int Int(string? text, int fallback, int min, int max) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max ? value : fallback;
}

/// <summary>Claves públicas de confianza para los manifiestos: embebidas (<c>update-keys.json</c>) y, fuera de producción, las de la configuración.</summary>
public static class UpdateKeys
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static LicenseKeyRing Load(IEnumerable<string> developmentKeys)
    {
        var keys = new List<LicensePublicKey>();
        using var stream = typeof(UpdateKeys).Assembly.GetManifestResourceStream("Pos.Updates.TrustedKeys.json");
        if (stream is not null && JsonSerializer.Deserialize<KeysFile>(stream, Json) is { } file)
        {
            keys.AddRange(file.Keys.Select(k => LicensePublicKey.TryParse(k.X, out var key) && key.Kid == k.Kid ? key : null).OfType<LicensePublicKey>());
        }

        keys.AddRange(developmentKeys.Select(x => LicensePublicKey.TryParse(x, out var key) ? key : null).OfType<LicensePublicKey>());
        return new LicenseKeyRing(keys.DistinctBy(k => k.Kid));
    }

    private sealed record KeysFile(IReadOnlyList<PublicKeyDto> Keys);
}

/// <summary>
/// Ciclo del actualizador (D13-08): consulta el manifiesto cada ⚙️ 6 h, descarga y verifica el paquete, y lo aplica en la ventana segura
/// (⚙️ 02:00, sin jornadas abiertas) o cuando el propietario pide "instalar ahora". Publica su estado para el servidor.
/// </summary>
public sealed partial class UpdateWorker(
    UpdaterSettings settings,
    IUpdateHost host,
    LicenseKeyRing trustedKeys,
    TimeProvider time,
    ILogger<UpdateWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = CreateClient(settings);
    private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;
    private (UpdateManifest Manifest, string Package, SignedUpdateManifest Signed)? _ready;
    private string? _lastError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _lastError = ex.Message;
                LogFailed(logger, ex);
            }

            Publish();
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
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
        var forced = File.Exists(request);
        var inWindow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Hour == settings.WindowHour;
        if (!forced && !inWindow && settings.Mode == UpdaterMode.Server)
        {
            return;
        }

        if (forced)
        {
            File.Delete(request);
        }

        var result = await new UpdateOrchestrator(host, settings.Mode, settings.DataRoot, time).ApplyAsync(ready.Manifest, ready.Package, forced, cancellationToken);
        LogResult(logger, ready.Manifest.Version, result);
        if (result == UpdateResult.Deferred)
        {
            return;
        }

        if (result == UpdateResult.Applied && settings.Mode == UpdaterMode.Server)
        {
            // Las cajas se actualizan desde aquí (D13-09): el servidor ofrece el manifiesto firmado y el paquete de la versión instalada.
            var folder = UpdateFiles.Directory(settings.DataRoot);
            await File.WriteAllTextAsync(Path.Combine(folder, UpdateFiles.InstalledManifest), UpdateSigning.Serialize(ready.Signed), cancellationToken);
            File.Copy(ready.Package, Path.Combine(folder, UpdateFiles.InstalledPackage), overwrite: true);
        }

        _ready = null;
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var manifestUrl = ManifestUri();
        if (manifestUrl is null)
        {
            return;
        }

        var signed = await _http.GetFromJsonAsync<SignedUpdateManifest>(manifestUrl, Json, cancellationToken);
        var status = UpdateSigning.Verify(signed, trustedKeys, settings.Product, out var manifest);
        if (status != ManifestStatus.Valid || manifest is null)
        {
            _lastError = $"Manifiesto rechazado ({status}).";
            return;
        }

        if (settings.Mode == UpdaterMode.Server && manifest.Channel != settings.Channel
            || !SemanticVersion.TryParse(manifest.Version, out var available) || !SemanticVersion.TryParse(host.CurrentVersion, out var current)
            || available <= current)
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
            await using (var source = await _http.GetStreamAsync(packageUrl, cancellationToken))
            await using (var target = File.Create(package + ".part"))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            File.Move(package + ".part", package, overwrite: true);
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

    private Uri? ManifestUri() =>
        settings.Mode == UpdaterMode.Terminal
            ? settings.ServerUrl is { Length: > 0 } server ? new Uri(new Uri(server), "api/v1/system/updates/manifest") : null
            : Uri.TryCreate(settings.ManifestUrl, UriKind.Absolute, out var url) ? url : null;

    private static async Task<bool> MatchesAsync(string path, UpdateManifest manifest, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
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

    private static HttpClient CreateClient(UpdaterSettings settings)
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

        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló el ciclo del actualizador.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Actualización a {Version}: {Result}")]
    private static partial void LogResult(ILogger logger, string version, UpdateResult result);
}
