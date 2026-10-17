using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Pos.Cloud.Host.Downloads;

/// <summary>Configuración de "Descargas" (sección <c>Downloads</c>; en el contenedor, variables <c>Downloads__…</c>).</summary>
internal sealed class DownloadOptions
{
    public const string SectionName = "Downloads";

    /// <summary>
    /// Dirección del manifiesto de actualizaciones que ya usa el POS (p. ej. <c>https://businesspost.tutiendanueva.com/updates/stable.json</c>).
    /// Vacía = la misma dirección del portal + <c>updates/stable.json</c> (Caddy sirve esa carpeta). En producción conviene fijarla.
    /// </summary>
    public string? ManifestUrl { get; set; }

    /// <summary>Nombre del instalador en la misma carpeta del manifiesto; <c>{version}</c> se cambia por la versión publicada.</summary>
    public string InstallerFileName { get; set; } = "BusinessPost-Setup-{version}.exe";

    /// <summary>Nombre del manual en PDF en la misma carpeta del manifiesto. Vacío = no hay PDF (se ofrece "Imprimir / Guardar como PDF").</summary>
    public string? ManualPdfFileName { get; set; }

    /// <summary>Minutos que se recuerda el manifiesto leído (0 = leerlo siempre; las pruebas lo usan).</summary>
    public int CacheMinutes { get; set; } = 5;

    /// <summary>Segundos máximos de espera al leer el manifiesto.</summary>
    public int TimeoutSeconds { get; set; } = 5;
}

/// <summary>Última versión publicada que se muestra en "Descargas".</summary>
/// <param name="Version">Versión <c>MAYOR.MENOR.PARCHE</c> (ya validada: solo números y puntos).</param>
/// <param name="PublishedAt">Fecha de publicación, si el manifiesto la trae.</param>
/// <param name="Notes">Novedades en texto plano (la página las codifica al mostrarlas).</param>
/// <param name="InstallerUrl">Enlace del instalador (ruta del portal, p. ej. <c>/updates/BusinessPost-Setup-1.2.3.exe</c>, o absoluto).</param>
/// <param name="InstallerFileName">Nombre del archivo del instalador.</param>
internal sealed record LatestRelease(string Version, DateTimeOffset? PublishedAt, string? Notes, string InstallerUrl, string InstallerFileName);

/// <summary>
/// Lee el manifiesto de actualizaciones (el mismo <c>stable.json</c> del actualizador del POS) para saber la última versión publicada.
/// Solo toma la versión, la fecha y las novedades: NO verifica la firma, porque aquí no se usa para nada de seguridad (el POS sí la
/// verifica antes de instalar). Si el manifiesto no existe o no responde, devuelve <c>null</c> sin error.
/// </summary>
internal sealed partial class DownloadCatalog(IHttpClientFactory httpClients, IOptionsMonitor<DownloadOptions> options, TimeProvider time, ILogger<DownloadCatalog> logger)
{
    public const string HttpClientName = "BusinessPost.Downloads";

