using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Sales.Domain;

public enum SaleStatus
{
    Open,
    OnHold,
    Completed,
    Cancelled,
    Voided,
}

public enum SaleReturnStatus
{
    None,
    Partial,
    Full,
}

public enum SaleLineStatus
{
    Active,
    Voided,
}

public enum DiscountScope
{
    Line,
    Global,
}

public enum DiscountStatus
{
    Active,
    Removed,
}

/// <summary>Datos de un producto leído o elegido en la caja (snapshot, D7-07).</summary>
public sealed record SaleLineInput(
    Guid ProductId,
    string Sku,
    string Name,
    string? ScannedCode,
    string Source,
    string BaseUnitCode,
    Guid? PackagingId,
    string? PackagingName,
    decimal Factor,
    decimal Quantity,
    decimal UnitPrice,
    bool PriceIncludesTax,
    Guid CategoryId,
    Guid? BrandId,
    bool IsStockable,
    bool AllowsDecimalQuantity,
    bool AllowsOpenPrice,
    IReadOnlyList<PricingTax> Taxes,
    Guid? ExpiredAuthorizedBy = null);

/// <summary>Cliente de la venta (null = Consumidor final).</summary>
public sealed record CustomerSnapshot(Guid? PartyId, string Name, string IdentificationType, string IdentificationNumber, string? Email);

/// <summary>Impuesto de una línea: la tarifa se fija al escanear; base y valor se recalculan con la venta.</summary>
public sealed class SaleLineTax : Entity<Guid>
{
    private SaleLineTax(Guid id)
        : base(id)
    {
    }

    public Guid TaxId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Kind { get; private set; } = string.Empty;

    public decimal? Rate { get; private set; }

    public decimal? FixedAmount { get; private set; }

    public decimal TaxBase { get; private set; }

    public decimal Amount { get; private set; }

    internal static SaleLineTax Create(Guid id, PricingTax tax) =>
        new(id) { TaxId = tax.TaxId, Code = tax.Code, Kind = tax.Kind, Rate = tax.Rate, FixedAmount = tax.FixedAmount };

    internal PricingTax ToPricing() => new(TaxId, Code, Kind, Rate, FixedAmount);

    internal void Apply(PricedTax priced)
    {
        TaxBase = priced.Base;
        Amount = priced.Amount;
    }
}

/// <summary>Línea de venta con el snapshot del producto. Eliminarla no la borra: queda <c>VOIDED</c> (RN-SAL-06).</summary>
public sealed class SaleLine : Entity<Guid>
{
    private readonly List<SaleLineTax> _taxes = [];

    private SaleLine(Guid id)
        : base(id)
    {
    }

    public int LineNo { get; private set; }

    public Guid ProductId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? ScannedCode { get; private set; }

    public string Source { get; private set; } = string.Empty;

    public string BaseUnitCode { get; private set; } = string.Empty;

    public Guid? PackagingId { get; private set; }

    public string? PackagingName { get; private set; }

