using System.Text.Json;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Inventory.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Inventory.Application;

internal static class CountMapping
{
    public static async Task<CountDto> ToDtoAsync(this InventoryCount c, ICatalogReader catalog, CancellationToken cancellationToken)
    {
        var products = await catalog.GetProductsAsync([.. c.Lines.Select(l => l.ProductId)], cancellationToken);
        var hide = c.IsBlind && c.Status == CountStatus.InProgress;
        var lines = c.Lines.Select(l =>
            {
                var counted = c.CountedOf(l.ProductId);
                var product = products.GetValueOrDefault(l.ProductId);
                var recounted = c.Entries.Any(e => e.ProductId == l.ProductId && e.Round == 2);
                return new CountLineDto(
                    l.ProductId, product?.Sku ?? string.Empty, product?.Name ?? string.Empty, hide ? null : l.SystemQty, counted,
                    hide ? null : l.ExpectedQty, hide ? null : l.Difference, counted is not null,
                    c.Status == CountStatus.InReview && l.Difference is { } d && d != 0m && !recounted);
            })
            .OrderBy(l => l.Sku, StringComparer.Ordinal)
            .ToList();
        return new CountDto(c.Id, c.Number, c.WarehouseId, c.CountType.Db(), c.IsBlind, c.Status.Db(), c.StartedAt, c.PostedAt, lines.Count,
            lines.Count(l => l.Counted), lines);
    }
}

public sealed record CreateCountCommand(Guid WarehouseId, CountType Type, bool IsBlind, IReadOnlyList<Guid>? CategoryIds, IReadOnlyList<Guid>? ProductIds, string? Notes)
    : ICommand<CountDto>;

internal sealed class CreateCountHandler(
    IInstallationContext installation, IInventoryStore store, InventoryGuards guards, ICatalogReader catalog, IDocumentNumberAllocator numbers, IIdGenerator ids)
    : ICommandHandler<CreateCountCommand, CountDto>
{
    public async Task<Result<CountDto>> Handle(CreateCountCommand request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        var warehouse = await guards.LocalWarehouseAsync(request.WarehouseId, cancellationToken);
        if (warehouse.IsFailure)
        {
            return warehouse.Error;
        }

        var scope = JsonSerializer.Serialize(new
        {
            categoryIds = request.Type == CountType.Partial ? request.CategoryIds ?? [] : [],
            productIds = request.Type == CountType.Partial ? request.ProductIds ?? [] : [],
        });
        if (request.Type == CountType.Partial && (request.CategoryIds?.Count ?? 0) + (request.ProductIds?.Count ?? 0) == 0)
        {
            return InventoryErrors.CountEmpty;
        }

        var number = await numbers.NextForBranchAsync("INVENTORY_COUNT", local.Value.BranchId, cancellationToken);
        var count = InventoryCount.Create(ids.NewId(), local.Value.CompanyId, local.Value.BranchId, request.WarehouseId, number.Number, request.Type,
            request.IsBlind, scope, request.Notes);
        store.Add(count);
        return await count.ToDtoAsync(catalog, cancellationToken);
    }
}

/// <summary>Inicia el conteo: congela el saldo teórico y el seq del kardex de cada producto del alcance.</summary>
public sealed record StartCountCommand(Guid CountId) : ICommand<CountDto>;

