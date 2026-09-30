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
    IReadOnlyList<UpdateHistoryEntry> History);

internal static class UpdatesEndpoints
{
    public static RouteGroupBuilder MapUpdateEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/system/updates").WithTags("Sistema");

        group.MapGet("/", (ProductPaths paths) =>
            {
                var state = UpdateFiles.ReadState(paths.DataRoot);
                var history = UpdateFiles.ReadHistory(paths.DataRoot);
                return Results.Ok(new UpdateStatusDto(
                    ProductInfo.Version, state?.AvailableVersion, state?.AvailableNotes, state?.Ready ?? false, state?.NextWindow, state?.LastCheckAt,
                    state?.LastError, File.Exists(Path.Combine(UpdateFiles.Directory(paths.DataRoot), UpdateFiles.InstallNowRequest)),
                    [.. history.OrderByDescending(h => h.At).Take(30)]));
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
        group.MapGet("/manifest", (ProductPaths paths) =>
            {
                var path = Path.Combine(UpdateFiles.Directory(paths.DataRoot), UpdateFiles.InstalledManifest);
                return File.Exists(path) ? Results.File(path, "application/json") : Results.NotFound();
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
