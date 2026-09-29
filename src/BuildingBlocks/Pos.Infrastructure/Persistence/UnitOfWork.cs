using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Application.Abstractions.Data;
using Pos.SharedKernel.Results;

namespace Pos.Infrastructure.Persistence;

internal sealed class UnitOfWork(PosDbContext context) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
}

/// <summary>Traduce violaciones de restricciones de PostgreSQL a errores de negocio (ninguna llega como 500).</summary>
public sealed class ConstraintErrorTranslator
{
    public const string GenericCode = "DATABASE.CONSTRAINT_VIOLATION";
    public const string ConcurrencyCode = "DATABASE.CONCURRENT_UPDATE";

    private readonly Dictionary<string, Error> _errors;

    public ConstraintErrorTranslator(IEnumerable<IConstraintErrorProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _errors = new Dictionary<string, Error>(StringComparer.Ordinal);
        foreach (var (constraint, error) in providers.SelectMany(p => p.GetConstraintErrors()))
        {
            if (!_errors.TryAdd(constraint, error))
            {
                throw new InvalidOperationException($"La restricción '{constraint}' tiene más de un error asignado.");
            }
        }
    }

    public bool TryTranslate(Exception exception, out Error error)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is DbUpdateConcurrencyException)
        {
            error = Error.Conflict(ConcurrencyCode, "Otro usuario modificó este registro. Recargue los datos e intente de nuevo.");
            return true;
        }

        var postgres = exception as PostgresException ?? exception.InnerException as PostgresException;
        if (postgres is null
            || postgres.SqlState is not (PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation
                or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NotNullViolation or PostgresErrorCodes.ExclusionViolation))
        {
            error = Error.None;
            return false;
        }

        if (postgres.ConstraintName is { } name && _errors.TryGetValue(name, out var known))
        {
            error = known;
            return true;
        }

        error = postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation
            ? Error.Conflict(GenericCode, "El registro está relacionado con otros datos y no se puede completar la operación.")
            : Error.Conflict(GenericCode, "Los datos no cumplen una regla de integridad de la base de datos.");
        return true;
    }
}
