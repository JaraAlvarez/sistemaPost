using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Modules.Purchasing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Purchasing.Application;

// ─────────────────────── Reabastecimiento: pedido sugerido y órdenes de compra por proveedor ───────────────────────

/// <summary>Productos bajo el mínimo con el pedido sugerido, filtrables por bodega, categoría (y sus subcategorías) y proveedor.</summary>
public sealed record GetReplenishmentQuery(Guid? WarehouseId, Guid? CategoryId, Guid? SupplierId) : IQuery<ReplenishmentDto>;

internal sealed class GetReplenishmentHandler(IInstallationContext installation, IPurchasingQueries queries, IPermissionChecker permissions)
    : IQueryHandler<GetReplenishmentQuery, ReplenishmentDto>
{
    public const int MaxRows = 5000;

    public async Task<Result<ReplenishmentDto>> Handle(GetReplenishmentQuery request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        var costs = await permissions.HasPermissionAsync(InventoryPermissions.CostView, cancellationToken: cancellationToken);
        var rows = await queries.ListReplenishmentAsync(
            new ReplenishmentFilter(local.Value.BranchId, request.WarehouseId, request.CategoryId, request.SupplierId, MaxRows + 1), cancellationToken);
        return new ReplenishmentDto([.. rows.Take(MaxRows).Select(r => ToDto(r, costs))], rows.Count > MaxRows);
    }

    internal static ReplenishmentLineDto ToDto(ReplenishmentRow r, bool includeCosts)
    {
        var factor = r.PackagingId is not null && r.PackagingFactor is > 0m ? r.PackagingFactor.Value : 1m;
        var baseQuantity = ReplenishmentMath.SuggestedBaseQuantity(
            r.Quantity, r.PendingQuantity, r.MinQuantity, r.MaxQuantity, r.ReorderQuantity, r.ReorderPoint);
        var (packages, roundedBase) = ReplenishmentMath.RoundToPackaging(baseQuantity, factor, r.AllowsDecimalQuantity);
        return new ReplenishmentLineDto(
            r.WarehouseId, r.WarehouseCode, r.ProductId, r.Sku, r.ProductName, r.CategoryId, r.CategoryName, r.BaseUnitCode, r.AllowsDecimalQuantity,
            r.Quantity, r.MinQuantity, r.MaxQuantity, r.ReorderPoint, r.PendingQuantity, roundedBase, r.SupplierId, r.SupplierName,
            includeCosts ? r.LastCost : null, r.LastPurchaseAt, factor == 1m ? null : r.PackagingId, factor == 1m ? null : r.PackagingName, factor, packages);
    }
}

/// <summary>
/// "Crear órdenes de compra" desde el pedido sugerido: agrupa las líneas elegidas por proveedor y bodega y crea una orden en BORRADOR por
/// grupo con el mismo caso de uso de "Nueva orden" (mismas validaciones: proveedor activo, bodega activa de la sucursal, productos que se
/// compran, costo = último costo del proveedor × presentación si no se indica). Todo o nada: si una orden falla, no se crea ninguna.
/// <para>
/// Contra pedidos dobles: <c>BatchId</c> (lo genera la pantalla una vez por envío) es la clave de idempotencia: repetir el mismo envío
/// (doble clic, reintento tras un corte) devuelve las mismas órdenes. Además los envíos de una sucursal se hacen de a uno (candado) y,
/// dentro de la transacción, se recalcula el sugerido: si ya no es el que se veía (otra persona creó órdenes), se pide recargar.
/// </para>
/// </summary>
public sealed record CreateSuggestedOrdersCommand(
    Guid BatchId, IReadOnlyList<SuggestedOrderLineRequest> Lines, DateOnly? ExpectedDate = null, string? Notes = null)
    : ICommand<IReadOnlyList<SuggestedOrderDto>>;

