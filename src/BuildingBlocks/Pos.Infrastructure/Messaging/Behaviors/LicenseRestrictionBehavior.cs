using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.SharedKernel.Results;

namespace Pos.Infrastructure.Messaging.Behaviors;

/// <summary>
/// Con la licencia restringida rechaza con <c>LICENSE.RESTRICTED</c> todo comando que no esté marcado
/// <see cref="IAllowedWhenRestricted"/> (D12B-02). Los de venta (<see cref="IRestrictedSalesWindow"/>) solo pasan durante las primeras
/// 24 h de la restricción (H6). Las consultas pasan siempre (RN-LIC-04).
/// </summary>
internal sealed class LicenseRestrictionBehavior<TRequest, TResponse>(ILicenseGate gate) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    private static readonly bool Filtered = !typeof(IAllowedWhenRestricted).IsAssignableFrom(typeof(TRequest)) && typeof(TRequest).GetInterfaces()
        .Any(i => i == typeof(ICommand) || (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)));

    private static readonly bool SalesWindow = typeof(IRestrictedSalesWindow).IsAssignableFrom(typeof(TRequest));

    public Task<TResponse> Handle(TRequest request, RequestHandlerNext<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        if ((Filtered || SalesWindow) && gate.Current is { Restricted: true } state && (Filtered || state.SalesWindowClosed))
        {
            var basis = Filtered ? LicenseGateErrors.Restricted : LicenseGateErrors.SalesWindowClosed;
            var error = Error.BusinessRule(basis.Code, state.Reason is null ? basis.Message : $"{state.Reason} {basis.Message}");
            return Task.FromResult(ResultFailureFactory<TResponse>.Create(error));
        }

        return next();
    }
}
