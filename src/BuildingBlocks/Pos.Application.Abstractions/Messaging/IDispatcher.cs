using Pos.SharedKernel.Results;

namespace Pos.Application.Abstractions.Messaging;

/// <summary>
/// Envía una petición a su único handler, pasando por los comportamientos del pipeline
/// (logging, validación y —desde la Fase 2— transacción y auditoría). Ver ADR-0006.
/// </summary>
public interface IDispatcher
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        where TResponse : Result;
}

public delegate Task<TResponse> RequestHandlerNext<TResponse>()
    where TResponse : Result;

/// <summary>
/// Comportamiento transversal que envuelve a los handlers. El orden de registro define el orden de ejecución
/// (el primero registrado es el más externo).
/// </summary>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    Task<TResponse> Handle(TRequest request, RequestHandlerNext<TResponse> next, CancellationToken cancellationToken);
}
