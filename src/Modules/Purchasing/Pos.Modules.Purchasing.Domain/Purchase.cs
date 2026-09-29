using Pos.SharedKernel;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Finance;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Purchasing.Domain;

public enum PurchaseStatus
{
    Draft,
    Posted,
    Voided,
}

public enum PaymentMode
{
    /// <summary>De contado: al contabilizar se registra también el pago con el medio indicado.</summary>
    Cash,

    /// <summary>A crédito: vence en la fecha de la factura + el plazo del proveedor.</summary>
    Credit,
}

public enum WithholdingKind
{
    Retefuente,
    Reteiva,
    Reteica,
}

/// <summary>Encabezado de la compra (datos de la factura del proveedor).</summary>
public sealed record PurchaseHeader(
    string SupplierInvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly BusinessDate,
    DateOnly? DueDate,
    PaymentMode PaymentMode,
    Guid? PaymentMethodId,
    string? PaymentReference,
    decimal? InvoiceTotal,
    ProrationMethod Proration,
    decimal ChargesTotal,
    string? ChargesNotes,
    string? Notes);

/// <summary>Línea tal como se digita: cantidad y costo por la presentación facturada, impuestos con su tarifa.</summary>
public sealed record PurchaseLineInput(
    Guid ProductId,
    Guid? PackagingId,
    decimal Factor,
    decimal Quantity,
    decimal UnitCost,
    decimal Discount,
    IReadOnlyList<CostingTax> Taxes,
    decimal? ManualCharges,
    string? LotNumber,
    DateOnly? ExpiryDate,
    Guid? OrderLineId);

public sealed record WithholdingInput(WithholdingKind Kind, decimal Base, decimal? Rate, decimal Amount);

/// <summary>Impuesto de una línea de compra.</summary>
public sealed class PurchaseLineTax : Entity<Guid>
{
    private PurchaseLineTax(Guid id)
        : base(id)
    {
        TaxCode = string.Empty;
    }

    private PurchaseLineTax(Guid id, CostedTax tax)
        : base(id)
    {
        TaxId = tax.TaxId;
        TaxCode = tax.Code;
        IsVat = tax.IsVat;
        Rate = tax.Rate;
        FixedAmount = tax.FixedAmount;
        Base = tax.Base;
        Amount = tax.Amount;
        IsDeductible = tax.IsDeductible;
    }

    public Guid? TaxId { get; private set; }

    public string TaxCode { get; private set; }

    public bool IsVat { get; private set; }

    public decimal? Rate { get; private set; }

    public decimal? FixedAmount { get; private set; }

    public decimal Base { get; private set; }

    public decimal Amount { get; private set; }

    public bool IsDeductible { get; private set; }

    internal static PurchaseLineTax Create(Guid id, CostedTax tax) => new(id, tax);
}

/// <summary>Línea de compra con su costeo. <c>NetUnitCost</c> por unidad base es el costo de entrada al kardex.</summary>
public sealed class PurchaseLine : Entity<Guid>
{
    private readonly List<PurchaseLineTax> _taxes = [];

    private PurchaseLine(Guid id, int lineNumber, Guid productId)
        : base(id)
    {
        LineNumber = lineNumber;
        ProductId = productId;
    }

