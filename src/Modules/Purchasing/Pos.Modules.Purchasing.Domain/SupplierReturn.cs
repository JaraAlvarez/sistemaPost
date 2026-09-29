using Pos.SharedKernel;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Finance;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Purchasing.Domain;

public enum SupplierReturnStatus
{
    Draft,
    Posted,
    Settled,
    Cancelled,
}

/// <summary>Cómo liquida el proveedor la devolución.</summary>
public enum ReturnSettlement
{
    /// <summary>Nota crédito: la reducción de la cuenta por pagar ya aplicada queda en firme.</summary>
    CreditNote,

    /// <summary>Reintegro de dinero: el saldo a favor se cancela con un asiento de reintegro.</summary>
    Refund,

    /// <summary>Reposición de la mercancía: vuelve a entrar al costo de la compra y la deuda se restablece.</summary>
    Replacement,
}

public sealed record ReturnLineInput(Guid PurchaseLineId, Guid ProductId, decimal BaseQuantity, decimal UnitCost, Guid? LotId);


public sealed class SupplierReturnLine : Entity<Guid>
{
    private SupplierReturnLine(Guid id)
        : base(id)
    {
    }

    private SupplierReturnLine(Guid id, int lineNumber, ReturnLineInput input)
        : base(id)
    {
        LineNumber = lineNumber;
        PurchaseLineId = input.PurchaseLineId;
        ProductId = input.ProductId;
        BaseQuantity = input.BaseQuantity;
        UnitCost = input.UnitCost;
        LotId = input.LotId;
        Total = RoundingPolicy.Colombia.RoundMoney(input.BaseQuantity * input.UnitCost);
    }

    /// <summary>Lo que se descuenta de la cuenta por pagar: proporción del total de la línea comprada (con impuestos).</summary>
    public decimal CreditAmount { get; private set; }

    public int LineNumber { get; private set; }

    public Guid PurchaseLineId { get; private set; }

    public Guid ProductId { get; private set; }

    public decimal BaseQuantity { get; private set; }

    /// <summary>Costo neto por unidad base de la compra original (D5-07).</summary>
    public decimal UnitCost { get; private set; }

    public decimal Total { get; private set; }

    public Guid? LotId { get; private set; }

    internal static SupplierReturnLine Create(Guid id, int lineNumber, ReturnLineInput input, PurchaseLine purchased) => new(id, lineNumber, input)
    {
        CreditAmount = input.BaseQuantity == purchased.ReturnableBaseQuantity && purchased.ReturnedBaseQuantity == 0m
            ? purchased.LineTotal
            : RoundingPolicy.Colombia.RoundMoney(purchased.LineTotal * input.BaseQuantity / purchased.BaseQuantity),
    };
}

/// <summary>
/// Devolución a proveedor contra una compra: <c>DRAFT</c> → <c>POSTED</c> (salida del kardex al costo de la compra y
/// asiento que reduce la cuenta por pagar) → <c>SETTLED</c> (nota crédito, reintegro o reposición); <c>CANCELLED</c> desde
/// borrador. Cada línea ≤ comprado − devuelto (RN-PUR-06).
/// </summary>
[Audited("purchasing")]
public sealed class SupplierReturn : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private readonly List<SupplierReturnLine> _lines = [];

    private SupplierReturn(Guid id, Guid companyId, Guid branchId, Guid warehouseId, Guid supplierId, Guid purchaseId, string number, string reason)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        WarehouseId = warehouseId;
        SupplierId = supplierId;
        PurchaseId = purchaseId;
        Number = number;
        Reason = reason;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid SupplierId { get; private set; }

    public Guid PurchaseId { get; private set; }

    public string Number { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public string Reason { get; private set; }

    public SupplierReturnStatus Status { get; private set; } = SupplierReturnStatus.Draft;

    public decimal Total { get; private set; }

    /// <summary>Σ de lo que se descuenta de la cuenta por pagar (valor de la nota crédito esperada).</summary>
    public decimal CreditTotal { get; private set; }

    public ReturnSettlement? Settlement { get; private set; }

    public string? SettlementReference { get; private set; }

    public DateTimeOffset? PostedAt { get; private set; }

    public Guid? PostedBy { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    public Guid? SettledBy { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public IReadOnlyList<SupplierReturnLine> Lines => _lines;

    public string AuditLabel => $"Devolución a proveedor {Number}";

    public static Result<SupplierReturn> Create(
        Guid id, Purchase purchase, string number, DateOnly businessDate, string reason, IReadOnlyList<ReturnLineInput> lines, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(newId);
        if (purchase.Status != PurchaseStatus.Posted)
        {
            return PurchasingErrors.InvalidStatus;
        }

        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length is < 5 or > 300)
        {
            return PurchasingErrors.VoidReasonRequired;
        }

        if (lines.Count == 0)
        {
            return PurchasingErrors.NoLines;
        }

        if (lines.Select(l => l.PurchaseLineId).Distinct().Count() != lines.Count)
        {
            return PurchasingErrors.DuplicatedLine;
        }

        foreach (var line in lines)
        {
            var purchased = purchase.Lines.SingleOrDefault(l => l.Id == line.PurchaseLineId);
            if (purchased is null || purchased.ProductId != line.ProductId || line.BaseQuantity <= 0m
                || !Guard.HasAtMostDecimals(line.BaseQuantity, RoundingPolicy.QuantityDecimals))
            {
                return PurchasingErrors.InvalidLine;
            }

            if (line.BaseQuantity > purchased.ReturnableBaseQuantity)
            {
                return PurchasingErrors.ReturnExceedsPurchase;
            }
        }

        var supplierReturn = new SupplierReturn(id, purchase.CompanyId, purchase.BranchId, purchase.WarehouseId, purchase.SupplierId, purchase.Id, number, trimmed)
        {
            BusinessDate = businessDate,
        };
        supplierReturn._lines.AddRange(lines.Select((l, i) => SupplierReturnLine.Create(newId(), i + 1, l, purchase.Lines.Single(p => p.Id == l.PurchaseLineId))));
        supplierReturn.Total = supplierReturn._lines.Sum(l => l.Total);
        supplierReturn.CreditTotal = supplierReturn._lines.Sum(l => l.CreditAmount);
        return supplierReturn;
    }

    public void MarkPosted(Guid userId, DateTimeOffset now)
    {
        if (Status != SupplierReturnStatus.Draft)
        {
            throw new DomainException("La devolución ya fue contabilizada o cancelada.");
        }

        Status = SupplierReturnStatus.Posted;
        PostedAt = now;
        PostedBy = userId;
    }

    public Result Settle(ReturnSettlement settlement, string? reference, Guid userId, DateTimeOffset now)
    {
        if (Status != SupplierReturnStatus.Posted)
        {
            return PurchasingErrors.InvalidStatus;
        }

        var trimmed = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        if (trimmed is null || trimmed.Length > 60)
        {
            return PurchasingErrors.InvalidSettlement;
        }

        Status = SupplierReturnStatus.Settled;
        Settlement = settlement;
        SettlementReference = trimmed;
        SettledAt = now;
        SettledBy = userId;
        return Result.Success();
    }

    public Result Cancel(DateTimeOffset now)
    {
        if (Status != SupplierReturnStatus.Draft)
        {
            return PurchasingErrors.InvalidStatus;
        }

        Status = SupplierReturnStatus.Cancelled;
        CancelledAt = now;
        return Result.Success();
    }
}
