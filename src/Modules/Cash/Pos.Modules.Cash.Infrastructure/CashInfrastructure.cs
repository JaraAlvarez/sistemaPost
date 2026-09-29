using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Cash.Application;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Cash.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Cash.Infrastructure;

internal sealed class CashModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentMethod>(b =>
        {
            b.ToTable("payment_methods", "cash");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<Denomination>(b =>
        {
            b.ToTable("denominations", "cash");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<CashSession>(b =>
        {
            b.ToTable("cash_sessions", "cash");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.HasMany(x => x.Counts).WithOne().HasForeignKey("SessionId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Counts).HasField("_counts");
            b.HasMany(x => x.Totals).WithOne().HasForeignKey("SessionId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Totals).HasField("_totals");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<CashCount>(b =>
        {
            b.ToTable("cash_counts", "cash");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
            b.HasMany(x => x.Lines).WithOne().HasForeignKey("CountId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Lines).HasField("_lines");
        });

        modelBuilder.Entity<CashCountLine>(b =>
        {
            b.ToTable("cash_count_lines", "cash");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<CashSessionTotal>(b =>
        {
            b.ToTable("cash_session_totals", "cash");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<CashMovement>(b =>
        {
            b.ToTable("cash_movements", "cash");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.MovementType).HasUpperSnakeConversion();
            b.Ignore(x => x.SignedAmount);
            b.HasOne<CashSession>().WithMany().HasForeignKey(x => new { x.SessionId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class CashStore(PosDbContext context) : ICashStore
{
    public void Add(PaymentMethod method) => context.Add(method);

    public async Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(CancellationToken cancellationToken) =>
        await context.Set<PaymentMethod>().ToListAsync(cancellationToken);

    public Task<PaymentMethod?> GetPaymentMethodAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<PaymentMethod>().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

    public void Add(Denomination denomination) => context.Add(denomination);

    public void Add(CashSession session) => context.Add(session);

    public void Add(CashMovement movement) => context.Add(movement);

    public async Task<IReadOnlyList<Denomination>> GetDenominationsAsync(CancellationToken cancellationToken) =>
        await context.Set<Denomination>().ToListAsync(cancellationToken);

    public Task<CashSession?> GetSessionAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<CashSession>().Include(s => s.Counts).ThenInclude(c => c.Lines).Include(s => s.Totals).AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<CashSession?> GetUnclosedSessionAsync(Guid? posTerminalId, Guid? cashierId, CancellationToken cancellationToken) =>
        context.Set<CashSession>().Include(s => s.Counts).ThenInclude(c => c.Lines).Include(s => s.Totals).AsSplitQuery()
            .Where(s => s.Status != CashSessionStatus.Closed)
            .Where(s => posTerminalId == null || s.PosTerminalId == posTerminalId)
            .Where(s => cashierId == null || s.CashierId == cashierId)
            .FirstOrDefaultAsync(cancellationToken);
}

internal sealed class PaymentMethodDirectory(PosDbContext context) : IPaymentMethodDirectory
{
    public async Task<PaymentMethodInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await context.Set<PaymentMethod>().AsNoTracking().Where(m => m.Id == id).ToListAsync(cancellationToken) is [var m]
            ? new PaymentMethodInfo(m.Id, m.Code, m.Name, PaymentMethodMapping.Db(m.Kind), m.DianCode, m.RequiresReference, m.AffectsCashDrawer,
                m.Status == MasterStatus.Active)
            : null;

    public async Task<PaymentMethodInfo?> GetByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        await context.Set<PaymentMethod>().AsNoTracking().Where(m => m.Code == code).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(cancellationToken) is { } id
            ? await GetAsync(id, cancellationToken)
            : null;
}

internal sealed class CashConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_payment_methods__company_code"] = CashErrors.PaymentMethodCodeDuplicated,
        ["ux_cash_sessions__terminal_open"] = CashErrors.SessionAlreadyOpen,
        ["ux_cash_sessions__cashier_open"] = CashErrors.SessionAlreadyOpen,
        ["ux_cash_movements__session_line"] = Error.Conflict("CASH.CONCURRENT_MOVEMENT", "Otro movimiento se registró al mismo tiempo en la caja: intente de nuevo."),
        ["ck_cash_sessions__reviewer"] = CashErrors.SelfReview,
    };
}

public static class CashInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, CashModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, CashConstraintErrors>();
        services.AddScoped<ICashStore, CashStore>();
        services.AddScoped<IPaymentMethodDirectory, PaymentMethodDirectory>();
        services.AddScoped<ICashLedger, CashLedger>();
        services.AddScoped<ICashReadModel, CashReadModel>();
    }
}
