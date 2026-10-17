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

/// <summary>
/// Marca un comando que habla con un servicio externo lento (p. ej. una pasarela de pagos) y guarda en transacciones CORTAS propias con
/// <c>IUnitOfWork</c>: la canalización no lo envuelve en una transacción, para no dejar una conexión a la base de datos abierta mientras
/// el servicio responde ni perder, al revertir, el registro de algo que ya existe afuera.
/// </summary>
#pragma warning disable CA1040 // Interfaz marcadora intencional.
public interface ISelfTransactedCommand;
#pragma warning restore CA1040

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
