using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure;
using Pos.Modules.Sales.Application;
using Pos.Modules.Sales.Contracts;
using Pos.Modules.Sales.Infrastructure;

namespace Pos.Modules.Sales.Api;

/// <summary>Módulo Sales (Fase 7): venta en curso, cobro atómico, anulación, cambios de mercancía y tiquete.</summary>
public sealed class SalesModule : IModule
{
    public string Name => "sales";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(ISalesStore).Assembly);
        SalesInfrastructureRegistration.Register(services);
        services.AddSingleton<IPermissionCatalogProvider, SalesPermissionCatalog>();
        services.AddSingleton<ISettingDefinitionProvider, SalesSettingsProvider>();
        services.AddScoped<TerminalResolver>();
        services.AddScoped<PromotionRules>();
        services.AddScoped<StockGuard>();
        services.AddScoped<CustomerResolver>();
        services.AddScoped<SaleEditor>();
        services.AddScoped<SaleInventory>();
        services.AddScoped<SaleReceipts>();
        services.AddScoped<FiscalTicketWait>();
        services.AddScoped<ExchangeCompletion>();
        services.AddScoped<IPriceSimulator, PriceSimulator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/sales").WithTags("Ventas");

        group.MapPost("/", async (IDispatcher d, CancellationToken ct) => (await d.Send(new StartSaleCommand(), ct)).ToCreatedResult(s => $"/api/v1/sales/{s.Id}"))
            .RequirePermission(SalesPermissions.SaleCreate)
            .WithSummary("Inicia una venta en la caja de la sesión (exige la jornada abierta; una venta en curso a la vez)");
        group.MapGet("/current", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetCurrentSaleQuery(), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleCreate)
            .WithSummary("Venta en curso de esta caja (para retomarla tras un corte)");
        group.MapGet("/held", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListHeldSalesQuery(), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleCreate)
            .WithSummary("Ventas suspendidas de esta caja");
        group.MapGet("/", async (DateOnly? from, DateOnly? to, string? status, string? number, Guid? terminalId, Guid? cashSessionId, int? limit, Guid? customerId,
                IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListSalesQuery(from, to, status, number, terminalId, cashSessionId, limit, customerId), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleView)
            .WithSummary("Ventas de la sucursal por fecha de negocio, estado, número, caja o jornada");
        group.MapGet("/{saleId:guid}", async (Guid saleId, IDispatcher d, CancellationToken ct) => (await d.Send(new GetSaleQuery(saleId), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleView);

        group.MapPost("/{saleId:guid}/lines", async (Guid saleId, AddLineRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddLineCommand(saleId, r.Code, r.ProductId, r.PackagingId, r.Quantity), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleCreate)
            .WithSummary("Agrega un producto por código (barras, báscula o SKU) o por Id; valida existencias y lotes vencidos");
        group.MapPost("/{saleId:guid}/lines/expired", async (Guid saleId, AddLineRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddLineCommand(saleId, r.Code, r.ProductId, r.PackagingId, r.Quantity, AuthorizeExpired: true), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.ExpiredSell, allowSupervisor: true)
            .WithSummary("Agrega un producto con existencias de un lote vencido: permiso o autorización de supervisor (RN-SAL-18)");
        group.MapPatch("/{saleId:guid}/lines/{lineId:guid}", async (Guid saleId, Guid lineId, QuantityRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeQuantityCommand(saleId, lineId, r.Quantity), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleCreate);
        group.MapPost("/{saleId:guid}/lines/{lineId:guid}/void", async (Guid saleId, Guid lineId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new VoidLineCommand(saleId, lineId), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.LineVoid)
            .WithSummary("Elimina una línea (queda registrada como anulada)");
        group.MapPost("/{saleId:guid}/lines/{lineId:guid}/price", async (Guid saleId, Guid lineId, PriceRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new OverridePriceCommand(saleId, lineId, r.Price), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.PriceOverride, allowSupervisor: true)
            .WithSummary("Precio abierto: permiso o autorización de supervisor (RN-SAL-05)");

        group.MapPost("/{saleId:guid}/discounts", async (Guid saleId, DiscountRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ApplyDiscountCommand(saleId, r.LineId, r.Percent, r.Amount, r.Reason), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.DiscountApply, allowSupervisor: true)
            .WithSummary("Descuento manual de línea o global: SIEMPRE autorizado (RN-SAL-04); se aplica después de la promoción");
        group.MapPost("/{saleId:guid}/discounts/{discountId:guid}/remove", async (Guid saleId, Guid discountId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RemoveDiscountCommand(saleId, discountId), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.DiscountApply, allowSupervisor: true);
        group.MapPut("/{saleId:guid}/customer", async (Guid saleId, CustomerRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetCustomerCommand(saleId, r.PartyId, r.InvoiceRequested), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleCreate)
            .WithSummary("Cliente de la venta (partyId null = Consumidor final) y si pide factura electrónica; aplica su lista de precio y re-precia las líneas");
        group.MapPost("/{saleId:guid}/hold", async (Guid saleId, HoldRequest? r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new HoldSaleCommand(saleId, r?.Label), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleCreate)
            .WithSummary("Suspende la venta con una etiqueta");
        group.MapPost("/{saleId:guid}/resume", async (Guid saleId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ResumeSaleCommand(saleId), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleCreate)
            .WithSummary("Recupera una venta suspendida en la misma caja");
        group.MapPost("/{saleId:guid}/cancel", async (Guid saleId, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelSaleCommand(saleId, r.Reason), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleCancel, allowSupervisor: true)
            .WithSummary("Cancela una venta en curso o suspendida (con motivo, sin número)");

        group.MapPost("/{saleId:guid}/complete", async (
                    Guid saleId, CompleteRequest r, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IDispatcher d, FiscalTicketWait fiscal,
                    CancellationToken ct) =>
                (await fiscal.SaleAsync(await d.Send(new CompleteSaleCommand(saleId, r.Payments, idempotencyKey), ct), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleCreate)
            .WithSummary("Cobra y completa en una transacción (idempotente con Idempotency-Key): número, kardex, caja, comprobante y tiquete; con la facturación electrónica espera ⚙️ hasta 3 s los datos fiscales");
        group.MapPost("/{saleId:guid}/void", async (Guid saleId, ReasonRequest r, IDispatcher d, FiscalTicketWait fiscal, CancellationToken ct) =>
                (await fiscal.SaleAsync(await d.Send(new VoidSaleCommand(saleId, r.Reason), ct), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleVoid, allowSupervisor: true)
            .WithSummary("Anula una venta hecha por error: solo con su jornada de caja abierta (D7-10)");
        group.MapPost("/{saleId:guid}/reprint", async (Guid saleId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReprintSaleCommand(saleId), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleReprint)
            .WithSummary("Tiquete marcado COPIA (auditado), con los datos fiscales si ya llegaron");

        var exchanges = api.MapGroup("/exchanges").WithTags("Ventas");
        exchanges.MapPost("/", async (StartExchangeCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(e => $"/api/v1/exchanges/{e.Exchange.Id}"))
            .RequirePermission(SalesPermissions.ExchangeCreate, allowSupervisor: true)
            .WithSummary("Cambio de mercancía: crédito al precio pagado y venta nueva de igual o mayor valor (no se devuelve dinero)");
        exchanges.MapGet("/{exchangeId:guid}", async (Guid exchangeId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetExchangeQuery(exchangeId), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleView);
        exchanges.MapPost("/warranty-refund", async (WarrantyRefundCommand command, IDispatcher d, FiscalTicketWait fiscal, CancellationToken ct) =>
                (await fiscal.RefundAsync(await d.Send(command, ct), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.WarrantyRefund)
            .WithSummary("Reintegro en efectivo por garantía (Ley 1480): solo el propietario, auditado como crítico");
    }
}

public sealed record AddLineRequest(string? Code, Guid? ProductId, Guid? PackagingId, decimal? Quantity);

public sealed record QuantityRequest(decimal Quantity);

public sealed record PriceRequest(decimal Price);

public sealed record DiscountRequest(Guid? LineId, decimal? Percent, decimal? Amount, string Reason);

public sealed record CustomerRequest(Guid? PartyId, bool? InvoiceRequested = null);

public sealed record HoldRequest(string? Label);

public sealed record ReasonRequest(string Reason);

public sealed record CompleteRequest(IReadOnlyList<PaymentRequest> Payments);

