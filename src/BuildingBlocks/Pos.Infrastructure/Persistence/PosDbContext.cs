using Microsoft.EntityFrameworkCore;
using Pos.Application.Abstractions.Installation;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Contexto único de la aplicación (decisión A3). No define entidades propias: su modelo lo arman los
/// <see cref="IModelContributor"/> de la infraestructura y de cada módulo. El esquema lo crean las migraciones SQL
/// (no las de EF); una prueba de conformidad verifica que el modelo coincida con la BD real.
/// </summary>
public sealed class PosDbContext(
    DbContextOptions<PosDbContext> options,
    IEnumerable<IModelContributor> contributors,
    IInstallationContext installation)
    : DbContext(options)
{
    /// <summary>
    /// Empresa por la que se filtran las entidades <see cref="SharedKernel.Domain.ICompanyOwned"/>.
    /// <c>null</c> (antes del asistente inicial) = sin filtro.
    /// </summary>
    public Guid? TenantCompanyId => installation.CompanyId;

    /// <summary>
    /// Toda escritura va en una transacción explícita: la auditoría reserva su consecutivo (seq) dentro de ella y
    /// el horizonte seguro del sellado depende de que ninguna transacción dure más que transaction_timeout.
    /// </summary>
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
        foreach (var contributor in contributors)
        {
            contributor.Configure(modelBuilder);
        }

        ModelConventions.ApplyDomainConventions(modelBuilder, this);
    }
}
