using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Pos.Api.Abstractions;
using Pos.SharedKernel.Results;

namespace Pos.Server.IntegrationTests;

/// <summary>Contrato de traducción de errores de negocio a HTTP (lo consumirán la UI y las integraciones).</summary>
public class ResultHttpMappingTests
{
    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.BusinessRule, 422)]
    [InlineData(ErrorType.Unexpected, 500)]
    [InlineData(ErrorType.None, 500)]
    public void Cada_tipo_de_error_tiene_su_codigo_HTTP_y_titulo(ErrorType type, int expectedStatus)
    {
        ResultHttpExtensions.StatusCodeFor(type).ShouldBe(expectedStatus);
        ResultHttpExtensions.TitleFor(type).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Result_exitoso_sin_valor_es_204()
    {
        Result.Success().ToHttpResult().ShouldBeOfType<NoContent>();
    }

    [Fact]
    public void Result_exitoso_con_valor_es_200_con_el_valor()
    {
        var ok = Result.Success(42).ToHttpResult().ShouldBeOfType<Ok<int>>();
        ok.Value.ShouldBe(42);
    }

    [Fact]
    public void Result_fallido_es_ProblemDetails_con_codigo_y_errores_de_campo()
    {
        var error = Error.Validation("VALIDATION.FAILED", "Datos inválidos", [new FieldError("Name", "REQUIRED", "Obligatorio")]);

        var problem = Result.Failure(error).ToHttpResult().ShouldBeOfType<ProblemHttpResult>();

        problem.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        problem.ProblemDetails.Extensions[ResultHttpExtensions.ErrorCodeExtension].ShouldBe("VALIDATION.FAILED");
        problem.ProblemDetails.Extensions[ResultHttpExtensions.FieldErrorsExtension].ShouldBe(error.FieldErrors);
    }

    [Fact]
    public void Result_generico_fallido_es_ProblemDetails()
    {
        Result<int> result = Error.Conflict("SALES.ALREADY_COMPLETED", "La venta ya fue completada.");

        result.ToHttpResult().ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }
}
