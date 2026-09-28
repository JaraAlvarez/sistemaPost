using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pos.Application.Abstractions.Messaging;
using Pos.SharedKernel.Results;

namespace Pos.Infrastructure.Messaging.Behaviors;

/// <summary>Registra cada caso de uso con su duración y, si falla, el código de error.</summary>
internal sealed partial class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerNext<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        var requestName = typeof(TRequest).Name;
        var start = Stopwatch.GetTimestamp();

        var response = await next();

        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (response.IsSuccess)
        {
            LogSucceeded(logger, requestName, elapsedMs);
        }
        else
        {
            LogFailed(logger, requestName, response.Error.Code, elapsedMs);
        }

        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Caso de uso {RequestName} completado en {ElapsedMs:0.0} ms")]
    private static partial void LogSucceeded(ILogger logger, string requestName, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Caso de uso {RequestName} falló con {ErrorCode} en {ElapsedMs:0.0} ms")]
    private static partial void LogFailed(ILogger logger, string requestName, string errorCode, double elapsedMs);
}
