using Pos.Application.Abstractions.Security;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Inventory.Contracts;

/// <summary>Permisos del módulo Inventory (docs/fases/fase-04-propuesta.md §7).</summary>
public static class InventoryPermissions
{
    public const string StockView = "inventory.stock.view";
    public const string CostView = "inventory.cost.view";
    public const string AdjustmentManage = "inventory.adjustment.manage";
    public const string AdjustmentApprove = "inventory.adjustment.approve";
    public const string CountManage = "inventory.count.manage";
    public const string CountRegister = "inventory.count.register";
    public const string CountApprove = "inventory.count.approve";
    public const string TransferManage = "inventory.transfer.manage";
    public const string StockVerify = "inventory.stock.verify";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(StockView, "Consultar existencias por bodega (sin costos)", isSensitive: false),
        new(CostView, "Ver costos, valor del inventario y kardex valorizado", isSensitive: true),
        new(AdjustmentManage, "Crear ajustes de inventario y saldos iniciales", isSensitive: true),
        new(AdjustmentApprove, "Aprobar ajustes que superan el umbral", isSensitive: true),
        new(CountManage, "Crear, iniciar, revisar y cerrar conteos físicos", isSensitive: false),
        new(CountRegister, "Registrar cantidades contadas", isSensitive: false),
        new(CountApprove, "Aprobar un conteo físico (genera los ajustes)", isSensitive: true),
        new(TransferManage, "Crear, despachar y recibir traslados entre bodegas", isSensitive: false),
        new(StockVerify, "Verificar y reconstruir saldos contra el kardex", isSensitive: true),
    ];
}

/// <summary>Tipos de movimiento del kardex (doc 07). Cada uno tiene dirección fija, salvo la reversión.</summary>
public enum MovementType
{
    InitialBalance,
    PurchaseReceipt,
    Sale,
    SaleVoid,
    CustomerReturn,
    CustomerReturnDamaged,
    SupplierReturn,
    AdjustmentIn,
    AdjustmentOut,
    Loss,
    Damage,
    Expiry,
    InternalUse,
    TransferOut,
    TransferIn,
    CountAdjustmentIn,
    CountAdjustmentOut,
}

/// <summary>
/// Línea a publicar en el kardex. <c>Quantity</c> siempre positiva y en la unidad BASE. <c>UnitCost</c>: obligatorio en
/// las entradas valorizadas (saldo inicial, compra, traslado de entrada, devoluciones); en las demás se usa el costo
/// promedio vigente.
/// </summary>
public sealed record PostingLine(
    Guid WarehouseId,
    Guid ProductId,
    MovementType MovementType,
    decimal Quantity,
    decimal? UnitCost = null,
    Guid? SourceLineId = null,
    Guid? ReasonId = null,
    Guid? PackagingId = null,
    decimal? PackagingQuantity = null);

/// <summary>Documento que origina los movimientos (ajuste, conteo, traslado, compra, venta…).</summary>
public sealed record InventoryPosting(
    string SourceType,
    Guid SourceId,
    string? SourceNumber,
    Guid BranchId,
    DateOnly BusinessDate,
    IReadOnlyList<PostingLine> Lines,
    bool AllowNegativeStock = false);

/// <summary>Movimiento registrado: costo aplicado y saldo resultante.</summary>
public sealed record PostedMovement(
    Guid MovementId,
    long Seq,
    Guid WarehouseId,
    Guid ProductId,
    MovementType MovementType,
    decimal Quantity,
    decimal UnitCost,
    decimal TotalCost,
    decimal BalanceQuantity,
    decimal BalanceValue,
    decimal BalanceAverageCost,
    Guid? SourceLineId);

/// <summary>
/// Único punto de entrada al kardex (doc 07): valida, bloquea los saldos en orden (producto, bodega), calcula el costo
/// promedio y registra movimiento y saldo en la transacción del documento origen. Los servicios no generan movimientos.
/// Si alguna salida deja saldo negativo y no está permitido, no registra nada y devuelve INVENTORY.INSUFFICIENT_STOCK.
/// </summary>
public interface IInventoryPosting
{
    Task<Result<IReadOnlyList<PostedMovement>>> PostAsync(InventoryPosting posting, CancellationToken cancellationToken = default);
}

/// <summary>Consultas de inventario para otros módulos (catálogo).</summary>
public interface IInventoryQueries
{
    Task<bool> HasMovementsAsync(Guid productId, CancellationToken cancellationToken = default);

