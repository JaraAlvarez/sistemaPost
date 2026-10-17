using System.Net;
using System.Net.Sockets;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Persistence;
using Pos.Server.Host.Configuration;
using Pos.Updates.Contracts;

namespace Pos.Server.Host.Updates;

/// <summary>Permisos de las actualizaciones (docs/fases/fase-13-propuesta.md §7).</summary>
public static class UpdatePermissions
{
    public const string View = "system.update.view";
    public const string Manage = "system.update.manage";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(View, "Ver la versión instalada, la actualización pendiente y el historial de actualizaciones", isSensitive: false),
        new(Manage, "Instalar ahora una actualización descargada (sin esperar la ventana nocturna)", isSensitive: true),
    ];
}

internal sealed class UpdatePermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => UpdatePermissions.All;
}

/// <summary>Estado de las actualizaciones para el propietario.</summary>
public sealed record UpdateStatusDto(
    string InstalledVersion,
    string? AvailableVersion,
    string? AvailableNotes,
    bool ReadyToInstall,
    DateTimeOffset? NextWindow,
    DateTimeOffset? LastCheckAt,
    string? LastError,
    bool InstallRequested,
    IReadOnlyList<UpdateHistoryEntry> History,
    string? MinimumTerminalVersion = null,
    IReadOnlyList<string>? OutdatedTerminals = null,
    bool NeedsSupport = false);

/// <summary>ALTA 4: manifiesto de la versión instalada y las cajas que tienen una versión anterior a la mínima que exige.</summary>
internal static class TerminalVersions
{
    /// <summary>Cabecera con la que el endpoint del manifiesto de las cajas propaga "instalar ahora" (con la hora del pedido, ISO 8601).</summary>
    public const string InstallNowHeader = "X-Pos-Install-Now";

    /// <summary>Versión instalada que informa la caja al pedir el manifiesto, y el nombre de su equipo.</summary>
    public const string TerminalVersionHeader = "X-Pos-Terminal-Version";

    public const string TerminalHostHeader = "X-Pos-Terminal-Host";

    /// <summary>Versión de la caja normalizada a <c>MAYOR.MENOR.PARCHE</c> (sin preliminar ni metadatos); <c>null</c> si no es válida.</summary>
    public static string? Normalize(string? version) =>
        SemanticVersion.TryParse(version, out var parsed) ? $"{parsed.Major}.{parsed.Minor}.{parsed.Patch}" : null;

    /// <summary>Hora del pedido "instalar ahora" para las cajas, si sigue vigente (24 h).</summary>
    public static DateTimeOffset? InstallNowRequestedAt(string dataRoot, DateTimeOffset now)
    {
        var path = Path.Combine(UpdateFiles.Directory(dataRoot), UpdateFiles.TerminalsInstallNow);
        if (!File.Exists(path))
        {
            return null;
        }

        var at = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        return now - at < InstallNowLifetime ? at : null;
    }

