using Pos.SharedKernel.Measurement;

namespace Pos.SharedKernel.UnitTests.Measurement;

public class WeightUnitsTests
{
    [Theory]
    [InlineData("KG", 0.700, 0.700)]
    [InlineData("kg", 1.235, 1.235)]
    [InlineData("G", 0.700, 700)]
    [InlineData("G", 0.0005, 1)]
    [InlineData("LB500", 0.700, 1.400)]
    [InlineData("LB500", 0.333, 0.666)]
    [InlineData("LB", 0.700, 1.543)]
    public void Convierte_el_peso_de_la_bascula_a_la_unidad_del_producto(string unit, decimal kg, decimal expected) =>
        WeightUnits.QuantityFromKg(kg, unit).ShouldBe(expected);

    [Theory]
    [InlineData("UND")]
    [InlineData("L")]
    [InlineData(null)]
    public void Una_unidad_que_no_es_de_peso_no_se_convierte(string? unit)
    {
        WeightUnits.QuantityFromKg(1m, unit).ShouldBeNull();
        WeightUnits.IsWeight(unit).ShouldBeFalse();
    }

    [Fact]
    public void Libra_colombiana_de_500_gramos_y_etiquetas()
    {
        WeightUnits.KilogramsPerUnit("LB500").ShouldBe(0.5m);
        WeightUnits.ToKilograms(1.4m, "LB500").ShouldBe(0.700m);
        WeightUnits.ToKilograms(700m, "G").ShouldBe(0.700m);
        WeightUnits.Label("LB500").ShouldBe("lb");
        WeightUnits.Label("KG").ShouldBe("kg");
    }
}
