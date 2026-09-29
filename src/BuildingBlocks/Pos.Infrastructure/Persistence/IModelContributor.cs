using Microsoft.EntityFrameworkCore;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Cada módulo aporta el mapeo de SUS tablas al <see cref="PosDbContext"/> único (decisión A3): una sola transacción
/// y un solo punto de interceptores (auditoría, outbox) para toda la aplicación.
/// </summary>
public interface IModelContributor
{
    void Configure(ModelBuilder modelBuilder);
}

/// <summary>
/// Traducción de violaciones de restricciones de la BD a errores de negocio legibles
/// (p. ej. <c>ux_branches__company_code</c> → <c>ORGANIZATION.BRANCH_CODE_DUPLICATED</c>). Cada módulo aporta las suyas.
/// </summary>
public interface IConstraintErrorProvider
{
    IReadOnlyDictionary<string, SharedKernel.Results.Error> GetConstraintErrors();
}
