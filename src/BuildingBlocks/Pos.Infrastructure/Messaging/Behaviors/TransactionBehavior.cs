using Microsoft.EntityFrameworkCore;
using Pos.Application.Abstractions.Messaging;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Settings;
using Pos.SharedKernel.Results;

namespace Pos.Infrastructure.Messaging.Behaviors;

/// <summary>
/// Envuelve cada COMANDO en una transacción: confirma si el resultado es exitoso y revierte si falla.
/// Guarda lo pendiente (p. ej. auditoría explícita) antes de confirmar. Las violaciones de restricciones de la BD
/// se devuelven como errores de negocio legibles. Las consultas no abren transacción.
/// </summary>
internal sealed class TransactionBehavior<TRequest, TResponse>(
    PosDbContext context, ConstraintErrorTranslator translator, CommitCallbacks callbacks)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    private static readonly bool IsCommand = typeof(TRequest).GetInterfaces()
        .Any(i => i == typeof(ICommand) || (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)));

    public async Task<TResponse> Handle(TRequest request, RequestHandlerNext<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (!IsCommand || context.Database.CurrentTransaction is not null)
        {
            return await next();
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var response = await next();
            if (response.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                context.ChangeTracker.Clear();
                callbacks.Clear();
                return response;
            }

            if (context.ChangeTracker.HasChanges())
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            callbacks.RunAndClear();
            return response;
        }
        catch (Exception ex) when (translator.TryTranslate(ex, out var error))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            context.ChangeTracker.Clear();
            callbacks.Clear();
            return ResultFailureFactory<TResponse>.Create(error);
        }
    }
}
