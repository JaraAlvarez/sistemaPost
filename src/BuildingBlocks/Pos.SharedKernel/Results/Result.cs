namespace Pos.SharedKernel.Results;

/// <summary>
/// Resultado de una operación de negocio. Los fallos esperados (reglas de negocio, validación, no encontrado)
/// se devuelven como <see cref="Result"/> fallido, no como excepciones.
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (isSuccess && error != Error.None)
        {
            throw new ArgumentException("Un resultado exitoso no puede tener error.", nameof(error));
        }

        if (!isSuccess && error == Error.None)
        {
            throw new ArgumentException("Un resultado fallido debe tener un error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    /// <summary>Error del fallo, o <see cref="Error.None"/> si fue exitoso.</summary>
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);

    public static implicit operator Result(Error error) => Failure(error);

    public static Result FromError(Error error) => Failure(error);
}

/// <summary>Resultado con valor.</summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error) => _value = value;

    /// <summary>Valor del resultado. Acceder a él en un resultado fallido es un error de programación.</summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"No se puede leer el valor de un resultado fallido ({Error.Code}).");

#pragma warning disable CA1000 // Las conversiones implícitas son la forma idiomática de construir resultados.
    public static implicit operator Result<TValue>(TValue value) => Success(value);

    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
#pragma warning restore CA1000
}
