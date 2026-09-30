using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure;
using Pos.Modules.Licensing.Application;
using Pos.Modules.Licensing.Contracts;
using Pos.Modules.Licensing.Infrastructure;

namespace Pos.Modules.Licensing.Api;

/// <summary>
/// Módulo Licensing (Fase 12-B): licencia dentro del POS. Activación con la clave, verificación diaria con el servidor de licencias,
/// estados locales calculados (DEMO, VALID, GRACE, RESTRICTED…) y restricciones en el backend. Nunca detiene una jornada abierta ni
/// bloquea consultas, reportes, exportación o backups (RN-LIC-01/04).
/// </summary>
public sealed class LicensingModule : IModule
{
    public string Name => "licensing";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(LicenseService).Assembly);
        LicensingInfrastructureRegistration.Register(services);
        services.AddSingleton<IPermissionCatalogProvider, LicensePermissionCatalog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/license").WithTags("Licencia");

        group.MapGet("/", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetLicenseStatusQuery(), ct)).ToHttpResult())
            .RequirePermission(LicensePermissions.View)
            .WithSummary("Estado de la licencia de este equipo, días restantes, avisos y última verificación");

        group.MapGet("/checkins", async (int? limit, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListLicenseCheckinsQuery(limit ?? 50), ct)).ToHttpResult())
            .RequirePermission(LicensePermissions.View)
            .WithSummary("Historial de activaciones, verificaciones y liberaciones con el servidor de licencias");

        group.MapPost("/activate", async (ActivateLicenseRequest request, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ActivateLicenseCommand(request.LicenseKey), ct)).ToHttpResult())
            .RequirePermission(LicensePermissions.Manage)
            .WithSummary("Activa la licencia con la clave POS-XXXXX-XXXXX-XXXXX-XXXXX (requiere Internet)");

        group.MapPost("/check", async (IDispatcher d, CancellationToken ct) => (await d.Send(new CheckLicenseNowCommand(), ct)).ToHttpResult())
            .RequirePermission(LicensePermissions.Check)
            .WithSummary("Verifica ahora con el servidor de licencias (renovaciones, suspensiones, hora confiable)");

        group.MapPost("/deactivate", async (DeactivateLicenseRequest request, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeactivateLicenseCommand(request.Reason), ct)).ToHttpResult())
            .RequirePermission(LicensePermissions.Manage)
            .WithSummary("Libera este equipo de la licencia (antes de cambiar de computador); queda restringido hasta activar de nuevo");
    }
}

public sealed record ActivateLicenseRequest(string LicenseKey);

public sealed record DeactivateLicenseRequest(string? Reason);
