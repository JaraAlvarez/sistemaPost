using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure;
using Pos.Modules.Inventory.Application;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Inventory.Domain;
using Pos.Modules.Inventory.Infrastructure;

namespace Pos.Modules.Inventory.Api;

/// <summary>Módulo Inventory: kardex, existencias, ajustes, saldo inicial, conteos, traslados, políticas y verificación.</summary>
public sealed class InventoryModule : IModule
{
    public string Name => "inventory";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IInventoryStore).Assembly);
        InventoryInfrastructureRegistration.Register(services);
        services.AddScoped<InventoryGuards>();
        services.AddScoped<AdjustmentPoster>();
        services.AddScoped<ICompanyInitializer, InventoryInitializer>();
        services.AddSingleton<ISettingDefinitionProvider, InventorySettingsProvider>();
        services.AddSingleton<IPermissionCatalogProvider, InventoryPermissionCatalog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/inventory").WithTags("Inventario");

        group.MapGet("/stock", async (Guid? warehouseId, Guid? productId, string? search, bool? belowMinimum, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetStockQuery(warehouseId, productId, search, belowMinimum ?? false), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.StockView)
            .WithSummary("Existencias por bodega de la sucursal (costos solo con inventory.cost.view)");

        group.MapGet("/kardex", async (Guid warehouseId, Guid productId, DateOnly? from, DateOnly? to, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetKardexQuery(warehouseId, productId, from, to), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.StockView)
            .WithSummary("Kardex de un producto en una bodega: cada fila explica por qué cambió el inventario");

        group.MapGet("/lots", async (Guid? productId, Guid? warehouseId, bool? expiring, int? days, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListLotsQuery(productId, warehouseId, expiring ?? false, days), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.StockView)
            .WithSummary("Existencias por lote; con expiring=true, lotes vencidos o por vencer (inventory.expiry_alert_days)");

        MapReasons(group);
        MapAdjustments(group.MapGroup("/adjustments"));
        MapCounts(group.MapGroup("/counts"));
        MapTransfers(group.MapGroup("/transfers"));

        group.MapPut("/policies", async (SetStockPolicyCommand command, IDispatcher d, CancellationToken ct) => (await d.Send(command, ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.AdjustmentManage)
            .WithSummary("Mínimo, máximo y punto de pedido de un producto en una bodega");

        group.MapPost("/verification", async (IDispatcher d, CancellationToken ct) => (await d.Send(new VerifyStockCommand(), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.StockVerify)
            .WithSummary("Compara cada saldo con su kardex (una diferencia queda como incidente crítico)");
        group.MapGet("/verification", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListVerificationsQuery(), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.StockVerify);
        group.MapPost("/verification/rebuild", async (RebuildStockCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.StockVerify)
            .WithSummary("Reconstruye un saldo desde su kardex (con motivo; auditado como crítico)");
    }

    private static void MapReasons(RouteGroupBuilder group)
    {
        group.MapGet("/reasons", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListReasonsQuery(), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.StockView);
        group.MapPost("/reasons", async (CreateReasonCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(r => $"/api/v1/inventory/reasons/{r.Id}"))
            .RequirePermission(InventoryPermissions.AdjustmentApprove);
        group.MapPut("/reasons/{reasonId:guid}", async (Guid reasonId, UpdateReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateReasonCommand(reasonId, r.Name, r.RequiresNote, r.IsActive), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.AdjustmentApprove);
    }

    private static void MapAdjustments(RouteGroupBuilder group)
    {
        group.MapGet("/", async (string? status, IDispatcher d, CancellationToken ct) => (await d.Send(new ListDocumentsQuery("ADJUSTMENT", status), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.StockView);
        group.MapGet("/{adjustmentId:guid}", async (Guid adjustmentId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetAdjustmentQuery(adjustmentId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.StockView);
        group.MapPost("/", async (CreateAdjustmentCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(a => $"/api/v1/inventory/adjustments/{a.Id}"))
            .RequirePermission(InventoryPermissions.AdjustmentManage)
            .WithSummary("Ajuste en borrador (positivo entra, negativo sale; las salidas tipificadas se toman como salidas)");
        group.MapPut("/{adjustmentId:guid}", async (Guid adjustmentId, UpdateAdjustmentRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateAdjustmentCommand(adjustmentId, r.ReasonId, r.Notes, r.Lines), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.AdjustmentManage);
        group.MapPost("/{adjustmentId:guid}/post", async (Guid adjustmentId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostAdjustmentCommand(adjustmentId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.AdjustmentManage)
            .WithSummary("Publica en el kardex o, si supera el umbral, lo deja pendiente de aprobación");
        group.MapPost("/{adjustmentId:guid}/approve", async (Guid adjustmentId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ApproveAdjustmentCommand(adjustmentId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.AdjustmentApprove)
            .WithSummary("Aprueba (otro usuario) y publica");
        group.MapPost("/{adjustmentId:guid}/cancel", async (Guid adjustmentId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelAdjustmentCommand(adjustmentId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.AdjustmentManage);
        group.MapPost("/initial-balance/import", async (Guid warehouseId, HttpRequest http, IDispatcher d, CancellationToken ct) =>
            {
                if (await UploadedFile.ReadAsync(http, ct) is not { } file)
                {
                    return UploadedFile.Missing().ToProblem();
                }

                await using var stream = file.OpenReadStream();
                return (await d.Send(new ImportInitialBalanceCommand(warehouseId, file.FileName, stream), ct))
                    .ToCreatedResult(a => $"/api/v1/inventory/adjustments/{a.Id}");
            })
            .DisableAntiforgery()
            .RequirePermission(InventoryPermissions.AdjustmentManage)
            .WithSummary("Saldo inicial desde Excel/CSV (producto, cantidad, costo): crea un ajuste en borrador o devuelve los errores por fila");
    }

    private static void MapCounts(RouteGroupBuilder group)
    {
        group.MapGet("/", async (string? status, IDispatcher d, CancellationToken ct) => (await d.Send(new ListDocumentsQuery("COUNT", status), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.CountRegister);
        group.MapGet("/{countId:guid}", async (Guid countId, IDispatcher d, CancellationToken ct) => (await d.Send(new GetCountQuery(countId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.CountRegister)
            .WithSummary("Conteo con sus líneas (en un conteo ciego en curso no se muestra el teórico)");
        group.MapPost("/", async (CreateCountCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(c => $"/api/v1/inventory/counts/{c.Id}"))
            .RequirePermission(InventoryPermissions.CountManage);
        group.MapPost("/{countId:guid}/start", async (Guid countId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new StartCountCommand(countId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.CountManage)
            .WithSummary("Congela el teórico; la tienda sigue vendiendo");
        group.MapPost("/{countId:guid}/entries", async (Guid countId, IReadOnlyList<CountEntryRequest> entries, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RegisterCountCommand(countId, entries), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.CountRegister)
            .WithSummary("Registra capturas (varios contadores; en revisión, reconteo)");
        group.MapPost("/{countId:guid}/review", async (Guid countId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReviewCountCommand(countId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.CountManage);
        group.MapPost("/{countId:guid}/approve", async (Guid countId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ApproveCountCommand(countId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.CountApprove)
            .WithSummary("Aprueba y lleva las diferencias al kardex");
        group.MapPost("/{countId:guid}/cancel", async (Guid countId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelCountCommand(countId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.CountManage);
    }

    private static void MapTransfers(RouteGroupBuilder group)
    {
        group.MapGet("/", async (string? status, IDispatcher d, CancellationToken ct) => (await d.Send(new ListDocumentsQuery("TRANSFER", status), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.StockView);
        group.MapGet("/{transferId:guid}", async (Guid transferId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetTransferQuery(transferId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.StockView);
        group.MapPost("/", async (CreateTransferCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(t => $"/api/v1/inventory/transfers/{t.Id}"))
            .RequirePermission(InventoryPermissions.TransferManage);
        group.MapPost("/{transferId:guid}/dispatch", async (Guid transferId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DispatchTransferCommand(transferId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.TransferManage);
        group.MapPost("/{transferId:guid}/receive", async (Guid transferId, ReceiveTransferRequest? r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReceiveTransferCommand(transferId, r?.Received), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.TransferManage)
            .WithSummary("Recibe (lo no indicado se recibe completo; el faltante queda como pérdida)");
        group.MapPost("/{transferId:guid}/cancel", async (Guid transferId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelTransferCommand(transferId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.TransferManage);
    }
}

public sealed record UpdateReasonRequest(string Name, bool RequiresNote, bool IsActive);

public sealed record UpdateAdjustmentRequest(Guid ReasonId, string? Notes, IReadOnlyList<AdjustmentLineRequest> Lines);

public sealed record ReceiveTransferRequest(IReadOnlyList<TransferLineRequest>? Received);