    /// <summary>Cuánto dura el pedido "instalar ahora" para las cajas (las que estaban apagadas lo reciben al encender).</summary>
    public static readonly TimeSpan InstallNowLifetime = TimeSpan.FromHours(24);

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static UpdateManifest? Installed(string dataRoot)
    {
        var path = Path.Combine(UpdateFiles.Directory(dataRoot), UpdateFiles.InstalledManifest);
        try
        {
            // El actualizador (o el instalador) solo deja aquí un manifiesto que ya verificó; para el aviso basta leer su contenido.
            return File.Exists(path) && UpdateSigning.Deserialize(File.ReadAllText(path)) is { Payload.Length: > 0 } signed
                ? System.Text.Json.JsonSerializer.Deserialize<UpdateManifest>(System.Buffers.Text.Base64Url.DecodeFromChars(signed.Payload), Json)
                : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or FormatException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> Outdated(IEnumerable<(string Hostname, string? AppVersion)> terminals, string? minimum)
    {
        if (!SemanticVersion.TryParse(minimum, out var min))
        {
            return [];
        }

        return [.. terminals
            .Where(t => !SemanticVersion.TryParse(t.AppVersion, out var version) || version < min)
            .Select(t => $"{t.Hostname} ({(string.IsNullOrEmpty(t.AppVersion) ? "versión desconocida" : t.AppVersion)})")
            .Order(StringComparer.Ordinal)];
    }

    public static bool InstallNowRequested(string dataRoot, DateTimeOffset now) => InstallNowRequestedAt(dataRoot, now) is not null;
}

internal static class UpdatesEndpoints
{
    public static RouteGroupBuilder MapUpdateEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/system/updates").WithTags("Sistema");

        group.MapGet("/", async (ProductPaths paths, PosDbContext context, CancellationToken ct) =>
            {
                var state = UpdateFiles.ReadState(paths.DataRoot);
                var history = UpdateFiles.ReadHistory(paths.DataRoot);

                // ALTA 4: aviso del Tablero con las cajas emparejadas que tienen una versión anterior a la mínima de esta versión.
                var minimum = TerminalVersions.Installed(paths.DataRoot)?.MinimumTerminalVersion;
                IReadOnlyList<string> outdated = [];
                if (minimum is not null)
                {
                    var connection = context.Database.GetDbConnection();
                    var terminals = await connection.QueryAsync<(string Hostname, string? AppVersion)>(new CommandDefinition(
                        "SELECT hostname, app_version FROM org.devices WHERE status = 'ACTIVE' AND kind = 'TERMINAL'", cancellationToken: ct));
                    outdated = TerminalVersions.Outdated(terminals, minimum);
                }

                return Results.Ok(new UpdateStatusDto(
                    ProductInfo.Version, state?.AvailableVersion, state?.AvailableNotes, state?.Ready ?? false, state?.NextWindow, state?.LastCheckAt,
                    state?.LastError, File.Exists(Path.Combine(UpdateFiles.Directory(paths.DataRoot), UpdateFiles.InstallNowRequest)),
                    [.. history.OrderByDescending(h => h.At).Take(30)], minimum, outdated, state?.NeedsSupport ?? false));
            })
            .RequirePermission(UpdatePermissions.View)
            .WithSummary("Versión instalada, actualización descargada, próxima ventana e historial (lo publica el actualizador)");

        group.MapPost("/install-now", async (ProductPaths paths, IAuditWriter audit, PosDbContext context, CancellationToken ct) =>
            {
                var state = UpdateFiles.ReadState(paths.DataRoot);
                if (state is not { Ready: true, AvailableVersion: { } version })
                {
                    return Pos.SharedKernel.Results.Error.BusinessRule("UPDATE.NOT_READY", "No hay una actualización descargada y verificada para instalar.").ToProblem();
                }

                Directory.CreateDirectory(UpdateFiles.Directory(paths.DataRoot));
                await File.WriteAllTextAsync(Path.Combine(UpdateFiles.Directory(paths.DataRoot), UpdateFiles.InstallNowRequest), version, ct);
                // ALTA 4: también las cajas instalan sin esperar la madrugada (durante 24 h, para las que estén apagadas).
                await File.WriteAllTextAsync(Path.Combine(UpdateFiles.Directory(paths.DataRoot), UpdateFiles.TerminalsInstallNow), version, ct);
                await audit.WriteAsync(
                    new AuditEntry("system", "UPDATE_INSTALL_REQUESTED", "Update", null, version,
                        $"Se pidió instalar ya la versión {version} (sin esperar la ventana). El actualizador la aplica en el próximo minuto, aunque haya jornadas abiertas.",
                        Severity: AuditSeverity.Warning),
                    ct);
                await context.SaveChangesAsync(ct);
                return Results.Accepted(value: new { message = $"La versión {version} se instalará en el próximo minuto. El servidor se reiniciará." });
            })
            .RequirePermission(UpdatePermissions.Manage)
            .WithSummary("Instala ahora la actualización descargada (el servidor se reinicia; úselo sin cajas vendiendo)");

        // Las cajas se actualizan desde el servidor de la tienda (D13-09): software firmado y público, sin datos del negocio.
        group.MapGet("/manifest", async (ProductPaths paths, HttpContext http, Pos.SharedKernel.Time.IClock clock, PosDbContext context, CancellationToken ct) =>
            {
                // Ronda 3 (4): la caja informa su versión real; se guarda en su registro de org.devices (por el nombre del equipo) para el
                // aviso de cajas desactualizadas. Es informativo: un nombre ajeno solo cambiaría ese aviso.
                if (TerminalVersions.Normalize(http.Request.Headers[TerminalVersions.TerminalVersionHeader]) is { } version
                    && http.Request.Headers[TerminalVersions.TerminalHostHeader].ToString() is { Length: > 0 and <= 100 } hostname)
                {
                    await context.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
                        "UPDATE org.devices SET app_version = @version, last_seen_at = now() " +
                        "WHERE kind = 'TERMINAL' AND status = 'ACTIVE' AND lower(hostname) = lower(@hostname)",
                        new { version, hostname }, cancellationToken: ct));
                }

                var path = Path.Combine(UpdateFiles.Directory(paths.DataRoot), UpdateFiles.InstalledManifest);
                if (!File.Exists(path))
                {
                    return Results.NotFound();
                }

                if (TerminalVersions.InstallNowRequestedAt(paths.DataRoot, clock.UtcNow) is { } requestedAt)
                {
                    // Ronda 3 (6): con la hora del pedido (la caja lo atiende 30 minutos, o al encender).
                    http.Response.Headers[TerminalVersions.InstallNowHeader] = requestedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                }

                return Results.File(path, "application/json");
            })
            .AllowAnonymousByDesign("Manifiesto firmado de la versión instalada: las cajas lo verifican con su clave de confianza.")
            .WithSummary("Manifiesto firmado de la versión instalada (para las cajas)");

