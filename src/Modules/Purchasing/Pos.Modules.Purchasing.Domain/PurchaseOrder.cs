using Pos.SharedKernel;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Finance;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Purchasing.Domain;

public enum PurchaseOrderStatus
{
    Draft,
    Approved,
    Sent,
    PartiallyReceived,
    Received,
    Closed,
    Cancelled,
}

public sealed record OrderLineInput(Guid ProductId, Guid? PackagingId, decimal Factor, decimal Quantity, decimal UnitCost);

/// <summary>Línea de la orden: cantidades en presentación y en unidad base; lo recibido en unidad base.</summary>
public sealed class PurchaseOrderLine : Entity<Guid>
{
    private PurchaseOrderLine(Guid id)
        : base(id)
    {
    }

    private PurchaseOrderLine(Guid id, int lineNumber, OrderLineInput input)
        : base(id)
    {
        LineNumber = lineNumber;
        ProductId = input.ProductId;
        PackagingId = input.PackagingId;
        Factor = input.Factor;
        Quantity = input.Quantity;
        BaseQuantity = decimal.Round(input.Quantity * input.Factor, RoundingPolicy.QuantityDecimals, MidpointRounding.AwayFromZero);
        UnitCost = input.UnitCost;
    }

    public int LineNumber { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid? PackagingId { get; private set; }

    public decimal Factor { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal BaseQuantity { get; private set; }

    public decimal UnitCost { get; private set; }

    public decimal ReceivedBaseQuantity { get; private set; }

    public decimal PendingBaseQuantity => Math.Max(0m, BaseQuantity - ReceivedBaseQuantity);

    internal static PurchaseOrderLine Create(Guid id, int lineNumber, OrderLineInput input) => new(id, lineNumber, input);

    internal void AddReceived(decimal baseQuantity) => ReceivedBaseQuantity += baseQuantity;
}

/// <summary>
/// Orden de compra: <c>DRAFT</c> → <c>APPROVED</c> → <c>SENT</c> → <c>PARTIALLY_RECEIVED</c> / <c>RECEIVED</c> → <c>CLOSED</c>;
/// <c>CANCELLED</c> mientras no tenga recepciones. Lo recibido por las compras no supera lo pedido más la tolerancia
/// (RN-PUR-04); anular una compra devuelve la orden a pendiente.
/// </summary>
[Audited("purchasing")]
public sealed class PurchaseOrder : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private readonly List<PurchaseOrderLine> _lines = [];

    private PurchaseOrder(Guid id, Guid companyId, Guid branchId, Guid warehouseId, Guid supplierId, string number)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        WarehouseId = warehouseId;
        SupplierId = supplierId;
        Number = number;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid SupplierId { get; private set; }

    public string Number { get; private set; }

    public DateOnly OrderDate { get; private set; }

    public DateOnly? ExpectedDate { get; private set; }

    public PurchaseOrderStatus Status { get; private set; } = PurchaseOrderStatus.Draft;

    public string? Notes { get; private set; }

    public decimal Total { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public IReadOnlyList<PurchaseOrderLine> Lines => _lines;

    public bool IsReceivable => Status is PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent or PurchaseOrderStatus.PartiallyReceived;

    public string AuditLabel => $"Orden de compra {Number}";

    public static Result<PurchaseOrder> Create(
        Guid id, Guid companyId, Guid branchId, Guid warehouseId, Guid supplierId, string number, DateOnly orderDate, DateOnly? expectedDate,
        string? notes, IReadOnlyList<OrderLineInput> lines, Func<Guid> newId)
    {
        var order = new PurchaseOrder(id, companyId, branchId, warehouseId, supplierId, number);
        var result = order.Edit(orderDate, expectedDate, notes, lines, newId);
        return result.IsSuccess ? order : result.Error;
    }

    public Result Edit(DateOnly orderDate, DateOnly? expectedDate, string? notes, IReadOnlyList<OrderLineInput> lines, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(newId);
        if (Status != PurchaseOrderStatus.Draft)
        {
            return PurchasingErrors.InvalidStatus;
        }

        if (lines.Count == 0)
        {
            return PurchasingErrors.NoLines;
        }

        if (lines.Select(l => (l.ProductId, l.PackagingId)).Distinct().Count() != lines.Count)
        {
            return PurchasingErrors.DuplicatedLine;
        }

        if (lines.Any(l => l.Quantity <= 0m || l.Factor <= 0m || l.UnitCost < 0m || !Guard.HasAtMostDecimals(l.Quantity, RoundingPolicy.QuantityDecimals)))
        {
            return PurchasingErrors.InvalidLine;
        }

        if (expectedDate < orderDate || notes?.Trim().Length > 500)
        {
            return PurchasingErrors.InvalidHeader;
        }

        OrderDate = orderDate;
        ExpectedDate = expectedDate;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        _lines.Clear();
        _lines.AddRange(lines.Select((l, i) => PurchaseOrderLine.Create(newId(), i + 1, l)));
        Total = _lines.Sum(l => RoundingPolicy.Colombia.RoundMoney(l.Quantity * l.UnitCost));
        return Result.Success();
    }

    public Result Approve(Guid userId, DateTimeOffset now)
    {
        if (Status != PurchaseOrderStatus.Draft)
        {
            return PurchasingErrors.InvalidStatus;
        }

        Status = PurchaseOrderStatus.Approved;
        ApprovedAt = now;
        ApprovedBy = userId;
        return Result.Success();
    }

    public Result MarkSent(DateTimeOffset now)
    {
        if (Status != PurchaseOrderStatus.Approved)
        {
            return PurchasingErrors.InvalidStatus;
        }

        Status = PurchaseOrderStatus.Sent;
        SentAt = now;
        return Result.Success();
    }

    /// <summary>Registra lo recibido por una compra contabilizada (en unidad base).</summary>
    public Result RegisterReceipt(Guid orderLineId, decimal baseQuantity, decimal tolerancePercent)
    {
        if (!IsReceivable)
        {
            return PurchasingErrors.OrderNotReceivable;
        }

        var line = _lines.SingleOrDefault(l => l.Id == orderLineId);
        if (line is null)
        {
            return PurchasingErrors.OrderLineMismatch;
        }

        var allowed = line.BaseQuantity * (1m + (tolerancePercent / 100m));
        if (line.ReceivedBaseQuantity + baseQuantity > allowed)
        {
            return PurchasingErrors.ReceiptExceedsOrder;
        }

        line.AddReceived(baseQuantity);
        RefreshReceiptStatus();
        return Result.Success();
    }

    /// <summary>Anulación de una compra: lo recibido vuelve a quedar pendiente.</summary>
    public void RevertReceipt(Guid orderLineId, decimal baseQuantity)
    {
        var line = _lines.SingleOrDefault(l => l.Id == orderLineId) ?? throw new DomainException("La línea no pertenece a la orden.");
        line.AddReceived(-Math.Min(baseQuantity, line.ReceivedBaseQuantity));
        if (Status != PurchaseOrderStatus.Closed)
        {
            RefreshReceiptStatus();
        }
    }

    public Result Close(DateTimeOffset now)
    {
        if (Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent or PurchaseOrderStatus.PartiallyReceived or PurchaseOrderStatus.Received))
        {
            return PurchasingErrors.InvalidStatus;
        }

        Status = PurchaseOrderStatus.Closed;
        ClosedAt = now;
        return Result.Success();
    }

    public Result Cancel(DateTimeOffset now)
    {
        if (_lines.Any(l => l.ReceivedBaseQuantity > 0m))
        {
            return PurchasingErrors.OrderHasReceipts;
        }

        if (Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent))
        {
            return PurchasingErrors.InvalidStatus;
        }

        Status = PurchaseOrderStatus.Cancelled;
        CancelledAt = now;
        return Result.Success();
    }

    private void RefreshReceiptStatus()
    {
        if (_lines.All(l => l.ReceivedBaseQuantity >= l.BaseQuantity))
        {
            Status = PurchaseOrderStatus.Received;
        }
        else if (_lines.Any(l => l.ReceivedBaseQuantity > 0m))
        {
            Status = PurchaseOrderStatus.PartiallyReceived;
        }
        else
        {
            Status = SentAt is null ? PurchaseOrderStatus.Approved : PurchaseOrderStatus.Sent;
        }
    }
}