    /// <summary>Existencias totales del producto en las bodegas de este nodo.</summary>
    Task<decimal> GetOnHandAsync(Guid productId, CancellationToken cancellationToken = default);

    /// <summary>Costo promedio del producto en la sucursal (Σ valor ÷ Σ cantidad de sus bodegas con saldo positivo).</summary>
    Task<decimal?> GetAverageCostAsync(Guid productId, Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>Costos promedio de varios productos en la sucursal (los que no tienen saldo positivo no aparecen).</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetAverageCostsAsync(IReadOnlyCollection<Guid> productIds, Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>Productos (de la lista) que tienen al menos un movimiento de inventario.</summary>
    Task<IReadOnlySet<Guid>> GetProductsWithMovementsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default);
}

public sealed record AdjustmentReasonDto(Guid Id, string Code, string Name, string Kind, bool RequiresNote, bool IsSystem, string Status);

/// <summary>Existencia de un producto en una bodega. Costos en null si el usuario no tiene inventory.cost.view.</summary>
public sealed record StockDto(
    Guid WarehouseId,
    string WarehouseCode,
    Guid ProductId,
    string Sku,
    string ProductName,
    string BaseUnitCode,
    decimal Quantity,
    decimal? AverageCost,
    decimal? TotalValue,
    decimal? MinQuantity,
    bool BelowMinimum,
    DateTimeOffset? LastMovementAt);

public sealed record KardexEntryDto(
    long Seq,
    DateTimeOffset OccurredAt,
    DateOnly BusinessDate,
    string MovementType,
    string SourceType,
    Guid SourceId,
    string? SourceNumber,
    decimal? QuantityIn,
    decimal? QuantityOut,
    decimal BalanceQuantity,
    decimal? UnitCost,
    decimal? TotalCost,
    decimal? BalanceValue,
    decimal? BalanceAverageCost,
    string? Reason,
    string? UserName);

public sealed record KardexDto(Guid WarehouseId, Guid ProductId, string Sku, string ProductName, decimal OpeningQuantity, IReadOnlyList<KardexEntryDto> Entries);

public sealed record AdjustmentLineDto(Guid Id, int LineNumber, Guid ProductId, string Sku, string ProductName, decimal Quantity, decimal? UnitCost, string? Notes);

public sealed record AdjustmentDto(
    Guid Id,
    string Number,
    Guid BranchId,
    Guid WarehouseId,
    DateOnly BusinessDate,
    Guid ReasonId,
    string ReasonCode,
    string Status,
    string? Notes,
    decimal? TotalValue,
    bool ApprovalRequired,
    Guid CreatedBy,
    Guid? ApprovedBy,
    DateTimeOffset? PostedAt,
    IReadOnlyList<AdjustmentLineDto> Lines);

public sealed record CountLineDto(
    Guid ProductId,
    string Sku,
    string ProductName,
    decimal? SystemQuantity,
    decimal? CountedQuantity,
    decimal? ExpectedQuantity,
    decimal? Difference,
    bool Counted,
    bool NeedsRecount);

public sealed record CountDto(
    Guid Id,
    string Number,
    Guid WarehouseId,
    string CountType,
    bool IsBlind,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? PostedAt,
    int ProductCount,
    int CountedProducts,
    IReadOnlyList<CountLineDto> Lines);

public sealed record TransferLineDto(Guid ProductId, string Sku, string ProductName, decimal QuantitySent, decimal? QuantityReceived, decimal? UnitCost);

public sealed record TransferDto(
    Guid Id,
    string Number,
    Guid OriginWarehouseId,
    Guid DestinationWarehouseId,
    string Status,
    string? Notes,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? ReceivedAt,
    IReadOnlyList<TransferLineDto> Lines);

public sealed record StockDiscrepancyDto(Guid WarehouseId, Guid ProductId, decimal BalanceQuantity, decimal KardexQuantity, decimal BalanceValue, decimal KardexValue);

public sealed record VerificationDto(Guid Id, string Kind, DateTimeOffset StartedAt, int CheckedBalances, int Discrepancies, IReadOnlyList<StockDiscrepancyDto> Details);

public sealed record StockPolicyDto(Guid Id, Guid WarehouseId, Guid ProductId, decimal MinQuantity, decimal? MaxQuantity, decimal? ReorderPoint, decimal? ReorderQuantity);
