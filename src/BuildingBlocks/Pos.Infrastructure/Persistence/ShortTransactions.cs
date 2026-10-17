using Microsoft.EntityFrameworkCore;
using Pos.Application.Abstractions.Data;
using Pos.SharedKernel.Results;

namespace Pos.Infrastructure.Persistence;

/// <summary>Implementa <see cref="IShortTransactions"/> sobre el contexto del caso de uso (cada guardado abre y cierra su transacción).</summary>
internal sealed class ShortTransactions(PosDbContext context, ConstraintErrorTranslator translator) : IShortTransactions
{
    public async Task<Result> SaveAsync(Action? reapply = null, CancellationToken cancellationToken = default)
    {
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
                    translator.TryTranslate(ex, out var conflict);
                    return Result.Failure(conflict);
                }

                reapply();
            }
            catch (Exception ex) when (ex is not DbUpdateConcurrencyException && translator.TryTranslate(ex, out var error))
            {
                return Result.Failure(error);
            }
        }
    }
}
