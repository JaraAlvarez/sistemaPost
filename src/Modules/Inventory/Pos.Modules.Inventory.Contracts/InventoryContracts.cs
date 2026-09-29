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
    public const string AdjustmentQuick = "inventory.adjustment.quick";

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
        new(AdjustmentQuick, "Ajuste rápido de un producto desde la caja cuando el sistema no tiene existencias (admite autorización de supervisor)",
            isSensitive: true),
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

    /// <summary>Movimiento inverso de otro (anulación de un documento): dirección y costo contrarios al original.</summary>
    Reversal,
}

/// <summary>
/// Línea a publicar en el kardex. <c>Quantity</c> siempre positiva y en la unidad BASE. <c>UnitCost</c>: obligatorio en
/// las entradas valorizadas (saldo inicial, compra, traslado de entrada, devoluciones); en la devolución a proveedor es
/// el costo de la compra (salida valorizada, D5-07); en las demás se usa el costo promedio vigente.
/// <c>LotId</c>: lote de la entrada o de la salida; una salida sin lote de un producto con lotes se reparte FEFO.
/// <c>ReversesMovementId</c>: solo con <see cref="MovementType.Reversal"/> (cantidad, lote y costo salen del original).
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
    decimal? PackagingQuantity = null,
    Guid? LotId = null,
    Guid? ReversesMovementId = null);

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
    Guid? SourceLineId,
    Guid? LotId = null);

/// <summary>Anulación de un documento: revierte todos sus movimientos aún no revertidos (nunca deja saldos negativos).</summary>
public sealed record InventoryReversal(
    string OriginalSourceType,
    Guid OriginalSourceId,
    string SourceType,
    Guid SourceId,
    string? SourceNumber,
    Guid BranchId,
    DateOnly BusinessDate);

/// <summary>
/// Único punto de entrada al kardex (doc 07): valida, bloquea los saldos en orden (producto, bodega), calcula el costo
/// promedio y registra movimiento y saldo en la transacción del documento origen. Los servicios no generan movimientos.
/// Si alguna salida deja saldo negativo y no está permitido, no registra nada y devuelve INVENTORY.INSUFFICIENT_STOCK.
/// </summary>
public interface IInventoryPosting
{
    Task<Result<IReadOnlyList<PostedMovement>>> PostAsync(InventoryPosting posting, CancellationToken cancellationToken = default);

    /// <summary>
    /// Movimientos inversos (<see cref="MovementType.Reversal"/>) de todo lo que el documento original registró, al mismo
    /// costo y en el mismo lote. Si revertir deja un saldo o un lote negativo no registra nada (RN-PUR-05).
    /// </summary>
    Task<Result<IReadOnlyList<PostedMovement>>> ReverseAsync(InventoryReversal reversal, CancellationToken cancellationToken = default);
}

/// <summary>Lotes de la sucursal local (D5-05): el número es único por sucursal y producto.</summary>
public interface IInventoryLots
{
    /// <summary>
    /// Lote existente o nuevo. Exige fecha de vencimiento si el producto la controla; un lote existente conserva su
    /// vencimiento (si se indica otro, INVENTORY.LOT_EXPIRY_MISMATCH).
    /// </summary>
    Task<Result<Guid>> EnsureLotAsync(Guid productId, string lotNumber, DateOnly? expiryDate, DateOnly? manufacturedDate, CancellationToken cancellationToken = default);

    /// <summary>Lote existente de la sucursal local por número.</summary>
    Task<Guid?> FindLotAsync(Guid productId, string lotNumber, CancellationToken cancellationToken = default);
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

    /// <summary>
    /// Existencias para vender en una bodega (Fase 7, RN-SAL-17/18): saldo del producto y cantidad en lotes vencidos a la
    /// fecha (FEFO los consume primero). Los productos sin saldo no aparecen.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, SaleAvailability>> GetSaleAvailabilityAsync(
        Guid warehouseId, IReadOnlyCollection<Guid> productIds, DateOnly today, CancellationToken cancellationToken = default);
}

/// <summary>Saldo de un producto en una bodega y cantidad que está en lotes vencidos.</summary>
public sealed record SaleAvailability(decimal OnHand, decimal ExpiredQuantity);

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
    string? UserName,
    string? LotNumber = null);

public sealed record KardexDto(Guid WarehouseId, Guid ProductId, string Sku, string ProductName, decimal OpeningQuantity, IReadOnlyList<KardexEntryDto> Entries);

public sealed record AdjustmentLineDto(
    Guid Id, int LineNumber, Guid ProductId, string Sku, string ProductName, decimal Quantity, decimal? UnitCost, string? Notes,
    string? LotNumber = null, DateOnly? ExpiryDate = null);

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

/// <summary>Diferencia saldo ↔ kardex. Con <c>LotId</c>: la cantidad del lote no coincide con sus movimientos (el valor es 0).</summary>
public sealed record StockDiscrepancyDto(
    Guid WarehouseId, Guid ProductId, decimal BalanceQuantity, decimal KardexQuantity, decimal BalanceValue, decimal KardexValue, Guid? LotId = null);

/// <summary>Existencia de un lote en una bodega. <c>DaysToExpiry</c> negativo = vencido.</summary>
public sealed record LotStockDto(
    Guid LotId,
    Guid ProductId,
    string Sku,
    string ProductName,
    string LotNumber,
    DateOnly? ExpiryDate,
    DateOnly? ManufacturedDate,
    Guid WarehouseId,
    string WarehouseCode,
    decimal Quantity,
    int? DaysToExpiry);

public sealed record VerificationDto(Guid Id, string Kind, DateTimeOffset StartedAt, int CheckedBalances, int Discrepancies, IReadOnlyList<StockDiscrepancyDto> Details);

public sealed record StockPolicyDto(Guid Id, Guid WarehouseId, Guid ProductId, decimal MinQuantity, decimal? MaxQuantity, decimal? ReorderPoint, decimal? ReorderQuantity);
