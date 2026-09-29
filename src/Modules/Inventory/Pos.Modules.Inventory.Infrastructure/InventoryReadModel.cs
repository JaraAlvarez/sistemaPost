using System.Globalization;
using Dapper;
using Pos.Infrastructure.Persistence;
using Pos.Application.Abstractions.Installation;
using Pos.Modules.Inventory.Application;
using Pos.Modules.Inventory.Contracts;
using Pos.SharedKernel.Text;

namespace Pos.Modules.Inventory.Infrastructure;

/// <summary>
/// Consultas de pantalla con SQL directo (lectura). Para mostrar nombres cruza el kardex con catalog.products,
/// org.warehouses e identity.users: es un modelo de lectura; las escrituras nunca tocan otros esquemas. Usa la conexión de la
/// petición: dentro de un comando ve lo que ese comando ya cambió.
/// </summary>
internal sealed class InventoryReadModel(PosDbContext context, IInstallationContext installation) : IInventoryReadModel
{
    public const int MaxRows = 5000;

    public async Task<IReadOnlyList<StockDto>> GetStockAsync(StockFilter filter, bool includeCosts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var tokens = TextNormalization.ForSearch(filter.Search).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var search = tokens.Length == 0 ? null : "%" + string.Join('%', tokens.Select(Escape)) + "%";
        var sku = filter.Search?.Trim().ToUpperInvariant();
        var source = filter.BelowMinimumOnly
            ? """
              FROM inventory.stock_policies sp
              JOIN org.warehouses w ON w.id = sp.warehouse_id
              JOIN catalog.products p ON p.id = sp.product_id
              LEFT JOIN inventory.stock_balances b ON b.warehouse_id = sp.warehouse_id AND b.product_id = sp.product_id AND b.lot_id IS NULL
              WHERE sp.deleted_at IS NULL AND sp.branch_id = @branchId AND COALESCE(b.quantity, 0) < sp.min_qty
              """
            : """
              FROM inventory.stock_balances b
              JOIN org.warehouses w ON w.id = b.warehouse_id
              JOIN catalog.products p ON p.id = b.product_id
              LEFT JOIN inventory.stock_policies sp ON sp.warehouse_id = b.warehouse_id AND sp.product_id = b.product_id AND sp.deleted_at IS NULL
              WHERE b.branch_id = @branchId AND b.lot_id IS NULL
              """;
        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        var rows = await connection.QueryAsync<StockRow>(new CommandDefinition(
            $"""
            SELECT w.id AS WarehouseId, w.code AS WarehouseCode, p.id AS ProductId, p.sku AS Sku, p.name AS ProductName, p.base_unit_code AS BaseUnitCode,
                   COALESCE(b.quantity, 0) AS Quantity, COALESCE(b.average_cost, 0) AS AverageCost, COALESCE(b.total_value, 0) AS TotalValue,
                   sp.min_qty AS MinQuantity, b.last_movement_at AS LastMovementAt
            {source}
              AND (@warehouseId::uuid IS NULL OR w.id = @warehouseId)
              AND (@productId::uuid IS NULL OR p.id = @productId)
              AND (@search::text IS NULL OR p.search_text LIKE @search OR p.sku = @sku)
            ORDER BY w.code, p.name COLLATE es_co
            LIMIT {MaxRows}
            """,
            new { branchId = filter.BranchId, filter.WarehouseId, filter.ProductId, search, sku }, transaction: transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new StockDto(
            r.WarehouseId, r.WarehouseCode, r.ProductId, r.Sku, r.ProductName, r.BaseUnitCode, r.Quantity,
            includeCosts ? r.AverageCost : null, includeCosts ? r.TotalValue : null, r.MinQuantity, r.MinQuantity is { } min && r.Quantity < min,
            r.LastMovementAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null))];
    }

