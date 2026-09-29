using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Application.Abstractions.Installation;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Inventory.Application;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Inventory.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Inventory.Infrastructure;

/// <summary>Mapeo de los documentos y maestros de inventario. El kardex y los saldos se escriben con SQL directo.</summary>
internal sealed class InventoryModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AdjustmentReason>(b =>
        {
            b.ToTable("adjustment_reasons", "inventory");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Ignore(x => x.IsInboundOnly);
            b.Ignore(x => x.IsOutboundOnly);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<InventoryAdjustment>(b =>
        {
            b.ToTable("inventory_adjustments", "inventory");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasMany(x => x.Lines).WithOne().HasForeignKey("AdjustmentId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Lines).HasField("_lines");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<AdjustmentLine>(b =>
        {
            b.ToTable("inventory_adjustment_lines", "inventory");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<InventoryCount>(b =>
        {
            b.ToTable("inventory_counts", "inventory");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.CountType).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.Scope).HasColumnType("jsonb");
            b.HasMany(x => x.Lines).WithOne().HasForeignKey("CountId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Lines).HasField("_lines");
            b.HasMany(x => x.Entries).WithOne().HasForeignKey("CountId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Entries).HasField("_entries");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<CountLine>(b =>
        {
            b.ToTable("inventory_count_lines", "inventory");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<CountEntry>(b =>
        {
            b.ToTable("inventory_count_entries", "inventory");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<StockTransfer>(b =>
        {
            b.ToTable("stock_transfers", "inventory");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasMany(x => x.Lines).WithOne().HasForeignKey("TransferId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Lines).HasField("_lines");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<TransferLine>(b =>
        {
            b.ToTable("stock_transfer_lines", "inventory");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<StockPolicy>(b =>
        {
            b.ToTable("stock_policies", "inventory");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasControlColumns();
            b.HasXminConcurrency();
        });
    }
}

internal sealed class InventoryStore(PosDbContext context) : IInventoryStore
{
    public void Add(AdjustmentReason reason) => context.Add(reason);

    public void Add(InventoryAdjustment adjustment) => context.Add(adjustment);

    public void Add(InventoryCount count) => context.Add(count);

    public void Add(StockTransfer transfer) => context.Add(transfer);

    public void Add(StockPolicy policy) => context.Add(policy);

    public void Remove(object entity) => context.Remove(entity);

    public async Task<IReadOnlyList<AdjustmentReason>> GetReasonsAsync(CancellationToken cancellationToken) =>
        await context.Set<AdjustmentReason>().ToListAsync(cancellationToken);

    public Task<AdjustmentReason?> GetReasonAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<AdjustmentReason>().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<InventoryAdjustment?> GetAdjustmentAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<InventoryAdjustment>().Include(a => a.Lines).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<InventoryCount?> GetCountAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<InventoryCount>().Include(c => c.Lines).Include(c => c.Entries).AsSplitQuery().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<StockTransfer?> GetTransferAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<StockTransfer>().Include(t => t.Lines).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<StockPolicy?> GetPolicyAsync(Guid warehouseId, Guid productId, CancellationToken cancellationToken) =>
        context.Set<StockPolicy>().SingleOrDefaultAsync(p => p.WarehouseId == warehouseId && p.ProductId == productId, cancellationToken);
}

/// <summary>Saldos y kardex con SQL directo en la transacción de la petición.</summary>
internal sealed class StockLedger(PosDbContext context) : IStockLedger
{
    public async Task<IReadOnlyDictionary<Guid, StockState>> GetStatesAsync(
        Guid warehouseId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        var rows = await connection.QueryAsync<StateRow>(new CommandDefinition(
            """
            SELECT product_id AS ProductId, quantity AS Quantity, total_value AS TotalValue, average_cost AS AverageCost, last_seq AS LastSeq
            FROM inventory.stock_balances WHERE warehouse_id = @warehouseId AND product_id = ANY(@productIds) AND lot_id IS NULL
            """,
            new { warehouseId, productIds = productIds.ToArray() }, transaction, cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.ProductId, r => new StockState(r.Quantity, r.TotalValue, r.AverageCost));
    }

    public async Task<IReadOnlyList<(Guid ProductId, decimal SystemQty, long SnapshotSeq)>> SnapshotAsync(
        Guid warehouseId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        var rows = (await connection.QueryAsync<StateRow>(new CommandDefinition(
            """
            SELECT product_id AS ProductId, quantity AS Quantity, total_value AS TotalValue, average_cost AS AverageCost, last_seq AS LastSeq
            FROM inventory.stock_balances WHERE warehouse_id = @warehouseId AND product_id = ANY(@productIds) AND lot_id IS NULL
            """,
            new { warehouseId, productIds = productIds.ToArray() }, transaction, cancellationToken: cancellationToken))).ToDictionary(r => r.ProductId);
        return [.. productIds.Distinct().Select(id => rows.TryGetValue(id, out var row) ? (id, row.Quantity, row.LastSeq) : (id, 0m, 0L))];
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> ExpectedAsync(Guid warehouseId, IReadOnlyCollection<CountLine> lines, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        var deltas = (await connection.QueryAsync<(Guid ProductId, decimal Delta)>(new CommandDefinition(
            """
            SELECT l.product_id, COALESCE(SUM(m.direction * m.quantity), 0)
            FROM unnest(@productIds::uuid[], @seqs::bigint[]) AS l(product_id, snapshot_seq)
            LEFT JOIN inventory.stock_movements m
                ON m.warehouse_id = @warehouseId AND m.product_id = l.product_id AND m.seq > l.snapshot_seq
            GROUP BY l.product_id
            """,
            new { warehouseId, productIds = lines.Select(l => l.ProductId).ToArray(), seqs = lines.Select(l => l.SnapshotSeq).ToArray() },
            transaction, cancellationToken: cancellationToken))).ToDictionary(r => r.ProductId, r => r.Delta);
        return lines.ToDictionary(l => l.ProductId, l => l.SystemQty + deltas.GetValueOrDefault(l.ProductId));
    }

    public async Task<IReadOnlySet<Guid>> ProductsWithMovementsAsync(Guid warehouseId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT DISTINCT product_id FROM inventory.stock_movements WHERE warehouse_id = @warehouseId AND product_id = ANY(@productIds)",
                new { warehouseId, productIds = productIds.ToArray() }, transaction, cancellationToken: cancellationToken)))
            .ToHashSet();
    }

    private sealed class StateRow
    {
        public Guid ProductId { get; set; }

        public decimal Quantity { get; set; }

        public decimal TotalValue { get; set; }

        public decimal AverageCost { get; set; }

        public long LastSeq { get; set; }
    }
}

/// <summary>Contrato <see cref="IInventoryQueries"/> para el catálogo (conexión propia: lee lo confirmado).</summary>
internal sealed class InventoryQueries(NpgsqlDataSource dataSource, IInstallationContext installation) : IInventoryQueries
{
    public async Task<bool> HasMovementsAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM inventory.stock_movements WHERE product_id = @productId)", new { productId }, cancellationToken: cancellationToken));
    }

    public async Task<decimal> GetOnHandAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "SELECT COALESCE(SUM(quantity), 0) FROM inventory.stock_balances WHERE product_id = @productId AND node_id = @nodeId AND lot_id IS NULL",
            new { productId, nodeId = installation.NodeId }, cancellationToken: cancellationToken));
    }

    public async Task<decimal?> GetAverageCostAsync(Guid productId, Guid branchId, CancellationToken cancellationToken = default) =>
        (await GetAverageCostsAsync([productId], branchId, cancellationToken)).TryGetValue(productId, out var cost) ? cost : null;

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetAverageCostsAsync(
        IReadOnlyCollection<Guid> productIds, Guid branchId, CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<(Guid ProductId, decimal Cost)>(new CommandDefinition(
            """
            SELECT product_id, round(SUM(total_value) / SUM(quantity), 4)
            FROM inventory.stock_balances
            WHERE branch_id = @branchId AND product_id = ANY(@productIds) AND quantity > 0 AND lot_id IS NULL
            GROUP BY product_id
            """,
            new { branchId, productIds = productIds.ToArray() }, cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.ProductId, r => r.Cost);
    }

    public async Task<IReadOnlySet<Guid>> GetProductsWithMovementsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT DISTINCT product_id FROM inventory.stock_movements WHERE product_id = ANY(@productIds)",
                new { productIds = productIds.ToArray() }, cancellationToken: cancellationToken)))
            .ToHashSet();
    }
}

/// <summary>Restricciones de la BD → errores de negocio legibles.</summary>
internal sealed class InventoryConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_adjustment_reasons__company_code"] = Error.Conflict("INVENTORY.REASON_CODE_DUPLICATED", "Ya existe un motivo con ese código."),
        ["ux_stock_policies__warehouse_product"] = Error.Conflict("INVENTORY.POLICY_DUPLICATED", "Ya existe una política para ese producto en esa bodega."),
        ["ck_inventory_adjustments__approver"] = InventoryErrors.SelfApproval,
        ["ck_stock_transfer_lines__quantities"] = InventoryErrors.ReceivedExceedsSent,
        ["fk_stock_movements__warehouse"] = InventoryErrors.WarehouseNotLocal,
        ["ux_inventory_adjustment_lines__product"] = InventoryErrors.DuplicatedProduct,
        ["ux_stock_movements__reverses"] = InventoryErrors.AlreadyReversed,
        ["ux_stock_transfer_lines__product"] = InventoryErrors.DuplicatedProduct,
    };
}

public static class InventoryInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, InventoryModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, InventoryConstraintErrors>();
        services.AddScoped<IInventoryStore, InventoryStore>();
        services.AddScoped<IStockLedger, StockLedger>();
        services.AddScoped<IInventoryPosting, InventoryPostingService>();
        services.AddScoped<IInventoryLots, InventoryLots>();
        services.AddScoped<IInventoryQueries, InventoryQueries>();
        services.AddScoped<IInventoryReadModel, InventoryReadModel>();
        services.AddScoped<IStockVerifier, StockVerifier>();
        services.AddHostedService<StockVerificationScheduler>();
    }
}
