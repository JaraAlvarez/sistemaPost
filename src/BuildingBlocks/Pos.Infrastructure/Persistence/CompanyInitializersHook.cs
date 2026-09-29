using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Abstractions.Installation;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Al arrancar con la BD lista, ejecuta los <see cref="ICompanyInitializer"/> de todos los módulos sobre la empresa local:
/// una instalación creada antes de que existiera un módulo recibe sus datos iniciales (idempotente).
/// </summary>
internal sealed class CompanyInitializersHook : IDatabaseReadyHook
{
    public async Task RunAsync(IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        var installation = scopedServices.GetRequiredService<IInstallationContext>();
        if (installation.CompanyId is not { } companyId)
        {
            return;
        }

        var context = scopedServices.GetRequiredService<PosDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        foreach (var initializer in scopedServices.GetServices<ICompanyInitializer>().OrderBy(i => i.Order))
        {
            await initializer.InitializeAsync(companyId, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
