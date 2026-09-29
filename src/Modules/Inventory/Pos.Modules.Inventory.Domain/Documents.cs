using Pos.SharedKernel;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Finance;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Inventory.Domain;

public enum MasterStatus
{
    Active,
    Inactive,
}

/// <summary>Tipo de motivo: fija la dirección y el tipo de movimiento del kardex.</summary>
public enum ReasonKind
{
    /// <summary>Saldo inicial: entrada con costo.</summary>
    InitialBalance,

    /// <summary>Ajuste: entrada o salida según el signo de la línea.</summary>
    Adjustment,

    Loss,
    Damage,
    Expiry,
    InternalUse,
}

/// <summary>Motivo de ajuste configurable por empresa, mapeado a un tipo fijo (los reportes siempre se agrupan).</summary>
[Audited("inventory")]
public sealed class AdjustmentReason : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private AdjustmentReason(Guid id, Guid companyId, string code, string name)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        Name = name;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public ReasonKind Kind { get; private set; }

    public bool RequiresNote { get; private set; }

    public bool IsSystem { get; private set; }

    public MasterStatus Status { get; private set; } = MasterStatus.Active;

    public bool IsInboundOnly => Kind == ReasonKind.InitialBalance;

    public bool IsOutboundOnly => Kind is ReasonKind.Loss or ReasonKind.Damage or ReasonKind.Expiry or ReasonKind.InternalUse;

    public string AuditLabel => $"Motivo {Code} · {Name}";

    public static Result<AdjustmentReason> Create(Guid id, Guid companyId, string code, string name, ReasonKind kind, bool requiresNote, bool isSystem = false)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        var trimmed = (name ?? string.Empty).Trim();
        if (normalized.Length is < 2 or > 30 || !normalized.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')
            || trimmed.Length is 0 or > 80)
        {
            return Error.Validation("INVENTORY.INVALID_REASON", "Código de 2 a 30 mayúsculas, dígitos o _ y nombre de hasta 80 caracteres.");
        }

        return new AdjustmentReason(id, companyId, normalized, trimmed) { Kind = kind, RequiresNote = requiresNote, IsSystem = isSystem };
    }

    public Result Update(string name, bool requiresNote, bool isActive)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 80)
        {
            return Error.Validation("INVENTORY.INVALID_REASON", "El nombre del motivo es obligatorio (máximo 80 caracteres).");
        }

        Name = trimmed;
        RequiresNote = requiresNote;
        Status = isActive ? MasterStatus.Active : MasterStatus.Inactive;
        return Result.Success();
    }
}

public enum AdjustmentStatus
{
    Draft,
    PendingApproval,
    Posted,
    Cancelled,
}

/// <summary>Línea de ajuste: cantidad con signo (positiva entra, negativa sale) en unidad base.</summary>
public sealed class AdjustmentLine : Entity<Guid>
{
    private AdjustmentLine(Guid id, int lineNumber, Guid productId, decimal quantity)
        : base(id)
    {
        LineNumber = lineNumber;
        ProductId = productId;
        Quantity = quantity;
    }

    public int LineNumber { get; private set; }

    public Guid ProductId { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal? UnitCost { get; private set; }

    public string? Notes { get; private set; }

    internal static AdjustmentLine Create(Guid id, int lineNumber, AdjustmentLineInput input, decimal quantity) =>
        new(id, lineNumber, input.ProductId, quantity) { UnitCost = input.UnitCost, Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim() };
}

public sealed record AdjustmentLineInput(Guid ProductId, decimal Quantity, decimal? UnitCost = null, string? Notes = null);

public enum PostingDecision
{
    ReadyToPost,
    NeedsApproval,
}

/// <summary>
/// Ajuste de inventario (incluye el saldo inicial). <c>DRAFT</c> → (<c>PENDING_APPROVAL</c> si su valor supera el umbral)
/// → <c>POSTED</c>; <c>CANCELLED</c> desde borrador o pendiente. Quien aprueba no puede ser quien lo creó (RN-INV-04).
/// </summary>
[Audited("inventory")]
public sealed class InventoryAdjustment : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private readonly List<AdjustmentLine> _lines = [];

    private InventoryAdjustment(Guid id, Guid companyId, Guid branchId, Guid warehouseId, string number)
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

    public DateOnly BusinessDate { get; private set; }

    public Guid ReasonId { get; private set; }

    public AdjustmentStatus Status { get; private set; } = AdjustmentStatus.Draft;

    public string? Notes { get; private set; }

    public decimal? TotalValue { get; private set; }

