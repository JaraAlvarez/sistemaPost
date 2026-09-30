using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure;
using Pos.Modules.Audit.Application;
using Pos.Modules.Audit.Contracts;
using Pos.Modules.Audit.Infrastructure;

namespace Pos.Modules.Audit.Api;

/// <summary>
/// Módulo Audit: consulta de la bitácora (búsqueda, historial de un registro, actividad de un usuario), verificación de integridad
/// (manual y programada), incidentes y constancia de integridad. La captura vive en la infraestructura.
/// </summary>
public sealed class AuditModule : IModule
{
    public string Name => "audit";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IAuditReadModel).Assembly);
        AuditInfrastructureRegistration.Register(services);
        services.AddSingleton<IPermissionCatalogProvider, AuditPermissionCatalog>();
        services.AddSingleton<ISettingDefinitionProvider, AuditSettingsProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/audit").WithTags("Auditoría");

        group.MapGet("/logs", async (
                    string? entityType, Guid? entityId, Guid? userId, string? module, string? action,
                    DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize, string? severity, Guid? terminalId, Guid? authorizedBy, string? q,
                    IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SearchAuditLogQuery(entityType, entityId, userId, module, action, from, to, page ?? 1, pageSize ?? 50,
                    severity, terminalId, authorizedBy, q), ct)).ToHttpResult())
            .RequirePermission(AuditPermissions.LogView)
            .WithSummary("Bitácora con filtros (severidad, caja, autorizador, texto) y cambios campo a campo en español");

        group.MapGet("/entities/{entityType}/{entityId:guid}/history", async (string entityType, Guid entityId, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new EntityHistoryQuery(entityType, entityId, page ?? 1, pageSize ?? 100), ct)).ToHttpResult())
            .RequirePermission(AuditPermissions.LogView)
            .WithSummary("Historial completo de un registro en orden cronológico, con las diferencias de cada cambio");

        group.MapGet("/users/{userId:guid}/activity", async (Guid userId, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UserActivityQuery(userId, from, to, page ?? 1, pageSize ?? 100), ct)).ToHttpResult())
            .RequirePermission(AuditPermissions.LogView)
            .WithSummary("Actividad de un usuario: lo que hizo y lo que autorizó como supervisor");

        group.MapGet("/actions", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListAuditActionsQuery(), ct)).ToHttpResult())
            .RequirePermission(AuditPermissions.LogView)
            .WithSummary("Catálogo de acciones: código, módulo, nombre en español y severidad");

        group.MapPost("/verify", async (IDispatcher d, CancellationToken ct) => (await d.Send(new VerifyAuditCommand(), ct)).ToHttpResult())
            .RequirePermission(AuditPermissions.LogVerify)
            .WithSummary("Verificación manual completa (filas, sellos y cadena); queda registrada y abre un incidente si hay hallazgos nuevos");

        group.MapGet("/verifications", async (int? limit, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListVerificationsQuery(limit ?? 50), ct)).ToHttpResult())
            .RequirePermission(AuditPermissions.LogVerify)
            .WithSummary("Historial de verificaciones (diarias, semanales y manuales)");

        group.MapGet("/incidents", async (bool? onlyOpen, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListIncidentsQuery(onlyOpen ?? false), ct)).ToHttpResult())
            .RequirePermission(AuditPermissions.LogVerify)
            .WithSummary("Incidentes de integridad con sus hallazgos y su reconocimiento");

        group.MapPost("/incidents/{incidentId:guid}/acknowledge", async (Guid incidentId, AcknowledgeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AcknowledgeIncidentCommand(incidentId, r.Note), ct)).ToHttpResult())
            .RequirePermission(AuditPermissions.IncidentAcknowledge)
            .WithSummary("El propietario reconoce el incidente con una nota (qué revisó y qué encontró)");

        group.MapGet("/integrity-certificate", async (IDispatcher d, CancellationToken ct) =>
            {
                var result = await d.Send(new IssueIntegrityCertificateCommand(), ct);
                return result.IsFailure ? result.Error.ToProblem() : Results.File(result.Value.Content, "application/pdf", result.Value.FileName);
            })
            .RequirePermission(AuditPermissions.LogVerify)
            .WithSummary("Constancia de integridad en PDF con el último sello (guárdela fuera del equipo)");

        group.MapGet("/seals/{sealNo:long}/check", async (long sealNo, string code, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CheckSealQuery(sealNo, code), ct)).ToHttpResult())
            .RequirePermission(AuditPermissions.LogVerify)
            .WithSummary("Comprueba el sello impreso en un reporte Z (número y código) contra la bitácora");
    }
}

public sealed record AcknowledgeRequest(string Note);
