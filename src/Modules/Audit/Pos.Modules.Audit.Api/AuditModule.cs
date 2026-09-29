using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure;
using Pos.Modules.Audit.Application;
using Pos.Modules.Audit.Contracts;
using Pos.Modules.Audit.Infrastructure;

namespace Pos.Modules.Audit.Api;

/// <summary>Módulo Audit: consulta de la bitácora y verificación de integridad (la captura vive en la infraestructura).</summary>
public sealed class AuditModule : IModule
{
    public string Name => "audit";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IAuditReadModel).Assembly);
        AuditInfrastructureRegistration.Register(services);
        services.AddSingleton<IPermissionCatalogProvider, AuditPermissionCatalog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/audit").WithTags("Auditoría");

        group.MapGet("/logs", async (
                    string? entityType, Guid? entityId, Guid? userId, string? module, string? action,
                    DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SearchAuditLogQuery(entityType, entityId, userId, module, action, from, to, page ?? 1, pageSize ?? 50), ct))
                .ToHttpResult())
            .RequirePermission(AuditPermissions.LogView)
            .WithSummary("Bitácora de auditoría (historial de un registro, actividad de un usuario…)");

        group.MapPost("/verify", async (IDispatcher d, CancellationToken ct) => (await d.Send(new VerifyAuditQuery(), ct)).ToHttpResult())
            .RequirePermission(AuditPermissions.LogVerify)
            .WithSummary("Verifica filas, sellos y cadena; devuelve el código del último sello");

        group.MapGet("/seals/{sealNo:long}/check", async (long sealNo, string code, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CheckSealQuery(sealNo, code), ct)).ToHttpResult())
            .RequirePermission(AuditPermissions.LogVerify)
            .WithSummary("Comprueba el sello impreso en un reporte Z (número y código) contra la bitácora");
    }
}

internal sealed class AuditPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => AuditPermissions.All;
}
