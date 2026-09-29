using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Expenses.Contracts;
using Pos.Modules.Expenses.Domain;
using Pos.Modules.Parties.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Expenses.Application;

/// <summary>Gastos y categorías (EF Core).</summary>
public interface IExpenseStore
{
    void Add(ExpenseCategory category);

    void Add(Expense expense);

    Task<IReadOnlyList<ExpenseCategory>> GetCategoriesAsync(CancellationToken cancellationToken);

    Task<Expense?> GetExpenseAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<(Expense Expense, DateTimeOffset CreatedAt)>> ListAsync(ExpenseFilter filter, CancellationToken cancellationToken);
}

public sealed record ExpenseFilter(Guid BranchId, DateOnly? From, DateOnly? To, Guid? CategoryId, Guid? CashSessionId);

public sealed class ExpensesPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => ExpensesPermissions.All;
}

/// <summary>Categorías de gasto básicas de cada empresa (propuesta §10). Idempotente.</summary>
public sealed class ExpensesInitializer(IExpenseStore store, IIdGenerator ids) : ICompanyInitializer
{
    public int Order => 70;

    public async Task InitializeAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var existing = await store.GetCategoriesAsync(cancellationToken);
        (string Name, string[] Children)[] seeds =
        [
            ("Servicios públicos", ["Energía", "Agua y alcantarillado", "Gas", "Internet y teléfono"]),
            ("Arriendo", []),
            ("Aseo y cafetería", []),
            ("Transporte y fletes", []),
            ("Papelería", []),
            ("Mantenimiento", []),
            ("Otros", []),
        ];
        var order = 0;
        foreach (var (name, children) in seeds)
        {
            order += 10;
            var parent = existing.FirstOrDefault(c => c.ParentId is null && c.Name == name);
            if (parent is null)
            {
                parent = ExpenseCategory.Create(ids.NewId(), companyId, name, null, order).Value;
                store.Add(parent);
            }

            var childOrder = 0;
            foreach (var child in children.Where(c => !existing.Any(e => e.ParentId == parent.Id && e.Name == c)))
            {
                store.Add(ExpenseCategory.Create(ids.NewId(), companyId, child, parent, childOrder += 10).Value);
            }
        }
    }
}

internal static class ExpenseMapping
{
    public static ExpenseDto ToDto(this Expense e, string categoryName, DateTimeOffset createdAt) => new(
        e.Id, e.Number, e.BusinessDate, e.CategoryId, categoryName, e.PartyId, e.Description, e.Amount, e.TaxAmount, e.PaymentMethodId, e.CashSessionId,
        e.Reference, e.Status.ToString().ToUpperInvariant(), e.VoidReason, createdAt);

    public static ExpenseCategoryDto ToDto(this ExpenseCategory c) => new(c.Id, c.ParentId, c.Name, c.SortOrder, c.Status.ToString().ToUpperInvariant());
}

// ─────────────────────────────── Categorías ───────────────────────────────

public sealed record ListExpenseCategoriesQuery : IQuery<IReadOnlyList<ExpenseCategoryDto>>;

internal sealed class ListExpenseCategoriesHandler(IExpenseStore store) : IQueryHandler<ListExpenseCategoriesQuery, IReadOnlyList<ExpenseCategoryDto>>
{
    public async Task<Result<IReadOnlyList<ExpenseCategoryDto>>> Handle(ListExpenseCategoriesQuery request, CancellationToken cancellationToken) =>
        (await store.GetCategoriesAsync(cancellationToken)).OrderBy(c => c.ParentId.HasValue).ThenBy(c => c.SortOrder).Select(c => c.ToDto()).ToList();
}

public sealed record CreateExpenseCategoryCommand(string Name, Guid? ParentId, int SortOrder) : ICommand<ExpenseCategoryDto>;

