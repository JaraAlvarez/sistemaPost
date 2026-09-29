using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Expenses.Application;
using Pos.Modules.Expenses.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Expenses.Infrastructure;

internal sealed class ExpensesModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExpenseCategory>(b =>
        {
            b.ToTable("expense_categories", "expenses");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.HasOne<ExpenseCategory>().WithMany().HasForeignKey(x => new { x.ParentId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<Expense>(b =>
        {
            b.ToTable("expenses", "expenses");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasOne<ExpenseCategory>().WithMany().HasForeignKey(x => new { x.CategoryId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });
    }
}

internal sealed class ExpenseStore(PosDbContext context) : IExpenseStore
{
    public void Add(ExpenseCategory category) => context.Add(category);

    public void Add(Expense expense) => context.Add(expense);

    public async Task<IReadOnlyList<ExpenseCategory>> GetCategoriesAsync(CancellationToken cancellationToken) =>
        await context.Set<ExpenseCategory>().ToListAsync(cancellationToken);

    public Task<Expense?> GetExpenseAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<Expense>().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<(Expense Expense, DateTimeOffset CreatedAt)>> ListAsync(ExpenseFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = context.Set<Expense>().AsNoTracking().Where(e => e.BranchId == filter.BranchId);
        if (filter.From is { } from)
        {
            query = query.Where(e => e.BusinessDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(e => e.BusinessDate <= to);
        }

        if (filter.CategoryId is { } category)
        {
            query = query.Where(e => e.CategoryId == category);
        }

        if (filter.CashSessionId is { } session)
        {
            query = query.Where(e => e.CashSessionId == session);
        }

        var rows = await query.OrderByDescending(e => EF.Property<DateTimeOffset>(e, ModelConventions.CreatedAt)).Take(2000)
            .Select(e => new { Expense = e, CreatedAt = EF.Property<DateTimeOffset>(e, ModelConventions.CreatedAt) })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => (r.Expense, r.CreatedAt))];
    }
}

internal sealed class ExpensesConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_expense_categories__parent_name"] = ExpensesErrors.CategoryDuplicated,
        ["fk_expenses__party"] = Error.NotFound("PARTIES.NOT_FOUND", "El tercero no existe."),
    };
}

public static class ExpensesInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, ExpensesModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, ExpensesConstraintErrors>();
        services.AddScoped<IExpenseStore, ExpenseStore>();
    }
}
