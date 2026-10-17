using Pos.Modules.Inventory.Application;
using Pos.Modules.Inventory.Domain;

namespace Pos.Modules.Inventory.UnitTests;

/// <summary>Reabastecimiento: "Calcular mínimos sugeridos" con la venta diaria promedio y los días de entrega y de cobertura.</summary>
public class MinimumSuggestionTests
{
    [Fact]
    public void Minimo_por_dias_de_entrega_y_maximo_con_la_cobertura()
    {
        // 90 unidades en 30 días = 3 diarias; entrega en 4 días → mínimo 12; + 7 de cobertura → máximo 33.
        var (average, min, max) = MinimumSuggestion.Calculate(90m, 30, 4, 7, allowsDecimal: false);
        average.ShouldBe(3m);
        min.ShouldBe(12m);
        max.ShouldBe(33m);
    }

    [Fact]
    public void Redondea_hacia_arriba_en_unidades_o_a_tres_decimales_si_es_por_peso()
    {
        // 10 en 30 días = 0,333…/día × 2 días = 0,67 → 1 unidad; máximo × 9 días = 3.
        var units = MinimumSuggestion.Calculate(10m, 30, 2, 7, allowsDecimal: false);
        units.Min.ShouldBe(1m);
        units.Max.ShouldBe(3m);

        var weight = MinimumSuggestion.Calculate(10m, 30, 2, 7, allowsDecimal: true);
        weight.Min.ShouldBe(0.667m);
        weight.Max.ShouldBe(3m);
    }

    [Fact]
    public void Sin_venta_o_venta_negativa_da_cero_y_el_maximo_nunca_es_menor()
    {
        MinimumSuggestion.Calculate(0m, 30, 3, 7, false).ShouldBe((0m, 0m, 0m));
        MinimumSuggestion.Calculate(-5m, 30, 3, 7, false).ShouldBe((0m, 0m, 0m));
        var noCoverage = MinimumSuggestion.Calculate(30m, 30, 3, 0, false);
        noCoverage.Max.ShouldBe(noCoverage.Min);
        Should.Throw<ArgumentOutOfRangeException>(() => MinimumSuggestion.Calculate(1m, 0, 3, 7, false));
    }

    [Fact]
    public void La_plantilla_de_importacion_trae_las_columnas()
    {
        var template = StockPolicyImportColumns.Template();
        template.ShouldStartWith("sku;bodega;minimo;maximo;punto_pedido");
        template.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(3);
    }
}
