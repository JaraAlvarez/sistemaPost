using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Abstractions.Messaging;
using Pos.SharedKernel.Results;

namespace Pos.Infrastructure.Messaging;

/// <summary>
/// Despachador propio (ADR-0006): resuelve el handler de la petición y lo envuelve con los
/// <see cref="IPipelineBehavior{TRequest,TResponse}"/> registrados. La reflexión se usa una sola vez
/// por tipo de petición; después se reutiliza el envoltorio en caché.
/// </summary>
internal sealed class Dispatcher(IServiceProvider serviceProvider) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, object> Wrappers = new();

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        where TResponse : Result
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = (RequestHandlerWrapper<TResponse>)Wrappers.GetOrAdd(
            request.GetType(),
            static requestType => Activator.CreateInstance(
                typeof(RequestHandlerWrapper<,>).MakeGenericType(requestType, typeof(TResponse)))!);

        return wrapper.Handle(request, serviceProvider, cancellationToken);
    }
}

internal abstract class RequestHandlerWrapper<TResponse>
    where TResponse : Result
{
    public abstract Task<TResponse> Handle(
        IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapper<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    public override Task<TResponse> Handle(
        IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var handler = serviceProvider.GetService<IRequestHandler<TRequest, TResponse>>()
            ?? throw new InvalidOperationException(
                $"No hay un handler registrado para la petición '{typeof(TRequest).FullName}'.");

        RequestHandlerNext<TResponse> pipeline = () => handler.Handle(typedRequest, cancellationToken);

        // El primer comportamiento registrado queda como el más externo.
        foreach (var behavior in serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse())
        {
            var next = pipeline;
            pipeline = () => behavior.Handle(typedRequest, next, cancellationToken);
        }

        return pipeline();
    }
}
