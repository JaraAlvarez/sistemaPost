using Pos.SharedKernel;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Finance;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Inventory.Domain;

public enum CountType
{
    Full,
    Partial,
}

public enum CountStatus
{
    Draft,
    InProgress,
    InReview,
    Posted,
    Cancelled,
}

/// <summary>Línea de conteo: saldo teórico y seq del kardex congelados al iniciar (RN-INV-06).</summary>
public sealed class CountLine : Entity<Guid>
{
    private CountLine(Guid id, Guid productId, decimal systemQty, long snapshotSeq)
        : base(id)
    {
        ProductId = productId;
        SystemQty = systemQty;
        SnapshotSeq = snapshotSeq;
    }

    public Guid ProductId { get; private set; }

    public decimal SystemQty { get; private set; }

    public long SnapshotSeq { get; private set; }

    /// <summary>Contado: suma de las capturas del reconteo si lo hay; si no, de la primera ronda. <c>null</c> = no contado.</summary>
    public decimal? CountedQty { get; private set; }

    /// <summary>Teórico al revisar: congelado + movimientos del producto posteriores al congelamiento.</summary>
    public decimal? ExpectedQty { get; private set; }

    public decimal? Difference { get; private set; }

    internal static CountLine Create(Guid id, Guid productId, decimal systemQty, long snapshotSeq) => new(id, productId, systemQty, snapshotSeq);

    internal void Evaluate(decimal? counted, decimal expected)
    {
        CountedQty = counted;
        ExpectedQty = expected;
        Difference = counted is { } c ? c - expected : null;
    }
}

/// <summary>Captura de un contador (varios usuarios cuentan el mismo producto en distintos pasillos).</summary>
public sealed class CountEntry : Entity<Guid>
{
    private CountEntry(Guid id, Guid productId, decimal quantity, short round, Guid countedBy, DateTimeOffset countedAt)
        : base(id)
    {
        ProductId = productId;
        Quantity = quantity;
        Round = round;
        CountedBy = countedBy;
        CountedAt = countedAt;
    }

    public Guid ProductId { get; private set; }

    public decimal Quantity { get; private set; }

    /// <summary>1 = conteo; 2 = reconteo (reemplaza a la primera ronda del producto).</summary>
    public short Round { get; private set; }

    public string? Location { get; private set; }

    public Guid CountedBy { get; private set; }

    public DateTimeOffset CountedAt { get; private set; }

    internal static CountEntry Create(Guid id, Guid productId, decimal quantity, short round, string? location, Guid countedBy, DateTimeOffset countedAt) =>
        new(id, productId, quantity, round, countedBy, countedAt) { Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim() };
}

/// <summary>Diferencia a llevar al kardex al aprobar un conteo.</summary>
public sealed record CountDifference(Guid ProductId, decimal Difference);

