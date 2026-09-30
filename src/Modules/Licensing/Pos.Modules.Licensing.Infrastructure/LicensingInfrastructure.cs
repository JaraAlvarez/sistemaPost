using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Infrastructure.Persistence;
using Pos.Licensing.Contracts;
using Pos.Modules.Licensing.Application;
using Pos.Modules.Licensing.Contracts;

namespace Pos.Modules.Licensing.Infrastructure;

/// <summary>Configuración <c>Pos:Licensing</c>.</summary>
public sealed class LicensingOptions
{
    public const string SectionName = "Pos:Licensing";

    /// <summary>Dirección del servidor de licencias en la nube (p. ej. <c>https://licencias.midominio.com</c>). Vacía = no configurado.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Horas entre verificaciones programadas (D12B-06).</summary>
    public int CheckinHours { get; set; } = 24;

    /// <summary>
    /// Claves públicas adicionales (base64url). SOLO fuera de producción (pruebas y desarrollo contra una nube local): en producción
    /// solo valen las embebidas en el binario (D12B-04).
    /// </summary>
    public IReadOnlyList<string> DevelopmentTrustedKeys { get; set; } = [];

    /// <summary>Huella fija (<c>fp1.…</c>) para pruebas; ignorada en producción.</summary>
    public string? DevelopmentFingerprint { get; set; }
}

// ------------------------------------------------------------------------------------------------ Cliente de la nube

/// <summary>Cliente HTTP del servidor de licencias (rutas y DTOs del contrato compartido <c>Pos.Licensing.Contracts</c>).</summary>
internal sealed partial class LicenseCloudClient(HttpClient? http, ILogger<LicenseCloudClient> logger) : ILicenseCloud
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => http?.BaseAddress is not null;

    public Task<CloudResult> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken) =>
        PostAsync(LicensingRoutes.Activations, request, cancellationToken);

    public Task<CloudResult> CheckinAsync(CheckinRequest request, CancellationToken cancellationToken) =>
        PostAsync(LicensingRoutes.Checkins, request, cancellationToken);

    public Task<CloudResult> DeactivateAsync(DeactivationRequest request, CancellationToken cancellationToken) =>
        PostAsync(LicensingRoutes.Deactivations, request, cancellationToken);

    private async Task<CloudResult> PostAsync<T>(string route, T body, CancellationToken cancellationToken)
    {
        if (http is null)
        {
            return CloudResult.NoConnection;
        }

        try
        {
            using var response = await http.PostAsJsonAsync(route.TrimStart('/'), body, Json, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return response.StatusCode == HttpStatusCode.NoContent
                    ? CloudResult.Ok(null)
                    : CloudResult.Ok(await response.Content.ReadFromJsonAsync<LicenseTokenResponse>(Json, cancellationToken));
            }

            var problem = await ReadProblemAsync(response, cancellationToken);
            return CloudResult.Fail(problem?.Code ?? $"LICENSE.HTTP_{(int)response.StatusCode}", problem?.Detail ?? problem?.Title);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, route, ex.Message);
            return CloudResult.NoConnection;
        }
    }

    private static async Task<Problem?> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<Problem>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Servidor de licencias no disponible ({Route}): {Error}")]
    private static partial void LogUnreachable(ILogger logger, string route, string error);

    private sealed record Problem(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("detail")] string? Detail);
}

// ------------------------------------------------------------------------------------------------ Equipo

/// <summary>
/// Huella del equipo (D12B-05): UUID de la placa (<c>Win32_ComputerSystemProduct</c>), serie del disco del sistema y
/// <c>MachineGuid</c> de Windows. Se lee una vez; solo viajan los hashes (formato <c>fp1</c>).
/// </summary>
internal sealed class WindowsDeviceIdentity : IDeviceIdentity
{
    private readonly Lazy<DeviceFingerprint?> _fingerprint;