internal sealed class StartCountHandler(IInventoryStore store, IStockLedger ledger, ICatalogReader catalog, IIdGenerator ids, IClock clock)
    : ICommandHandler<StartCountCommand, CountDto>
{
    public async Task<Result<CountDto>> Handle(StartCountCommand request, CancellationToken cancellationToken)
    {
        var count = await store.GetCountAsync(request.CountId, cancellationToken);
        if (count is null)
        {
            return InventoryErrors.CountNotFound;
        }

        var scope = JsonDocument.Parse(count.Scope).RootElement;
        var categories = Ids(scope, "categoryIds");
        var products = Ids(scope, "productIds");
        IReadOnlyList<Guid> productIds;
        if (count.CountType == CountType.Full)
        {
            productIds = await catalog.ListStockableProductIdsAsync(null, cancellationToken);
        }
        else
        {
            var byCategory = categories.Count > 0 ? await catalog.ListStockableProductIdsAsync(categories, cancellationToken) : [];
            var explicitProducts = (await catalog.GetProductsAsync(products, cancellationToken)).Values.Where(p => p.IsStockable).Select(p => p.Id);
            productIds = [.. byCategory.Concat(explicitProducts).Distinct()];
        }

        var snapshot = await ledger.SnapshotAsync(count.WarehouseId, productIds, cancellationToken);
        var started = count.Start(snapshot, ids.NewId, clock.UtcNow);
        return started.IsSuccess ? await count.ToDtoAsync(catalog, cancellationToken) : started.Error;
    }

    private static List<Guid> Ids(JsonElement scope, string property) =>
        scope.TryGetProperty(property, out var array) ? [.. array.EnumerateArray().Select(e => e.GetGuid())] : [];
}

/// <summary>Captura de un contador: por Id de producto o por código (SKU o código de barras).</summary>
public sealed record CountEntryRequest(Guid? ProductId, string? Code, decimal Quantity, string? Location);

public sealed record RegisterCountCommand(Guid CountId, IReadOnlyList<CountEntryRequest> Entries) : ICommand<CountDto>;

internal sealed class RegisterCountHandler(IInventoryStore store, ICatalogReader catalog, IActorContext actor, IIdGenerator ids, IClock clock)
    : ICommandHandler<RegisterCountCommand, CountDto>
{
    public async Task<Result<CountDto>> Handle(RegisterCountCommand request, CancellationToken cancellationToken)
    {
        var count = await store.GetCountAsync(request.CountId, cancellationToken);
        if (count is null)
        {
            return InventoryErrors.CountNotFound;
        }

        foreach (var entry in request.Entries ?? [])
        {
            var productId = entry.ProductId
                ?? (entry.Code is null ? null : (await catalog.FindByCodeAsync(entry.Code, cancellationToken))?.Id);
            if (productId is null)
            {
                return InventoryErrors.ProductNotFound;
            }

            var registered = count.Register(productId.Value, entry.Quantity, entry.Location, actor.ActorId!.Value, clock.UtcNow, ids.NewId);
            if (registered.IsFailure)
            {
                return registered.Error;
            }
        }

        return await count.ToDtoAsync(catalog, cancellationToken);
    }
}

/// <summary>Cierra la captura y calcula diferencias con el teórico actualizado (se puede reconteo antes de aprobar).</summary>
public sealed record ReviewCountCommand(Guid CountId) : ICommand<CountDto>;

internal sealed class ReviewCountHandler(IInventoryStore store, IStockLedger ledger, ICatalogReader catalog, IClock clock)
    : ICommandHandler<ReviewCountCommand, CountDto>
{
    public async Task<Result<CountDto>> Handle(ReviewCountCommand request, CancellationToken cancellationToken)
    {
        var count = await store.GetCountAsync(request.CountId, cancellationToken);
        if (count is null)
        {
            return InventoryErrors.CountNotFound;
        }

        var expected = await ledger.ExpectedAsync(count.WarehouseId, count.Lines, cancellationToken);
        var reviewed = count.Review(expected, clock.UtcNow);
        return reviewed.IsSuccess ? await count.ToDtoAsync(catalog, cancellationToken) : reviewed.Error;
    }
}

/// <summary>Aprueba el conteo y lleva sus diferencias al kardex (COUNT_ADJUSTMENT_IN/OUT al costo promedio).</summary>
public sealed record ApproveCountCommand(Guid CountId) : ICommand<CountDto>;

