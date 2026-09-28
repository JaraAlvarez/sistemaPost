using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Pos.Api.Abstractions;
using Pos.SharedKernel.Domain;

namespace Pos.Server.Host.ErrorHandling;

/// <summary>
/// Convierte cualquier excepción no controlada en un <see cref="ProblemDetails"/> con código estable.
/// Fuera de desarrollo nunca expone detalles internos: el usuario recibe el código de correlación para soporte.
/// </summary>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public const string UnexpectedErrorCode = "SYSTEM.UNEXPECTED";

    public const string InvalidRequestCode = "REQUEST.INVALID";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case DomainException domainException:
                LogDomainViolation(logger, domainException.Error.Code, domainException);
                problem = Create(
                    ResultHttpExtensions.StatusCodeFor(domainException.Error.Type),
                    ResultHttpExtensions.TitleFor(domainException.Error.Type),
                    domainException.Error.Message,
                    domainException.Error.Code);
                break;

            case BadHttpRequestException badRequest:
                LogBadRequest(logger, badRequest.Message);
                problem = Create(
                    badRequest.StatusCode,
                    "Solicitud inválida",
                    environment.IsDevelopment() ? badRequest.Message : "La solicitud no es válida.",
                    InvalidRequestCode);
                break;

            default:
                LogUnexpected(logger, exception);
                problem = Create(
                    StatusCodes.Status500InternalServerError,
                    "Error inesperado",
                    environment.IsDevelopment()
                        ? exception.Message
                        : "Ocurrió un error inesperado. Informe a soporte el código de correlación.",
                    UnexpectedErrorCode);
                break;
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static ProblemDetails Create(int status, string title, string detail, string code)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions[ResultHttpExtensions.ErrorCodeExtension] = code;
        return problem;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invariante de dominio violada: {ErrorCode}")]
    private static partial void LogDomainViolation(ILogger logger, string errorCode, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Solicitud HTTP inválida: {Reason}")]
    private static partial void LogBadRequest(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error no controlado")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);
}
