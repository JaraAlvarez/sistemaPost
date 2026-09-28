using System.Reflection;
using Pos.SharedKernel.Results;

namespace Pos.Infrastructure.Messaging;

/// <summary>
/// Construye un <typeparamref name="TResponse"/> fallido (<see cref="Result"/> o <see cref="Result{TValue}"/>)
/// desde código genérico del pipeline. El delegado se crea una sola vez por tipo.
/// </summary>
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

        if (responseType.IsGenericType && responseType.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var failure = typeof(Result)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == nameof(Result.Failure) && m.IsGenericMethodDefinition)
                .MakeGenericMethod(responseType.GetGenericArguments()[0]);

            return failure.CreateDelegate<Func<Error, TResponse>>();
        }

        throw new InvalidOperationException($"Tipo de respuesta no soportado por el pipeline: {responseType.FullName}.");
    }
}
