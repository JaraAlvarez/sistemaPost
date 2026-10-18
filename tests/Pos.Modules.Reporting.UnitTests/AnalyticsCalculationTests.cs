using Pos.Modules.Reporting.Application;
using Pos.Modules.Reporting.Contracts;

namespace Pos.Modules.Reporting.UnitTests;

/// <summary>Clasificación ABC (Pareto): cortes, bordes, ceros y negativos, base del SQL y cortes inválidos.</summary>
public class AbcClassifierTests
{
    private static AbcClass[] Classes(params decimal[] values) => [.. AbcClassifier.Classify(values, AbcCutoffs.Default).Select(i => i.Class)];

    [Fact]
    public void El_producto_que_cruza_el_80_por_ciento_queda_en_A()
    {
        // 70 + 20 + 6 + 4 = 100: el acumulado antes del segundo es 70 (< 80) → A; antes del tercero 90 (< 95) → B; antes del cuarto 96 → C.
        Classes(70m, 20m, 6m, 4m).ShouldBe([AbcClass.A, AbcClass.A, AbcClass.B, AbcClass.C]);
    }

    [Fact]
    public void Exactamente_en_el_corte_ya_no_es_A()
    {
        // Antes del segundo el acumulado es 80: no es menor que 80 → B.
        Classes(80m, 15m, 5m).ShouldBe([AbcClass.A, AbcClass.B, AbcClass.C]);
    }

    [Fact]
    public void Participacion_y_acumulado_suman_100()
    {
        var items = AbcClassifier.Classify([50m, 30m, 20m], AbcCutoffs.Default);
        items.Select(i => i.Share).ShouldBe([50m, 30m, 20m]);
        items.Select(i => i.Cumulative).ShouldBe([50m, 80m, 100m]);
    }

    [Fact]
    public void Un_solo_producto_es_A_con_el_100_por_ciento()
    {
        var item = AbcClassifier.Classify([1234.5m], AbcCutoffs.Default).ShouldHaveSingleItem();
        (item.Share, item.Cumulative, item.Class).ShouldBe((100m, 100m, AbcClass.A));
    }

    [Fact]
    public void Ceros_y_negativos_van_a_C_con_participacion_cero_y_no_cuentan_en_la_base()
    {
        var items = AbcClassifier.Classify([60m, 0m, -10m, 40m], AbcCutoffs.Default);
        items.Select(i => i.Class).ShouldBe([AbcClass.A, AbcClass.C, AbcClass.C, AbcClass.A]);
        items[0].Share.ShouldBe(60m);
        items[3].Share.ShouldBe(40m);
        items[1].Share.ShouldBe(0m);
        items[2].Share.ShouldBe(0m);
    }

    [Fact]
    public void Sin_valores_positivos_todo_es_C()
    {
        Classes(0m, -5m).ShouldBe([AbcClass.C, AbcClass.C]);
        AbcClassifier.Classify([], AbcCutoffs.Default).ShouldBeEmpty();
    }

    [Fact]
    public void Clasifica_de_mayor_a_menor_aunque_lleguen_desordenados()
    {
        Classes(4m, 70m, 6m, 20m).ShouldBe([AbcClass.C, AbcClass.A, AbcClass.B, AbcClass.A]);
    }

    [Fact]
    public void Con_la_base_del_SQL_el_reporte_cortado_sigue_correcto()
    {
        // Solo llegaron las 2 primeras filas de un total de 200: participación sobre 200, no sobre 150.
        var items = AbcClassifier.Classify([100m, 50m], AbcCutoffs.Default, baseTotal: 200m);
        items.Select(i => i.Share).ShouldBe([50m, 25m]);
        items.Select(i => i.Class).ShouldBe([AbcClass.A, AbcClass.A]);
    }

    [Fact]
    public void Cortes_configurables()
    {
        AbcClassifier.Classify([50m, 30m, 20m], new AbcCutoffs(50m, 30m)).Select(i => i.Class).ShouldBe([AbcClass.A, AbcClass.B, AbcClass.C]);
    }

    [Theory]
    [InlineData(80, 15, true)]
    [InlineData(1, 0, true)]
    [InlineData(99, 1, true)]
    [InlineData(0, 15, false)]
    [InlineData(100, 0, false)]
    [InlineData(80, 25, false)]
    [InlineData(80, -1, false)]
    public void Validacion_de_los_cortes(int a, int b, bool valid) =>
        (new AbcCutoffs(a, b).Validate() is null).ShouldBe(valid);
}

