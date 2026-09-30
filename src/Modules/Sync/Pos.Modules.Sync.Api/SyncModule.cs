using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Sync.Application;
using Pos.Modules.Sync.Contracts;
using Pos.Modules.Sync.Infrastructure;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Sync.Api;

/// <summary>
/// Módulo Sync (Fase 16): sube ventas, cierres de caja, existencias y productos a la nube en cuanto hay Internet, y exporta el paquete
/// <c>.possync</c> cifrado para cargarlo en el portal cuando no lo hay. No usa el pipeline de comandos: el envío es HTTP largo y cada paso
/// se guarda por separado (el cursor avanza solo con el acuse).
/// </summary>
public sealed class SyncModule : IModule
{
    public string Name => "sync";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        SyncInfrastructureRegistration.Register(services);
        services.AddSingleton<IPermissionCatalogProvider, SyncPermissionCatalog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/sync").WithTags("Sincronización");

        group.MapGet("/", async (SyncService sync, CancellationToken ct) => Results.Ok(await sync.StatusAsync(ct)))
            .RequirePermission(SyncPermissions.View)
            .WithSummary("Estado: datos pendientes por tipo, último acuse de la nube y lotes recientes");

        group.MapPost("/push", async (SyncService sync, CancellationToken ct) =>
                Results.Ok(new { confirmed = await sync.PushAsync(maxBatches: 50, ct) }))
            .RequirePermission(SyncPermissions.Manage)
            .WithSummary("Sincroniza ahora con la nube (lo mismo que hace el proceso cada minuto)");

        group.MapGet("/export", async (SyncService sync, CancellationToken ct) =>
                await sync.ExportAsync(ct) is { } file
                    ? Results.File(file.Content, "application/octet-stream", file.FileName)
                    : Error.BusinessRule("SYNC.NO_CLOUD_KEY", "Falta la clave pública de sincronización de la nube: no se puede cifrar el paquete.").ToProblem())
            .RequirePermission(SyncPermissions.Manage)
            .WithSummary("Descarga el paquete .possync cifrado con lo que aún no subió (para cargarlo en el portal desde otro equipo)");
    }
}