    private const int MaxManifestBytes = 256 * 1024;

    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, ManifestData? Data)> _cache = new(StringComparer.Ordinal);

    /// <summary>La última versión publicada, o <c>null</c> si aún no hay ninguna (o no se pudo leer).</summary>
    /// <param name="portalBaseUri">Dirección base del portal (con su ruta, p. ej. <c>https://tutiendanueva.com/businesspost/</c>).</param>
    /// <param name="cancellationToken">Cancelación de la página.</param>
    public async Task<LatestRelease?> GetLatestAsync(Uri portalBaseUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(portalBaseUri);
        var settings = options.CurrentValue;
        var data = await ReadCachedAsync(ManifestUrl(settings, portalBaseUri), settings, cancellationToken);
        if (data is null)
        {
            return null;
        }

        var installer = (string.IsNullOrWhiteSpace(settings.InstallerFileName) ? "BusinessPost-Setup-{version}.exe" : settings.InstallerFileName.Trim())
            .Replace("{version}", data.Version, StringComparison.Ordinal);
        return new LatestRelease(data.Version, data.PublishedAt, data.Notes, Link(settings, portalBaseUri, installer), installer);
    }

    /// <summary>Enlace del manual en PDF (<see cref="DownloadOptions.ManualPdfFileName"/>), o <c>null</c> si no está configurado.</summary>
    public string? GetManualPdfUrl(Uri portalBaseUri)
    {
        ArgumentNullException.ThrowIfNull(portalBaseUri);
        var settings = options.CurrentValue;
        return string.IsNullOrWhiteSpace(settings.ManualPdfFileName) ? null : Link(settings, portalBaseUri, settings.ManualPdfFileName.Trim());
    }

    private static Uri? ConfiguredManifestUrl(DownloadOptions settings) =>
        Uri.TryCreate(settings.ManifestUrl?.Trim(), UriKind.Absolute, out var url) && url.Scheme is "https" or "http" ? url : null;

    private static Uri ManifestUrl(DownloadOptions settings, Uri portalBaseUri) =>
        ConfiguredManifestUrl(settings) ?? new Uri(portalBaseUri, "updates/stable.json");

    /// <summary>
    /// Archivo de la misma carpeta del manifiesto. Sin dirección configurada está en /updates del mismo sitio: el enlace es una ruta
    /// (incluye la ruta base del portal, p. ej. <c>/updates/…</c> o <c>/businesspost/updates/…</c>).
    /// </summary>
    private static string Link(DownloadOptions settings, Uri portalBaseUri, string fileName) =>
        ConfiguredManifestUrl(settings) is { } manifest
            ? new Uri(manifest, Uri.EscapeDataString(fileName)).AbsoluteUri
            : portalBaseUri.AbsolutePath + "updates/" + Uri.EscapeDataString(fileName);

    private async Task<ManifestData?> ReadCachedAsync(Uri manifestUrl, DownloadOptions settings, CancellationToken cancellationToken)
    {
        var key = manifestUrl.AbsoluteUri;
        var now = time.GetUtcNow();
        if (settings.CacheMinutes > 0 && _cache.TryGetValue(key, out var cached) && cached.Expires > now)
        {
            return cached.Data;
        }

        var data = await ReadAsync(manifestUrl, settings, cancellationToken);
        if (settings.CacheMinutes > 0)
        {
            // Un fallo se recuerda menos tiempo: al publicar la primera versión aparece pronto.
            var minutes = data is null ? Math.Min(1, settings.CacheMinutes) : settings.CacheMinutes;
            _cache[key] = (now.AddMinutes(minutes), data);
        }

        return data;
    }

    private async Task<ManifestData?> ReadAsync(Uri manifestUrl, DownloadOptions settings, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 30)));
            var http = httpClients.CreateClient(HttpClientName);
            using var response = await http.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogManifestStatus(logger, manifestUrl, (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxManifestBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > MaxManifestBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return Parse(buffer.ToArray());
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException && !cancellationToken.IsCancellationRequested)
        {
            LogManifestError(logger, manifestUrl, ex);
            return null;
        }
    }

    /// <summary>
    /// Toma la versión, la fecha y las novedades del manifiesto firmado (<c>payload</c> en base64url) o de un manifiesto sin firmar.
    /// Cualquier forma inesperada devuelve <c>null</c>.
    /// </summary>
    internal static ManifestData? Parse(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.String)
            {
                var inner = Base64Url.DecodeFromChars(payload.GetString());
                using var innerDocument = JsonDocument.Parse(inner);
                return Read(innerDocument.RootElement);
            }

            return Read(root);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private static ManifestData? Read(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object
            || !manifest.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String
            || versionElement.GetString() is not { } version || !VersionPattern().IsMatch(version))
        {
            return null;
        }

        DateTimeOffset? publishedAt = manifest.TryGetProperty("publishedAt", out var date) && date.ValueKind == JsonValueKind.String
                                      && date.TryGetDateTimeOffset(out var parsed) ? parsed : null;
        var notes = manifest.TryGetProperty("notes", out var notesElement) && notesElement.ValueKind == JsonValueKind.String ? notesElement.GetString() : null;
        if (notes is { Length: > 2000 })
        {
            notes = notes[..2000] + "…";
        }

        return new ManifestData(version, publishedAt, string.IsNullOrWhiteSpace(notes) ? null : notes.Trim());
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Descargas: el manifiesto {Url} respondió {Status}")]
    private static partial void LogManifestStatus(ILogger logger, Uri url, int status);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Descargas: no se pudo leer el manifiesto {Url}")]
    private static partial void LogManifestError(ILogger logger, Uri url, Exception exception);

    /// <summary>Solo números y puntos: la versión termina en el nombre del archivo y en el enlace.</summary>
    [GeneratedRegex(@"^\d{1,5}\.\d{1,5}\.\d{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    internal sealed record ManifestData(string Version, DateTimeOffset? PublishedAt, string? Notes);
}
