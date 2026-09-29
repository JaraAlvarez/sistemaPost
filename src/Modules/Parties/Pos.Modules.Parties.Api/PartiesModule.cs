using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure;
using Pos.Modules.Parties.Application;
using Pos.Modules.Parties.Contracts;
using Pos.Modules.Parties.Infrastructure;

namespace Pos.Modules.Parties.Api;

/// <summary>Módulo Parties: terceros (personas naturales y jurídicas) y sus contactos.</summary>
public sealed class PartiesModule : IModule
{
    public string Name => "parties";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IPartyStore).Assembly);
        PartiesInfrastructureRegistration.Register(services);
        services.AddScoped<PartyReferenceGuard>();
        services.AddScoped<ICompanyInitializer, PartiesInitializer>();
        services.AddSingleton<IPermissionCatalogProvider, PartiesPermissionCatalog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/parties").WithTags("Terceros");

        group.MapGet("/", async (string? search, bool? includeInactive, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SearchPartiesQuery(search, includeInactive ?? false, page ?? 1, pageSize ?? 50), ct)).ToHttpResult())
            .RequirePermission(PartiesPermissions.PartyView)
            .WithSummary("Busca terceros por nombre (sin tildes) o por número de identificación (con o sin DV)");

        group.MapGet("/by-identification", async (string type, string number, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new FindPartyByIdentificationQuery(type, number), ct)).ToHttpResult())
            .RequirePermission(PartiesPermissions.PartyView)
            .WithSummary("Tercero por tipo y número de identificación exactos");

        group.MapGet("/{partyId:guid}", async (Guid partyId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetPartyQuery(partyId), ct)).ToHttpResult())
            .RequirePermission(PartiesPermissions.PartyView);

        group.MapPost("/", async (PartyInput party, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreatePartyCommand(party), ct)).ToCreatedResult(p => $"/api/v1/parties/{p.Id}"))
            .RequirePermission(PartiesPermissions.PartyManage)
            .WithSummary("Crea un tercero (NIT con DV validado; identificación única por empresa)");

        group.MapPut("/{partyId:guid}", async (Guid partyId, PartyInput party, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdatePartyCommand(partyId, party), ct)).ToHttpResult())
            .RequirePermission(PartiesPermissions.PartyManage)
            .WithSummary("Modifica el tercero (los contactos se reemplazan si se envían)");

        group.MapPost("/{partyId:guid}/activate", async (Guid partyId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetPartyActiveCommand(partyId, true), ct)).ToHttpResult())
            .RequirePermission(PartiesPermissions.PartyManage);

        group.MapPost("/{partyId:guid}/deactivate", async (Guid partyId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetPartyActiveCommand(partyId, false), ct)).ToHttpResult())
            .RequirePermission(PartiesPermissions.PartyManage);
    }
}
