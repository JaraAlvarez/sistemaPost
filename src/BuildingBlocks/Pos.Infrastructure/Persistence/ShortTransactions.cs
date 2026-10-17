using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Pos.Application.Abstractions.Data;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Infrastructure.Persistence;

/// <summary>Implementa <see cref="IShortTransactions"/> sobre el contexto del caso de uso (cada guardado abre y cierra su transacción).</summary>
internal sealed class ShortTransactions(PosDbContext context, ConstraintErrorTranslator translator) : IShortTransactions
{
    public async Task<Result> SaveAsync(Action? reapply = null, CancellationToken cancellationToken = default)
    {
        EnsureRetrySafe();
        for (var round = 0; ; round++)
        {
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }
            catch (DbUpdateConcurrencyException ex)
            {
                foreach (var entry in ex.Entries)
                {
                    await entry.ReloadAsync(cancellationToken);
                }

                if (reapply is null || round > 0)
                {
                    await DiscardAsync(cancellationToken);
                    translator.TryTranslate(ex, out var conflict);
                    return Result.Failure(conflict);
                }

                reapply();
            }
            catch (DbUpdateException ex) when (translator.TryTranslate(ex, out var error))
            {
                // Lo que no se pudo guardar no queda pendiente: si no, "envenenaría" los guardados siguientes del mismo contexto.
                await DiscardAsync(cancellationToken);
                return Result.Failure(error);
            }
        }
    }

    /// <summary>
    /// Solo se reintentan entidades sin efectos de los interceptores que se repetirían al reintentar (auditoría automática de filas,
    /// cambios para sincronizar o eventos de dominio): con ellas un reintento podría duplicar bitácora, sincronización o mensajes.
    /// </summary>
    private void EnsureRetrySafe()
    {
        foreach (var entry in context.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var type = entry.Metadata.ClrType;
            if (type.GetCustomAttribute<AuditedAttribute>() is not null || entry.Entity is ISyncVersioned
                || entry.Entity is IAggregateRoot { DomainEvents.Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"IShortTransactions no admite {type.Name}: tiene auditoría automática, sincronización o eventos de dominio. Use la transacción del caso de uso.");
            }
        }
    }

    /// <summary>Descarta lo pendiente: filas nuevas fuera del contexto y filas cambiadas recargadas desde la base de datos.</summary>
    private async Task DiscardAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in context.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            await DiscardAsync(entry, cancellationToken);
        }
    }

    private static async Task DiscardAsync(EntityEntry entry, CancellationToken cancellationToken)
    {
        if (entry.State == EntityState.Added)
        {
            entry.State = EntityState.Detached;
            return;
        }

        await entry.ReloadAsync(cancellationToken);
    }
}
