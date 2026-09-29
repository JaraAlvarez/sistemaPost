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
using Pos.Modules.Billing.Application;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Billing.Domain;
using Pos.Modules.Billing.Infrastructure;

namespace Pos.Modules.Billing.Api;

/// <summary>Módulo Billing (Fase 7): comprobantes de venta y, en la Fase 11-B, documentos electrónicos con Factus.</summary>
public sealed class BillingModule : IModule
{
    public string Name => "billing";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IFiscalDocumentStore).Assembly);
        BillingInfrastructureRegistration.Register(services);
        services.AddSingleton<IPermissionCatalogProvider, BillingPermissionCatalog>();
        services.AddSingleton<ISettingDefinitionProvider, BillingSettingsProvider>();
        services.AddSingleton<IFiscalProvider, NullFiscalProvider>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<FiscalSubmissionService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/billing/documents").WithTags("Facturación");

        group.MapGet("/", async (DateOnly? from, DateOnly? to, FiscalStatus? status, int? limit, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListFiscalDocumentsQuery(from, to, status, limit), ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.DocumentView)
            .WithSummary("Comprobantes y documentos fiscales de la sucursal con sus eventos");
        group.MapGet("/{documentId:guid}", async (Guid documentId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetFiscalDocumentQuery(documentId), ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.DocumentView);
        group.MapPost("/{documentId:guid}/retry", async (Guid documentId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RetryFiscalDocumentCommand(documentId), ct)).ToHttpResult())
            .RequirePermission(BillingPermissions.DocumentManage)
            .WithSummary("Reintenta el envío de un documento electrónico pendiente, con error o rechazado");
    }
}
