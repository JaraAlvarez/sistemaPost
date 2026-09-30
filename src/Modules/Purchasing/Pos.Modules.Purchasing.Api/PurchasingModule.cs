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
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Purchasing.Application;
using Pos.Modules.Purchasing.Contracts;
using Pos.Modules.Purchasing.Domain;
using Pos.Modules.Purchasing.Infrastructure;

namespace Pos.Modules.Purchasing.Api;

/// <summary>Módulo Purchasing: proveedores, órdenes de compra, compras, cuentas por pagar, pagos y devoluciones a proveedor.</summary>
public sealed class PurchasingModule : IModule
{
    public string Name => "purchasing";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IPurchasingStore).Assembly);
        PurchasingInfrastructureRegistration.Register(services);
        services.AddScoped<PaymentMethodGuard>();
        services.AddScoped<PurchaseLineResolver>();
        services.AddScoped<PurchasingGuards>();
        services.AddScoped<PurchaseInputBuilder>();
        services.AddSingleton<ISettingDefinitionProvider, PurchasingSettingsProvider>();
        services.AddSingleton<IPermissionCatalogProvider, PurchasingPermissionCatalog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/purchasing").WithTags("Compras");
        MapSuppliers(group.MapGroup("/suppliers"));
        MapOrders(group.MapGroup("/orders"));
        MapPurchases(group.MapGroup("/purchases"));
        MapPayables(group);
        MapReturns(group.MapGroup("/returns"));
        MapSupplierImprovements(group);
    }

    /// <summary>Fase 8, bloque 8.4 (D8-14): resumen, costos por producto, agenda, cuentas bancarias, retenciones sugeridas y vencimientos.</summary>
    private static void MapSupplierImprovements(RouteGroupBuilder group)
    {
        var suppliers = group.MapGroup("/suppliers");
        suppliers.MapGet("/{supplierId:guid}/summary", async (Guid supplierId, DateOnly? from, DateOnly? to, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetSupplierSummaryQuery(supplierId, from, to), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PayableView)
            .WithSummary("Ficha resumen: comprado en el período (por defecto 90 días), compras, última compra, saldo, vencido, devoluciones y productos activos");
        suppliers.MapGet("/{supplierId:guid}/schedule", async (Guid supplierId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetSupplierScheduleQuery(supplierId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseView)
            .WithSummary("Agenda de visita, pedido y entrega (por sucursal o todas) y pedido mínimo");
        suppliers.MapPut("/{supplierId:guid}/schedule", async (Guid supplierId, SupplierScheduleRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetSupplierScheduleCommand(supplierId, r.MinimumOrderAmount, r.OrderCutoffNote, r.Entries), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.SupplierManage)
            .WithSummary("Reemplaza la agenda completa y fija el pedido mínimo y la nota de hora de corte");
        suppliers.MapGet("/{supplierId:guid}/bank-accounts", async (Guid supplierId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListSupplierBankAccountsQuery(supplierId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PayableView)
            .WithSummary("Cuentas bancarias del proveedor con su estado de verificación");
        suppliers.MapPost("/{supplierId:guid}/bank-accounts", async (Guid supplierId, BankAccountRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateSupplierBankAccountCommand(supplierId, r), ct))
                    .ToCreatedResult(a => $"/api/v1/purchasing/suppliers/{supplierId}/bank-accounts/{a.Id}"))
            .RequirePermission(PurchasingPermissions.SupplierBankManage, allowSupervisor: true)
            .WithSummary("Registra una cuenta: queda POR VERIFICAR (auditoría crítica, RN-PUR-09)");
        suppliers.MapPut("/{supplierId:guid}/bank-accounts/{accountId:guid}",
                async (Guid supplierId, Guid accountId, BankAccountRequest r, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new UpdateSupplierBankAccountCommand(supplierId, accountId, r), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.SupplierBankManage, allowSupervisor: true)
            .WithSummary("Modifica, marca principal, inactiva o reactiva la cuenta: un cambio de datos o reactivación la deja POR VERIFICAR");
        suppliers.MapPost("/{supplierId:guid}/bank-accounts/{accountId:guid}/verify", async (Guid supplierId, Guid accountId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new VerifySupplierBankAccountCommand(supplierId, accountId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.SupplierBankManage, allowSupervisor: true)
            .WithSummary("Verifica la cuenta: otro usuario distinto del que la registró o modificó");
        suppliers.MapGet("/{supplierId:guid}/withholdings", async (Guid supplierId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetSupplierWithholdingsQuery(supplierId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseView)
            .WithSummary("Retenciones sugeridas (tipo y tarifa) que pre-llenan las compras nuevas");
        suppliers.MapPut("/{supplierId:guid}/withholdings", async (Guid supplierId, SupplierWithholdingsRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetSupplierWithholdingsCommand(supplierId, r.Withholdings), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.SupplierManage)
            .WithSummary("Reemplaza las retenciones sugeridas (una por tipo); la compra las sigue mostrando para confirmar (RN-PUR-10)");

        group.MapGet("/banks", async (bool? includeInactive, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListBanksQuery(includeInactive ?? false), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseView)
            .WithSummary("Bancos de Colombia (código ACH) para registrar cuentas de proveedores");
        group.MapGet("/products/{productId:guid}/suppliers", async (Guid productId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListProductSuppliersQuery(productId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseView)
            .WithSummary("¿Quién me vende esto?: proveedores del producto, días de entrega, preferido (último costo solo con inventory.cost.view)");
        group.MapGet("/products/{productId:guid}/cost-history",
                async (Guid productId, Guid? supplierId, DateOnly? from, DateOnly? to, int? limit, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new GetCostHistoryQuery(productId, supplierId, from, to, limit), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.CostView)
            .WithSummary("Costo neto por unidad base en cada compra contabilizada, por proveedor");
        group.MapGet("/payables/due", async (int? days, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetDuePayablesQuery(days), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PayableView)
            .WithSummary("Vencimientos: cuentas vencidas y las que vencen en los próximos días (por defecto 7)");
    }

    private static void MapSuppliers(RouteGroupBuilder group)
    {
        group.MapGet("/", async (string? search, bool? includeInactive, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListSuppliersQuery(search, includeInactive ?? false), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseView)
            .WithSummary("Proveedores con su saldo por pagar (búsqueda por nombre, código o identificación)");
        group.MapGet("/{supplierId:guid}", async (Guid supplierId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetSupplierQuery(supplierId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseView);
        group.MapPost("/", async (CreateSupplierRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateSupplierCommand(r.PartyId, r.Supplier), ct)).ToCreatedResult(s => $"/api/v1/purchasing/suppliers/{s.Id}"))
            .RequirePermission(PurchasingPermissions.SupplierManage)
            .WithSummary("Convierte un tercero en proveedor (condiciones de pago; si no factura, sus compras requieren documento soporte)");
        group.MapPut("/{supplierId:guid}", async (Guid supplierId, SupplierInput supplier, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateSupplierCommand(supplierId, supplier), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.SupplierManage);
        group.MapPost("/{supplierId:guid}/status", async (Guid supplierId, SupplierStatusRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetSupplierStatusCommand(supplierId, r.Status), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.SupplierManage)
            .WithSummary("Activa, bloquea (sin órdenes ni compras nuevas) o inactiva el proveedor");
        group.MapGet("/{supplierId:guid}/products", async (Guid supplierId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListSupplierProductsQuery(supplierId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseView)
            .WithSummary("Productos que suministra (último costo solo con inventory.cost.view)");
        group.MapPut("/{supplierId:guid}/products", async (Guid supplierId, SupplierProductRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetSupplierProductCommand(supplierId, r.ProductId, r.PackagingId, r.SupplierCode, r.LeadTimeDays, r.IsPreferred), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.SupplierManage);
        group.MapDelete("/{supplierId:guid}/products/{productId:guid}", async (Guid supplierId, Guid productId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RemoveSupplierProductCommand(supplierId, productId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.SupplierManage);
        group.MapGet("/{supplierId:guid}/statement", async (Guid supplierId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetSupplierStatementQuery(supplierId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PayableView)
            .WithSummary("Estado de cuenta: cuentas por pagar con sus asientos y pagos");
    }

    private static void MapOrders(RouteGroupBuilder group)
    {
        group.MapGet("/", async (Guid? supplierId, string? status, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListDocumentsQuery("ORDER", supplierId, status), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseView);
        group.MapGet("/{orderId:guid}", async (Guid orderId, IDispatcher d, CancellationToken ct) => (await d.Send(new GetOrderQuery(orderId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseView);
        group.MapPost("/", async (CreateOrderCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(o => $"/api/v1/purchasing/orders/{o.Id}"))
            .RequirePermission(PurchasingPermissions.OrderManage)
            .WithSummary("Orden en borrador (sin costo, toma el último costo del proveedor)");
        group.MapPut("/{orderId:guid}", async (Guid orderId, UpdateOrderRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateOrderCommand(orderId, r.OrderDate, r.ExpectedDate, r.Notes, r.Lines), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.OrderManage);
        group.MapPost("/{orderId:guid}/approve", async (Guid orderId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeOrderStatusCommand(orderId, OrderAction.Approve), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.OrderApprove);
        group.MapPost("/{orderId:guid}/send", async (Guid orderId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeOrderStatusCommand(orderId, OrderAction.Send), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.OrderManage);
        group.MapPost("/{orderId:guid}/close", async (Guid orderId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeOrderStatusCommand(orderId, OrderAction.Close), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.OrderManage)
            .WithSummary("Cierra la orden: no admite más recepciones");
        group.MapPost("/{orderId:guid}/cancel", async (Guid orderId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeOrderStatusCommand(orderId, OrderAction.Cancel), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.OrderManage);
    }

    private static void MapPurchases(RouteGroupBuilder group)
    {
        group.MapGet("/", async (Guid? supplierId, string? status, bool? requiresSupportDocument, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListDocumentsQuery("PURCHASE", supplierId, status, requiresSupportDocument), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseView)
            .WithSummary("Compras de la sucursal (requiresSupportDocument=true: pendientes de documento soporte)");
        group.MapGet("/{purchaseId:guid}", async (Guid purchaseId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetPurchaseQuery(purchaseId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseView);
        group.MapPost("/", async (PurchaseRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreatePurchaseCommand(r), ct)).ToCreatedResult(p => $"/api/v1/purchasing/purchases/{p.Id}"))
            .RequirePermission(PurchasingPermissions.PurchaseManage)
            .WithSummary("Compra en borrador; contra una orden y sin líneas, propone lo pendiente");
        group.MapPut("/{purchaseId:guid}", async (Guid purchaseId, PurchaseRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdatePurchaseCommand(purchaseId, r), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseManage)
            .WithSummary("Reemplaza encabezado, líneas, cargos y retenciones del borrador (recalcula el costeo)");
        group.MapPost("/{purchaseId:guid}/post", async (Guid purchaseId, PostPurchaseRequest? r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostPurchaseCommand(purchaseId, r?.CashSessionId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchasePost)
            .WithSummary("Contabiliza: kardex al costo neto, lotes, cuenta por pagar (y pago si es de contado); devuelve alertas");
        group.MapPost("/{purchaseId:guid}/void", async (Guid purchaseId, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new VoidPurchaseCommand(purchaseId, r.Reason), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseVoid)
            .WithSummary("Anula con movimientos inversos (sin pagos, sin devoluciones y sin dejar existencias negativas)");
    }

    private static void MapPayables(RouteGroupBuilder group)
    {
        group.MapGet("/payables", async (Guid? supplierId, bool? openOnly, DateOnly? asOf, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListPayablesQuery(supplierId, openOnly ?? true, asOf), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PayableView);
        group.MapGet("/payables/aging", async (DateOnly? asOf, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetAgingQuery(asOf), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PayableView)
            .WithSummary("Cartera por edades: corriente, 1–30, 31–60, 61–90 y más de 90 días vencidos");
        group.MapGet("/payables/{payableId:guid}", async (Guid payableId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetPayableQuery(payableId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PayableView)
            .WithSummary("Cuenta por pagar con su libro de asientos");

        group.MapGet("/payments", async (Guid? supplierId, string? status, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListDocumentsQuery("PAYMENT", supplierId, status), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PayableView);
        group.MapGet("/payments/{paymentId:guid}", async (Guid paymentId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetPaymentQuery(paymentId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PayableView);
        group.MapPost("/payments", async (RegisterPaymentCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(p => $"/api/v1/purchasing/payments/{p.Id}"))
            .RequirePermission(PurchasingPermissions.PayablePay)
            .WithSummary("Pago aplicado a una o varias facturas del proveedor");
        group.MapPost("/payments/{paymentId:guid}/void", async (Guid paymentId, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new VoidPaymentCommand(paymentId, r.Reason), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PayablePay)
            .WithSummary("Anula el pago con asientos inversos");
    }

    private static void MapReturns(RouteGroupBuilder group)
    {
        group.MapGet("/", async (Guid? supplierId, string? status, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListDocumentsQuery("RETURN", supplierId, status), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseView);
        group.MapGet("/{returnId:guid}", async (Guid returnId, IDispatcher d, CancellationToken ct) => (await d.Send(new GetReturnQuery(returnId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.PurchaseView);
        group.MapPost("/", async (CreateReturnCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(r => $"/api/v1/purchasing/returns/{r.Id}"))
            .RequirePermission(PurchasingPermissions.ReturnManage)
            .WithSummary("Devolución en borrador contra una compra (al costo de la compra y del lote original)");
        group.MapPost("/{returnId:guid}/post", async (Guid returnId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostReturnCommand(returnId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.ReturnManage)
            .WithSummary("Salida del kardex y reducción de la cuenta por pagar");
        group.MapPost("/{returnId:guid}/settle", async (Guid returnId, SettleReturnRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SettleReturnCommand(returnId, r.Settlement, r.Reference), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.ReturnManage)
            .WithSummary("Liquida con nota crédito, reintegro o reposición de la mercancía");
        group.MapPost("/{returnId:guid}/cancel", async (Guid returnId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelReturnCommand(returnId), ct)).ToHttpResult())
            .RequirePermission(PurchasingPermissions.ReturnManage);
    }
}

public sealed record CreateSupplierRequest(Guid PartyId, SupplierInput Supplier);

public sealed record SupplierStatusRequest(SupplierStatus Status);

public sealed record SupplierProductRequest(Guid ProductId, Guid? PackagingId, string? SupplierCode, int? LeadTimeDays, bool IsPreferred);

public sealed record UpdateOrderRequest(DateOnly OrderDate, DateOnly? ExpectedDate, string? Notes, IReadOnlyList<OrderLineRequest> Lines);

public sealed record ReasonRequest(string Reason);

/// <summary>Compra de contado pagada desde una jornada de caja abierta (opcional).</summary>
public sealed record PostPurchaseRequest(Guid? CashSessionId);

public sealed record SettleReturnRequest(ReturnSettlement Settlement, string Reference);

public sealed record SupplierScheduleRequest(decimal? MinimumOrderAmount, string? OrderCutoffNote, IReadOnlyList<ScheduleEntryInput>? Entries);

public sealed record SupplierWithholdingsRequest(IReadOnlyList<WithholdingDefaultRequest>? Withholdings);
