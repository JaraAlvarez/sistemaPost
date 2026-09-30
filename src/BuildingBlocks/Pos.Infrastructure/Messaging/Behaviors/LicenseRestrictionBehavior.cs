using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.SharedKernel.Results;

namespace Pos.Infrastructure.Messaging.Behaviors;

/// <summary>
/// Con la licencia restringida rechaza con <c>LICENSE.RESTRICTED</c> todo comando que no esté marcado
/// <see cref="IAllowedWhenRestricted"/> (D12B-02). Las consultas pasan siempre (RN-LIC-04).
/// </summary>
internal sealed class LicenseRestrictionBehavior<TRequest, TResponse>(ILicenseGate gate) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    private static readonly bool Filtered = !typeof(IAllowedWhenRestricted).IsAssignableFrom(typeof(TRequest)) && typeof(TRequest).GetInterfaces()
        .Any(i => i == typeof(ICommand) || (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)));

    public Task<TResponse> Handle(TRequest request, RequestHandlerNext<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (Filtered && gate.Current is { Restricted: true } state)
        {
            var error = Error.BusinessRule(LicenseGateErrors.Restricted.Code, state.Reason is null
                ? LicenseGateErrors.Restricted.Message
                : $"{state.Reason} {LicenseGateErrors.Restricted.Message}");
            return Task.FromResult(ResultFailureFactory<TResponse>.Create(error));
        }

        return next();
    }
}