    public decimal Factor { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal BaseQuantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public bool PriceIncludesTax { get; private set; }

    public bool PriceOverridden { get; private set; }

    public decimal? OriginalUnitPrice { get; private set; }

    public Guid? PriceAuthorizedBy { get; private set; }

    public Guid CategoryId { get; private set; }

    public Guid? BrandId { get; private set; }

    public bool IsStockable { get; private set; }

    public bool AllowsDecimalQuantity { get; private set; }

    public bool AllowsOpenPrice { get; private set; }

    public decimal Gross { get; private set; }

    public Guid? PromotionId { get; private set; }

    public string? PromotionName { get; private set; }

    public decimal PromotionDiscount { get; private set; }

    public decimal LineDiscount { get; private set; }

    public decimal GlobalDiscountShare { get; private set; }

    public decimal TaxBase { get; private set; }

    public decimal TaxTotal { get; private set; }

    public decimal Total { get; private set; }

    /// <summary>Costo unitario (por unidad base) que asignó el kardex al completar (D7-06).</summary>
    public decimal? UnitCost { get; private set; }

    public decimal? CostTotal { get; private set; }

    /// <summary>Lote principal del que salió la línea (FEFO); a él vuelve un cambio de mercancía.</summary>
    public Guid? LotId { get; private set; }

    public Guid? ExpiredAuthorizedBy { get; private set; }

    public SaleLineStatus Status { get; private set; } = SaleLineStatus.Active;

    public Guid? VoidedBy { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    public decimal ReturnedQuantity { get; private set; }

    public IReadOnlyList<SaleLineTax> Taxes => _taxes;

    public bool IsActive => Status == SaleLineStatus.Active;

    internal static SaleLine Create(Guid id, int lineNo, SaleLineInput input, Func<Guid> newId)
    {
        var line = new SaleLine(id)
        {
            LineNo = lineNo,
            ProductId = input.ProductId,
            Sku = input.Sku,
            Name = input.Name,
            ScannedCode = input.ScannedCode,
            Source = input.Source,
            BaseUnitCode = input.BaseUnitCode,
            PackagingId = input.PackagingId,
            PackagingName = input.PackagingName,
            Factor = input.Factor,
            UnitPrice = input.UnitPrice,
            PriceIncludesTax = input.PriceIncludesTax,
            CategoryId = input.CategoryId,
            BrandId = input.BrandId,
            IsStockable = input.IsStockable,
            AllowsDecimalQuantity = input.AllowsDecimalQuantity,
            AllowsOpenPrice = input.AllowsOpenPrice,
            ExpiredAuthorizedBy = input.ExpiredAuthorizedBy,
        };
        line.SetQuantity(input.Quantity);
        line._taxes.AddRange(input.Taxes.Select(t => SaleLineTax.Create(newId(), t)));
        return line;
    }

    /// <summary>Se suma a esta línea (mismo producto, presentación y precio de lista, sin cambios manuales ni peso).</summary>
    internal bool CanMerge(SaleLineInput input) =>
        IsActive && !PriceOverridden && ProductId == input.ProductId && PackagingId == input.PackagingId && UnitPrice == input.UnitPrice
        && Source is not ("SCALE_WEIGHT" or "SCALE_PRICE") && input.Source is not ("SCALE_WEIGHT" or "SCALE_PRICE")
        && (ExpiredAuthorizedBy is null) == (input.ExpiredAuthorizedBy is null);

    internal void SetQuantity(decimal quantity)
    {
        Quantity = quantity;
        BaseQuantity = decimal.Round(quantity * Factor, 4, MidpointRounding.AwayFromZero);
    }

    internal void OverridePrice(decimal price, Guid? authorizedBy)
    {
        OriginalUnitPrice ??= UnitPrice;
        UnitPrice = price;
        PriceOverridden = true;
        PriceAuthorizedBy = authorizedBy;
    }

    internal void Void(Guid userId, DateTimeOffset now)
    {
        Status = SaleLineStatus.Voided;
        VoidedBy = userId;
        VoidedAt = now;
    }

    internal PricingLine ToPricing(ManualDiscount? discount) =>
        new(Id, ProductId, PackagingId, CategoryId, BrandId, Quantity, Factor, UnitPrice, PriceIncludesTax, [.. _taxes.Select(t => t.ToPricing())], discount,
            PromotionsAllowed: !PriceOverridden);

    internal void Apply(PricedLine? priced)
    {
        Gross = priced?.Gross ?? 0m;
        PromotionId = priced?.PromotionId;
        PromotionName = priced?.PromotionName;
        PromotionDiscount = priced?.PromotionDiscount ?? 0m;
        LineDiscount = priced?.LineDiscount ?? 0m;
        GlobalDiscountShare = priced?.GlobalDiscountShare ?? 0m;
        TaxBase = priced?.Base ?? 0m;
        TaxTotal = priced?.TaxTotal ?? 0m;
        Total = priced?.Total ?? 0m;
        foreach (var tax in _taxes)
        {
            if (priced?.Taxes.FirstOrDefault(t => t.TaxId == tax.TaxId) is { } pricedTax)
            {
                tax.Apply(pricedTax);
            }
            else
            {
                tax.Apply(new PricedTax(tax.TaxId, tax.Code, tax.Kind, tax.Rate, tax.FixedAmount, 0m, 0m));
            }
        }
    }

    internal void SetCost(decimal unitCost, decimal costTotal, Guid? lotId)
    {
        UnitCost = unitCost;
        CostTotal = costTotal;
        LotId = lotId;
    }

    internal void AddReturned(decimal quantity) => ReturnedQuantity += quantity;
}

/// <summary>Pago aplicado a una venta completada (snapshot del medio).</summary>
public sealed class SalePayment : Entity<Guid>
{
    private SalePayment(Guid id)
        : base(id)
    {
    }

    public int LineNo { get; private set; }

    public Guid PaymentMethodId { get; private set; }

    public string MethodCode { get; private set; } = string.Empty;

    public string MethodKind { get; private set; } = string.Empty;

    public bool AffectsCashDrawer { get; private set; }

    public decimal Tendered { get; private set; }

    public decimal Applied { get; private set; }

    public decimal Change { get; private set; }

    public string? Reference { get; private set; }

    public string? CardFranchise { get; private set; }

    public string? CardLast4 { get; private set; }

    internal static SalePayment Create(Guid id, int lineNo, AllocatedPayment payment) => new(id)
    {
        LineNo = lineNo,
        PaymentMethodId = payment.Tender.PaymentMethodId,
        MethodCode = payment.Tender.MethodCode,
        MethodKind = payment.Tender.Kind,
        AffectsCashDrawer = payment.Tender.AffectsCashDrawer,
        Tendered = payment.Tender.Amount,
        Applied = payment.Applied,
        Change = payment.Change,
        Reference = payment.Tender.Reference,
        CardFranchise = payment.Tender.CardFranchise,
        CardLast4 = payment.Tender.CardLast4,
    };
}

/// <summary>Descuento manual autorizado (RN-SAL-04): de una línea o global (prorrateado). Quitarlo lo deja <c>REMOVED</c>.</summary>
public sealed class SaleDiscount : Entity<Guid>
{
    private SaleDiscount(Guid id)
        : base(id)
    {
    }

    public DiscountScope Scope { get; private set; }

    public Guid? SaleLineId { get; private set; }

    public decimal? Percent { get; private set; }

    public decimal? Amount { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public Guid AppliedBy { get; private set; }

    public Guid? AuthorizedBy { get; private set; }

    public DateTimeOffset AppliedAt { get; private set; }

    public DiscountStatus Status { get; private set; } = DiscountStatus.Active;

    public ManualDiscount ToManual() => new(Percent, Amount);

    internal static SaleDiscount Create(
        Guid id, DiscountScope scope, Guid? lineId, decimal? percent, decimal? amount, string reason, Guid appliedBy, Guid? authorizedBy, DateTimeOffset now) =>
        new(id)
        {
            Scope = scope,
            SaleLineId = lineId,
            Percent = percent,
            Amount = amount,
            Reason = reason,
            AppliedBy = appliedBy,
            AuthorizedBy = authorizedBy,
            AppliedAt = now,
        };

    internal void Remove() => Status = DiscountStatus.Removed;
}

/// <summary>
/// Venta (doc 04 §H.8, D7-01): persistida desde el primer escaneo. <c>OPEN</c> ⇄ <c>ON_HOLD</c> → <c>COMPLETED</c> (número,
/// pagos, costos) → <c>VOIDED</c> (anulación con la jornada abierta); o <c>CANCELLED</c> sin número. Cada cambio recalcula
/// con <see cref="SaleCalculator"/>. Completada no se edita (RN-SAL-12).
/// </summary>
public sealed class Sale : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private readonly List<SaleLine> _lines = [];

    private readonly List<SalePayment> _payments = [];

    private readonly List<SaleDiscount> _discounts = [];

    private Sale(Guid id, Guid companyId, Guid branchId, Guid posTerminalId, Guid warehouseId, Guid cashSessionId, Guid cashierId)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        PosTerminalId = posTerminalId;
        WarehouseId = warehouseId;
        CashSessionId = cashSessionId;
        CashierId = cashierId;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid PosTerminalId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid CashSessionId { get; private set; }

    public Guid CashierId { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public SaleStatus Status { get; private set; } = SaleStatus.Open;

    public SaleReturnStatus ReturnStatus { get; private set; } = SaleReturnStatus.None;

    public string? Number { get; private set; }

    public Guid? CustomerId { get; private set; }

    public string CustomerName { get; private set; } = string.Empty;

    public string CustomerIdentificationType { get; private set; } = string.Empty;

    public string CustomerIdentification { get; private set; } = string.Empty;

    public string? CustomerEmail { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? CompletionKey { get; private set; }

    public string? HoldLabel { get; private set; }

    public DateTimeOffset? HeldAt { get; private set; }

    public string? CancelReason { get; private set; }

    public Guid? CancelledBy { get; private set; }

    public Guid? CancelAuthorizedBy { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public string? VoidReason { get; private set; }

    public Guid? VoidedBy { get; private set; }

    public Guid? VoidAuthorizedBy { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    /// <summary>Cambio de mercancía que originó la venta: su crédito paga parte de ella (D7-11).</summary>
    public Guid? ExchangeId { get; private set; }

    public decimal ExchangeCredit { get; private set; }

    public decimal Gross { get; private set; }

    public decimal PromotionTotal { get; private set; }

    public decimal DiscountTotal { get; private set; }

    public decimal Subtotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    public decimal RoundingAdjustment { get; private set; }

    /// <summary>Total a pagar: Σ totales de línea + ajuste de redondeo del efectivo.</summary>
    public decimal Total { get; private set; }

    public decimal PaidTotal { get; private set; }

    public decimal ChangeTotal { get; private set; }

    public IReadOnlyList<SaleLine> Lines => _lines;

    public IReadOnlyList<SalePayment> Payments => _payments;

    public IReadOnlyList<SaleDiscount> Discounts => _discounts;

    public IEnumerable<SaleLine> ActiveLines => _lines.Where(l => l.IsActive);

    public string AuditLabel => $"Venta {Number ?? Id.ToString()}";

    public static Sale Start(
        Guid id, Guid companyId, Guid branchId, Guid posTerminalId, Guid warehouseId, Guid cashSessionId, Guid cashierId, DateOnly businessDate,
        DateTimeOffset now, CustomerSnapshot consumer, Guid? exchangeId = null, decimal exchangeCredit = 0m)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var sale = new Sale(id, companyId, branchId, posTerminalId, warehouseId, cashSessionId, cashierId)
        {
            BusinessDate = businessDate,
            OpenedAt = now,
            ExchangeId = exchangeId,
            ExchangeCredit = exchangeCredit,
        };
        sale.ApplyCustomer(consumer);
        return sale;
    }

    // ─────────────────────────────── Edición (solo OPEN) ───────────────────────────────

    /// <summary>Agrega un producto (o suma la cantidad a la línea igual si <paramref name="merge"/>). Devuelve la línea afectada.</summary>
    public Result<SaleLine> AddLine(SaleLineInput input, bool merge, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(newId);
        if (Status != SaleStatus.Open)
        {
            return SalesErrors.NotOpen;
        }

        if (!ValidQuantity(input.Quantity, input.AllowsDecimalQuantity) || input.Factor <= 0m || input.UnitPrice < 0m)
        {
            return SalesErrors.InvalidQuantity;
        }

        if (merge && _lines.FirstOrDefault(l => l.CanMerge(input)) is { } existing)
        {
            existing.SetQuantity(existing.Quantity + input.Quantity);
            return existing;
        }

        var line = SaleLine.Create(newId(), _lines.Count + 1, input, newId);
        _lines.Add(line);
        return line;
    }

    public Result<SaleLine> ChangeQuantity(Guid lineId, decimal quantity)
    {
        var line = EditableLine(lineId);
        if (line.IsFailure)
        {
            return line;
        }

        if (!ValidQuantity(quantity, line.Value.AllowsDecimalQuantity))
        {
            return SalesErrors.InvalidQuantity;
        }

        line.Value.SetQuantity(quantity);
        return line;
    }

    public Result<SaleLine> VoidLine(Guid lineId, Guid userId, DateTimeOffset now)
    {
        var line = EditableLine(lineId);
        if (line.IsFailure)
        {
            return line;
        }

        line.Value.Void(userId, now);
        foreach (var discount in _discounts.Where(d => d.SaleLineId == lineId && d.Status == DiscountStatus.Active))
        {
            discount.Remove();
        }

        return line;
    }

    /// <summary>Precio abierto (RN-SAL-05): solo productos que lo admiten; la línea deja de recibir promociones.</summary>
    public Result<SaleLine> OverridePrice(Guid lineId, decimal price, Guid? authorizedBy)
    {
        var line = EditableLine(lineId);
        if (line.IsFailure)
        {
            return line;
        }

        if (!line.Value.AllowsOpenPrice)
        {
            return SalesErrors.OpenPriceNotAllowed;
        }

        if (price <= 0m || decimal.Round(price, 2) != price)
        {
            return SalesErrors.InvalidPrice;
        }

        line.Value.OverridePrice(price, authorizedBy);
        return line;
    }

    /// <summary>Descuento manual autorizado (RN-SAL-04): reemplaza el descuento activo de la línea (o el global).</summary>
    public Result<SaleDiscount> ApplyDiscount(
        Guid id, Guid? lineId, decimal? percent, decimal? amount, string reason, Guid appliedBy, Guid? authorizedBy, DateTimeOffset now)
    {
        if (Status != SaleStatus.Open)
        {
            return SalesErrors.NotOpen;
        }

        if (lineId is { } target && EditableLine(target).IsFailure)
        {
            return SalesErrors.LineNotFound;
        }

        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length is < 5 or > 300)
        {
            return SalesErrors.ReasonRequired;
        }

        if ((percent is null) == (amount is null) || percent is <= 0m or > 100m || amount is <= 0m
            || (percent is { } p && decimal.Round(p, 2) != p) || (amount is { } a && decimal.Round(a, 2) != a))
        {
            return SalesErrors.InvalidDiscount;
        }

        foreach (var active in _discounts.Where(d => d.Status == DiscountStatus.Active && d.SaleLineId == lineId))
        {
            active.Remove();
        }

        var discount = SaleDiscount.Create(id, lineId is null ? DiscountScope.Global : DiscountScope.Line, lineId, percent, amount, trimmed, appliedBy, authorizedBy, now);
        _discounts.Add(discount);
        return discount;
    }

    public Result RemoveDiscount(Guid discountId)
    {
        if (Status != SaleStatus.Open)
        {
            return SalesErrors.NotOpen;
        }

        if (_discounts.FirstOrDefault(d => d.Id == discountId && d.Status == DiscountStatus.Active) is not { } discount)
        {
            return SalesErrors.InvalidDiscount;
        }

        discount.Remove();
        return Result.Success();
    }

    public Result SetCustomer(CustomerSnapshot customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        if (Status != SaleStatus.Open)
        {
            return SalesErrors.NotOpen;
        }

        ApplyCustomer(customer);
        return Result.Success();
    }

    public Result Hold(string? label, DateTimeOffset now)
    {
        if (Status != SaleStatus.Open)
        {
            return SalesErrors.NotOpen;
        }

        if (!ActiveLines.Any())
        {
            return SalesErrors.EmptySale;
        }

        var trimmed = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        HoldLabel = trimmed is { Length: > 60 } ? trimmed[..60] : trimmed;
        HeldAt = now;
        Status = SaleStatus.OnHold;
        return Result.Success();
    }

    public Result Resume()
    {
        if (Status != SaleStatus.OnHold)
        {
            return SalesErrors.InvalidStatus;
        }

        Status = SaleStatus.Open;
        return Result.Success();
    }

    /// <summary>Cancelar (RN-SAL-08): con motivo, sin número; una venta de cambio cancelada deja el cambio sin efecto.</summary>
    public Result Cancel(string reason, Guid userId, Guid? authorizedBy, DateTimeOffset now)
    {
        if (Status is not (SaleStatus.Open or SaleStatus.OnHold))
        {
            return SalesErrors.InvalidStatus;
        }

        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length is < 5 or > 300)
        {
            return SalesErrors.ReasonRequired;
        }

        Status = SaleStatus.Cancelled;
        CancelReason = trimmed;
        CancelledBy = userId;
        CancelAuthorizedBy = authorizedBy;
        CancelledAt = now;
        return Result.Success();
    }

    /// <summary>Recalcula totales con las promociones vigentes y los descuentos activos (D7-02, D7-16).</summary>
    public PricedSale Recalculate(IReadOnlyList<PromotionRule> promotions)
    {
        var lineDiscounts = _discounts.Where(d => d.Status == DiscountStatus.Active && d.Scope == DiscountScope.Line)
            .ToDictionary(d => d.SaleLineId!.Value, d => d.ToManual());
        var global = _discounts.FirstOrDefault(d => d.Status == DiscountStatus.Active && d.Scope == DiscountScope.Global)?.ToManual();
        var priced = SaleCalculator.Calculate([.. ActiveLines.Select(l => l.ToPricing(lineDiscounts.GetValueOrDefault(l.Id)))], promotions, global);
        var byKey = priced.Lines.ToDictionary(l => l.Key);
        foreach (var line in _lines)
        {
            line.Apply(line.IsActive ? byKey[line.Id] : null);
        }

        Gross = priced.Gross;
        PromotionTotal = priced.PromotionTotal;
        DiscountTotal = priced.DiscountTotal;
        Subtotal = priced.Subtotal;
        TaxTotal = priced.TaxTotal;
        Total = priced.Total;
        RoundingAdjustment = 0m;
        return priced;
    }

    // ─────────────────────────────── Cobro, costos, anulación y cambios ───────────────────────────────

    /// <summary>
    /// Completa la venta (RN-SAL-11): número de la serie de la caja, cliente, pagos asignados y ajuste de redondeo. Los costos
    /// los fija el kardex después (<see cref="SetLineCost"/>).
    /// </summary>
    public Result Complete(string number, PaymentAllocation allocation, string completionKey, DateTimeOffset now, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(newId);
        if (Status != SaleStatus.Open)
        {
            return SalesErrors.NotOpen;
        }

        if (!ActiveLines.Any())
        {
            return SalesErrors.EmptySale;
        }

        if (ActiveLines.Any(l => l.UnitPrice <= 0m))
        {
            return SalesErrors.PriceRequired;
        }

        if (allocation.Total != Total + allocation.RoundingAdjustment || allocation.Paid - allocation.Change != allocation.Total)
        {
            return SalesErrors.InsufficientPayment;
        }

        Number = number;
        CompletionKey = completionKey;
        CompletedAt = now;
        RoundingAdjustment = allocation.RoundingAdjustment;
        Total = allocation.Total;
        PaidTotal = allocation.Paid;
        ChangeTotal = allocation.Change;
        var lineNo = 0;
        foreach (var payment in allocation.Payments)
        {
            _payments.Add(SalePayment.Create(newId(), ++lineNo, payment));
        }

        Status = SaleStatus.Completed;
        return Result.Success();
    }

    public void SetLineCost(Guid lineId, decimal unitCost, decimal costTotal, Guid? lotId) =>
        _lines.Single(l => l.Id == lineId).SetCost(unitCost, costTotal, lotId);

    /// <summary>Anulación (D7-10): solo completada y sin cambios de mercancía; la jornada abierta la verifica la caja.</summary>
    public Result Void(string reason, Guid userId, Guid? authorizedBy, DateTimeOffset now)
    {
        if (Status != SaleStatus.Completed || ReturnStatus != SaleReturnStatus.None)
        {
            return SalesErrors.VoidNotAllowed;
        }

        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length is < 5 or > 300)
        {
            return SalesErrors.ReasonRequired;
        }

        Status = SaleStatus.Voided;
        VoidReason = trimmed;
        VoidedBy = userId;
        VoidAuthorizedBy = authorizedBy;
        VoidedAt = now;
        return Result.Success();
    }

    /// <summary>Registra lo cambiado de una línea (RN-RET-02) y actualiza el estado de devolución de la venta.</summary>
    public Result RegisterReturned(Guid lineId, decimal quantity)
    {
        if (Status != SaleStatus.Completed)
        {
            return SalesErrors.ExchangeNotAllowed;
        }

        if (_lines.FirstOrDefault(l => l.Id == lineId && l.IsActive) is not { } line)
        {
            return SalesErrors.LineNotFound;
        }

        if (quantity <= 0m || line.ReturnedQuantity + quantity > line.Quantity)
        {
            return SalesErrors.ExchangeQuantityExceeded;
        }

        line.AddReturned(quantity);
        ReturnStatus = ActiveLines.All(l => l.ReturnedQuantity == l.Quantity) ? SaleReturnStatus.Full : SaleReturnStatus.Partial;
        return Result.Success();
    }

    private static bool ValidQuantity(decimal quantity, bool allowsDecimal) =>
        quantity > 0m && quantity <= 100_000m && decimal.Round(quantity, 4) == quantity && (allowsDecimal || decimal.Truncate(quantity) == quantity);

    private Result<SaleLine> EditableLine(Guid lineId)
    {
        if (Status != SaleStatus.Open)
        {
            return SalesErrors.NotOpen;
        }

        return _lines.FirstOrDefault(l => l.Id == lineId && l.IsActive) is { } line ? line : SalesErrors.LineNotFound;
    }

    private void ApplyCustomer(CustomerSnapshot customer)
    {
        CustomerId = customer.PartyId;
        CustomerName = customer.Name;
        CustomerIdentificationType = customer.IdentificationType;
        CustomerIdentification = customer.IdentificationNumber;
        CustomerEmail = customer.Email;
    }
}
