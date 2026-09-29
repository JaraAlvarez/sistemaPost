using Pos.SharedKernel.Fiscal;

namespace Pos.SharedKernel.UnitTests;

/// <summary>DV del NIT con NITs públicos conocidos (algoritmo módulo 11 de la DIAN).</summary>
public class NitTests
{
    [Theory]
    [InlineData("800197268", 4)] // DIAN
    [InlineData("890903938", 8)] // Bancolombia
    [InlineData("890900608", 9)] // Almacenes Éxito
    [InlineData("900123456", 8)]
    public void Calcula_el_digito_de_verificacion_oficial(string nit, int expected)
    {
        Nit.ComputeCheckDigit(nit).ShouldBe(expected);
        Nit.IsValid(nit, expected.ToString(System.Globalization.CultureInfo.InvariantCulture)).ShouldBeTrue();
    }

    [Fact]
    public void Restos_0_y_1_producen_el_mismo_digito()
    {
        // Se buscan NIT cuyo resto sea 0 y 1 para cubrir la rama "DV = resto".
        var zero = Enumerable.Range(1, 200).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .First(n => Nit.ComputeCheckDigit(n) == 0);
        var one = Enumerable.Range(1, 200).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .First(n => Nit.ComputeCheckDigit(n) == 1);

        Nit.IsValid(zero, "0").ShouldBeTrue();
        Nit.IsValid(one, "1").ShouldBeTrue();
    }

    [Theory]
    [InlineData("800197268", "5")]
    [InlineData("800197268", "")]
    [InlineData("800197268", "44")]
    [InlineData("80019726A", "4")]
    [InlineData("", "0")]
    [InlineData("1234567890123456", "0")]
    public void Rechaza_digitos_o_numeros_invalidos(string nit, string checkDigit) => Nit.IsValid(nit, checkDigit).ShouldBeFalse();

    [Fact]
    public void Un_NIT_mal_formado_no_se_calcula() =>
        Should.Throw<ArgumentException>(() => Nit.ComputeCheckDigit("900.123.456"));
}