    public int LineNumber { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid? PackagingId { get; private set; }

    public decimal Factor { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal BaseQuantity { get; private set; }

    public decimal UnitCost { get; private set; }

    public decimal GrossAmount { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal ChargesAmount { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal NonDeductibleTax { get; private set; }

    public decimal LineTotal { get; private set; }

    public decimal NetUnitCost { get; private set; }

    public string? LotNumber { get; private set; }

    public DateOnly? ExpiryDate { get; private set; }

    public Guid? LotId { get; private set; }

    public Guid? OrderLineId { get; private set; }

    public decimal ReturnedBaseQuantity { get; private set; }

    public decimal ReturnableBaseQuantity => BaseQuantity - ReturnedBaseQuantity;

    public IReadOnlyList<PurchaseLineTax> Taxes => _taxes;

    internal static PurchaseLine Create(Guid id, int lineNumber, PurchaseLineInput input, CostedLine costed, Func<Guid> newId)
    {
        var line = new PurchaseLine(id, lineNumber, input.ProductId)
        {
            PackagingId = input.PackagingId,
            Factor = input.Factor,
            Quantity = input.Quantity,
            BaseQuantity = costed.BaseQuantity,
            UnitCost = input.UnitCost,
            GrossAmount = costed.Gross,
            DiscountAmount = costed.Discount,
            ChargesAmount = costed.Charges,
            TaxAmount = costed.TaxAmount,
            NonDeductibleTax = costed.NonDeductibleTax,
            LineTotal = costed.LineTotal,
            NetUnitCost = costed.NetUnitCost,
            LotNumber = string.IsNullOrWhiteSpace(input.LotNumber) ? null : input.LotNumber.Trim().ToUpperInvariant(),
            ExpiryDate = input.ExpiryDate,
            OrderLineId = input.OrderLineId,
        };
        line._taxes.AddRange(costed.Taxes.Select(t => PurchaseLineTax.Create(newId(), t)));
        return line;
    }

    internal void AssignLot(Guid lotId) => LotId = lotId;

    internal Result RegisterReturn(decimal baseQuantity)
    {
        if (baseQuantity <= 0m || baseQuantity > ReturnableBaseQuantity)
        {
            return PurchasingErrors.ReturnExceedsPurchase;
        }

        ReturnedBaseQuantity += baseQuantity;
        return Result.Success();
    }
}

/// <summary>Retención digitada en la compra (D5-11): reduce lo que se paga, no el costo.</summary>
public sealed class PurchaseWithholding : Entity<Guid>
{
    private PurchaseWithholding(Guid id)
        : base(id)
    {
    }

    private PurchaseWithholding(Guid id, WithholdingInput input)
        : base(id)
    {
        Kind = input.Kind;
        Base = input.Base;
        Rate = input.Rate;
        Amount = input.Amount;
    }

    public WithholdingKind Kind { get; private set; }

    public decimal Base { get; private set; }

    public decimal? Rate { get; private set; }

    public decimal Amount { get; private set; }

    internal static PurchaseWithholding Create(Guid id, WithholdingInput input) => new(id, input);
}

/// <summary>
/// Compra (factura del proveedor con la mercancía recibida). <c>DRAFT</c> editable por varios usuarios → <c>POSTED</c>
/// (entra al kardex al costo neto y nace la cuenta por pagar, RN-PUR-01) → <c>VOIDED</c> (movimientos inversos, D5-08).
/// Los totales se recalculan en cada edición; al contabilizar deben cuadrar con el total de la factura ⚙️.
/// </summary>
[Audited("purchasing")]
public sealed class Purchase : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private readonly List<PurchaseLine> _lines = [];

    private readonly List<PurchaseWithholding> _withholdings = [];

    private Purchase(Guid id, Guid companyId, Guid branchId, Guid warehouseId, Guid supplierId, string number)
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

    public Guid? PurchaseOrderId { get; private set; }

    public string Number { get; private set; }

    public string SupplierInvoiceNumber { get; private set; } = string.Empty;

    public DateOnly InvoiceDate { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public DateOnly DueDate { get; private set; }

    public PaymentMode PaymentMode { get; private set; }

    public Guid? PaymentMethodId { get; private set; }

    public string? PaymentReference { get; private set; }

    public bool RequiresSupportDocument { get; private set; }

    public decimal? InvoiceTotal { get; private set; }

    public ProrationMethod ProrationMethod { get; private set; }

    public decimal ChargesTotal { get; private set; }

    public string? ChargesNotes { get; private set; }

    public decimal Subtotal { get; private set; }

    public decimal DiscountTotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    public decimal DeductibleTaxTotal { get; private set; }

    public decimal WithholdingTotal { get; private set; }

    public decimal Total { get; private set; }

    public decimal PayableTotal { get; private set; }

    public PurchaseStatus Status { get; private set; } = PurchaseStatus.Draft;

    public string? Notes { get; private set; }

    public DateTimeOffset? PostedAt { get; private set; }

    public Guid? PostedBy { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    public Guid? VoidedBy { get; private set; }

    public string? VoidReason { get; private set; }

    public IReadOnlyList<PurchaseLine> Lines => _lines;

    public IReadOnlyList<PurchaseWithholding> Withholdings => _withholdings;

    public string AuditLabel => $"Compra {Number} (factura {SupplierInvoiceNumber})";

    public static Result<Purchase> Create(
        Guid id, Guid companyId, Guid branchId, Guid warehouseId, Supplier supplier, Guid? purchaseOrderId, string number, PurchaseHeader header,
        IReadOnlyList<PurchaseLineInput> lines, IReadOnlyList<WithholdingInput> withholdings, bool vatDeductible, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        var purchase = new Purchase(id, companyId, branchId, warehouseId, supplier.Id, number) { PurchaseOrderId = purchaseOrderId };
        var result = purchase.Edit(supplier, header, lines, withholdings, vatDeductible, newId);
        return result.IsSuccess ? purchase : result.Error;
    }

    public static string NormalizeInvoiceNumber(string? number) => (number ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Reemplaza encabezado, líneas y retenciones y recalcula el costeo (solo en borrador).</summary>
    public Result Edit(
        Supplier supplier, PurchaseHeader header, IReadOnlyList<PurchaseLineInput> lines, IReadOnlyList<WithholdingInput> withholdings, bool vatDeductible,
        Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(withholdings);
        ArgumentNullException.ThrowIfNull(newId);
        if (Status != PurchaseStatus.Draft)
        {
            return PurchasingErrors.InvalidStatus;
        }

        var invoice = NormalizeInvoiceNumber(header.SupplierInvoiceNumber);
        var dueDate = header.DueDate ?? (header.PaymentMode == PaymentMode.Credit ? header.InvoiceDate.AddDays(supplier.PaymentTermDays) : header.InvoiceDate);
        if (invoice.Length is 0 or > 40 || dueDate < header.InvoiceDate || header.BusinessDate < header.InvoiceDate
            || header.ChargesTotal < 0m || header.InvoiceTotal < 0m || header.Notes?.Trim().Length > 500 || header.ChargesNotes?.Trim().Length > 200
            || header.PaymentReference?.Trim().Length > 60)
        {
            return PurchasingErrors.InvalidHeader;
        }

        if (lines.Count == 0)
        {
            return PurchasingErrors.NoLines;
        }

        if (lines.Any(l => l.Quantity <= 0m || l.Factor <= 0m || l.UnitCost < 0m || l.Discount < 0m
                           || !Guard.HasAtMostDecimals(l.Quantity, RoundingPolicy.QuantityDecimals)
                           || l.Discount > RoundingPolicy.Colombia.RoundMoney(l.Quantity * l.UnitCost) || l.ManualCharges < 0m
                           || l.LotNumber?.Trim().Length > 40 || (l.ExpiryDate is not null && string.IsNullOrWhiteSpace(l.LotNumber))))
        {
            return PurchasingErrors.InvalidLine;
        }

        if (withholdings.Select(w => w.Kind).Distinct().Count() != withholdings.Count
            || withholdings.Any(w => w.Base < 0m || w.Amount <= 0m || w.Rate is < 0m or > 100m))
        {
            return PurchasingErrors.InvalidWithholding;
        }

        var costing = PurchaseCosting.Calculate(
            [.. lines.Select(l => new CostingLine(l.Quantity, l.Factor, l.UnitCost, l.Discount, l.Taxes, l.ManualCharges))],
            header.ChargesTotal, header.Proration, vatDeductible);
        if (header.Proration == ProrationMethod.Manual && costing.Totals.ChargesTotal != RoundingPolicy.Colombia.RoundMoney(header.ChargesTotal))
        {
            return PurchasingErrors.ChargesMismatch;
        }

        var withholdingTotal = withholdings.Sum(w => RoundingPolicy.Colombia.RoundMoney(w.Amount));
        if (withholdingTotal > costing.Totals.Total)
        {
            return PurchasingErrors.WithholdingsExceedTotal;
        }

        SupplierInvoiceNumber = invoice;
        InvoiceDate = header.InvoiceDate;
        BusinessDate = header.BusinessDate;
        DueDate = dueDate;
        PaymentMode = header.PaymentMode;
        PaymentMethodId = header.PaymentMethodId;
        PaymentReference = string.IsNullOrWhiteSpace(header.PaymentReference) ? null : header.PaymentReference.Trim();
        RequiresSupportDocument = !supplier.IssuesInvoices;
        InvoiceTotal = header.InvoiceTotal;
        ProrationMethod = header.Proration;
        ChargesTotal = costing.Totals.ChargesTotal;
        ChargesNotes = string.IsNullOrWhiteSpace(header.ChargesNotes) ? null : header.ChargesNotes.Trim();
        Notes = string.IsNullOrWhiteSpace(header.Notes) ? null : header.Notes.Trim();
        Subtotal = costing.Totals.Subtotal;
        DiscountTotal = costing.Totals.DiscountTotal;
        TaxTotal = costing.Totals.TaxTotal;
        DeductibleTaxTotal = costing.Totals.DeductibleTaxTotal;
        Total = costing.Totals.Total;
        WithholdingTotal = withholdingTotal;
        PayableTotal = Total - withholdingTotal;
        _lines.Clear();
        _lines.AddRange(lines.Select((l, i) => PurchaseLine.Create(newId(), i + 1, l, costing.Lines[i], newId)));
        _withholdings.Clear();
        _withholdings.AddRange(withholdings.Select(w => PurchaseWithholding.Create(newId(), w with { Amount = RoundingPolicy.Colombia.RoundMoney(w.Amount) })));
        return Result.Success();
    }

    /// <summary>Validaciones previas a contabilizar: total de la factura, medio de pago de contado (RN-PUR-01).</summary>
    public Result CanPost(decimal invoiceTolerance, bool paymentMethodRequiresReference)
    {
        if (Status != PurchaseStatus.Draft)
        {
            return PurchasingErrors.InvalidStatus;
        }

        if (InvoiceTotal is not { } declared)
        {
            return PurchasingErrors.InvoiceTotalRequired;
        }

        if (Math.Abs(declared - Total) > invoiceTolerance)
        {
            return Error.BusinessRule(PurchasingErrors.TotalsMismatch.Code, $"{PurchasingErrors.TotalsMismatch.Message} Calculado {Total:N2}, factura {declared:N2}.");
        }

        if (PaymentMode == PaymentMode.Cash && (PaymentMethodId is null || (paymentMethodRequiresReference && PaymentReference is null)))
        {
            return PurchasingErrors.PaymentMethodRequired;
        }

        return Result.Success();
    }

    public void MarkPosted(IReadOnlyDictionary<Guid, Guid> lotsByLine, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lotsByLine);
        if (Status != PurchaseStatus.Draft)
        {
            throw new DomainException("La compra ya fue contabilizada o anulada.");
        }

        foreach (var line in _lines)
        {
            if (lotsByLine.TryGetValue(line.Id, out var lot))
            {
                line.AssignLot(lot);
            }
        }

        Status = PurchaseStatus.Posted;
        PostedAt = now;
        PostedBy = userId;
    }

    public Result Void(string reason, Guid userId, DateTimeOffset now)
    {
        if (Status != PurchaseStatus.Posted)
        {
            return PurchasingErrors.InvalidStatus;
        }

        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length is < 5 or > 300)
        {
            return PurchasingErrors.VoidReasonRequired;
        }

        if (_lines.Any(l => l.ReturnedBaseQuantity > 0m))
        {
            return PurchasingErrors.PurchaseHasReturns;
        }

        Status = PurchaseStatus.Voided;
        VoidedAt = now;
        VoidedBy = userId;
        VoidReason = trimmed;
        return Result.Success();
    }

    /// <summary>Descuenta lo devuelto al proveedor de una línea (RN-PUR-06).</summary>
    public Result RegisterReturn(Guid lineId, decimal baseQuantity)
    {
        if (Status != PurchaseStatus.Posted)
        {
            return PurchasingErrors.InvalidStatus;
        }

        return _lines.SingleOrDefault(l => l.Id == lineId) is { } line ? line.RegisterReturn(baseQuantity) : PurchasingErrors.ReturnExceedsPurchase;
    }
}
