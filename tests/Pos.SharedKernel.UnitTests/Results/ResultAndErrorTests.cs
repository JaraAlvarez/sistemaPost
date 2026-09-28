using Pos.SharedKernel.Results;

namespace Pos.SharedKernel.UnitTests.Results;

public class ErrorTests
{
    [Theory]
    [InlineData("SALES.INSUFFICIENT_STOCK")]
    [InlineData("VALIDATION.FAILED")]
    [InlineData("BILLING.DIAN.REJECTED")]
    [InlineData("A1.B2")]
    public void Codigos_validos(string code)
    {
        Error.IsValidCode(code).ShouldBeTrue();
        Error.BusinessRule(code, "mensaje").Code.ShouldBe(code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SALES")]
    [InlineData("SALES.")]
    [InlineData("sales.insufficient_stock")]
    [InlineData("SALES.Insufficient")]
    [InlineData("1SALES.X")]
    public void Codigos_invalidos_lanzan_excepcion(string code)
    {
        Error.IsValidCode(code).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => Error.NotFound(code, "mensaje"));
    }

    [Fact]
    public void Mensaje_obligatorio()
    {
        Should.Throw<ArgumentException>(() => Error.Conflict("A.B", " "));
    }

    [Fact]
    public void Cada_fabrica_asigna_su_tipo()
    {
        Error.Validation("A.B", "m").Type.ShouldBe(ErrorType.Validation);
        Error.NotFound("A.B", "m").Type.ShouldBe(ErrorType.NotFound);
        Error.Conflict("A.B", "m").Type.ShouldBe(ErrorType.Conflict);
        Error.BusinessRule("A.B", "m").Type.ShouldBe(ErrorType.BusinessRule);
        Error.Unauthorized("A.B", "m").Type.ShouldBe(ErrorType.Unauthorized);
        Error.Forbidden("A.B", "m").Type.ShouldBe(ErrorType.Forbidden);
        Error.Unexpected("A.B", "m").Type.ShouldBe(ErrorType.Unexpected);
        Error.None.Type.ShouldBe(ErrorType.None);
    }

    [Fact]
    public void Errores_de_campo()
    {
        var error = Error.Validation("VALIDATION.FAILED", "Datos inválidos", [new FieldError("Name", "REQUIRED", "Obligatorio")]);

        error.FieldErrors.ShouldHaveSingleItem().Field.ShouldBe("Name");
        Error.NotFound("A.B", "m").FieldErrors.ShouldBeEmpty();
    }
}

public class ResultTests
{
    private static readonly Error SampleError = Error.BusinessRule("SALES.SAMPLE", "Error de ejemplo");

    [Fact]
    public void Success_y_Failure()
    {
        var ok = Result.Success();
        ok.IsSuccess.ShouldBeTrue();
        ok.IsFailure.ShouldBeFalse();
        ok.Error.ShouldBe(Error.None);

        var fail = Result.Failure(SampleError);
        fail.IsFailure.ShouldBeTrue();
        fail.Error.ShouldBe(SampleError);
        Result.FromError(SampleError).Error.ShouldBe(SampleError);
    }

    [Fact]
    public void Resultado_con_valor()
    {
        Result<int> ok = 42;
        ok.Value.ShouldBe(42);

        Result<int> fail = SampleError;
        fail.IsFailure.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => fail.Value).Message.ShouldContain("SALES.SAMPLE");
    }

    [Fact]
    public void Conversion_implicita_desde_error()
    {
        Result result = SampleError;
        result.Error.Code.ShouldBe("SALES.SAMPLE");
    }

    [Fact]
    public void Combinaciones_incoherentes_son_rechazadas()
    {
        Should.Throw<ArgumentException>(() => new InvalidResult(true, SampleError));
        Should.Throw<ArgumentException>(() => new InvalidResult(false, Error.None));
        Should.Throw<ArgumentNullException>(() => new InvalidResult(false, null!));
    }

    private sealed class InvalidResult(bool isSuccess, Error error) : Result(isSuccess, error);
}