        group.MapGet("/package", (ProductPaths paths) =>
            {
                var path = Path.Combine(UpdateFiles.Directory(paths.DataRoot), UpdateFiles.InstalledPackage);
                return File.Exists(path) ? Results.File(path, "application/zip", enableRangeProcessing: true) : Results.NotFound();
            })
            .AllowAnonymousByDesign("Paquete firmado de la versión instalada: la caja comprueba su huella con el manifiesto firmado.")
            .WithSummary("Paquete de la versión instalada (para las cajas)");

        return api;
    }
}

/// <summary>
/// Al arrancar, audita lo que el actualizador dejó en el historial (<c>UPDATE_DOWNLOADED</c>, <c>UPDATE_APPLIED</c>, <c>UPDATE_FAILED</c>,
/// <c>UPDATE_ROLLED_BACK</c>). El actualizador no escribe en la BD: el servidor lo hace con su auditoría encadenada.
/// </summary>
internal sealed class UpdateHistoryAuditor(ProductPaths paths) : IDatabaseReadyHook
{
    private const string Marker = "history.audited";

    public async Task RunAsync(IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        var installation = scopedServices.GetRequiredService<IInstallationContext>();
        if (!installation.IsSetupCompleted)
        {
            return;
        }

        var folder = UpdateFiles.Directory(paths.DataRoot);
        var markerPath = Path.Combine(folder, Marker);
        var last = File.Exists(markerPath) && Guid.TryParse(await File.ReadAllTextAsync(markerPath, cancellationToken), out var id) ? id : Guid.Empty;
        var pending = UpdateFiles.ReadHistory(paths.DataRoot).Where(e => e.Id.CompareTo(last) > 0).OrderBy(e => e.Id).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var context = scopedServices.GetRequiredService<PosDbContext>();
        var audit = scopedServices.GetRequiredService<IAuditWriter>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        foreach (var entry in pending)
        {
            await audit.WriteAsync(
                new AuditEntry("system", entry.Outcome, "Update", null, entry.ToVersion, $"{entry.Detail} ({entry.At:yyyy-MM-dd HH:mm} UTC)",
                    new Dictionary<string, object?> { ["version"] = entry.FromVersion },
                    new Dictionary<string, object?> { ["version"] = entry.ToVersion },
                    Severity: entry.Outcome is UpdateOutcomes.Failed or UpdateOutcomes.RolledBack ? AuditSeverity.Critical : AuditSeverity.Info),
                cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await File.WriteAllTextAsync(markerPath, pending[^1].Id.ToString(), cancellationToken);
    }
}

/// <summary>
/// Responde a la difusión de las cajas en la LAN (D13-07), solo en Multicaja: nombre de la tienda, sucursal, puerto HTTPS y la huella
/// del certificado, que el cajero compara con la que muestra el servidor antes de emparejar.
/// </summary>
internal sealed partial class LanDiscoveryResponder(
    IServiceScopeFactory scopes,
    IServerIdentity identity,
    Microsoft.Extensions.Options.IOptions<PosOptions> options,
    ILogger<LanDiscoveryResponder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (identity.CertificateFingerprint is not { } fingerprint || !options.Value.Server.LanDiscovery)
        {
            return;
        }

        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, LanDiscovery.Port));
        }
        catch (SocketException ex)
        {
            // Otro proceso usa el puerto (p. ej. dos servidores en el mismo equipo): las cajas usan la IP escrita a mano.
            LogFailed(logger, ex.Message);
            return;
        }

        using var _ = udp;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var received = await udp.ReceiveAsync(stoppingToken);
                if (!LanDiscovery.IsRequest(received.Buffer))
                {
                    continue;
                }

                var (store, branch) = await NamesAsync(stoppingToken);
                var response = LanDiscovery.Serialize(new DiscoveryResponse(
                    ProductInfo.Name, store, branch, Environment.MachineName, options.Value.Server.LanHttpsPort, fingerprint, ProductInfo.Version));
                await udp.SendAsync(response, received.RemoteEndPoint, stoppingToken);
            }
            catch (SocketException ex)
            {
                LogFailed(logger, ex.Message);
            }
        }
    }

    private async Task<(string? Store, string? Branch)> NamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var installation = scope.ServiceProvider.GetRequiredService<IInstallationContext>();
            if (installation.BranchId is not { } branchId)
            {
                return (null, null);
            }

            var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            await context.Database.OpenConnectionAsync(cancellationToken);
            var row = await context.Database.GetDbConnection().QuerySingleOrDefaultAsync<(string Store, string Branch)>(new CommandDefinition(
                "SELECT c.trade_name, b.name FROM org.branches b JOIN org.companies c ON c.id = b.company_id WHERE b.id = @branchId",
                new { branchId }, cancellationToken: cancellationToken));
            return (row.Store, row.Branch);
        }
        catch (Npgsql.NpgsqlException)
        {
            return (null, null);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Descubrimiento en la LAN: {Error}")]
    private static partial void LogFailed(ILogger logger, string error);
}