    public WindowsDeviceIdentity(string? developmentFingerprint)
    {
        _fingerprint = new Lazy<DeviceFingerprint?>(() =>
            developmentFingerprint is not null && DeviceFingerprint.TryParse(developmentFingerprint, out var fixedPrint)
                ? fixedPrint
                : System.OperatingSystem.IsWindows() ? ReadWindows() : null);
    }

    public DeviceFingerprint? Fingerprint => _fingerprint.Value;

    public string DeviceName => Environment.MachineName;

    public string OperatingSystem => RuntimeInformation.OSDescription;

    public string AppVersion { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    [SupportedOSPlatform("windows")]
    private static DeviceFingerprint? ReadWindows()
    {
        var fingerprint = DeviceFingerprint.FromHardware(
            Safe(() => Wmi("SELECT UUID FROM Win32_ComputerSystemProduct", "UUID")),
            Safe(SystemDiskSerial),
            Safe(() => Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?.GetValue("MachineGuid") as string));
        return fingerprint.IsUsable ? fingerprint : null;
    }

    [SupportedOSPlatform("windows")]
    private static string? SystemDiskSerial()
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        using var partitions = new System.Management.ManagementObjectSearcher(
            $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{drive}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
        foreach (var partition in partitions.Get())
        {
            using (partition)
            {
                using var disks = new System.Management.ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                foreach (var disk in disks.Get())
                {
                    using (disk)
                    {
                        if (disk["SerialNumber"]?.ToString()?.Trim() is { Length: > 0 } serial)
                        {
                            return serial;
                        }
                    }
                }
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? Wmi(string query, string property)
    {
        using var searcher = new System.Management.ManagementObjectSearcher(query);
        foreach (var item in searcher.Get())
        {
            using (item)
            {
                if (item[property]?.ToString()?.Trim() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? Safe(Func<string?> read)
    {
        try
        {
            return read();
        }
#pragma warning disable CA1031 // Un componente ilegible no impide la huella (2 de 3).
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}

// ------------------------------------------------------------------------------------------------ Claves de confianza

/// <summary>
/// Claves públicas de confianza (D12B-04): las embebidas en el binario (<c>trusted-keys.json</c>, se completa al compilar la versión
/// de producción con las claves ACTIVE y STANDBY del servidor) más, solo fuera de producción, las de la configuración.
/// </summary>
internal sealed class EmbeddedLicenseKeys : ILicenseKeys
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public EmbeddedLicenseKeys(IEnumerable<string> developmentKeys)
    {
        var keys = new List<LicensePublicKey>();
        using var stream = typeof(EmbeddedLicenseKeys).Assembly.GetManifestResourceStream("Pos.Licensing.TrustedKeys.json");
        if (stream is not null && JsonSerializer.Deserialize<TrustedKeysFile>(stream, Json) is { } file)
        {
            keys.AddRange(file.Keys.Select(k => LicensePublicKey.TryParse(k.X, out var key) && key.Kid == k.Kid ? key : null).OfType<LicensePublicKey>());
        }

        keys.AddRange(developmentKeys.Select(x => LicensePublicKey.TryParse(x, out var key) ? key : null).OfType<LicensePublicKey>());
        Ring = new LicenseKeyRing(keys.DistinctBy(k => k.Kid));
    }

    public LicenseKeyRing Ring { get; }

    private sealed record TrustedKeysFile(IReadOnlyList<PublicKeyDto> Keys);
}

// ------------------------------------------------------------------------------------------------ Proceso en segundo plano

/// <summary>
/// Crea la fila de la licencia al arrancar, recalcula el estado cada minuto (vencimientos, reloj) y verifica con la nube cada
/// ⚙️ 24 h ± 2 h, al arrancar y con reintentos crecientes (1 min → 1 h) si falla (D12B-06).
/// </summary>
internal sealed partial class LicenseWorker(
    IServiceScopeFactory scopes,
    DatabaseReadiness readiness,
    IInstallationContext installation,
    ILicenseCloud cloud,
    LicensingOptions options,
    TimeProvider time,
    ILogger<LicenseWorker> logger) : BackgroundService
{
    private DateTimeOffset _nextCheckin = DateTimeOffset.MinValue;
    private int _failures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (readiness.IsReady)
                {
                    await TickAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }

            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var status = await dispatcher.Send(new RefreshLicenseCommand(), cancellationToken);
        var now = time.GetUtcNow();
        if (status.IsFailure || !installation.IsSetupCompleted || !cloud.IsConfigured || status.Value.LicenseKeyPrefix is null
            || status.Value.State == LicenseStates.Demo || now < _nextCheckin)
        {
            return;
        }

        var kind = _nextCheckin == DateTimeOffset.MinValue ? "STARTUP" : "SCHEDULED";
        var last = status.Value.LastCheckinAt;
        if (kind == "STARTUP" && last is { } l && now - l < TimeSpan.FromHours(options.CheckinHours))
        {
            _nextCheckin = l + Interval();
            return;
        }

        var result = await dispatcher.Send(new CheckLicenseNowCommand(kind), cancellationToken);
        var ok = result.IsSuccess && result.Value.LastCheckinError is null;
        _failures = ok ? 0 : _failures + 1;
        _nextCheckin = now + (ok ? Interval() : TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(_failures - 1, 6)))));
    }

    private TimeSpan Interval() =>
        TimeSpan.FromHours(options.CheckinHours) + TimeSpan.FromMinutes(Random.Shared.Next(-120, 121));

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló el proceso de la licencia.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

public static class LicensingInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        // La configuración se lee al construir los servicios (la de las pruebas y la del instalador llegan después del registro).
        services.AddSingleton(sp => Read(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<LicenseStateCache>();
        services.AddSingleton<ILicenseGate>(sp => sp.GetRequiredService<LicenseStateCache>());
        services.AddSingleton<ILicenseStatus>(sp => sp.GetRequiredService<LicenseStateCache>());
        // Las claves y la huella de desarrollo nunca valen en producción (D12B-04).
        services.AddSingleton<ILicenseKeys>(sp => new EmbeddedLicenseKeys(
            sp.GetRequiredService<IHostEnvironment>().IsProduction() ? [] : sp.GetRequiredService<LicensingOptions>().DevelopmentTrustedKeys));
        services.AddSingleton<IDeviceIdentity>(sp => new WindowsDeviceIdentity(
            sp.GetRequiredService<IHostEnvironment>().IsProduction() ? null : sp.GetRequiredService<LicensingOptions>().DevelopmentFingerprint));
        services.AddSingleton<ILicenseCloud>(sp => new LicenseCloudClient(
            Uri.TryCreate(sp.GetRequiredService<LicensingOptions>().ServerUrl, UriKind.Absolute, out var url)
                ? new HttpClient { BaseAddress = new Uri(url.ToString().TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) }
                : null,
            sp.GetRequiredService<ILogger<LicenseCloudClient>>()));
        services.AddScoped<LicenseStateStore>();
        services.AddScoped<ILicenseStateStore>(sp => sp.GetRequiredService<LicenseStateStore>());
        services.AddScoped<LicenseService>();
        services.AddSingleton<LicenseWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<LicenseWorker>());
    }

    private static LicensingOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(LicensingOptions.SectionName);
        return new LicensingOptions
        {
            ServerUrl = section[nameof(LicensingOptions.ServerUrl)],
            CheckinHours = int.TryParse(section[nameof(LicensingOptions.CheckinHours)], out var hours) && hours is >= 1 and <= 168 ? hours : 24,
            DevelopmentTrustedKeys = [.. section.GetSection(nameof(LicensingOptions.DevelopmentTrustedKeys)).GetChildren().Select(c => c.Value).OfType<string>()],
            DevelopmentFingerprint = section[nameof(LicensingOptions.DevelopmentFingerprint)],
        };
    }
}