internal sealed class CreateExpenseCategoryHandler(IInstallationContext installation, IExpenseStore store, IIdGenerator ids)
    : ICommandHandler<CreateExpenseCategoryCommand, ExpenseCategoryDto>
{
    public async Task<Result<ExpenseCategoryDto>> Handle(CreateExpenseCategoryCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
        }

        ExpenseCategory? parent = null;
        if (request.ParentId is { } parentId)
        {
            parent = (await store.GetCategoriesAsync(cancellationToken)).SingleOrDefault(c => c.Id == parentId);
            if (parent is null)
            {
                return ExpensesErrors.CategoryNotFound;
            }
        }

        var category = ExpenseCategory.Create(ids.NewId(), companyId, request.Name, parent, request.SortOrder);
        if (category.IsFailure)
        {
            return category.Error;
        }

        store.Add(category.Value);
        return category.Value.ToDto();
    }
}

public sealed record UpdateExpenseCategoryCommand(Guid CategoryId, string Name, int SortOrder, bool IsActive) : ICommand<ExpenseCategoryDto>;

internal sealed class UpdateExpenseCategoryHandler(IExpenseStore store) : ICommandHandler<UpdateExpenseCategoryCommand, ExpenseCategoryDto>
{
    public async Task<Result<ExpenseCategoryDto>> Handle(UpdateExpenseCategoryCommand request, CancellationToken cancellationToken)
    {
        var category = (await store.GetCategoriesAsync(cancellationToken)).SingleOrDefault(c => c.Id == request.CategoryId);
        if (category is null)
        {
            return ExpensesErrors.CategoryNotFound;
        }

        var updated = category.Update(request.Name, request.SortOrder, request.IsActive);
        return updated.IsSuccess ? category.ToDto() : updated.Error;
    }
}

// ─────────────────────────────── Gastos ───────────────────────────────

public sealed record ExpenseRequest(
    Guid CategoryId, string Description, decimal Amount, decimal TaxAmount, Guid PaymentMethodId, Guid? PartyId, Guid? CashSessionId, string? Reference,
    DateOnly? BusinessDate);

/// <summary>
/// Registra un gasto. Con <c>CashSessionId</c> sale del cajón en la misma transacción (movimiento EXPENSE, efectivo
/// suficiente, RN-CSH-06). <c>FromCash</c>: el gasto menor que registra el cajero desde su jornada (exige la jornada).
/// </summary>
public sealed record CreateExpenseCommand(ExpenseRequest Expense, bool FromCash = false) : ICommand<ExpenseDto>;