/// <summary>
/// Conteo físico sin cerrar la tienda (propuesta §5.4): <c>DRAFT</c> → <c>IN_PROGRESS</c> (congela el teórico) →
/// capturas → <c>IN_REVIEW</c> (diferencias, reconteo) → <c>POSTED</c> (genera los movimientos); <c>CANCELLED</c>.
/// Diferencia = contado − (teórico congelado + movimientos posteriores al congelamiento).
/// </summary>
[Audited("inventory")]
public sealed class InventoryCount : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private readonly List<CountLine> _lines = [];
    private readonly List<CountEntry> _entries = [];

    private InventoryCount(Guid id, Guid companyId, Guid branchId, Guid warehouseId, string number)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        WarehouseId = warehouseId;
        Number = number;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public string Number { get; private set; }

    public CountType CountType { get; private set; }

    public bool IsBlind { get; private set; }

    /// <summary>JSON: <c>{"categoryIds":[…],"productIds":[…]}</c>.</summary>
    [NotAudited]
    public string Scope { get; private set; } = "{}";

    public CountStatus Status { get; private set; } = CountStatus.Draft;

    public string? Notes { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateTimeOffset? PostedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public IReadOnlyList<CountLine> Lines => _lines;

    public IReadOnlyList<CountEntry> Entries => _entries;

    public string AuditLabel => $"Conteo {Number}";

    public static InventoryCount Create(
        Guid id, Guid companyId, Guid branchId, Guid warehouseId, string number, CountType type, bool isBlind, string scopeJson, string? notes) =>
        new(id, companyId, branchId, warehouseId, number)
        {
            CountType = type,
            IsBlind = isBlind,
            Scope = scopeJson,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        };

    /// <summary>Congela el saldo teórico de cada producto del alcance.</summary>
    public Result Start(IReadOnlyList<(Guid ProductId, decimal SystemQty, long SnapshotSeq)> snapshot, Func<Guid> newId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(newId);
        if (Status != CountStatus.Draft)
        {
            return InventoryErrors.InvalidStatus;
        }

        if (snapshot.Count == 0)
        {
            return InventoryErrors.CountEmpty;
        }

        _lines.AddRange(snapshot.Select(s => CountLine.Create(newId(), s.ProductId, s.SystemQty, s.SnapshotSeq)));
        Status = CountStatus.InProgress;
        StartedAt = now;
        return Result.Success();
    }

    /// <summary>Registra una captura: primera ronda en IN_PROGRESS, reconteo en IN_REVIEW.</summary>
    public Result Register(Guid productId, decimal quantity, string? location, Guid userId, DateTimeOffset now, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(newId);
        if (Status is not (CountStatus.InProgress or CountStatus.InReview))
        {
            return InventoryErrors.InvalidStatus;
        }

        if (_lines.All(l => l.ProductId != productId))
        {
            return InventoryErrors.ProductNotInCount;
        }

        if (quantity < 0m || !Guard.HasAtMostDecimals(quantity, RoundingPolicy.QuantityDecimals))
        {
            return InventoryErrors.InvalidQuantity;
        }

        _entries.Add(CountEntry.Create(newId(), productId, quantity, Status == CountStatus.InReview ? (short)2 : (short)1, location, userId, now));
        return Result.Success();
    }

    /// <summary>Contado de un producto: suma del reconteo si existe; si no, de la primera ronda; null si nadie lo contó.</summary>
    public decimal? CountedOf(Guid productId)
    {
        var entries = _entries.Where(e => e.ProductId == productId).ToList();
        if (entries.Count == 0)
        {
            return null;
        }

        var round = entries.Max(e => e.Round);
        return entries.Where(e => e.Round == round).Sum(e => e.Quantity);
    }

    /// <summary>Cierra la captura y calcula las diferencias con el teórico actualizado de cada producto.</summary>
    public Result Review(IReadOnlyDictionary<Guid, decimal> expected, DateTimeOffset now)
    {
        if (Status != CountStatus.InProgress)
        {
            return InventoryErrors.InvalidStatus;
        }

        Evaluate(expected);
        Status = CountStatus.InReview;
        ReviewedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Aprueba: recalcula con el teórico al momento de aprobar y devuelve las diferencias distintas de cero. Los productos
    /// no contados se excluyen o se asumen en cero según <paramref name="uncountedAsZero"/>.
    /// </summary>
    public Result<IReadOnlyList<CountDifference>> Approve(IReadOnlyDictionary<Guid, decimal> expected, bool uncountedAsZero, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (Status != CountStatus.InReview)
        {
            return InventoryErrors.InvalidStatus;
        }

        Evaluate(expected, uncountedAsZero);
        Status = CountStatus.Posted;
        PostedAt = now;
        ApprovedBy = userId;
        return _lines.Where(l => l.Difference is { } d && d != 0m).Select(l => new CountDifference(l.ProductId, l.Difference!.Value)).ToList();
    }

    public Result Cancel(DateTimeOffset now)
    {
        if (Status is CountStatus.Posted or CountStatus.Cancelled)
        {
            return InventoryErrors.InvalidStatus;
        }

        Status = CountStatus.Cancelled;
        CancelledAt = now;
        return Result.Success();
    }

    private void Evaluate(IReadOnlyDictionary<Guid, decimal> expected, bool uncountedAsZero = false)
    {
        foreach (var line in _lines)
        {
            var counted = CountedOf(line.ProductId) ?? (uncountedAsZero ? 0m : null);
            line.Evaluate(counted, expected.TryGetValue(line.ProductId, out var e) ? e : line.SystemQty);
        }
    }
}

public enum TransferStatus
{
    Draft,
    InTransit,
    Received,
    ReceivedWithDifferences,
    Cancelled,
}

public sealed class TransferLine : Entity<Guid>
{
    private TransferLine(Guid id, int lineNumber, Guid productId, decimal quantitySent)
        : base(id)
    {
        LineNumber = lineNumber;
        ProductId = productId;
        QuantitySent = quantitySent;
    }

    public int LineNumber { get; private set; }

    public Guid ProductId { get; private set; }

    public decimal QuantitySent { get; private set; }

    public decimal? QuantityReceived { get; private set; }

    /// <summary>Costo promedio del origen al despachar (el destino recibe a ese costo).</summary>
    public decimal? UnitCost { get; private set; }

    internal static TransferLine Create(Guid id, int lineNumber, Guid productId, decimal quantity) => new(id, lineNumber, productId, quantity);

    internal void SetCost(decimal unitCost) => UnitCost = unitCost;

    internal void SetReceived(decimal quantity) => QuantityReceived = quantity;
}

/// <summary>
/// Traslado entre bodegas de la misma sucursal (propuesta §5.5): <c>DRAFT</c> → despacho (sale del origen y entra a la
/// bodega de tránsito) → <c>IN_TRANSIT</c> → recepción (sale de tránsito y entra al destino; el faltante queda como
/// pérdida) → <c>RECEIVED</c> o <c>RECEIVED_WITH_DIFFERENCES</c>.
/// </summary>
[Audited("inventory")]
public sealed class StockTransfer : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private readonly List<TransferLine> _lines = [];

    private StockTransfer(Guid id, Guid companyId, Guid branchId, string number, Guid originWarehouseId, Guid destinationWarehouseId)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        Number = number;
        OriginWarehouseId = originWarehouseId;
        DestinationWarehouseId = destinationWarehouseId;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    public string Number { get; private set; }

    public Guid OriginWarehouseId { get; private set; }

    public Guid DestinationWarehouseId { get; private set; }

    public TransferStatus Status { get; private set; } = TransferStatus.Draft;

    public string? Notes { get; private set; }

    public DateTimeOffset? DispatchedAt { get; private set; }

    public Guid? DispatchedBy { get; private set; }

    public DateTimeOffset? ReceivedAt { get; private set; }

    public Guid? ReceivedBy { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public IReadOnlyList<TransferLine> Lines => _lines;

    public string AuditLabel => $"Traslado {Number}";

    public static Result<StockTransfer> Create(
        Guid id, Guid companyId, Guid branchId, string number, Guid origin, Guid destination, string? notes,
        IReadOnlyList<(Guid ProductId, decimal Quantity)> lines, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(newId);
        if (origin == destination)
        {
            return InventoryErrors.SameWarehouse;
        }

        if (lines.Count == 0)
        {
            return InventoryErrors.NoLines;
        }

        if (lines.Select(l => l.ProductId).Distinct().Count() != lines.Count)
        {
            return InventoryErrors.DuplicatedProduct;
        }

        if (lines.Any(l => l.Quantity <= 0m || !Guard.HasAtMostDecimals(l.Quantity, RoundingPolicy.QuantityDecimals)))
        {
            return InventoryErrors.InvalidQuantity;
        }

        var transfer = new StockTransfer(id, companyId, branchId, number, origin, destination) { Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim() };
        var lineNumber = 0;
        transfer._lines.AddRange(lines.Select(l => TransferLine.Create(newId(), ++lineNumber, l.ProductId, l.Quantity)));
        return transfer;
    }

    public Result Dispatch(IReadOnlyDictionary<Guid, decimal> unitCosts, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(unitCosts);
        if (Status != TransferStatus.Draft)
        {
            return InventoryErrors.InvalidStatus;
        }

        foreach (var line in _lines)
        {
            line.SetCost(unitCosts.TryGetValue(line.ProductId, out var cost) ? cost : 0m);
        }

        Status = TransferStatus.InTransit;
        DispatchedAt = now;
        DispatchedBy = userId;
        return Result.Success();
    }

    /// <summary>Recibe; lo que no se indique se recibe completo.</summary>
    public Result Receive(IReadOnlyDictionary<Guid, decimal> received, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(received);
        if (Status != TransferStatus.InTransit)
        {
            return InventoryErrors.InvalidStatus;
        }

        foreach (var (productId, quantity) in received)
        {
            var line = _lines.SingleOrDefault(l => l.ProductId == productId);
            if (line is null)
            {
                return InventoryErrors.ProductNotFound;
            }

            if (quantity < 0m || quantity > line.QuantitySent || !Guard.HasAtMostDecimals(quantity, RoundingPolicy.QuantityDecimals))
            {
                return quantity > line.QuantitySent ? InventoryErrors.ReceivedExceedsSent : InventoryErrors.InvalidQuantity;
            }
        }

        foreach (var line in _lines)
        {
            line.SetReceived(received.TryGetValue(line.ProductId, out var quantity) ? quantity : line.QuantitySent);
        }

        Status = _lines.All(l => l.QuantityReceived == l.QuantitySent) ? TransferStatus.Received : TransferStatus.ReceivedWithDifferences;
        ReceivedAt = now;
        ReceivedBy = userId;
        return Result.Success();
    }

    public Result Cancel(DateTimeOffset now)
    {
        if (Status != TransferStatus.Draft)
        {
            return InventoryErrors.InvalidStatus;
        }

        Status = TransferStatus.Cancelled;
        CancelledAt = now;
        return Result.Success();
    }
}