    public bool ApprovalRequired { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateTimeOffset? PostedAt { get; private set; }

    public Guid? PostedBy { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public Guid? CancelledBy { get; private set; }

    public Guid CreatedBy { get; private set; }

    public IReadOnlyList<AdjustmentLine> Lines => _lines;

    public string AuditLabel => $"Ajuste {Number}";

    public static Result<InventoryAdjustment> Create(
        Guid id, Guid companyId, Guid branchId, Guid warehouseId, string number, DateOnly businessDate, AdjustmentReason reason, string? notes,
        IReadOnlyList<AdjustmentLineInput> lines, Func<Guid> newId, Guid createdBy)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var adjustment = new InventoryAdjustment(id, companyId, branchId, warehouseId, number) { BusinessDate = businessDate, CreatedBy = createdBy };
        var result = adjustment.Edit(reason, notes, lines, newId);
        return result.IsSuccess ? adjustment : result.Error;
    }

    /// <summary>Reemplaza motivo, observación y líneas (solo en borrador).</summary>
    public Result Edit(AdjustmentReason reason, string? notes, IReadOnlyList<AdjustmentLineInput> lines, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(newId);
        if (Status != AdjustmentStatus.Draft)
        {
            return InventoryErrors.InvalidStatus;
        }

        if (lines.Count == 0)
        {
            return InventoryErrors.NoLines;
        }

        if (lines.Select(l => l.ProductId).Distinct().Count() != lines.Count)
        {
            return InventoryErrors.DuplicatedProduct;
        }

        var trimmedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (reason.RequiresNote && trimmedNotes is null)
        {
            return InventoryErrors.NoteRequired;
        }

        var built = new List<AdjustmentLine>();
        foreach (var input in lines)
        {
            if (input.Quantity == 0m || !Guard.HasAtMostDecimals(input.Quantity, RoundingPolicy.QuantityDecimals))
            {
                return InventoryErrors.InvalidQuantity;
            }

            var quantity = reason.IsOutboundOnly ? -Math.Abs(input.Quantity) : input.Quantity;
            if (reason.IsInboundOnly && quantity < 0m)
            {
                return InventoryErrors.QuantitySignMismatch;
            }

            if (reason.Kind == ReasonKind.InitialBalance && input.UnitCost is not >= 0m)
            {
                return InventoryErrors.UnitCostRequired;
            }

            built.Add(AdjustmentLine.Create(newId(), built.Count + 1, reason.Kind == ReasonKind.InitialBalance ? input : input with { UnitCost = null }, quantity));
        }

        ReasonId = reason.Id;
        Notes = trimmedNotes;
        _lines.Clear();
        _lines.AddRange(built);
        return Result.Success();
    }

    /// <summary>Calcula el valor y decide si requiere aprobación (umbral ⚙️ en valor absoluto).</summary>
    public Result<PostingDecision> RequestPosting(decimal totalValue, decimal threshold, DateTimeOffset now)
    {
        if (Status == AdjustmentStatus.PendingApproval && ApprovedBy is not null)
        {
            return PostingDecision.ReadyToPost;
        }

        if (Status != AdjustmentStatus.Draft)
        {
            return InventoryErrors.InvalidStatus;
        }

        TotalValue = totalValue;
        SubmittedAt = now;
        if (Math.Abs(totalValue) > threshold)
        {
            ApprovalRequired = true;
            Status = AdjustmentStatus.PendingApproval;
            return PostingDecision.NeedsApproval;
        }

        return PostingDecision.ReadyToPost;
    }

    public Result Approve(Guid userId, DateTimeOffset now)
    {
        if (Status != AdjustmentStatus.PendingApproval)
        {
            return InventoryErrors.InvalidStatus;
        }

        if (userId == CreatedBy)
        {
            return InventoryErrors.SelfApproval;
        }

        ApprovedBy = userId;
        ApprovedAt = now;
        return Result.Success();
    }

    public void MarkPosted(Guid userId, DateTimeOffset now)
    {
        var allowed = (Status == AdjustmentStatus.Draft && !ApprovalRequired) || (Status == AdjustmentStatus.PendingApproval && ApprovedBy is not null);
        if (!allowed)
        {
            throw new DomainException("El ajuste no está listo para publicarse.");
        }

        Status = AdjustmentStatus.Posted;
        PostedAt = now;
        PostedBy = userId;
    }

    public Result Cancel(Guid userId, DateTimeOffset now)
    {
        if (Status is not (AdjustmentStatus.Draft or AdjustmentStatus.PendingApproval))
        {
            return InventoryErrors.InvalidStatus;
        }

        Status = AdjustmentStatus.Cancelled;
        CancelledAt = now;
        CancelledBy = userId;
        return Result.Success();
    }
}

/// <summary>Política de reposición de un producto en una bodega.</summary>
[Audited("inventory")]
public sealed class StockPolicy : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private StockPolicy(Guid id, Guid companyId, Guid branchId, Guid warehouseId, Guid productId)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        WarehouseId = warehouseId;
        ProductId = productId;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid ProductId { get; private set; }

    public decimal MinQty { get; private set; }

    public decimal? MaxQty { get; private set; }

    public decimal? ReorderPoint { get; private set; }

    public decimal? ReorderQty { get; private set; }

    public string AuditLabel => $"Política de reposición {ProductId:D}";

    public static Result<StockPolicy> Create(
        Guid id, Guid companyId, Guid branchId, Guid warehouseId, Guid productId, decimal min, decimal? max, decimal? reorderPoint, decimal? reorderQty)
    {
        var policy = new StockPolicy(id, companyId, branchId, warehouseId, productId);
        var result = policy.Update(min, max, reorderPoint, reorderQty);
        return result.IsSuccess ? policy : result.Error;
    }

    public Result Update(decimal min, decimal? max, decimal? reorderPoint, decimal? reorderQty)
    {
        if (min < 0m || max < min || reorderPoint < 0m || reorderQty <= 0m)
        {
            return InventoryErrors.InvalidPolicy;
        }

        MinQty = min;
        MaxQty = max;
        ReorderPoint = reorderPoint;
        ReorderQty = reorderQty;
        return Result.Success();
    }
}
