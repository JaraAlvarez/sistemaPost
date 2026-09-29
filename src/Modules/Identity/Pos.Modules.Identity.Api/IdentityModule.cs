using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure;
using Pos.Modules.Identity.Application;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Identity.Infrastructure;

namespace Pos.Modules.Identity.Api;

/// <summary>Módulo Identity. Fase 2: estructura RBAC y catálogo de permisos; el login llega en la Fase 3.</summary>
public sealed class IdentityModule : IModule
{
    public string Name => "identity";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IIdentityStore).Assembly);
        IdentityInfrastructureRegistration.Register(services);
        services.AddSingleton<IPermissionCatalogProvider, IdentityPermissionCatalog>();
        services.AddScoped<IIdentityProvisioning, IdentityProvisioning>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/identity").WithTags("Identidad");

        group.MapGet("/permissions", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ListPermissionsQuery(), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.PermissionView)
            .WithSummary("Catálogo de permisos");

        group.MapGet("/roles", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ListRolesQuery(), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.PermissionView)
            .WithSummary("Roles de la empresa con sus permisos");
    }
}
