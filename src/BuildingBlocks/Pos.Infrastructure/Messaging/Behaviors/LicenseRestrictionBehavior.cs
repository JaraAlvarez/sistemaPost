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

    private static readonly bool SessionOpening = typeof(IRestrictedSessionOpening).IsAssignableFrom(typeof(TRequest));

    public Task<TResponse> Handle(TRequest request, RequestHandlerNext<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        if ((Filtered || SalesWindow || SessionOpening) && gate.Current is { Restricted: true } state)
        {
            // Abrir jornada: solo con el estado alterado o el archivo ilegible y dentro de la ventana de 24 h (integración 1.2.0, ALTA 2).
            var blocked = Filtered
                || (SalesWindow && state.SalesWindowClosed)
                || (SessionOpening && (!state.AllowsOpeningSessions || state.SalesWindowClosed));
            if (blocked)
            {
                var basis = Filtered || (SessionOpening && !state.AllowsOpeningSessions) ? LicenseGateErrors.Restricted : LicenseGateErrors.SalesWindowClosed;
                var error = Error.BusinessRule(basis.Code, state.Reason is null ? basis.Message : $"{state.Reason} {basis.Message}");
                return Task.FromResult(ResultFailureFactory<TResponse>.Create(error));
            }
        }

        return next();
    }
}
