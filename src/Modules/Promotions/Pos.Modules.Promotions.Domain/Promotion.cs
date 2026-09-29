using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Promotions.Domain;

public enum PromotionType
{
    /// <summary>Lleve N pague M.</summary>
    MultiBuy,

    SpecialPrice,

    PercentOff,

    /// <summary>Desde X unidades base, cada una a un precio.</summary>
    QuantityPrice,

    Combo,
}

public enum PromotionStatus
{
    Draft,
    Active,
    Paused,
    Ended,
}

/// <summary>Días de la semana en que rige una promoción (máscara; <c>All</c> = todos).</summary>
[Flags]
public enum PromotionDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    All = 127,
}

/// <summary>A qué aplica: un producto (y opcionalmente su presentación), una categoría (con sus subcategorías) o una marca.</summary>
public sealed record PromotionItemInput(Guid? ProductId, Guid? PackagingId, Guid? CategoryId, Guid? BrandId, decimal Quantity = 1m);

/// <summary>Parámetros de la regla según el tipo.</summary>
public sealed record PromotionRuleInput(int? BuyQuantity, int? PayQuantity, decimal? Price, decimal? Percent, decimal? MinQuantity);

/// <summary>Datos editables de una promoción en borrador.</summary>
public sealed record PromotionInput(
    string Name,
    PromotionType Type,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    PromotionDays Days,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    IReadOnlyList<Guid> BranchIds,
    int? MaxApplications,
    string? TicketText,
    PromotionRuleInput Rule,
    IReadOnlyList<PromotionItemInput> Items);

public sealed class PromotionItem : Entity<Guid>
{
    private PromotionItem(Guid id)
        : base(id)
    {
    }

    public Guid? ProductId { get; private set; }

    public Guid? PackagingId { get; private set; }

    public Guid? CategoryId { get; private set; }

    public Guid? BrandId { get; private set; }

    public decimal Quantity { get; private set; }

    internal static PromotionItem Create(Guid id, PromotionItemInput input) => new(id)
    {
        ProductId = input.ProductId,
        PackagingId = input.ProductId is null ? null : input.PackagingId,
        CategoryId = input.CategoryId,
        BrandId = input.BrandId,
        Quantity = input.Quantity,
    };
}

public sealed class PromotionBranch : Entity<Guid>
{
    private PromotionBranch(Guid id)
        : base(id)
    {
    }

    public Guid BranchId { get; private set; }

    internal static PromotionBranch Create(Guid id, Guid branchId) => new(id) { BranchId = branchId };
}

public static class PromotionErrors
{
    public static readonly Error NotFound = Error.NotFound("PROMOTIONS.NOT_FOUND", "La promoción no existe.");

    public static readonly Error Invalid = Error.Validation(
        "PROMOTIONS.INVALID",
        "Promoción inválida: nombre de 3 a 80 caracteres, vigencia coherente, horario completo y días de la semana; al menos un producto, categoría o marca por ítem.");

    public static readonly Error InvalidRule = Error.Validation(
        "PROMOTIONS.INVALID_RULE",
        "Parámetros inválidos para el tipo: lleve N pague M (N > M ≥ 0), precio mayor que cero, porcentaje de 0,01 a 100, cantidad mínima mayor que cero o combo con dos o más componentes.");

    public static readonly Error NotEditable = Error.BusinessRule(
        "PROMOTIONS.NOT_EDITABLE", "Una promoción activa no se edita: páusela o termínela y cree otra (RN-PRM-04).");

    public static readonly Error InvalidStatus = Error.BusinessRule("PROMOTIONS.INVALID_STATUS", "La promoción no está en un estado que permita esta acción.");

    public static readonly Error Expired = Error.BusinessRule("PROMOTIONS.EXPIRED", "La vigencia de la promoción ya terminó.");
}