internal sealed class CreateExpenseHandler(
    IInstallationContext installation,
    IExpenseStore store,
    IPaymentMethodDirectory methods,
    IPartyDirectory parties,
    ICashRegister cash,
    IDocumentNumberAllocator numbers,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<CreateExpenseCommand, ExpenseDto>
{
    public async Task<Result<ExpenseDto>> Handle(CreateExpenseCommand command, CancellationToken cancellationToken)
    {
        var request = command.Expense;
        if (installation.CompanyId is not { } companyId || installation.BranchId is not { } branchId)
        {
            return Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
        }

        if (command.FromCash && request.CashSessionId is null)
        {
            return ExpensesErrors.SessionRequired;
        }

        var category = (await store.GetCategoriesAsync(cancellationToken)).SingleOrDefault(c => c.Id == request.CategoryId);
        if (category is not { Status: MasterStatus.Active })
        {
            return ExpensesErrors.CategoryNotFound;
        }

        if (await methods.GetAsync(request.PaymentMethodId, cancellationToken) is not { IsActive: true })
        {
            return Error.NotFound("CASH.PAYMENT_METHOD_NOT_FOUND", "El medio de pago no existe o está inactivo.");
        }

        if (request.PartyId is { } partyId && (await parties.GetAsync([partyId], cancellationToken)).GetValueOrDefault(partyId) is not { Status: "ACTIVE" })
        {
            return Error.NotFound("PARTIES.NOT_FOUND", "El tercero no existe o está inactivo.");
        }

        var number = await numbers.NextForBranchAsync("EXPENSE", branchId, cancellationToken);
        var expense = Expense.Create(ids.NewId(), companyId, branchId, number.Number, new ExpenseData(
            request.BusinessDate ?? clock.Today, category.Id, request.PartyId, request.Description, request.Amount, request.TaxAmount, request.PaymentMethodId,
            request.CashSessionId, request.Reference));
        if (expense.IsFailure)
        {
            return expense.Error;
        }

        store.Add(expense.Value);
        if (request.CashSessionId is { } sessionId)
        {
            var moved = await cash.RecordOutflowAsync(
                new CashOutflowRequest(sessionId, "EXPENSE", request.PaymentMethodId, expense.Value.Amount, "EXPENSE", expense.Value.Id, expense.Value.Number,
                    $"{category.Name}: {expense.Value.Description}"),
                cancellationToken);
            if (moved.IsFailure)
            {
                return moved.Error;
            }
        }

        await audit.WriteAsync(
            new AuditEntry("expenses", "EXPENSE_POSTED", nameof(Expense), expense.Value.Id, expense.Value.AuditLabel,
                $"Gasto {expense.Value.Number} ({category.Name}) por {expense.Value.Amount:N2}{(request.CashSessionId is null ? string.Empty : ", pagado desde la caja")}."),
            cancellationToken);
        return expense.Value.ToDto(category.Name, clock.UtcNow);
    }
}

/// <summary>Anula un gasto; si salió de la caja, el dinero vuelve con una corrección en esa jornada (que debe seguir abierta).</summary>
public sealed record VoidExpenseCommand(Guid ExpenseId, string Reason) : ICommand<ExpenseDto>;

internal sealed class VoidExpenseHandler(IExpenseStore store, ICashRegister cash, IActorContext actor, IAuditWriter audit, IClock clock)
    : ICommandHandler<VoidExpenseCommand, ExpenseDto>
{
    public async Task<Result<ExpenseDto>> Handle(VoidExpenseCommand request, CancellationToken cancellationToken)
    {
        var expense = await store.GetExpenseAsync(request.ExpenseId, cancellationToken);
        if (expense is null)
        {
            return ExpensesErrors.ExpenseNotFound;
        }

        var voided = expense.Void(request.Reason, actor.ActorId!.Value, clock.UtcNow);
        if (voided.IsFailure)
        {
            return voided.Error;
        }

        if (expense.CashSessionId is { } sessionId)
        {
            var returned = await cash.ReturnOutflowAsync(
                new CashOutflowRequest(sessionId, "EXPENSE", expense.PaymentMethodId, expense.Amount, "EXPENSE_VOID", expense.Id, expense.Number, null),
                $"Anulación del gasto {expense.Number}: {expense.VoidReason}",
                cancellationToken);
            if (returned.IsFailure)
            {
                return returned.Error;
            }
        }

        await audit.WriteAsync(
            new AuditEntry("expenses", "EXPENSE_VOIDED", nameof(Expense), expense.Id, expense.AuditLabel,
                $"Gasto {expense.Number} por {expense.Amount:N2} anulado: {expense.VoidReason}", Severity: AuditSeverity.Warning),
            cancellationToken);
        var category = (await store.GetCategoriesAsync(cancellationToken)).SingleOrDefault(c => c.Id == expense.CategoryId);
        return expense.ToDto(category?.Name ?? string.Empty, clock.UtcNow);
    }
}

public sealed record ListExpensesQuery(DateOnly? From, DateOnly? To, Guid? CategoryId, Guid? CashSessionId) : IQuery<IReadOnlyList<ExpenseDto>>;

internal sealed class ListExpensesHandler(IInstallationContext installation, IExpenseStore store) : IQueryHandler<ListExpensesQuery, IReadOnlyList<ExpenseDto>>
{
    public async Task<Result<IReadOnlyList<ExpenseDto>>> Handle(ListExpensesQuery request, CancellationToken cancellationToken)
    {
        if (installation.BranchId is not { } branchId)
        {
            return Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
        }

        var categories = (await store.GetCategoriesAsync(cancellationToken)).ToDictionary(c => c.Id, c => c.Name);
        var rows = await store.ListAsync(new ExpenseFilter(branchId, request.From, request.To, request.CategoryId, request.CashSessionId), cancellationToken);
        return rows.Select(r => r.Expense.ToDto(categories.GetValueOrDefault(r.Expense.CategoryId) ?? string.Empty, r.CreatedAt)).ToList();
    }
}
