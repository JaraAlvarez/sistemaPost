using System.Reflection;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.Infrastructure.Messaging;

/// <summary>
/// Permiso del portal declarado en la petición con <see cref="RequiresPermissionAttribute"/>. Se verifica aquí (no solo en la
/// pantalla o en el endpoint): una pantalla interactiva y la API <c>/admin</c> aplican exactamente la misma regla.
/// </summary>
internal sealed class PortalPermissionBehavior<TRequest, TResponse>(IPortalUserContext user, IPermissionChecker permissions)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    public const string AuthenticationRequiredCode = "AUTH.REQUIRED";
    public const string PermissionDeniedCode = "AUTH.PERMISSION_DENIED";

    private static readonly string? Permission = typeof(TRequest).GetCustomAttribute<RequiresPermissionAttribute>()?.Permission;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerNext<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (Permission is null)
        {
            return await next();
        }

        if (!user.IsAuthenticated)
        {
            return ResultFailureFactory<TResponse>.Create(Error.Unauthorized(AuthenticationRequiredCode, "Inicie sesión para continuar."));
        }

        return await permissions.HasPermissionAsync(Permission, cancellationToken: cancellationToken)
            ? await next()
            : ResultFailureFactory<TResponse>.Create(Error.Forbidden(PermissionDeniedCode, $"No tiene el permiso '{Permission}'."));
    }
}

/// <summary>
/// Cada COMANDO en una transacción (igual que en el POS): confirma si el resultado es exitoso, revierte si falla y traduce las
/// violaciones de restricciones de la BD a errores de negocio. Las consultas no abren transacción.
/// </summary>
internal sealed class CloudTransactionBehavior<TRequest, TResponse>(CloudDbContext context, ConstraintErrorTranslator translator)
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
                return response;
            }

            if (context.ChangeTracker.HasChanges())
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return response;
        }
        catch (Exception ex) when (translator.TryTranslate(ex, out var error))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            context.ChangeTracker.Clear();
            return ResultFailureFactory<TResponse>.Create(error);
        }
    }
}

/// <summary>Construye un <typeparamref name="TResponse"/> fallido desde el pipeline genérico (delegado creado una vez por tipo).</summary>
internal static class ResultFailureFactory<TResponse>
    where TResponse : Result
{
    private static readonly Func<Error, TResponse> Factory = BuildFactory();

    public static TResponse Create(Error error) => Factory(error);

    private static Func<Error, TResponse> BuildFactory()
    {
        var responseType = typeof(TResponse);
        if (responseType == typeof(Result))
        {
            return error => (TResponse)Result.Failure(error);
        }

        var failure = typeof(Result)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Result.Failure) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(responseType.GetGenericArguments()[0]);
        return failure.CreateDelegate<Func<Error, TResponse>>();
    }
}
