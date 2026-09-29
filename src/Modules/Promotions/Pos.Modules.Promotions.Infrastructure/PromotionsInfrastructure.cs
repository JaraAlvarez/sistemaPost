using System.Globalization;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Promotions.Application;
using Pos.Modules.Promotions.Contracts;
using Pos.Modules.Promotions.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Promotions.Infrastructure;

internal sealed class PromotionsModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Promotion>(b =>
        {
            b.ToTable("promotions", "promotions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Type).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.Days).HasConversion<int>();
            b.HasMany(x => x.Items).WithOne().HasForeignKey("PromotionId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Items).HasField("_items");
            b.HasMany(x => x.Branches).WithOne().HasForeignKey("PromotionId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Branches).HasField("_branches");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<PromotionItem>(b =>
        {
            b.ToTable("promotion_items", "promotions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<PromotionBranch>(b =>
        {
            b.ToTable("promotion_branches", "promotions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });
    }
}

internal sealed class PromotionStore(PosDbContext context) : IPromotionStore
{
    public void Add(Promotion promotion) => context.Add(promotion);

    public Task<Promotion?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Promotions().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Promotion>> ListAsync(PromotionStatus? status, CancellationToken cancellationToken) =>
        await Promotions().Where(p => status == null || p.Status == status).OrderByDescending(p => p.ValidFrom).Take(500).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Promotion>> ListActiveAsync(DateTimeOffset at, CancellationToken cancellationToken) =>
        await Promotions().AsNoTracking()
            .Where(p => p.Status == PromotionStatus.Active && p.ValidFrom <= at && (p.ValidTo == null || p.ValidTo > at))
            .ToListAsync(cancellationToken);

    private IQueryable<Promotion> Promotions() => context.Set<Promotion>().Include(p => p.Items).Include(p => p.Branches).AsSplitQuery();
}

/// <summary>Reporte por promoción con SQL directo sobre las ventas completadas (modelo de lectura).</summary>
internal sealed class PromotionReports(PosDbContext context) : IPromotionReports
{
    public async Task<IReadOnlyList<PromotionReportRowDto>> GetReportAsync(Guid branchId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        var rows = await connection.QueryAsync<PromotionReportRowDto>(new CommandDefinition(
            """
            SELECT l.promotion_id AS PromotionId, p.name AS Name, COUNT(DISTINCT s.id)::int AS Sales, SUM(l.quantity) AS Quantity,
                   SUM(l.promotion_discount) AS Discount, SUM(l.total) AS NetSales
            FROM sales.sale_lines l
            JOIN sales.sales s ON s.id = l.sale_id
            JOIN promotions.promotions p ON p.id = l.promotion_id
            WHERE s.branch_id = @branchId AND s.status = 'COMPLETED' AND l.status = 'ACTIVE'
              AND s.business_date BETWEEN @from::date AND @to::date
            GROUP BY l.promotion_id, p.name
            ORDER BY SUM(l.promotion_discount) DESC
            """,
            new
            {
                branchId, from = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
            context.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: cancellationToken));
        return [.. rows];
    }
}

internal sealed class PromotionsConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["fk_promotion_items__product"] = Error.Validation("PROMOTIONS.PRODUCT_NOT_FOUND", "Un producto de la promoción no existe."),
        ["fk_promotion_items__packaging"] = Error.Validation("PROMOTIONS.PACKAGING_NOT_FOUND", "La presentación no pertenece al producto."),
        ["fk_promotion_items__category"] = Error.Validation("PROMOTIONS.CATEGORY_NOT_FOUND", "Una categoría de la promoción no existe."),
        ["fk_promotion_items__brand"] = Error.Validation("PROMOTIONS.BRAND_NOT_FOUND", "Una marca de la promoción no existe."),
        ["fk_promotion_branches__branch"] = Error.Validation("PROMOTIONS.BRANCH_NOT_FOUND", "Una sucursal de la promoción no existe."),
    };
}

public static class PromotionsInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, PromotionsModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, PromotionsConstraintErrors>();
        services.AddScoped<IPromotionStore, PromotionStore>();
        services.AddScoped<IPromotionReports, PromotionReports>();
    }
}
