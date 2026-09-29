using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Expenses.Domain;

public enum MasterStatus
{
    Active,
    Inactive,
}

public enum ExpenseStatus
{
    Posted,
    Voided,
}

/// <summary>Categoría de gasto: árbol de dos niveles (p. ej. Servicios públicos > Energía).</summary>
[Audited("expenses")]
public sealed class ExpenseCategory : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private ExpenseCategory(Guid id, Guid companyId, string name)
        : base(id)
    {
        CompanyId = companyId;
        Name = name;
    }

    public Guid CompanyId { get; private set; }

    public Guid? ParentId { get; private set; }

    public string Name { get; private set; }

    public int SortOrder { get; private set; }

    public MasterStatus Status { get; private set; } = MasterStatus.Active;

    public string AuditLabel => $"Categoría de gasto {Name}";

    public static Result<ExpenseCategory> Create(Guid id, Guid companyId, string name, ExpenseCategory? parent, int sortOrder = 0)
    {
        if (parent?.ParentId is not null)
        {
            return ExpensesErrors.CategoryTooDeep;
        }

        var category = new ExpenseCategory(id, companyId, string.Empty) { ParentId = parent?.Id };
        var result = category.Update(name, sortOrder, isActive: true);
        return result.IsSuccess ? category : result.Error;
    }

    public Result Update(string name, int sortOrder, bool isActive)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 80)
        {
            return ExpensesErrors.InvalidCategory;
        }

        Name = trimmed;
        SortOrder = sortOrder;
        Status = isActive ? MasterStatus.Active : MasterStatus.Inactive;
        return Result.Success();
    }
}

public sealed record ExpenseData(
    DateOnly BusinessDate, Guid CategoryId, Guid? PartyId, string Description, decimal Amount, decimal TaxAmount, Guid PaymentMethodId, Guid? CashSessionId,
    string? Reference);

/// <summary>
/// Gasto (servicios, arriendo, aseo, transporte…). Pagado desde la caja, nace con su movimiento EXPENSE en la misma
/// transacción. Se anula (nunca se borra); si salió de la caja, el dinero vuelve con una corrección en esa jornada abierta.
/// </summary>
[Audited("expenses")]
public sealed class Expense : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private Expense(Guid id, Guid companyId, Guid branchId, string number)
        : base(id)
    {
        CompanyId = companyId;
        BranchId = branchId;
        Number = number;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    public string Number { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public Guid CategoryId { get; private set; }

    public Guid? PartyId { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public decimal TaxAmount { get; private set; }

    public Guid PaymentMethodId { get; private set; }

    public Guid? CashSessionId { get; private set; }

    public string? Reference { get; private set; }

    public ExpenseStatus Status { get; private set; } = ExpenseStatus.Posted;

    public DateTimeOffset? VoidedAt { get; private set; }

    public Guid? VoidedBy { get; private set; }

    public string? VoidReason { get; private set; }

    public string AuditLabel => $"Gasto {Number}";

    public static Result<Expense> Create(Guid id, Guid companyId, Guid branchId, string number, ExpenseData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var description = (data.Description ?? string.Empty).Trim();
        var reference = string.IsNullOrWhiteSpace(data.Reference) ? null : data.Reference.Trim();
        if (description.Length is 0 or > 300 || data.Amount <= 0m || data.TaxAmount < 0m || data.TaxAmount > data.Amount
            || decimal.Round(data.Amount, 2) != data.Amount || decimal.Round(data.TaxAmount, 2) != data.TaxAmount || reference?.Length > 60)
        {
            return ExpensesErrors.InvalidExpense;
        }

        return new Expense(id, companyId, branchId, number)
        {
            BusinessDate = data.BusinessDate,
            CategoryId = data.CategoryId,
            PartyId = data.PartyId,
            Description = description,
            Amount = data.Amount,
            TaxAmount = data.TaxAmount,
            PaymentMethodId = data.PaymentMethodId,
            CashSessionId = data.CashSessionId,
            Reference = reference,
        };
    }

    public Result Void(string reason, Guid userId, DateTimeOffset now)
    {
        if (Status != ExpenseStatus.Posted)
        {
            return ExpensesErrors.InvalidStatus;
        }

        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length is < 5 or > 300)
        {
            return ExpensesErrors.ReasonRequired;
        }

        Status = ExpenseStatus.Voided;
        VoidedAt = now;
        VoidedBy = userId;
        VoidReason = trimmed;
        return Result.Success();
    }
}

/// <summary>Errores de negocio de los gastos con código estable.</summary>
public static class ExpensesErrors
{
    public static readonly Error CategoryNotFound = Error.NotFound("EXPENSES.CATEGORY_NOT_FOUND", "La categoría de gasto no existe o está inactiva.");

    public static readonly Error CategoryTooDeep = Error.BusinessRule("EXPENSES.CATEGORY_TOO_DEEP", "Las categorías de gasto tienen solo dos niveles.");

    public static readonly Error InvalidCategory = Error.Validation("EXPENSES.INVALID_CATEGORY", "El nombre de la categoría es obligatorio (máximo 80 caracteres).");

    public static readonly Error CategoryDuplicated = Error.Conflict("EXPENSES.CATEGORY_DUPLICATED", "Ya existe una categoría con ese nombre en ese nivel.");

    public static readonly Error ExpenseNotFound = Error.NotFound("EXPENSES.NOT_FOUND", "El gasto no existe.");

    public static readonly Error InvalidExpense = Error.Validation(
        "EXPENSES.INVALID_EXPENSE", "Gasto inválido: descripción de hasta 300 caracteres, valor mayor que cero e IVA entre 0 y el valor.");

    public static readonly Error InvalidStatus = Error.BusinessRule("EXPENSES.INVALID_STATUS", "El gasto ya fue anulado.");

    public static readonly Error ReasonRequired = Error.Validation("EXPENSES.REASON_REQUIRED", "Indique el motivo (de 5 a 300 caracteres).");

    public static readonly Error SessionRequired = Error.Validation(
        "EXPENSES.CASH_SESSION_REQUIRED", "Un gasto en efectivo del cajón se registra desde la jornada de caja abierta.");
}
