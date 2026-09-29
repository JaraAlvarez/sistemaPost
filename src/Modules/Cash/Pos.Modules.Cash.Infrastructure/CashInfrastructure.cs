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
    }
}

internal sealed class CashStore(PosDbContext context) : ICashStore
{
    public void Add(PaymentMethod method) => context.Add(method);

    public async Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(CancellationToken cancellationToken) =>
        await context.Set<PaymentMethod>().ToListAsync(cancellationToken);

    public Task<PaymentMethod?> GetPaymentMethodAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<PaymentMethod>().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
}

internal sealed class PaymentMethodDirectory(PosDbContext context) : IPaymentMethodDirectory
{
    public async Task<PaymentMethodInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await context.Set<PaymentMethod>().AsNoTracking().Where(m => m.Id == id).ToListAsync(cancellationToken) is [var m]
            ? new PaymentMethodInfo(m.Id, m.Code, m.Name, PaymentMethodMapping.Db(m.Kind), m.DianCode, m.RequiresReference, m.AffectsCashDrawer,
                m.Status == MasterStatus.Active)
            : null;
}

internal sealed class CashConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_payment_methods__company_code"] = CashErrors.PaymentMethodCodeDuplicated,
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
    }
}
