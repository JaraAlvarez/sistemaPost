using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Pos.Infrastructure.Auditing;
using Pos.Infrastructure.Persistence;

namespace Pos.Cloud.Infrastructure.Persistence;

/// <summary>
/// Contexto único de la nube (mismo patrón que el POS, decisión A3): cada módulo de la nube aporta el mapeo de SUS tablas con
/// un <see cref="ICloudModelContributor"/>. El esquema lo crean las migraciones SQL; una prueba verifica el modelo contra la BD.
/// </summary>
public sealed class CloudDbContext(DbContextOptions<CloudDbContext> options, IEnumerable<ICloudModelContributor> contributors)
    : DbContext(options)
{
    internal string ModelKey { get; } = string.Join('|', contributors.Select(c => c.GetType().FullName).Order(StringComparer.Ordinal));

    /// <summary>Toda escritura va en una transacción explícita (la auditoría reserva su seq dentro de ella).</summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (Database.CurrentTransaction is not null)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync: la persistencia es asíncrona.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditLogRecord>(b =>
        {
            b.ToTable("audit_log", "audit");
            b.HasKey(x => new { x.OccurredAt, x.Id });
            b.Property(x => x.Seq).ValueGeneratedNever();
            b.Property(x => x.OldValues).HasColumnType("jsonb");
            b.Property(x => x.NewValues).HasColumnType("jsonb");
            b.Property(x => x.IpAddress).HasColumnType("inet");
            b.Property(x => x.RowHash).HasColumnType("char(64)");
        });

        foreach (var contributor in contributors)
        {
            contributor.Configure(modelBuilder);
        }
    }
}

/// <summary>Mapeo de las tablas de un módulo de la nube al <see cref="CloudDbContext"/>.</summary>
public interface ICloudModelContributor : IModelContributor;

/// <summary>Clave de caché del modelo: tipo de contexto + contribuidores (varias composiciones en el mismo proceso de pruebas).</summary>
internal sealed class CloudModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is CloudDbContext cloud ? (context.GetType(), cloud.ModelKey, designTime) : (object)(context.GetType(), designTime);
}