internal sealed class ApproveCountHandler(
    IInventoryStore store,
    IStockLedger ledger,
    IInventoryPosting posting,
    ICatalogReader catalog,
    ISettingsReader settings,
    IActorContext actor,
    IAuditWriter audit,
    IClock clock) : ICommandHandler<ApproveCountCommand, CountDto>
{
    public async Task<Result<CountDto>> Handle(ApproveCountCommand request, CancellationToken cancellationToken)
    {
        var count = await store.GetCountAsync(request.CountId, cancellationToken);
        if (count is null)
        {
            return InventoryErrors.CountNotFound;
        }

        var expected = await ledger.ExpectedAsync(count.WarehouseId, count.Lines, cancellationToken);
        var uncountedAsZero = await settings.GetAsync(InventorySettings.UncountedAsZero, new SettingContext(count.CompanyId), cancellationToken);
        var differences = count.Approve(expected, uncountedAsZero, actor.ActorId!.Value, clock.UtcNow);
        if (differences.IsFailure)
        {
            return differences.Error;
        }

        if (differences.Value.Count > 0)
        {
            var lines = differences.Value.Select(d => new PostingLine(
                count.WarehouseId, d.ProductId, d.Difference > 0m ? MovementType.CountAdjustmentIn : MovementType.CountAdjustmentOut, Math.Abs(d.Difference)))
                .ToList();
            var posted = await posting.PostAsync(
                new InventoryPosting("COUNT", count.Id, count.Number, count.BranchId, clock.Today, lines, AllowNegativeStock: true), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error;
            }
        }

        await audit.WriteAsync(
            new AuditEntry("inventory", "INVENTORY_COUNT_POSTED", nameof(InventoryCount), count.Id, count.AuditLabel,
                $"Conteo {count.Number} aprobado: {count.Lines.Count} productos, {differences.Value.Count} con diferencia.",
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return await count.ToDtoAsync(catalog, cancellationToken);
    }
}

public sealed record CancelCountCommand(Guid CountId) : ICommand;

internal sealed class CancelCountHandler(IInventoryStore store, IClock clock) : ICommandHandler<CancelCountCommand>
{
    public async Task<Result> Handle(CancelCountCommand request, CancellationToken cancellationToken) =>
        await store.GetCountAsync(request.CountId, cancellationToken) is { } count ? count.Cancel(clock.UtcNow) : InventoryErrors.CountNotFound;
}

public sealed record GetCountQuery(Guid CountId) : IQuery<CountDto>;

internal sealed class GetCountHandler(IInventoryStore store, ICatalogReader catalog) : IQueryHandler<GetCountQuery, CountDto>
{
    public async Task<Result<CountDto>> Handle(GetCountQuery request, CancellationToken cancellationToken) =>
        await store.GetCountAsync(request.CountId, cancellationToken) is { } count ? await count.ToDtoAsync(catalog, cancellationToken) : InventoryErrors.CountNotFound;
}

// ─────────────────────────────── Traslados ───────────────────────────────

internal static class TransferMapping
{
    public static async Task<TransferDto> ToDtoAsync(this StockTransfer t, ICatalogReader catalog, CancellationToken cancellationToken)
    {
        var products = await catalog.GetProductsAsync([.. t.Lines.Select(l => l.ProductId)], cancellationToken);
        return new TransferDto(t.Id, t.Number, t.OriginWarehouseId, t.DestinationWarehouseId, t.Status.Db(), t.Notes, t.DispatchedAt, t.ReceivedAt,
            [.. t.Lines.OrderBy(l => l.LineNumber).Select(l => new TransferLineDto(
                l.ProductId, products.GetValueOrDefault(l.ProductId)?.Sku ?? string.Empty, products.GetValueOrDefault(l.ProductId)?.Name ?? string.Empty,
                l.QuantitySent, l.QuantityReceived, l.UnitCost))]);
    }
}

public sealed record TransferLineRequest(Guid ProductId, decimal Quantity);

public sealed record CreateTransferCommand(Guid OriginWarehouseId, Guid DestinationWarehouseId, string? Notes, IReadOnlyList<TransferLineRequest> Lines)
    : ICommand<TransferDto>;

internal sealed class CreateTransferHandler(
    IInstallationContext installation, IInventoryStore store, InventoryGuards guards, ICatalogReader catalog, IDocumentNumberAllocator numbers, IIdGenerator ids)
    : ICommandHandler<CreateTransferCommand, TransferDto>
{
    public async Task<Result<TransferDto>> Handle(CreateTransferCommand request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        foreach (var warehouseId in new[] { request.OriginWarehouseId, request.DestinationWarehouseId })
        {
            var warehouse = await guards.LocalWarehouseAsync(warehouseId, cancellationToken);
            if (warehouse.IsFailure)
            {
                return warehouse.Error;
            }

            if (warehouse.Value.Kind == "IN_TRANSIT")
            {
                return Error.Validation("INVENTORY.IN_TRANSIT_NOT_ALLOWED", "La bodega de tránsito la usa el sistema: elija bodegas de origen y destino reales.");
            }
        }

        var products = await guards.StockableAsync([.. (request.Lines ?? []).Select(l => (l.ProductId, l.Quantity))], cancellationToken);
        if (products.IsFailure)
        {
            return products.Error;
        }

        var number = await numbers.NextForBranchAsync("INVENTORY_TRANSFER", local.Value.BranchId, cancellationToken);
        var transfer = StockTransfer.Create(ids.NewId(), local.Value.CompanyId, local.Value.BranchId, number.Number, request.OriginWarehouseId,
            request.DestinationWarehouseId, request.Notes, [.. (request.Lines ?? []).Select(l => (l.ProductId, l.Quantity))], ids.NewId);
        if (transfer.IsFailure)
        {
            return transfer.Error;
        }

        store.Add(transfer.Value);
        return await transfer.Value.ToDtoAsync(catalog, cancellationToken);
    }
}

/// <summary>Despacha: sale del origen al costo promedio y entra a la bodega de tránsito con ese mismo costo.</summary>
public sealed record DispatchTransferCommand(Guid TransferId) : ICommand<TransferDto>;

internal sealed class DispatchTransferHandler(
    IInventoryStore store, InventoryGuards guards, IInventoryPosting posting, ISettingsReader settings, ICatalogReader catalog, IActorContext actor, IClock clock)
    : ICommandHandler<DispatchTransferCommand, TransferDto>
{
    public async Task<Result<TransferDto>> Handle(DispatchTransferCommand request, CancellationToken cancellationToken)
    {
        var transfer = await store.GetTransferAsync(request.TransferId, cancellationToken);
        if (transfer is null)
        {
            return InventoryErrors.TransferNotFound;
        }

        if (transfer.Status != TransferStatus.Draft)
        {
            return InventoryErrors.InvalidStatus;
        }

        var transit = await guards.InTransitWarehouseAsync(transfer.BranchId, cancellationToken);
        if (transit.IsFailure)
        {
            return transit.Error;
        }

        var allowNegative = await settings.GetAsync(InventorySettings.AllowNegativeStock, new SettingContext(transfer.CompanyId, transfer.BranchId), cancellationToken);
        var outflow = await posting.PostAsync(
            new InventoryPosting("TRANSFER", transfer.Id, transfer.Number, transfer.BranchId, clock.Today,
                [.. transfer.Lines.Select(l => new PostingLine(transfer.OriginWarehouseId, l.ProductId, MovementType.TransferOut, l.QuantitySent, SourceLineId: l.Id))],
                allowNegative),
            cancellationToken);
        if (outflow.IsFailure)
        {
            return outflow.Error;
        }

        var costs = outflow.Value.ToDictionary(m => m.ProductId, m => m.UnitCost);
        var inflow = await posting.PostAsync(
            new InventoryPosting("TRANSFER", transfer.Id, transfer.Number, transfer.BranchId, clock.Today,
                [.. transfer.Lines.Select(l => new PostingLine(transit.Value.Id, l.ProductId, MovementType.TransferIn, l.QuantitySent, costs.GetValueOrDefault(l.ProductId), l.Id))],
                AllowNegativeStock: true),
            cancellationToken);
        if (inflow.IsFailure)
        {
            return inflow.Error;
        }

        var dispatched = transfer.Dispatch(costs, actor.ActorId!.Value, clock.UtcNow);
        return dispatched.IsSuccess ? await transfer.ToDtoAsync(catalog, cancellationToken) : dispatched.Error;
    }
}

/// <summary>Recibe (lo no indicado se recibe completo): sale de tránsito, entra al destino al costo del despacho; el faltante es pérdida.</summary>
public sealed record ReceiveTransferCommand(Guid TransferId, IReadOnlyList<TransferLineRequest>? Received) : ICommand<TransferDto>;

internal sealed class ReceiveTransferHandler(
    IInventoryStore store, InventoryGuards guards, IInventoryPosting posting, ICatalogReader catalog, IActorContext actor, IAuditWriter audit, IClock clock)
    : ICommandHandler<ReceiveTransferCommand, TransferDto>
{
    public async Task<Result<TransferDto>> Handle(ReceiveTransferCommand request, CancellationToken cancellationToken)
    {
        var transfer = await store.GetTransferAsync(request.TransferId, cancellationToken);
        if (transfer is null)
        {
            return InventoryErrors.TransferNotFound;
        }

        var transit = await guards.InTransitWarehouseAsync(transfer.BranchId, cancellationToken);
        if (transit.IsFailure)
        {
            return transit.Error;
        }

        var received = transfer.Receive((request.Received ?? []).ToDictionary(r => r.ProductId, r => r.Quantity), actor.ActorId!.Value, clock.UtcNow);
        if (received.IsFailure)
        {
            return received.Error;
        }

        var shortageReason = (await store.GetReasonsAsync(cancellationToken)).SingleOrDefault(r => r.Code == InventoryInitializer.TransferShortage);
        var lines = new List<PostingLine>();
        foreach (var line in transfer.Lines)
        {
            var quantity = line.QuantityReceived!.Value;
            if (quantity > 0m)
            {
                lines.Add(new PostingLine(transit.Value.Id, line.ProductId, MovementType.TransferOut, quantity, SourceLineId: line.Id));
                lines.Add(new PostingLine(transfer.DestinationWarehouseId, line.ProductId, MovementType.TransferIn, quantity, line.UnitCost ?? 0m, line.Id));
            }

            if (line.QuantitySent - quantity is > 0m and var shortage)
            {
                lines.Add(new PostingLine(transit.Value.Id, line.ProductId, MovementType.Loss, shortage, SourceLineId: line.Id, ReasonId: shortageReason?.Id));
            }
        }

        var posted = await posting.PostAsync(
            new InventoryPosting("TRANSFER", transfer.Id, transfer.Number, transfer.BranchId, clock.Today, lines, AllowNegativeStock: true), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        if (transfer.Status == TransferStatus.ReceivedWithDifferences)
        {
            await audit.WriteAsync(
                new AuditEntry("inventory", "INVENTORY_TRANSFER_SHORTAGE", nameof(StockTransfer), transfer.Id, transfer.AuditLabel,
                    $"Traslado {transfer.Number} recibido con faltantes en {transfer.Lines.Count(l => l.QuantityReceived < l.QuantitySent)} productos.",
                    Severity: AuditSeverity.Warning),
                cancellationToken);
        }

        return await transfer.ToDtoAsync(catalog, cancellationToken);
    }
}

public sealed record CancelTransferCommand(Guid TransferId) : ICommand;

internal sealed class CancelTransferHandler(IInventoryStore store, IClock clock) : ICommandHandler<CancelTransferCommand>
{
    public async Task<Result> Handle(CancelTransferCommand request, CancellationToken cancellationToken) =>
        await store.GetTransferAsync(request.TransferId, cancellationToken) is { } transfer ? transfer.Cancel(clock.UtcNow) : InventoryErrors.TransferNotFound;
}

public sealed record GetTransferQuery(Guid TransferId) : IQuery<TransferDto>;

internal sealed class GetTransferHandler(IInventoryStore store, ICatalogReader catalog) : IQueryHandler<GetTransferQuery, TransferDto>
{
    public async Task<Result<TransferDto>> Handle(GetTransferQuery request, CancellationToken cancellationToken) =>
        await store.GetTransferAsync(request.TransferId, cancellationToken) is { } transfer
            ? await transfer.ToDtoAsync(catalog, cancellationToken)
            : InventoryErrors.TransferNotFound;
}

// ─────────────────────────────── Existencias, kardex, documentos ───────────────────────────────

public sealed record DocumentSummaryDto(Guid Id, string Number, string Kind, string Status, Guid WarehouseId, DateTimeOffset CreatedAt);

/// <summary>Lista ajustes (ADJUSTMENT), conteos (COUNT) o traslados (TRANSFER) de la sucursal, más recientes primero.</summary>
public sealed record ListDocumentsQuery(string Kind, string? Status) : IQuery<IReadOnlyList<DocumentSummaryDto>>;

internal sealed class ListDocumentsHandler(IInventoryReadModel readModel) : IQueryHandler<ListDocumentsQuery, IReadOnlyList<DocumentSummaryDto>>
{
    public async Task<Result<IReadOnlyList<DocumentSummaryDto>>> Handle(ListDocumentsQuery request, CancellationToken cancellationToken) =>
        (await readModel.ListDocumentsAsync(request.Kind, request.Status, cancellationToken))
            .Select(d => new DocumentSummaryDto(d.Id, d.Number, d.Kind, d.Status, d.WarehouseId, d.CreatedAt)).ToList();
}

public sealed record GetStockQuery(Guid? WarehouseId, Guid? ProductId, string? Search, bool BelowMinimumOnly) : IQuery<IReadOnlyList<StockDto>>;

internal sealed class GetStockHandler(IInstallationContext installation, IInventoryReadModel readModel, IPermissionChecker permissions)
    : IQueryHandler<GetStockQuery, IReadOnlyList<StockDto>>
{
    public async Task<Result<IReadOnlyList<StockDto>>> Handle(GetStockQuery request, CancellationToken cancellationToken)
    {
        if (installation.RequireLocal() is { IsFailure: true } local)
        {
            return local.Error;
        }

        var costs = await permissions.HasPermissionAsync(InventoryPermissions.CostView, cancellationToken: cancellationToken);
        return Result.Success(await readModel.GetStockAsync(
            new StockFilter(request.WarehouseId, request.ProductId, request.Search, request.BelowMinimumOnly, installation.BranchId!.Value), costs, cancellationToken));
    }
}

public sealed record GetKardexQuery(Guid WarehouseId, Guid ProductId, DateOnly? From, DateOnly? To) : IQuery<KardexDto>;

internal sealed class GetKardexHandler(IInventoryReadModel readModel, IPermissionChecker permissions) : IQueryHandler<GetKardexQuery, KardexDto>
{
    public async Task<Result<KardexDto>> Handle(GetKardexQuery request, CancellationToken cancellationToken)
    {
        var costs = await permissions.HasPermissionAsync(InventoryPermissions.CostView, cancellationToken: cancellationToken);
        return await readModel.GetKardexAsync(request.WarehouseId, request.ProductId, request.From, request.To, costs, cancellationToken) is { } kardex
            ? kardex
            : InventoryErrors.ProductNotFound;
    }
}

// ─────────────────────────────── Políticas y verificación ───────────────────────────────

public sealed record SetStockPolicyCommand(Guid WarehouseId, Guid ProductId, decimal MinQuantity, decimal? MaxQuantity, decimal? ReorderPoint, decimal? ReorderQuantity)
    : ICommand<StockPolicyDto>;

internal sealed class SetStockPolicyHandler(IInstallationContext installation, IInventoryStore store, InventoryGuards guards, IIdGenerator ids)
    : ICommandHandler<SetStockPolicyCommand, StockPolicyDto>
{
    public async Task<Result<StockPolicyDto>> Handle(SetStockPolicyCommand request, CancellationToken cancellationToken)
    {
        var local = installation.RequireLocal();
        if (local.IsFailure)
        {
            return local.Error;
        }

        var warehouse = await guards.LocalWarehouseAsync(request.WarehouseId, cancellationToken);
        if (warehouse.IsFailure)
        {
            return warehouse.Error;
        }

        var product = await guards.StockableAsync([(request.ProductId, 1m)], cancellationToken);
        if (product.IsFailure)
        {
            return product.Error;
        }

        var policy = await store.GetPolicyAsync(request.WarehouseId, request.ProductId, cancellationToken);
        if (policy is null)
        {
            var created = StockPolicy.Create(ids.NewId(), local.Value.CompanyId, warehouse.Value.BranchId, request.WarehouseId, request.ProductId,
                request.MinQuantity, request.MaxQuantity, request.ReorderPoint, request.ReorderQuantity);
            if (created.IsFailure)
            {
                return created.Error;
            }

            policy = created.Value;
            store.Add(policy);
        }
        else
        {
            var updated = policy.Update(request.MinQuantity, request.MaxQuantity, request.ReorderPoint, request.ReorderQuantity);
            if (updated.IsFailure)
            {
                return updated.Error;
            }
        }

        return new StockPolicyDto(policy.Id, policy.WarehouseId, policy.ProductId, policy.MinQty, policy.MaxQty, policy.ReorderPoint, policy.ReorderQty);
    }
}

public sealed record VerifyStockCommand : ICommand<VerificationDto>;

internal sealed class VerifyStockHandler(IStockVerifier verifier, IActorContext actor) : ICommandHandler<VerifyStockCommand, VerificationDto>
{
    public async Task<Result<VerificationDto>> Handle(VerifyStockCommand request, CancellationToken cancellationToken) =>
        await verifier.VerifyAsync("MANUAL", actor.ActorId, cancellationToken);
}

public sealed record ListVerificationsQuery : IQuery<IReadOnlyList<VerificationDto>>;

internal sealed class ListVerificationsHandler(IStockVerifier verifier) : IQueryHandler<ListVerificationsQuery, IReadOnlyList<VerificationDto>>
{
    public async Task<Result<IReadOnlyList<VerificationDto>>> Handle(ListVerificationsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await verifier.ListAsync(20, cancellationToken));
}

/// <summary>Reconstruye un saldo desde su kardex (acción explícita, auditada como crítica; nunca automática).</summary>
public sealed record RebuildStockCommand(Guid WarehouseId, Guid ProductId, string Reason) : ICommand<StockDto>;

internal sealed class RebuildStockHandler(
    IInstallationContext installation, InventoryGuards guards, IStockVerifier verifier, IInventoryReadModel readModel, IAuditWriter audit)
    : ICommandHandler<RebuildStockCommand, StockDto>
{
    public async Task<Result<StockDto>> Handle(RebuildStockCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return InventoryErrors.NoteRequired;
        }

        var warehouse = await guards.LocalWarehouseAsync(request.WarehouseId, cancellationToken);
        if (warehouse.IsFailure)
        {
            return warehouse.Error;
        }

        var (before, after) = await verifier.RebuildAsync(request.WarehouseId, request.ProductId, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("inventory", "STOCK_BALANCE_REBUILT", "StockBalance", request.ProductId, $"Saldo en {warehouse.Value.Code}",
                $"Saldo reconstruido desde el kardex ({request.Reason.Trim()}): {before.Quantity:0.####} → {after.Quantity:0.####}.",
                new Dictionary<string, object?> { ["quantity"] = before.Quantity, ["total_value"] = before.Value },
                new Dictionary<string, object?> { ["quantity"] = after.Quantity, ["total_value"] = after.Value },
                Severity: AuditSeverity.Critical),
            cancellationToken);
        var stock = await readModel.GetStockAsync(
            new StockFilter(request.WarehouseId, request.ProductId, null, false, installation.BranchId!.Value), includeCosts: true, cancellationToken);
        return stock.Count > 0 ? stock[0] : InventoryErrors.ProductNotFound;
    }
}
