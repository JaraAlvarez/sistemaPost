using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.Infrastructure;
using Pos.Modules.Reference.Application;
using Pos.Modules.Reference.Infrastructure;

namespace Pos.Modules.Reference.Api;

/// <summary>Módulo Reference: catálogos oficiales de Colombia (DIVIPOLA, tipos de identificación DIAN…). Solo lectura.</summary>
public sealed class ReferenceModule : IModule
{
    public string Name => "reference";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IReferenceReadModel).Assembly);
        ReferenceInfrastructureRegistration.Register(services);
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        // Catálogos públicos para cualquier usuario autenticado (y para el asistente inicial, que aún no tiene usuarios).
        var group = api.MapGroup("/reference").WithTags("Catálogos de referencia");

        group.MapGet("/countries", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListCountriesQuery(), ct)).ToHttpResult())
            .AllowAnonymousByDesign("Catálogo oficial requerido por el asistente inicial.");

        group.MapGet("/departments", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListDepartmentsQuery(), ct)).ToHttpResult())
            .AllowAnonymousByDesign("Catálogo oficial requerido por el asistente inicial.");

        group.MapGet("/municipalities", async (string? departmentCode, string? search, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListMunicipalitiesQuery(departmentCode, search), ct)).ToHttpResult())
            .AllowAnonymousByDesign("Catálogo oficial requerido por el asistente inicial.");

        group.MapGet("/identification-types", async (IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListIdentificationTypesQuery(), ct)).ToHttpResult())
            .AllowAnonymousByDesign("Catálogo oficial requerido por el asistente inicial.");

        group.MapGet("/fiscal-responsibilities", async (IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListFiscalResponsibilitiesQuery(), ct)).ToHttpResult())
            .AllowAnonymousByDesign("Catálogo oficial requerido por el asistente inicial.");

        group.MapGet("/tax-regimes", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListTaxRegimesQuery(), ct)).ToHttpResult())
            .AllowAnonymousByDesign("Catálogo oficial requerido por el asistente inicial.");
    }
}
