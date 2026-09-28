using Microsoft.AspNetCore.Http;
using Pos.SharedKernel.Results;

namespace Pos.Api.Abstractions;

/// <summary>Traducción uniforme de <see cref="Result"/> a respuestas HTTP (RFC 9457 ProblemDetails).</summary>
public static class ResultHttpExtensions
{
    public const string ErrorCodeExtension = "code";

    public const string FieldErrorsExtension = "errors";

    public static IResult ToHttpResult(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    public static IResult ToHttpResult<TValue>(this Result<TValue> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var extensions = new Dictionary<string, object?> { [ErrorCodeExtension] = error.Code };
        if (error.FieldErrors.Count > 0)
        {
            extensions[FieldErrorsExtension] = error.FieldErrors;
        }

        return TypedResults.Problem(
            title: TitleFor(error.Type),
            detail: error.Message,
            statusCode: StatusCodeFor(error.Type),
            extensions: extensions);
    }

    public static int StatusCodeFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static string TitleFor(ErrorType type) => type switch
    {
        ErrorType.Validation => "Datos inválidos",
        ErrorType.Unauthorized => "No autenticado",
        ErrorType.Forbidden => "Acceso denegado",
        ErrorType.NotFound => "No encontrado",
        ErrorType.Conflict => "Conflicto",
        ErrorType.BusinessRule => "Regla de negocio incumplida",
        _ => "Error inesperado",
    };
}