    public async Task<KardexDto?> GetKardexAsync(Guid warehouseId, Guid productId, DateOnly? from, DateOnly? to, bool includeCosts, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        var product = await connection.QuerySingleOrDefaultAsync<ProductRow>(new CommandDefinition(
            "SELECT sku AS Sku, name AS Name FROM catalog.products WHERE id = @productId AND company_id = @companyId",
            new { productId, companyId = installation.CompanyId }, transaction: transaction, cancellationToken: cancellationToken));
        if (product is null)
        {
            return null;
        }

        var fromText = from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toText = to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rows = (await connection.QueryAsync<KardexRow>(new CommandDefinition(
            $"""
            SELECT m.seq AS Seq, m.occurred_at AS OccurredAt, m.business_date AS BusinessDate, m.movement_type AS MovementType,
                   m.source_type AS SourceType, m.source_id AS SourceId, m.source_number AS SourceNumber, m.direction AS Direction,
                   m.quantity AS Quantity, m.balance_quantity AS BalanceQuantity, m.unit_cost AS UnitCost, m.total_cost AS TotalCost,
                   m.balance_value AS BalanceValue, m.balance_avg_cost AS BalanceAvgCost, r.name AS Reason, u.display_name AS UserName,
                   lot.lot_number AS LotNumber
            FROM inventory.stock_movements m
            LEFT JOIN inventory.adjustment_reasons r ON r.id = m.reason_id
            LEFT JOIN identity.users u ON u.id = m.user_id
            LEFT JOIN inventory.inventory_lots lot ON lot.id = m.lot_id
            WHERE m.warehouse_id = @warehouseId AND m.product_id = @productId
              AND (@from::date IS NULL OR m.business_date >= @from::date)
              AND (@to::date IS NULL OR m.business_date <= @to::date)
            ORDER BY m.seq
            LIMIT {MaxRows * 2}
            """,
            new { warehouseId, productId, from = fromText, to = toText }, transaction: transaction, cancellationToken: cancellationToken))).ToList();

        decimal opening;
        if (rows.Count > 0)
        {
            opening = rows[0].BalanceQuantity - (rows[0].Direction * rows[0].Quantity);
        }
        else
        {
            opening = await connection.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                """
                SELECT balance_quantity FROM inventory.stock_movements
                WHERE warehouse_id = @warehouseId AND product_id = @productId AND (@from::date IS NULL OR business_date < @from::date)
                ORDER BY seq DESC LIMIT 1
                """,
                new { warehouseId, productId, from = fromText }, transaction: transaction, cancellationToken: cancellationToken)) ?? 0m;
        }

        return new KardexDto(warehouseId, productId, product.Sku, product.Name, opening,
            [.. rows.Select(r => new KardexEntryDto(
                r.Seq, new DateTimeOffset(DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc)), DateOnly.FromDateTime(r.BusinessDate), r.MovementType,
                r.SourceType, r.SourceId, r.SourceNumber, r.Direction > 0 ? r.Quantity : null, r.Direction < 0 ? r.Quantity : null, r.BalanceQuantity,
                includeCosts ? r.UnitCost : null, includeCosts ? r.TotalCost : null, includeCosts ? r.BalanceValue : null,
                includeCosts ? r.BalanceAvgCost : null, r.Reason, r.UserName, r.LotNumber))]);
    }

    public async Task<IReadOnlyList<LotStockDto>> GetLotsAsync(LotFilter filter, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        var limit = filter.ExpiringWithinDays is { } days ? today.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        var rows = await connection.QueryAsync<LotRow>(new CommandDefinition(
            $"""
            SELECT l.id AS LotId, p.id AS ProductId, p.sku AS Sku, p.name AS ProductName, l.lot_number AS LotNumber, l.expiry_date AS ExpiryDate,
                   l.manufactured_date AS ManufacturedDate, w.id AS WarehouseId, w.code AS WarehouseCode, b.quantity AS Quantity
            FROM inventory.stock_balances b
            JOIN inventory.inventory_lots l ON l.id = b.lot_id
            JOIN catalog.products p ON p.id = b.product_id
            JOIN org.warehouses w ON w.id = b.warehouse_id
            WHERE b.branch_id = @branchId AND b.lot_id IS NOT NULL
              AND (@productId::uuid IS NULL OR b.product_id = @productId)
              AND (@warehouseId::uuid IS NULL OR b.warehouse_id = @warehouseId)
              AND (b.quantity > 0 OR (@productId::uuid IS NOT NULL AND @limit::date IS NULL))
              AND (@limit::date IS NULL OR l.expiry_date <= @limit::date)
            ORDER BY l.expiry_date NULLS LAST, p.name COLLATE es_co, l.lot_number
            LIMIT {MaxRows}
            """,
            new { branchId = filter.BranchId, filter.ProductId, filter.WarehouseId, limit }, transaction: transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r =>
        {
            var expiry = r.ExpiryDate is { } e ? DateOnly.FromDateTime(e) : (DateOnly?)null;
            return new LotStockDto(r.LotId, r.ProductId, r.Sku, r.ProductName, r.LotNumber, expiry,
                r.ManufacturedDate is { } m ? DateOnly.FromDateTime(m) : null, r.WarehouseId, r.WarehouseCode, r.Quantity,
                expiry is { } d ? d.DayNumber - today.DayNumber : null);
        })];
    }

    public async Task<IReadOnlyList<(Guid Id, string Number, string Kind, string Status, Guid WarehouseId, DateTimeOffset CreatedAt)>> ListDocumentsAsync(
        string kind, string? status, CancellationToken cancellationToken)
    {
        var (table, warehouse) = (kind ?? string.Empty).ToUpperInvariant() switch
        {
            "COUNT" => ("inventory.inventory_counts", "warehouse_id"),
            "TRANSFER" => ("inventory.stock_transfers", "origin_warehouse_id"),
            _ => ("inventory.inventory_adjustments", "warehouse_id"),
        };
        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        var rows = await connection.QueryAsync<DocumentRow>(new CommandDefinition(
            $"""
            SELECT id AS Id, number AS Number, status AS Status, {warehouse} AS WarehouseId, created_at AS CreatedAt
            FROM {table}
            WHERE branch_id = @branchId AND (@status::text IS NULL OR status = @status)
            ORDER BY created_at DESC
            LIMIT 500
            """,
            new { branchId = installation.BranchId, status = status?.ToUpperInvariant() }, transaction: transaction, cancellationToken: cancellationToken));
        var label = (kind ?? string.Empty).ToUpperInvariant() is "COUNT" or "TRANSFER" ? kind!.ToUpperInvariant() : "ADJUSTMENT";
        return [.. rows.Select(r => (r.Id, r.Number, label, r.Status, r.WarehouseId, new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc))))];
    }

    private static string Escape(string token) =>
        token.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private sealed class StockRow
    {
        public Guid WarehouseId { get; set; }

        public string WarehouseCode { get; set; } = string.Empty;

        public Guid ProductId { get; set; }

        public string Sku { get; set; } = string.Empty;

        public string ProductName { get; set; } = string.Empty;

        public string BaseUnitCode { get; set; } = string.Empty;

        public decimal Quantity { get; set; }

        public decimal AverageCost { get; set; }

        public decimal TotalValue { get; set; }

        public decimal? MinQuantity { get; set; }

        public DateTime? LastMovementAt { get; set; }
    }

    private sealed class KardexRow
    {
        public long Seq { get; set; }

        public DateTime OccurredAt { get; set; }

        public DateTime BusinessDate { get; set; }

        public string MovementType { get; set; } = string.Empty;

        public string SourceType { get; set; } = string.Empty;

        public Guid SourceId { get; set; }

        public string? SourceNumber { get; set; }

        public short Direction { get; set; }

        public decimal Quantity { get; set; }

        public decimal BalanceQuantity { get; set; }

        public decimal UnitCost { get; set; }

        public decimal TotalCost { get; set; }

        public decimal BalanceValue { get; set; }

        public decimal BalanceAvgCost { get; set; }

        public string? Reason { get; set; }

        public string? UserName { get; set; }

        public string? LotNumber { get; set; }
    }

    private sealed class LotRow
    {
        public Guid LotId { get; set; }

        public Guid ProductId { get; set; }

        public string Sku { get; set; } = string.Empty;

        public string ProductName { get; set; } = string.Empty;

        public string LotNumber { get; set; } = string.Empty;

        public DateTime? ExpiryDate { get; set; }

        public DateTime? ManufacturedDate { get; set; }

        public Guid WarehouseId { get; set; }

        public string WarehouseCode { get; set; } = string.Empty;

        public decimal Quantity { get; set; }
    }

    private sealed class ProductRow
    {
        public string Sku { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }

    private sealed class DocumentRow
    {
        public Guid Id { get; set; }

        public string Number { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public Guid WarehouseId { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
