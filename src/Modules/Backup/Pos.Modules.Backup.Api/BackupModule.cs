using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure;
using Pos.Modules.Backup.Application;
using Pos.Modules.Backup.Contracts;
using Pos.Modules.Backup.Infrastructure;

namespace Pos.Modules.Backup.Api;

/// <summary>
/// Módulo Backup (Fase 11): backups cifrados programados, al cierre y manuales; destinos (carpeta, USB, red, nube S3); retención;
/// verificación y restauración de prueba; código de recuperación y alertas. Restaurar se hace desde la consola del servidor.
/// </summary>
public sealed class BackupModule : IModule
{
    public string Name => "backup";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IBackupStore).Assembly);
        BackupInfrastructureRegistration.Register(services);
        services.AddScoped<ICompanyInitializer, BackupInitializer>();
        services.AddSingleton<IPermissionCatalogProvider, BackupPermissionCatalog>();
        services.AddSingleton<ISettingDefinitionProvider, BackupSettingsProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/backups").WithTags("Backups");

        group.MapGet("/", async (int? limit, IDispatcher d, CancellationToken ct) => (await d.Send(new ListBackupsQuery(limit ?? 50), ct)).ToHttpResult())
            .RequirePermission(BackupPermissions.View)
            .WithSummary("Historial de backups con sus copias en cada destino");

        group.MapPost("/", async (IDispatcher d, CancellationToken ct) =>
            {
                var result = await d.Send(new RunBackupNowCommand(), ct);
                return result.IsFailure ? result.Error.ToProblem() : Results.Accepted(value: new { message = result.Value });
            })
            .RequirePermission(BackupPermissions.Run)
            .WithSummary("Respaldar ahora (se pone en cola; el resultado aparece en el historial)");

        group.MapPost("/{runId:guid}/verify", async (Guid runId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new VerifyBackupQuery(runId), ct)).ToHttpResult())
            .RequirePermission(BackupPermissions.Run)
            .WithSummary("Verifica el paquete: descifra, huellas del manifiesto y volcado legible");

        group.MapGet("/{runId:guid}/download", async (Guid runId, IDispatcher d, CancellationToken ct) =>
            {
                var result = await d.Send(new DownloadBackupCommand(runId), ct);
                return result.IsFailure
                    ? result.Error.ToProblem()
                    : Results.File(result.Value, "application/octet-stream", Path.GetFileName(result.Value));
            })
            .RequirePermission(BackupPermissions.View)
            .WithSummary("Descarga el paquete cifrado (.posbak); queda en la auditoría");

        group.MapGet("/restore-tests", async (int? limit, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListRestoreTestsQuery(limit ?? 20), ct)).ToHttpResult())
            .RequirePermission(BackupPermissions.View)
            .WithSummary("Restauraciones de prueba semanales");

        group.MapGet("/alerts", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetBackupAlertsQuery(), ct)).ToHttpResult())
            .RequirePermission(BackupPermissions.View)
            .WithSummary("Alertas: último backup viejo, destinos fallando, prueba fallida, código sin confirmar");

        group.MapGet("/destinations", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListDestinationsQuery(), ct)).ToHttpResult())
            .RequirePermission(BackupPermissions.View)
            .WithSummary("Destinos configurados (la clave secreta de S3 nunca se devuelve)");

        group.MapPost("/destinations", async (DestinationInput input, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SaveDestinationCommand(null, input), ct)).ToCreatedResult(x => $"/api/v1/backups/destinations/{x.Id}"))
            .RequirePermission(BackupPermissions.DestinationConfigure)
            .WithSummary("Agrega un destino: EXTERNAL (USB), NETWORK (carpeta de red) o S3 (MinIO en el VPS)");

        group.MapPut("/destinations/{destinationId:guid}", async (Guid destinationId, DestinationInput input, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SaveDestinationCommand(destinationId, input), ct)).ToHttpResult())
            .RequirePermission(BackupPermissions.DestinationConfigure)
            .WithSummary("Modifica un destino (secreto vacío = conserva el actual)");

        group.MapPost("/destinations/{destinationId:guid}/test", async (Guid destinationId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new TestDestinationQuery(destinationId), ct)).ToHttpResult())
            .RequirePermission(BackupPermissions.DestinationConfigure)
            .WithSummary("Prueba el destino: escribe, lee y borra un archivo pequeño");

        group.MapPost("/recovery-code", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GenerateRecoveryCodeCommand(), ct)).ToHttpResult())
            .RequirePermission(BackupPermissions.RecoveryManage)
            .WithSummary("Genera o regenera el código de recuperación (se muestra UNA sola vez)");

        group.MapPost("/recovery-code/confirm", async (ConfirmCodeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ConfirmRecoveryCodeCommand(r.Code), ct)).ToHttpResult())
            .RequirePermission(BackupPermissions.RecoveryManage)
            .WithSummary("El propietario escribe el código de nuevo para confirmar que lo guardó");
    }
}

public sealed record ConfirmCodeRequest(string Code);
