using Pos.SharedKernel.Measurement;

namespace Pos.SharedKernel.UnitTests.Measurement;

public class QuantityTests
{
    [Fact]
    public void Create_normaliza_la_unidad()
    {
        var result = Quantity.Create(0.742m, " kg ");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(0.742m);
        result.Value.UnitCode.ShouldBe("KG");
    }

    [Theory]
    [InlineData(-1, "UND", "QUANTITY.NEGATIVE")]
    [InlineData(1.23456, "KG", "QUANTITY.TOO_MANY_DECIMALS")]
    [InlineData(1, " ", "QUANTITY.UNIT_REQUIRED")]
    public void Create_rechaza_datos_invalidos_con_codigo_estable(decimal value, string unit, string expectedCode)
    {
        var result = Quantity.Create(value, unit);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(expectedCode);
    }

    [Fact]
    public void Of_lanza_excepcion_si_es_invalida()
    {
        Should.Throw<ArgumentException>(() => Quantity.Of(-1m, "UND"));
    }

    [Fact]
    public void Suma_y_resta_de_la_misma_unidad()
    {
        (Quantity.Of(1.5m, "KG") + Quantity.Of(0.25m, "KG")).Value.ShouldBe(1.75m);
        (Quantity.Of(10m, "UND") - Quantity.Of(4m, "UND")).Value.ShouldBe(6m);
    }

    [Fact]
    public void Resta_que_quedaria_negativa_lanza_excepcion()
    {
        Should.Throw<InvalidOperationException>(() => Quantity.Of(1m, "UND") - Quantity.Of(2m, "UND"));
    }

    [Fact]
    public void Operar_unidades_distintas_lanza_excepcion()
    {
        Should.Throw<InvalidOperationException>(() => Quantity.Of(1m, "KG") + Quantity.Of(1m, "UND"));
    }

    [Fact]
    public void ConvertTo_pasa_de_presentacion_a_unidad_base()
    {
        // 6 paquetes x6 = 36 unidades.
        var converted = Quantity.Of(6m, "PAQ6").ConvertTo("UND", 6m);

        converted.ShouldBe(Quantity.Of(36m, "UND"));
        Should.Throw<ArgumentOutOfRangeException>(() => Quantity.Of(1m, "UND").ConvertTo("UND", 0m));
    }

    [Fact]
    public void Zero_y_ToString()
    {
        Quantity.Zero("UND").IsZero.ShouldBeTrue();
        Quantity.Of(0.742m, "KG").ToString().ShouldBe("0.742 KG");
    }
}
