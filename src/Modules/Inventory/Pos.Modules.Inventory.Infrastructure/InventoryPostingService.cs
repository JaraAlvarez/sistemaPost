using System.Data.Common;
using System.Globalization;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Inventory.Application;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Inventory.Domain;
using Pos.Modules.Organization.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Inventory.Infrastructure;

/// <summary>Conexión y transacción del contexto EF de la petición, para SQL directo dentro de la misma transacción.</summary>
internal static class DbSession
{
    public static async Task<(DbConnection Connection, DbTransaction? Transaction)> OpenAsync(PosDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }
}

/// <summary>
/// Implementación de <see cref="IInventoryPosting"/> (doc 07, D4-01): por cada línea, en orden (producto, bodega) para
/// evitar deadlocks entre cajas, crea la fila de saldo si falta, la bloquea (FOR UPDATE), valoriza con
/// <see cref="StockValuation"/>, inserta el movimiento (solo inserción) y actualiza el saldo, todo en la transacción del
/// documento origen. Publica un evento SYNC con los movimientos.
/// <para>
/// Lotes (D5-05/D5-06): la fila del producto es el saldo valorizado; cada lote tiene su fila de cantidad. El bloqueo de
/// la fila del producto serializa también sus lotes (todo cambio de un lote pasa primero por ella). Una salida sin lote de
/// un producto con lotes se reparte FEFO (primero el lote que vence antes) en un movimiento por lote; lo que no cubren los
/// lotes sale sin lote (existencias anteriores al control de lotes).
/// </para>
/// </summary>
internal sealed class InventoryPostingService(
    PosDbContext context,
    ICatalogReader catalog,
    IWarehouseDirectory warehouses,
    IInstallationContext installation,
    IActorContext actor,
    IOutbox outbox,
    IIdGenerator ids,
    IClock clock) : IInventoryPosting
{
    public const string SyncEventType = "inventory.movements_posted.v1";

    public async Task<Result<IReadOnlyList<PostedMovement>>> PostAsync(InventoryPosting posting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(posting);
        EnsureTransaction();
        if (posting.Lines.Count == 0)
        {
            return Result.Success<IReadOnlyList<PostedMovement>>([]);
        }

        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        var originals = await LoadOriginalsAsync(
            connection, transaction, [.. posting.Lines.Where(l => l.ReversesMovementId is not null).Select(l => l.ReversesMovementId!.Value)], cancellationToken);
        return await PostLinesAsync(connection, transaction, posting, originals, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<PostedMovement>>> ReverseAsync(InventoryReversal reversal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reversal);
        EnsureTransaction();
        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        var ids = (await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT m.id FROM inventory.stock_movements m
            WHERE m.source_type = @sourceType AND m.source_id = @sourceId
              AND NOT EXISTS (SELECT 1 FROM inventory.stock_movements r WHERE r.reverses_movement_id = m.id)
            ORDER BY m.seq
            """,
            new { sourceType = reversal.OriginalSourceType, sourceId = reversal.OriginalSourceId }, transaction, cancellationToken: cancellationToken))).ToList();
        if (ids.Count == 0)
        {
            return InventoryErrors.NothingToReverse;
        }

        var originals = await LoadOriginalsAsync(connection, transaction, ids, cancellationToken);
        var lines = originals.Values
            .Select(o => new PostingLine(o.WarehouseId, o.ProductId, MovementType.Reversal, o.Quantity, SourceLineId: o.SourceLineId, ReversesMovementId: o.Id))
            .ToList();
        return await PostLinesAsync(
            connection, transaction,
            new InventoryPosting(reversal.SourceType, reversal.SourceId, reversal.SourceNumber, reversal.BranchId, reversal.BusinessDate, lines, AllowNegativeStock: false),
            originals, cancellationToken);
    }

    private void EnsureTransaction()
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("El kardex solo se escribe dentro de la transacción del documento origen.");
        }
    }

    private async Task<Result<IReadOnlyList<PostedMovement>>> PostLinesAsync(
        DbConnection connection, DbTransaction? transaction, InventoryPosting posting, IReadOnlyDictionary<Guid, OriginalMovement> originals,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(posting, originals, cancellationToken);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var products = validation.Value;
        var companyId = installation.CompanyId!.Value;
        var userId = actor.ActorId!.Value;
        var now = clock.UtcNow;
        var posted = new List<PostedMovement>();
        var directions = new Dictionary<Guid, int>();

        // Orden determinista de bloqueo: producto y luego bodega (doc 07). Servicios: sin kardex (RN-INV-10).
        foreach (var line in posting.Lines.Where(l => products[l.ProductId].IsStockable).OrderBy(l => l.ProductId).ThenBy(l => l.WarehouseId))
        {
            var product = products[line.ProductId];
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO inventory.stock_balances (id, company_id, node_id, branch_id, warehouse_id, product_id)
                VALUES (@id, @companyId, @nodeId, @branchId, @warehouseId, @productId)
                ON CONFLICT (warehouse_id, product_id, lot_id) DO NOTHING
                """,
                new { id = ids.NewId(), companyId, nodeId = installation.NodeId, branchId = posting.BranchId, line.WarehouseId, line.ProductId },
                transaction, cancellationToken: cancellationToken));

            var balance = await connection.QuerySingleAsync<BalanceRow>(new CommandDefinition(
                """
                SELECT id AS Id, quantity AS Quantity, total_value AS TotalValue, average_cost AS AverageCost
                FROM inventory.stock_balances
                WHERE warehouse_id = @warehouseId AND product_id = @productId AND lot_id IS NULL
                FOR UPDATE
                """,
                new { line.WarehouseId, line.ProductId }, transaction, cancellationToken: cancellationToken));

            var original = line.ReversesMovementId is { } reversed ? originals[reversed] : null;
            var direction = original is not null ? -original.Direction : MovementRules.Direction(line.MovementType);
            var segments = await SegmentsAsync(connection, transaction, line, original, direction, product, cancellationToken);
            if (segments.IsFailure)
            {
                return segments.Error;
            }

            var state = new StockState(balance.Quantity, balance.TotalValue, balance.AverageCost);
            foreach (var (lotId, quantity) in segments.Value)
            {
                var valuation = Valuate(state, line, original, direction, quantity);
                if (direction < 0 && valuation.After.Quantity < 0m && !posting.AllowNegativeStock)
                {
                    return Error.BusinessRule(
                        InventoryErrors.InsufficientStock.Code,
                        $"{InventoryErrors.InsufficientStock.Message} Producto {product.Sku} · {product.Name}: hay {state.Quantity:0.####}, se requieren {quantity:0.####}.");
                }

                var movementId = ids.NewId();
                var seq = await InsertMovementAsync(connection, transaction, posting, line, movementId, lotId, direction, quantity, valuation, now, companyId, userId, cancellationToken);
                if (lotId is { } lot)
                {
                    await ApplyLotAsync(connection, transaction, posting.BranchId, line, lot, direction * quantity, seq, now, companyId, cancellationToken);
                }

                state = valuation.After;
                directions[movementId] = direction;
                posted.Add(new PostedMovement(movementId, seq, line.WarehouseId, line.ProductId, line.MovementType, quantity, valuation.UnitCost,
                    valuation.TotalCost, state.Quantity, state.Value, state.AverageCost, line.SourceLineId, lotId));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE inventory.stock_balances
                SET quantity = @quantity, total_value = @value, average_cost = @averageCost, last_seq = @seq, last_movement_at = @now
                WHERE id = @id
                """,
                new { quantity = state.Quantity, value = state.Value, averageCost = state.AverageCost, seq = posted[^1].Seq, now, id = balance.Id },
                transaction, cancellationToken: cancellationToken));
        }

        if (posted.Count > 0)
        {
            outbox.Enqueue(
                SyncEventType,
                new
                {
                    posting.SourceType,
                    posting.SourceId,
                    posting.SourceNumber,
                    posting.BranchId,
                    NodeId = installation.NodeId,
                    BusinessDate = posting.BusinessDate,
                    Movements = posted.Select(m => new
                    {
                        m.MovementId, m.Seq, m.WarehouseId, m.ProductId, m.LotId, Type = MovementRules.Db(m.MovementType),
                        Direction = directions[m.MovementId],
                        m.Quantity, m.UnitCost, m.TotalCost, m.BalanceQuantity, m.BalanceValue, m.BalanceAverageCost,
                    }),
                },
                OutboxDestination.Sync);
        }

        return posted;
    }

    private async Task<Result<IReadOnlyDictionary<Guid, CatalogProductInfo>>> ValidateAsync(
        InventoryPosting posting, IReadOnlyDictionary<Guid, OriginalMovement> originals, CancellationToken cancellationToken)
    {
        var products = await catalog.GetProductsAsync([.. posting.Lines.Select(l => l.ProductId).Distinct()], cancellationToken);
        var warehouseCache = new Dictionary<Guid, WarehouseInfo?>();
        foreach (var line in posting.Lines)
        {
            if (!products.TryGetValue(line.ProductId, out var product))
            {
                return InventoryErrors.ProductNotFound;
            }

            if (line.Quantity <= 0m || (!product.AllowsDecimalQuantity && decimal.Truncate(line.Quantity) != line.Quantity))
            {
                return InventoryErrors.InvalidQuantity;
            }

            if ((line.MovementType == MovementType.Reversal) != (line.ReversesMovementId is not null))
            {
                throw new ArgumentException("Solo una reversión indica el movimiento que revierte.", nameof(posting));
            }

            if (line.ReversesMovementId is { } reversed
                && (!originals.TryGetValue(reversed, out var original) || original.WarehouseId != line.WarehouseId || original.ProductId != line.ProductId))
            {
                return InventoryErrors.AlreadyReversed;
            }

            if (line.LotId is not null && !product.TracksLots)
            {
                return InventoryErrors.LotNotTracked;
            }

            if (!warehouseCache.TryGetValue(line.WarehouseId, out var warehouse))
            {
                warehouse = await warehouses.GetAsync(line.WarehouseId, cancellationToken);
                warehouseCache[line.WarehouseId] = warehouse;
            }

            if (warehouse is null)
            {
                return InventoryErrors.WarehouseNotFound;
            }

            if (warehouse.BranchId != posting.BranchId || warehouse.BranchId != installation.BranchId)
            {
                return InventoryErrors.WarehouseNotLocal;
            }
        }

        return Result.Success(products);
    }

    /// <summary>Lote y cantidad de cada movimiento que genera la línea (una salida FEFO puede partirse en varios lotes).</summary>
    private static async Task<Result<IReadOnlyList<(Guid? LotId, decimal Quantity)>>> SegmentsAsync(
        DbConnection connection, DbTransaction? transaction, PostingLine line, OriginalMovement? original, int direction, CatalogProductInfo product,
        CancellationToken cancellationToken)
    {
        var lotId = original is not null ? original.LotId : line.LotId;
        if (direction > 0 || lotId is not null || !product.TracksLots)
        {
            if (direction < 0 && lotId is { } lot)
            {
                var available = await connection.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                    "SELECT quantity FROM inventory.stock_balances WHERE warehouse_id = @warehouseId AND product_id = @productId AND lot_id = @lot",
                    new { line.WarehouseId, line.ProductId, lot }, transaction, cancellationToken: cancellationToken)) ?? 0m;
                if (available < line.Quantity)
                {
                    return Error.BusinessRule(
                        InventoryErrors.LotInsufficient.Code,
                        $"{InventoryErrors.LotInsufficient.Message} Producto {product.Sku}: el lote tiene {available:0.####}, se requieren {line.Quantity:0.####}.");
                }
            }

            return Result.Success<IReadOnlyList<(Guid?, decimal)>>([(lotId, line.Quantity)]);
        }

        // FEFO: primero el que vence antes (sin vencimiento al final), luego el más antiguo.
        var lots = await connection.QueryAsync<(Guid LotId, decimal Quantity)>(new CommandDefinition(
            """
            SELECT b.lot_id, b.quantity
            FROM inventory.stock_balances b
            JOIN inventory.inventory_lots l ON l.id = b.lot_id
            WHERE b.warehouse_id = @warehouseId AND b.product_id = @productId AND b.lot_id IS NOT NULL AND b.quantity > 0
            ORDER BY l.expiry_date NULLS LAST, l.created_at, l.lot_number
            """,
            new { line.WarehouseId, line.ProductId }, transaction, cancellationToken: cancellationToken));
        var segments = new List<(Guid?, decimal)>();
        var pending = line.Quantity;
        foreach (var (id, quantity) in lots)
        {
            if (pending <= 0m)
            {
                break;
            }

            var take = Math.Min(pending, quantity);
            segments.Add((id, take));
            pending -= take;
        }

        if (pending > 0m)
        {
            segments.Add((null, pending));
        }

        return segments;
    }

    private static Valuation Valuate(StockState state, PostingLine line, OriginalMovement? original, int direction, decimal quantity)
    {
        if (original is not null)
        {
            // Reversión: al costo del movimiento original, en sentido contrario.
            return direction > 0
                ? StockValuation.Inflow(state, quantity, original.UnitCost, affectsAverage: true)
                : StockValuation.ValuedOutflow(state, quantity, original.UnitCost);
        }

        if (direction > 0)
        {
            return StockValuation.Inflow(state, quantity, line.UnitCost, MovementRules.IsValuedInflow(line.MovementType));
        }

        return MovementRules.IsValuedOutflow(line.MovementType) && line.UnitCost is { } cost
            ? StockValuation.ValuedOutflow(state, quantity, cost)
            : StockValuation.Outflow(state, quantity);
    }

    private async Task<long> InsertMovementAsync(
        DbConnection connection, DbTransaction? transaction, InventoryPosting posting, PostingLine line, Guid movementId, Guid? lotId, int direction,
        decimal quantity, Valuation valuation, DateTimeOffset now, Guid companyId, Guid userId, CancellationToken cancellationToken) =>
        await connection.QuerySingleAsync<long>(new CommandDefinition(
            """
            INSERT INTO inventory.stock_movements (
                id, company_id, node_id, branch_id, warehouse_id, product_id, lot_id, movement_type, direction, quantity, unit_cost, total_cost,
                balance_quantity, balance_value, balance_avg_cost, source_type, source_id, source_line_id, source_number, reason_id,
                reverses_movement_id, packaging_id, packaging_quantity, business_date, occurred_at, user_id)
            VALUES (
                @movementId, @companyId, @nodeId, @branchId, @warehouseId, @productId, @lotId, @movementType, @direction, @quantity, @unitCost, @totalCost,
                @balanceQuantity, @balanceValue, @balanceAvgCost, @sourceType, @sourceId, @sourceLineId, @sourceNumber, @reasonId,
                @reversesMovementId, @packagingId, @packagingQuantity, @businessDate::date, @now, @userId)
            RETURNING seq
            """,
            new
            {
                movementId,
                companyId,
                nodeId = installation.NodeId,
                branchId = posting.BranchId,
                line.WarehouseId,
                line.ProductId,
                lotId,
                movementType = MovementRules.Db(line.MovementType),
                direction = (short)direction,
                quantity,
                unitCost = valuation.UnitCost,
                totalCost = valuation.TotalCost,
                balanceQuantity = valuation.After.Quantity,
                balanceValue = valuation.After.Value,
                balanceAvgCost = valuation.After.AverageCost,
                sourceType = posting.SourceType,
                sourceId = posting.SourceId,
                sourceLineId = line.SourceLineId,
                sourceNumber = posting.SourceNumber,
                reasonId = line.ReasonId,
                reversesMovementId = line.ReversesMovementId,
                packagingId = line.PackagingId,
                packagingQuantity = line.PackagingQuantity,
                businessDate = posting.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                now,
                userId,
            },
            transaction, cancellationToken: cancellationToken));

    private async Task ApplyLotAsync(
        DbConnection connection, DbTransaction? transaction, Guid branchId, PostingLine line, Guid lotId, decimal delta, long seq, DateTimeOffset now,
        Guid companyId, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO inventory.stock_balances (id, company_id, node_id, branch_id, warehouse_id, product_id, lot_id)
            VALUES (@id, @companyId, @nodeId, @branchId, @warehouseId, @productId, @lotId)
            ON CONFLICT (warehouse_id, product_id, lot_id) DO NOTHING
            """,
            new { id = ids.NewId(), companyId, nodeId = installation.NodeId, branchId, line.WarehouseId, line.ProductId, lotId },
            transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE inventory.stock_balances SET quantity = quantity + @delta, last_seq = @seq, last_movement_at = @now
            WHERE warehouse_id = @warehouseId AND product_id = @productId AND lot_id = @lotId
            """,
            new { delta, seq, now, line.WarehouseId, line.ProductId, lotId }, transaction, cancellationToken: cancellationToken));
    }

    private static async Task<IReadOnlyDictionary<Guid, OriginalMovement>> LoadOriginalsAsync(
        DbConnection connection, DbTransaction? transaction, List<Guid> movementIds, CancellationToken cancellationToken)
    {
        if (movementIds.Count == 0)
        {
            return new Dictionary<Guid, OriginalMovement>();
        }

        // Los ya revertidos no aparecen: revertir dos veces lo impide también el índice único de reverses_movement_id.
        var rows = await connection.QueryAsync<OriginalMovement>(new CommandDefinition(
            """
            SELECT m.id AS Id, m.warehouse_id AS WarehouseId, m.product_id AS ProductId, m.lot_id AS LotId, m.direction AS Direction,
                   m.quantity AS Quantity, m.unit_cost AS UnitCost, m.source_line_id AS SourceLineId
            FROM inventory.stock_movements m
            WHERE m.id = ANY(@movementIds)
              AND NOT EXISTS (SELECT 1 FROM inventory.stock_movements r WHERE r.reverses_movement_id = m.id)
            """,
            new { movementIds = movementIds.ToArray() }, transaction, cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.Id);
    }

    private sealed class BalanceRow
    {
        public Guid Id { get; set; }

        public decimal Quantity { get; set; }

        public decimal TotalValue { get; set; }

        public decimal AverageCost { get; set; }
    }

    private sealed class OriginalMovement
    {
        public Guid Id { get; set; }

        public Guid WarehouseId { get; set; }

        public Guid ProductId { get; set; }

        public Guid? LotId { get; set; }

        public short Direction { get; set; }

        public decimal Quantity { get; set; }

        public decimal UnitCost { get; set; }

        public Guid? SourceLineId { get; set; }
    }
}
