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
using Pos.SharedKernel.Results;

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
        services.AddScoped<SaleViewer>();
        services.AddScoped<ScaleReadingVerifier>();
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

        group.MapPost("/{saleId:guid}/lines", async (Guid saleId, AddLineRequest r, HttpContext http, IDispatcher d, CancellationToken ct) =>
                PosClientCheck.Outdated(http, r) is { } outdated
                    ? ((Result<SaleDto>)outdated).ToHttpResult()
                    : (await d.Send(new AddLineCommand(saleId, r.Code, r.ProductId, r.PackagingId, r.Quantity, WeightKg: r.WeightKg, WeightSource: r.WeightSource, Scale: r.Scale), ct))
                        .ToHttpResult())
            .RequirePermission(SalesPermissions.SaleCreate)
            .WithSummary("Agrega un producto por código (barras, báscula o SKU) o por Id; valida existencias y lotes vencidos");
        group.MapPost("/{saleId:guid}/lines/expired", async (Guid saleId, AddLineRequest r, HttpContext http, IDispatcher d, CancellationToken ct) =>
                PosClientCheck.Outdated(http, r) is { } outdated
                    ? ((Result<SaleDto>)outdated).ToHttpResult()
                    : (await d.Send(new AddLineCommand(saleId, r.Code, r.ProductId, r.PackagingId, r.Quantity, AuthorizeExpired: true, WeightKg: r.WeightKg,
                        WeightSource: r.WeightSource, Scale: r.Scale), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.ExpiredSell, allowSupervisor: true)
            .WithSummary("Agrega un producto con existencias de un lote vencido: permiso o autorización de supervisor (RN-SAL-18)");
        group.MapGet("/scale/interlock", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetScaleInterlockQuery(), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleCreate)
            .WithSummary("Retorno a cero de la báscula de esta caja según el servidor (la siguiente pesada exige retirar el producto)");
        group.MapPost("/scale/reset", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ResetScaleInterlockCommand(), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.ScaleReset, allowSupervisor: true)
            .WithSummary("Reiniciar báscula: libera el retorno a cero de esta caja (permiso o supervisor; auditado)");
        group.MapPost("/{saleId:guid}/lines/manual-weight", async (Guid saleId, ManualWeightRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddLineCommand(saleId, r.Code, r.ProductId, null, null, WeightKg: r.WeightKg, WeightSource: WeightSources.Manual,
                    AuthorizeManualWeight: true), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.ManualWeight, allowSupervisor: true)
            .WithSummary("Agrega un producto de báscula con el peso digitado porque la báscula no responde: permiso o supervisor (Fase 7-C)");
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
                {
                    await d.Send(new RecheckSalePaymentsCommand(saleId), ct); // Sistecrédito: fuera de la transacción de la venta
                    var completed = await d.Send(new CompleteSaleCommand(saleId, r.Payments, idempotencyKey), ct);
                    if (completed.IsSuccess)
                    {
                        await d.Send(new SettleSalePaymentsCommand(saleId), CancellationToken.None); // pendientes de Sistecrédito que quedaron
                    }

                    return (await fiscal.SaleAsync(completed, ct)).ToHttpResult();
                })
            .RequirePermission(SalesPermissions.SaleCreate)
            .WithSummary("Cobra y completa en una transacción (idempotente con Idempotency-Key): número, kardex, caja, comprobante y tiquete; con la facturación electrónica espera ⚙️ hasta 3 s los datos fiscales");
        group.MapPost("/{saleId:guid}/sistecredito/release", async (Guid saleId, ReleaseCreditRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReleaseSistecreditoCreditCommand(saleId, r.TransactionId, r.OnlyNeeded), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.SaleCancel, allowSupervisor: true)
            .WithSummary("«No usar este crédito»: el crédito de Sistecrédito aprobado no se usa en la venta (con autorización de supervisor) y queda en revisión para reversarlo");
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

/// <summary>Compatibilidad de la interfaz de caja (Fase 7-C, BN10).</summary>
internal static class PosClientCheck
{
    /// <summary>
    /// Una pantalla de caja vieja (guardada en el navegador) envía el peso sin la lectura firmada y sin la cabecera de versión: se le pide
    /// recargar en lugar de rechazar el peso con un mensaje que no entiende.
    /// </summary>
    public static Error? Outdated(HttpContext http, AddLineRequest request) =>
        request.WeightKg is null
        || (int.TryParse(http.Request.Headers[PosClientBuild.Header].ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var build)
            && build >= PosClientBuild.SignedScaleReadings)
            ? null
            : Error.Conflict(PosClientBuild.OutdatedCode, "La pantalla de la caja está desactualizada: recárguela (Ctrl+F5) para pesar con la báscula.");
}

/// <summary>
/// Producto por código o Id. Con <c>WeightKg</c> es obligatorio <c>WeightSource</c> = SCALE (con la lectura del agente en <c>Scale</c>);
/// el peso digitado va por <c>/lines/manual-weight</c> (Fase 7-C).
/// </summary>
public sealed record AddLineRequest(
    string? Code, Guid? ProductId, Guid? PackagingId, decimal? Quantity, decimal? WeightKg = null, string? WeightSource = null, ScaleReadingInfo? Scale = null);

/// <summary>Peso digitado en la caja porque la báscula no respondía.</summary>
public sealed record ManualWeightRequest(string? Code, Guid? ProductId, decimal WeightKg);

public sealed record QuantityRequest(decimal Quantity);

public sealed record PriceRequest(decimal Price);

public sealed record DiscountRequest(Guid? LineId, decimal? Percent, decimal? Amount, string Reason);

public sealed record CustomerRequest(Guid? PartyId, bool? InvoiceRequested = null);

public sealed record HoldRequest(string? Label);

public sealed record ReasonRequest(string Reason);

/// <summary><c>TransactionId</c> vacío: todos los créditos aprobados de la venta.</summary>
public sealed record ReleaseCreditRequest(string? TransactionId, bool OnlyNeeded = false);

public sealed record CompleteRequest(IReadOnlyList<PaymentRequest> Payments);

