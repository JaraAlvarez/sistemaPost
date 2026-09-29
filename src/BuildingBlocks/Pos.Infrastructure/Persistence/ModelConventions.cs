using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.SharedKernel.Domain;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Convenciones físicas comunes (docs/fases/fase-02-propuesta.md §1). Las columnas de control son propiedades
/// "sombra" de EF: el dominio no las ve y las completa <see cref="PosSaveChangesInterceptor"/>.
/// </summary>
public static class ModelConventions
{
    public const string CreatedAt = "CreatedAt";
    public const string CreatedBy = "CreatedBy";
    public const string UpdatedAt = "UpdatedAt";
    public const string UpdatedBy = "UpdatedBy";
    public const string DeletedAt = "DeletedAt";
    public const string DeletedBy = "DeletedBy";
    public const string RowVersion = "RowVersion";
    public const string Xmin = "xmin";

    public const string SoftDeleteFilter = "SoftDelete";
    public const string TenantFilter = "Tenant";

    /// <summary>created_at/created_by (+ updated_at/updated_by si <paramref name="withUpdates"/>).</summary>
    public static EntityTypeBuilder<T> HasControlColumns<T>(this EntityTypeBuilder<T> builder, bool withUpdates = true)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Property<DateTimeOffset>(CreatedAt);
        builder.Property<Guid>(CreatedBy);
        if (withUpdates)
        {
            builder.Property<DateTimeOffset?>(UpdatedAt);
            builder.Property<Guid?>(UpdatedBy);
        }

        return builder;
    }

    /// <summary>Concurrencia optimista local con la columna de sistema <c>xmin</c> (sin columna extra).</summary>
    public static EntityTypeBuilder<T> HasXminConcurrency<T>(this EntityTypeBuilder<T> builder)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Property<uint>(Xmin).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        return builder;
    }

    /// <summary>Aplica las convenciones que dependen de interfaces del dominio a todas las entidades del modelo.</summary>
    internal static void ApplyDomainConventions(ModelBuilder modelBuilder, PosDbContext context)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => !t.IsOwned()).ToList())
        {
            var clr = entityType.ClrType;
            var builder = modelBuilder.Entity(clr);

            if (typeof(ISoftDeletable).IsAssignableFrom(clr))
            {
                builder.Property<DateTimeOffset?>(DeletedAt);
                builder.Property<Guid?>(DeletedBy);
                builder.HasQueryFilter(SoftDeleteFilter, SoftDeleteLambda(clr));
            }

            if (typeof(ISyncVersioned).IsAssignableFrom(clr))
            {
                builder.Property<long>(RowVersion).HasDefaultValue(1L);
            }

            if (typeof(ICompanyOwned).IsAssignableFrom(clr))
            {
                builder.HasQueryFilter(TenantFilter, TenantLambda(clr, context));
            }
        }
    }

    private static LambdaExpression SoftDeleteLambda(Type clr)
    {
        var entity = Expression.Parameter(clr, "e");
        var deletedAt = Expression.Call(
            typeof(EF), nameof(EF.Property), [typeof(DateTimeOffset?)], entity, Expression.Constant(DeletedAt));
        return Expression.Lambda(Expression.Equal(deletedAt, Expression.Constant(null, typeof(DateTimeOffset?))), entity);
    }

    // e => context.TenantCompanyId == null || e.CompanyId == context.TenantCompanyId
    private static LambdaExpression TenantLambda(Type clr, PosDbContext context)
    {
        var entity = Expression.Parameter(clr, "e");
        var tenant = Expression.Property(Expression.Constant(context), nameof(PosDbContext.TenantCompanyId));
        var companyId = Expression.Convert(Expression.Property(entity, nameof(ICompanyOwned.CompanyId)), typeof(Guid?));
        var body = Expression.OrElse(
            Expression.Equal(tenant, Expression.Constant(null, typeof(Guid?))),
            Expression.Equal(companyId, tenant));
        return Expression.Lambda(body, entity);
    }
}
