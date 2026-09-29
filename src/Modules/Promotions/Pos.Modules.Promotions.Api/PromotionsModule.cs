using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure;
using Pos.Modules.Promotions.Application;
using Pos.Modules.Promotions.Contracts;
using Pos.Modules.Promotions.Domain;
using Pos.Modules.Promotions.Infrastructure;
using Pos.Modules.Sales.Contracts;

namespace Pos.Modules.Promotions.Api;

/// <summary>Módulo Promotions (Fase 7, bloque 7.4): promociones automáticas administradas por el encargado.</summary>
public sealed class PromotionsModule : IModule
{
    public string Name => "promotions";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IPromotionStore).Assembly);
        PromotionsInfrastructureRegistration.Register(services);
        services.AddSingleton<IPermissionCatalogProvider, PromotionsPermissionCatalog>();
        services.AddScoped<IActivePromotions, ActivePromotions>();
        services.AddScoped<PromotionViews>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/promotions").WithTags("Promociones");

        group.MapGet("/", async (PromotionStatus? status, IDispatcher d, CancellationToken ct) => (await d.Send(new ListPromotionsQuery(status), ct)).ToHttpResult())
            .RequirePermission(PromotionsPermissions.PromotionView)
            .WithSummary("Promociones (inEffectNow: rige ahora en esta sucursal)");
        group.MapGet("/{promotionId:guid}", async (Guid promotionId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetPromotionQuery(promotionId), ct)).ToHttpResult())
            .RequirePermission(PromotionsPermissions.PromotionView);
        group.MapGet("/report", async (DateOnly from, DateOnly to, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PromotionReportQuery(from, to), ct)).ToHttpResult())
            .RequirePermission(PromotionsPermissions.PromotionView)
            .WithSummary("Ventas, unidades y descuento por promoción entre dos fechas de negocio");

        group.MapPost("/", async (PromotionRequest request, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreatePromotionCommand(request), ct)).ToCreatedResult(p => $"/api/v1/promotions/{p.Id}"))
            .RequirePermission(PromotionsPermissions.PromotionManage)
            .WithSummary("Promoción en borrador: lleve N pague M, precio especial, porcentaje, precio por cantidad o combo");
        group.MapPut("/{promotionId:guid}", async (Guid promotionId, PromotionRequest request, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdatePromotionCommand(promotionId, request), ct)).ToHttpResult())
            .RequirePermission(PromotionsPermissions.PromotionManage)
            .WithSummary("Modifica una promoción en borrador (una activa no se edita)");
        group.MapPost("/{promotionId:guid}/simulate", async (Guid promotionId, IReadOnlyList<SimulationLineRequest> lines, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SimulatePromotionQuery(promotionId, lines), ct)).ToHttpResult())
            .RequirePermission(PromotionsPermissions.PromotionManage)
            .WithSummary("Simula una venta de ejemplo con esta promoción (mismo motor de la caja)");
        group.MapPost("/{promotionId:guid}/activate", async (Guid promotionId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangePromotionStatusCommand(promotionId, PromotionTransition.Activate), ct)).ToHttpResult())
            .RequirePermission(PromotionsPermissions.PromotionManage);
        group.MapPost("/{promotionId:guid}/pause", async (Guid promotionId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangePromotionStatusCommand(promotionId, PromotionTransition.Pause), ct)).ToHttpResult())
            .RequirePermission(PromotionsPermissions.PromotionManage);
        group.MapPost("/{promotionId:guid}/end", async (Guid promotionId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangePromotionStatusCommand(promotionId, PromotionTransition.End), ct)).ToHttpResult())
            .RequirePermission(PromotionsPermissions.PromotionManage);
    }
}
