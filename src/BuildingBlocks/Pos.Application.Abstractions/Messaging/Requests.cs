using Pos.SharedKernel.Results;

namespace Pos.Application.Abstractions.Messaging;

/// <summary>Petición procesada por el <see cref="IDispatcher"/>. Todas las respuestas son <see cref="Result"/>.</summary>
#pragma warning disable CA1040 // Interfaz marcadora intencional: tipa la respuesta de la petición.
public interface IRequest<TResponse>
    where TResponse : Result;
#pragma warning restore CA1040

/// <summary>Caso de uso que modifica estado y no devuelve datos.</summary>
public interface ICommand : IRequest<Result>;

/// <summary>Caso de uso que modifica estado y devuelve un dato (p. ej. el Id creado).</summary>
public interface ICommand<TValue> : IRequest<Result<TValue>>;

/// <summary>Consulta sin efectos secundarios.</summary>
public interface IQuery<TValue> : IRequest<Result<TValue>>;

public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand> : IRequestHandler<TCommand, Result>
    where TCommand : ICommand;

public interface ICommandHandler<in TCommand, TValue> : IRequestHandler<TCommand, Result<TValue>>
    where TCommand : ICommand<TValue>;

public interface IQueryHandler<in TQuery, TValue> : IRequestHandler<TQuery, Result<TValue>>
    where TQuery : IQuery<TValue>;