internal sealed class CreateSuggestedOrdersHandler(
    IInstallationContext installation,
    ISuggestedOrderGate gate,
    IPurchasingQueries queries,
    IWarehouseDirectory warehouses,
    IPermissionChecker permissions,
    IDispatcher sender,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<CreateSuggestedOrdersCommand, IReadOnlyList<SuggestedOrderDto>>
{
    public const string DefaultNotes = "Creada desde el pedido sugerido (Reabastecimiento).";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<IReadOnlyList<SuggestedOrderDto>>> Handle(CreateSuggestedOrdersCommand request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        if (request.BatchId == Guid.Empty)
        {
            return PurchasingErrors.SuggestionBatchRequired;
        }

        var lines = request.Lines ?? [];
        var costs = await permissions.HasPermissionAsync(InventoryPermissions.CostView, cancellationToken: cancellationToken);

        // De a un envío por sucursal; luego, ¿este envío ya se hizo?
        await gate.LockAsync(local.Value.BranchId, cancellationToken);
        var key = $"{local.Value.BranchId:D}:{request.BatchId:D}";
        var hash = Hash(request);
        if (await gate.FindAsync(key, cancellationToken) is { } previous)
        {
            return previous.RequestHash == hash
                ? Result.Success<IReadOnlyList<SuggestedOrderDto>>(
                    [.. (JsonSerializer.Deserialize<List<SuggestedOrderDto>>(previous.ResponseJson, Json) ?? []).Select(o => costs ? o : o with { Total = null })])
                : PurchasingErrors.SuggestionBatchReused;
        }

        var groups = ReplenishmentMath.GroupBySupplier(
            [.. lines.Select(l => new SuggestedLine(l.WarehouseId, l.SupplierId, l.ProductId, l.PackagingId, l.Quantity, l.UnitCost))]);
        if (groups.IsFailure)
        {
            return groups.Error;
        }

        // Ya con el turno: el sugerido de ahora (con las órdenes que otros acaban de crear) debe ser el que se veía.
        var changed = await ChangedAsync(local.Value.BranchId, lines, cancellationToken);
        if (changed.Count > 0)
        {
            return PurchasingErrors.SuggestionChanged(changed);
        }

        var notes = string.IsNullOrWhiteSpace(request.Notes) ? DefaultNotes : request.Notes.Trim();
        var created = new List<SuggestedOrderDto>();
        foreach (var group in groups.Value)
        {
            var order = await sender.Send(
                new CreateOrderCommand(group.SupplierId, group.WarehouseId, null, request.ExpectedDate, notes,
                    [.. group.Lines.Select(l => new OrderLineRequest(l.ProductId, null, l.PackagingId, l.Quantity, l.UnitCost))]),
                cancellationToken);
            if (order.IsFailure)
            {
                // Se revierte todo (ninguna orden queda creada) y se dice cuál falló.
                return await DescribeAsync(order.Error, group, cancellationToken);
            }

            created.Add(new SuggestedOrderDto(
                order.Value.Id, order.Value.Number, order.Value.SupplierId, order.Value.SupplierName, order.Value.WarehouseId, order.Value.Lines.Count,
                order.Value.Total));
        }

        await gate.SaveAsync(key, hash, JsonSerializer.Serialize(created, Json), clock.UtcNow, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("purchasing", "PURCHASE_ORDERS_SUGGESTED", "PurchaseOrder", ids.NewId(), "Pedido sugerido",
                $"Se crearon {created.Count} órdenes de compra en borrador desde el pedido sugerido: "
                + string.Join(", ", created.Select(o => $"{o.Number} ({o.SupplierName}, {o.LineCount} productos)")) + ".",
                NewValues: new Dictionary<string, object?>
                {
                    ["batch"] = request.BatchId,
                    ["orders"] = created.Select(o => o.Number).ToArray(),
                    ["lines"] = created.Sum(o => o.LineCount),
                    ["total"] = created.Sum(o => o.Total ?? 0m),
                }),
            cancellationToken);
        return created.Select(o => costs ? o : o with { Total = null }).ToList();
    }

    /// <summary>Productos cuyo sugerido (con lo que se veía en pantalla) ya no es el mismo, o que ya no están bajo el mínimo.</summary>
    private async Task<List<string>> ChangedAsync(Guid branchId, IReadOnlyList<SuggestedOrderLineRequest> lines, CancellationToken cancellationToken)
    {
        var checkedLines = lines.Where(l => l.SeenSuggestedQuantity is not null).ToList();
        if (checkedLines.Count == 0)
        {
            return [];
        }

        var current = (await queries.ListReplenishmentAsync(new ReplenishmentFilter(branchId, null, null, null, int.MaxValue), cancellationToken))
            .Select(r => GetReplenishmentHandler.ToDto(r, includeCosts: false))
            .ToDictionary(l => (l.WarehouseId, l.ProductId));
        var changed = new List<string>();
        foreach (var line in checkedLines)
        {
            if (!current.TryGetValue((line.WarehouseId, line.ProductId), out var now))
            {
                changed.Add($"un producto que ya no está bajo el mínimo ({line.ProductId:D})");
            }
            else if (now.SuggestedQuantity != line.SeenSuggestedQuantity || now.PackagingId != line.PackagingId)
            {
                changed.Add($"{now.Sku} (ahora sugiere {now.SuggestedQuantity:0.###})");
            }
        }

        return changed;
    }

    /// <summary>El error de una orden con el proveedor y la bodega, para saber qué quitar.</summary>
    private async Task<Error> DescribeAsync(Error error, SuggestedOrderGroup group, CancellationToken cancellationToken)
    {
        var supplier = (await queries.SupplierNamesAsync([group.SupplierId], cancellationToken)).GetValueOrDefault(group.SupplierId) ?? "proveedor";
        var warehouse = await warehouses.GetAsync(group.WarehouseId, cancellationToken);
        var message = $"No se creó ninguna orden. La de {supplier} para la bodega {warehouse?.Code ?? "?"} falló: {error.Message}";
        return error.Type switch
        {
            ErrorType.Validation => Error.Validation(error.Code, message, error.FieldErrors),
            ErrorType.NotFound => Error.NotFound(error.Code, message),
            ErrorType.Conflict => Error.Conflict(error.Code, message),
            ErrorType.BusinessRule => Error.BusinessRule(error.Code, message),
            _ => error,
        };
    }

    private static string Hash(CreateSuggestedOrdersCommand request)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            lines = (request.Lines ?? []).OrderBy(l => l.WarehouseId).ThenBy(l => l.ProductId).ThenBy(l => l.SupplierId)
                .Select(l => new { l.WarehouseId, l.SupplierId, l.ProductId, l.PackagingId, l.Quantity, l.UnitCost }),
            request.ExpectedDate,
            request.Notes,
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
