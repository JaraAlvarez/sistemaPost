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
using Pos.Modules.Cash.Application;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Cash.Infrastructure;

namespace Pos.Modules.Cash.Api;

/// <summary>Módulo Cash: medios de pago (Fase 5); jornadas, movimientos, arqueos y cierres (Fase 6).</summary>
public sealed class CashModule : IModule
{
    public string Name => "cash";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(ICashStore).Assembly);
        CashInfrastructureRegistration.Register(services);
        services.AddScoped<ICompanyInitializer, CashInitializer>();
        services.AddSingleton<IPermissionCatalogProvider, CashPermissionCatalog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/cash").WithTags("Caja");

        group.MapGet("/payment-methods", async (bool? includeInactive, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListPaymentMethodsQuery(includeInactive ?? false), ct)).ToHttpResult())
            .RequireAuthenticatedUser()
            .WithSummary("Medios de pago de la empresa (efectivo, tarjetas, transferencias, billeteras, bonos)");

        group.MapPost("/payment-methods", async (CreatePaymentMethodCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(m => $"/api/v1/cash/payment-methods/{m.Id}"))
            .RequirePermission(CashPermissions.PaymentMethodManage);

        group.MapPut("/payment-methods/{paymentMethodId:guid}", async (Guid paymentMethodId, UpdatePaymentMethodRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdatePaymentMethodCommand(paymentMethodId, r.Name, r.DianCode, r.RequiresReference, r.SortOrder, r.IsActive), ct)).ToHttpResult())
            .RequirePermission(CashPermissions.PaymentMethodManage);
    }
}

public sealed record UpdatePaymentMethodRequest(string Name, string? DianCode, bool RequiresReference, int SortOrder, bool IsActive);
