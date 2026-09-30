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
using Pos.Modules.Reporting.Application;
using Pos.Modules.Reporting.Contracts;
using Pos.Modules.Reporting.Infrastructure;

namespace Pos.Modules.Reporting.Api;

/// <summary>
/// Módulo Reporting (Fase 9): reportes de solo lectura sobre las vistas <c>reporting.*</c>, con un catálogo en el código, un endpoint
/// genérico con JSON paginado y exportación a Excel, CSV y PDF (auditada), y el tablero del día.
/// </summary>
public sealed class ReportingModule : IModule
{
    private static readonly HashSet<string> ReservedQueryKeys = new(StringComparer.OrdinalIgnoreCase) { "format", "page", "pageSize" };

    public string Name => "reporting";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(ReportEngine).Assembly);
        ReportingInfrastructureRegistration.Register(services);
        services.AddSingleton<IPermissionCatalogProvider, ReportingPermissionCatalog>();
        services.AddSingleton<ISettingDefinitionProvider, ReportingSettingsProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/reports").WithTags("Reportes");

        group.MapGet("/", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListReportsQuery(), ct)).ToHttpResult())
            .RequireAuthenticatedUser()
            .WithSummary("Catálogo de reportes que el usuario puede ver, con sus parámetros y columnas");

        group.MapGet("/dashboard", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetDashboardQuery(), ct)).ToHttpResult())
            .RequirePermission(ReportingPermissions.SalesBasic)
            .WithSummary("Tablero del día: ventas vs. la semana anterior, por hora, top 10 y alertas de caja, inventario y datos personales");

        // El permiso depende del reporte: lo verifica el motor (403 si no lo tiene). Exportar exige además reporting.report.export.
        group.MapGet("/{code}", async (string code, string? format, int? page, int? pageSize, HttpRequest http, IDispatcher d, CancellationToken ct) =>
            {
                var arguments = http.Query
                    .Where(q => !ReservedQueryKeys.Contains(q.Key))
                    .ToDictionary(q => q.Key, q => (string?)q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                if (!TryParseFormat(format, out var parsed))
                {
                    return ReportingErrors.InvalidFormat.ToProblem();
                }

                if (parsed == ReportFormat.Json)
                {
                    return (await d.Send(new RunReportQuery(code, arguments, page ?? 1, pageSize ?? 200), ct)).ToHttpResult();
                }

                var file = await d.Send(new ExportReportCommand(code, arguments, parsed), ct);
                return file.IsFailure ? file.Error.ToProblem() : Results.File(file.Value.Content, file.Value.ContentType, file.Value.FileName);
            })
            .RequireAuthenticatedUser()
            .WithSummary("Ejecuta un reporte: JSON paginado (format=json) o archivo xlsx, csv o pdf (auditado)");
    }

    private static bool TryParseFormat(string? format, out ReportFormat parsed)
    {
        parsed = ReportFormat.Json;
        return string.IsNullOrWhiteSpace(format) || Enum.TryParse(format, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }
}