/// <summary>Periodos del comparativo: anterior, año pasado a 364 días y con las mismas fechas (29 de febrero, cruce de año).</summary>
public class ComparisonPeriodsTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void El_periodo_anterior_son_los_mismos_N_dias_justo_antes()
    {
        var periods = ComparisonPeriods.For(D(2026, 10, 1), D(2026, 10, 31), LastYearRule.SameDates);
        periods.Previous.ShouldBe(new PeriodRange(D(2026, 8, 31), D(2026, 9, 30)));
        periods.Previous.Days.ShouldBe(31);
        periods.PreviousShiftDays.ShouldBe(31);
    }

    [Fact]
    public void El_año_pasado_a_364_dias_cae_en_el_mismo_dia_de_la_semana()
    {
        var periods = ComparisonPeriods.For(D(2026, 10, 12), D(2026, 10, 18), LastYearRule.SameWeekday364);
        periods.LastYear.ShouldBe(new PeriodRange(D(2025, 10, 13), D(2025, 10, 19)));
        periods.LastYear.From.DayOfWeek.ShouldBe(periods.Current.From.DayOfWeek);
        periods.LastYear.To.DayOfWeek.ShouldBe(DayOfWeek.Sunday);
        periods.LastYearShiftDays.ShouldBe(364);
    }

    [Fact]
    public void El_año_pasado_con_las_mismas_fechas()
    {
        var periods = ComparisonPeriods.For(D(2026, 10, 1), D(2026, 10, 31), LastYearRule.SameDates);
        periods.LastYear.ShouldBe(new PeriodRange(D(2025, 10, 1), D(2025, 10, 31)));
        periods.LastYearShiftDays.ShouldBe(365);
    }

    [Fact]
    public void El_29_de_febrero_pasa_al_28_con_las_mismas_fechas()
    {
        var periods = ComparisonPeriods.For(D(2028, 2, 29), D(2028, 2, 29), LastYearRule.SameDates);
        periods.LastYear.ShouldBe(new PeriodRange(D(2027, 2, 28), D(2027, 2, 28)));
        periods.Previous.ShouldBe(new PeriodRange(D(2028, 2, 28), D(2028, 2, 28)));
    }

    [Fact]
    public void El_29_de_febrero_a_364_dias_es_el_mismo_dia_de_la_semana_del_año_anterior()
    {
        var periods = ComparisonPeriods.For(D(2028, 2, 29), D(2028, 2, 29), LastYearRule.SameWeekday364);
        periods.LastYear.From.ShouldBe(D(2027, 3, 2));
        periods.LastYear.From.DayOfWeek.ShouldBe(DayOfWeek.Tuesday);
    }

    [Fact]
    public void Cruce_de_año()
    {
        var periods = ComparisonPeriods.For(D(2026, 1, 1), D(2026, 1, 7), LastYearRule.SameDates);
        periods.Previous.ShouldBe(new PeriodRange(D(2025, 12, 25), D(2025, 12, 31)));
        periods.LastYear.ShouldBe(new PeriodRange(D(2025, 1, 1), D(2025, 1, 7)));

        var weekly = ComparisonPeriods.For(D(2026, 1, 1), D(2026, 1, 7), LastYearRule.SameWeekday364);
        weekly.LastYear.ShouldBe(new PeriodRange(D(2025, 1, 2), D(2025, 1, 8)));
    }

    [Fact]
    public void Un_solo_dia_se_compara_con_el_dia_anterior()
    {
        var periods = ComparisonPeriods.For(D(2026, 10, 17), D(2026, 10, 17), LastYearRule.SameWeekday364);
        periods.Previous.ShouldBe(new PeriodRange(D(2026, 10, 16), D(2026, 10, 16)));
        periods.LastYear.ShouldBe(new PeriodRange(D(2025, 10, 18), D(2025, 10, 18)));
    }

    [Fact]
    public void Rango_invertido_falla() =>
        Should.Throw<ArgumentException>(() => ComparisonPeriods.For(D(2026, 10, 2), D(2026, 10, 1), LastYearRule.SameDates));
}

/// <summary>Variaciones %, puntos porcentuales, ticket promedio y margen.</summary>
public class PeriodComparisonTests
{
    [Theory]
    [InlineData(120, 100, 20)]
    [InlineData(80, 100, -20)]
    [InlineData(100, 100, 0)]
    [InlineData(50, -100, 150)]
    [InlineData(1, 3, -66.67)]
    public void Variacion_porcentual(double current, double previous, double expected) =>
        PeriodComparison.Variation((decimal)current, (decimal)previous).ShouldBe((decimal)expected);

    [Fact]
    public void Con_valor_anterior_en_cero_o_vacio_la_variacion_queda_vacia()
    {
        PeriodComparison.Variation(100m, 0m).ShouldBeNull();
        PeriodComparison.Variation(100m, null).ShouldBeNull();
        PeriodComparison.Variation(null, 100m).ShouldBeNull();
    }

    [Fact]
    public void El_margen_varia_en_puntos_porcentuales()
    {
        PeriodComparison.PointChange(32.5m, 30m).ShouldBe(2.5m);
        PeriodComparison.PointChange(28m, 30m).ShouldBe(-2m);
        PeriodComparison.PointChange(28m, null).ShouldBeNull();
    }

    [Fact]
    public void Ticket_promedio_y_margen()
    {
        PeriodComparison.Average(10_000m, 3m).ShouldBe(3333.33m);
        PeriodComparison.Average(10_000m, 0m).ShouldBeNull();
        PeriodComparison.Margin(1000m, 750m).ShouldBe(25m);
        PeriodComparison.Margin(0m, 0m).ShouldBeNull();
        PeriodComparison.Margin(1000m, null).ShouldBe(100m);
    }
}

/// <summary>Intensidad de las celdas del mapa de calor.</summary>
public class HeatmapScaleTests
{
    [Theory]
    [InlineData(0, 1000, 0)]
    [InlineData(-5, 1000, 0)]
    [InlineData(1, 1000, 1)]
    [InlineData(200, 1000, 1)]
    [InlineData(201, 1000, 2)]
    [InlineData(500, 1000, 3)]
    [InlineData(999, 1000, 5)]
    [InlineData(1000, 1000, 5)]
    [InlineData(1500, 1000, 5)]
    [InlineData(100, 0, 0)]
    public void Nivel_proporcional_a_la_mayor_celda(int value, int max, int level) =>
        HeatmapScale.Level(value, max).ShouldBe(level);
}
