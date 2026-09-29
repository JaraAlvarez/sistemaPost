using System.Data.Common;
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
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("El kardex solo se escribe dentro de la transacción del documento origen.");
        }

        if (posting.Lines.Count == 0)
        {
            return Result.Success<IReadOnlyList<PostedMovement>>([]);
        }

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

        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        var companyId = installation.CompanyId!.Value;
        var userId = actor.ActorId!.Value;
        var now = clock.UtcNow;
        var posted = new List<PostedMovement>();

        // Orden determinista de bloqueo: producto y luego bodega (doc 07). Servicios: sin kardex (RN-INV-10).
        foreach (var line in posting.Lines.Where(l => products[l.ProductId].IsStockable).OrderBy(l => l.ProductId).ThenBy(l => l.WarehouseId))
        {
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

            var before = new StockState(balance.Quantity, balance.TotalValue, balance.AverageCost);
            var direction = MovementRules.Direction(line.MovementType);
            var valuation = direction > 0
                ? StockValuation.Inflow(before, line.Quantity, line.UnitCost, MovementRules.IsValuedInflow(line.MovementType))
                : StockValuation.Outflow(before, line.Quantity);
            if (direction < 0 && valuation.After.Quantity < 0m && !posting.AllowNegativeStock)
            {
                var product = products[line.ProductId];
                return Error.BusinessRule(
                    InventoryErrors.InsufficientStock.Code,
                    $"{InventoryErrors.InsufficientStock.Message} Producto {product.Sku} · {product.Name}: hay {before.Quantity:0.####}, se requieren {line.Quantity:0.####}.");
            }

            var movementId = ids.NewId();
            var seq = await connection.QuerySingleAsync<long>(new CommandDefinition(
                """
                INSERT INTO inventory.stock_movements (
                    id, company_id, node_id, branch_id, warehouse_id, product_id, movement_type, direction, quantity, unit_cost, total_cost,
                    balance_quantity, balance_value, balance_avg_cost, source_type, source_id, source_line_id, source_number, reason_id,
                    packaging_id, packaging_quantity, business_date, occurred_at, user_id)
                VALUES (
                    @movementId, @companyId, @nodeId, @branchId, @warehouseId, @productId, @movementType, @direction, @quantity, @unitCost, @totalCost,
                    @balanceQuantity, @balanceValue, @balanceAvgCost, @sourceType, @sourceId, @sourceLineId, @sourceNumber, @reasonId,
                    @packagingId, @packagingQuantity, @businessDate::date, @now, @userId)
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
                    movementType = MovementRules.Db(line.MovementType),
                    direction = (short)direction,
                    quantity = line.Quantity,
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
                    packagingId = line.PackagingId,
                    packagingQuantity = line.PackagingQuantity,
                    businessDate = posting.BusinessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    now,
                    userId,
                },
                transaction, cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE inventory.stock_balances
                SET quantity = @quantity, total_value = @value, average_cost = @averageCost, last_seq = @seq, last_movement_at = @now
                WHERE id = @id
                """,
                new { quantity = valuation.After.Quantity, value = valuation.After.Value, averageCost = valuation.After.AverageCost, seq, now, id = balance.Id },
                transaction, cancellationToken: cancellationToken));

            posted.Add(new PostedMovement(movementId, seq, line.WarehouseId, line.ProductId, line.MovementType, line.Quantity, valuation.UnitCost,
                valuation.TotalCost, valuation.After.Quantity, valuation.After.Value, valuation.After.AverageCost, line.SourceLineId));
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
                        m.MovementId, m.Seq, m.WarehouseId, m.ProductId, Type = MovementRules.Db(m.MovementType), Direction = MovementRules.Direction(m.MovementType),
                        m.Quantity, m.UnitCost, m.TotalCost, m.BalanceQuantity, m.BalanceValue, m.BalanceAverageCost,
                    }),
                },
                OutboxDestination.Sync);
        }

        return posted;
    }

    private sealed class BalanceRow
    {
        public Guid Id { get; set; }

        public decimal Quantity { get; set; }

        public decimal TotalValue { get; set; }

        public decimal AverageCost { get; set; }
    }
}
