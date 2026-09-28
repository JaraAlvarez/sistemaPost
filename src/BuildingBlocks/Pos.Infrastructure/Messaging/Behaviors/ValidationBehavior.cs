using FluentValidation;
using Pos.Application.Abstractions.Messaging;
using Pos.SharedKernel.Results;

namespace Pos.Infrastructure.Messaging.Behaviors;

/// <summary>
/// Ejecuta los validadores FluentValidation de la petición antes del handler. Si hay errores,
/// el handler no se ejecuta y se devuelve un <c>VALIDATION.FAILED</c> con el detalle por campo.
/// </summary>
internal sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    public const string ErrorCode = "VALIDATION.FAILED";

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerNext<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        var validatorList = validators as IReadOnlyCollection<IValidator<TRequest>> ?? validators.ToArray();
        if (validatorList.Count == 0)
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);
        var fieldErrors = new List<FieldError>();
        foreach (var validator in validatorList)
        {
            var result = await validator.ValidateAsync(context, cancellationToken);
            fieldErrors.AddRange(result.Errors.Select(f => new FieldError(f.PropertyName, f.ErrorCode, f.ErrorMessage)));
        }

        if (fieldErrors.Count == 0)
        {
            return await next();
        }

        var error = Error.Validation(ErrorCode, "Los datos enviados no son válidos.", fieldErrors);
        return ResultFailureFactory<TResponse>.Create(error);
    }
}