/// <summary>
/// Promoción automática (D7-16): la administra el encargado de promociones, nunca la caja. Borrador editable → activa
/// (no editable, RN-PRM-04) ⇄ pausada → terminada. Rige en su vigencia, en los días y el horario indicados (hora de Colombia)
/// y en todas las sucursales o en las elegidas.
/// </summary>
[Audited("promotions")]
public sealed class Promotion : AggregateRoot<Guid>, ICompanyOwned, ISyncVersioned, IHasAuditLabel
{
    private readonly List<PromotionItem> _items = [];

    private readonly List<PromotionBranch> _branches = [];

    private Promotion(Guid id, Guid companyId, string number)
        : base(id)
    {
        CompanyId = companyId;
        Number = number;
    }

    public Guid CompanyId { get; private set; }

    public string Number { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public PromotionType Type { get; private set; }

    public PromotionStatus Status { get; private set; } = PromotionStatus.Draft;

    public DateTimeOffset ValidFrom { get; private set; }

    public DateTimeOffset? ValidTo { get; private set; }

    public PromotionDays Days { get; private set; } = PromotionDays.All;

    public TimeOnly? StartTime { get; private set; }

    public TimeOnly? EndTime { get; private set; }

    public bool AllBranches { get; private set; }

    public int? MaxApplications { get; private set; }

    public string? TicketText { get; private set; }

    public int? BuyQuantity { get; private set; }

    public int? PayQuantity { get; private set; }

    public decimal? Price { get; private set; }

    public decimal? Percent { get; private set; }

    public decimal? MinQuantity { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public Guid? ActivatedBy { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public IReadOnlyList<PromotionItem> Items => _items;

    public IReadOnlyList<PromotionBranch> Branches => _branches;

    public string AuditLabel => $"Promoción {Number} · {Name}";

    public static Result<Promotion> Create(Guid id, Guid companyId, string number, PromotionInput input, Func<Guid> newId)
    {
        var promotion = new Promotion(id, companyId, number);
        var applied = promotion.Apply(input, newId);
        return applied.IsSuccess ? promotion : applied.Error;
    }

    public Result Update(PromotionInput input, Func<Guid> newId) => Status == PromotionStatus.Draft ? Apply(input, newId) : PromotionErrors.NotEditable;

    public Result Activate(Guid userId, DateTimeOffset now)
    {
        if (Status is not (PromotionStatus.Draft or PromotionStatus.Paused))
        {
            return PromotionErrors.InvalidStatus;
        }

        if (ValidTo is { } to && to <= now)
        {
            return PromotionErrors.Expired;
        }

        Status = PromotionStatus.Active;
        ActivatedAt ??= now;
        ActivatedBy ??= userId;
        return Result.Success();
    }

    public Result Pause()
    {
        if (Status != PromotionStatus.Active)
        {
            return PromotionErrors.InvalidStatus;
        }

        Status = PromotionStatus.Paused;
        return Result.Success();
    }

    public Result End(DateTimeOffset now)
    {
        if (Status is not (PromotionStatus.Active or PromotionStatus.Paused or PromotionStatus.Draft))
        {
            return PromotionErrors.InvalidStatus;
        }

        Status = PromotionStatus.Ended;
        EndedAt = now;
        return Result.Success();
    }

    /// <summary>¿Rige en la sucursal en ese instante? <paramref name="local"/> es la hora del negocio (Colombia).</summary>
    public bool IsInEffect(Guid branchId, DateTimeOffset utc, DateTime local)
    {
        if (Status != PromotionStatus.Active || utc < ValidFrom || (ValidTo is { } to && utc >= to))
        {
            return false;
        }

        if (!AllBranches && _branches.All(b => b.BranchId != branchId))
        {
            return false;
        }

        if (!Days.HasFlag(DayFlag(local.DayOfWeek)))
        {
            return false;
        }

        if (StartTime is { } start && EndTime is { } end)
        {
            var time = TimeOnly.FromDateTime(local);
            return start <= end ? time >= start && time < end : time >= start || time < end;
        }

        return true;
    }

    public static PromotionDays DayFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => PromotionDays.Monday,
        DayOfWeek.Tuesday => PromotionDays.Tuesday,
        DayOfWeek.Wednesday => PromotionDays.Wednesday,
        DayOfWeek.Thursday => PromotionDays.Thursday,
        DayOfWeek.Friday => PromotionDays.Friday,
        DayOfWeek.Saturday => PromotionDays.Saturday,
        _ => PromotionDays.Sunday,
    };

    private Result Apply(PromotionInput input, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(newId);
        var name = (input.Name ?? string.Empty).Trim();
        var ticket = string.IsNullOrWhiteSpace(input.TicketText) ? null : input.TicketText.Trim();
        if (name.Length is < 3 or > 80 || ticket is { Length: > 40 } || (input.ValidTo is { } to && to <= input.ValidFrom)
            || (input.StartTime is null) != (input.EndTime is null) || (input.StartTime is { } s && s == input.EndTime)
            || (input.Days & PromotionDays.All) == PromotionDays.None || input.MaxApplications is <= 0
            || input.Items is not { Count: > 0 } || input.Items.Any(i => !ValidItem(i)) || input.BranchIds.Distinct().Count() != input.BranchIds.Count)
        {
            return PromotionErrors.Invalid;
        }

        var rule = input.Rule ?? new PromotionRuleInput(null, null, null, null, null);
        if (!ValidRule(input.Type, rule, input.Items))
        {
            return PromotionErrors.InvalidRule;
        }

        Name = name;
        Type = input.Type;
        ValidFrom = input.ValidFrom;
        ValidTo = input.ValidTo;
        Days = input.Days & PromotionDays.All;
        StartTime = input.StartTime;
        EndTime = input.EndTime;
        AllBranches = input.BranchIds.Count == 0;
        MaxApplications = input.MaxApplications;
        TicketText = ticket;
        BuyQuantity = input.Type == PromotionType.MultiBuy ? rule.BuyQuantity : null;
        PayQuantity = input.Type == PromotionType.MultiBuy ? rule.PayQuantity : null;
        Price = input.Type is PromotionType.SpecialPrice or PromotionType.QuantityPrice or PromotionType.Combo ? rule.Price : null;
        Percent = input.Type == PromotionType.PercentOff ? rule.Percent : null;
        MinQuantity = input.Type == PromotionType.QuantityPrice ? rule.MinQuantity : null;
        _items.Clear();
        _items.AddRange(input.Items.Select(i => PromotionItem.Create(newId(), i)));
        _branches.Clear();
        _branches.AddRange(input.BranchIds.Select(b => PromotionBranch.Create(newId(), b)));
        return Result.Success();
    }

    private static bool ValidItem(PromotionItemInput item) =>
        new[] { item.ProductId is not null, item.CategoryId is not null, item.BrandId is not null }.Count(x => x) == 1
        && item.Quantity > 0m && decimal.Round(item.Quantity, 4) == item.Quantity;

    private static bool ValidRule(PromotionType type, PromotionRuleInput rule, IReadOnlyList<PromotionItemInput> items) => type switch
    {
        PromotionType.MultiBuy => rule is { BuyQuantity: >= 2 and <= 100, PayQuantity: >= 0 } && rule.PayQuantity < rule.BuyQuantity,
        PromotionType.SpecialPrice => rule.Price is > 0m && decimal.Round(rule.Price.Value, 2) == rule.Price,
        PromotionType.PercentOff => rule.Percent is > 0m and <= 100m && decimal.Round(rule.Percent.Value, 2) == rule.Percent,
        PromotionType.QuantityPrice => rule.Price is > 0m && decimal.Round(rule.Price.Value, 2) == rule.Price && rule.MinQuantity is > 0m,
        PromotionType.Combo => rule.Price is > 0m && decimal.Round(rule.Price.Value, 2) == rule.Price && items.Count >= 2 && items.All(i => i.ProductId is not null),
        _ => false,
    };
}
