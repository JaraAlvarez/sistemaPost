using Pos.Modules.Reporting.Application;
using Pos.Modules.Reporting.Contracts;

namespace Pos.Modules.Reporting.UnitTests;

/// <summary>Invariantes del catálogo: códigos y columnas únicos, columnas calculadas con su cálculo y los reportes de análisis.</summary>
public class ReportCatalogTests
{
    public static TheoryData<string> Codes => [.. ReportCatalog.All.Select(r => r.Code)];

    [Fact]
    public void Los_codigos_son_unicos() =>
        ReportCatalog.All.Select(r => r.Code).ShouldBeUnique();

    [Theory]
    [MemberData(nameof(Codes))]
    public void Cada_reporte_es_coherente(string code)
    {
        var report = ReportCatalog.Find(code).ShouldNotBeNull();
        report.Columns.Select(c => c.Key).ShouldBeUnique();
        // Las etiquetas son los encabezados del Excel: no se pueden repetir entre las columnas publicadas.
        report.VisibleColumns(includeCost: true).Select(c => c.Label).ShouldBeUnique();
        report.Sql.TrimEnd().ShouldNotEndWith(";");
        report.Sql.ShouldNotContain("reporting.\"");
        if (report.Columns.Any(c => c.Computed))
        {
            report.Calculation.ShouldNotBeNull($"{code} tiene columnas calculadas sin cálculo.");
        }

        foreach (var ratio in report.Columns.Select(c => c.Ratio).OfType<RatioTotal>())
        {
            report.Columns.Select(c => c.Key).ShouldContain(ratio.Numerator);
            report.Columns.Select(c => c.Key).ShouldContain(ratio.Denominator);
        }

        report.Columns.Where(c => c.Hidden).ShouldAllBe(c => !c.Computed);
        report.VisibleColumns(includeCost: false).ShouldAllBe(c => !c.IsCost && !c.Hidden);
    }

    [Fact]
    public void Grupo_Analisis_con_sus_permisos()
    {
        var analysis = ReportCatalog.All.Where(r => r.Group == ReportCatalog.Analysis).ToDictionary(r => r.Code, r => r.Permission);
        analysis.ShouldBe(new Dictionary<string, string>
        {
            ["ABC_BY_SALES"] = ReportingPermissions.SalesAdvanced,
            ["ABC_BY_PROFIT"] = ReportingPermissions.ProfitView,
            ["COMPARE_BY_DAY"] = ReportingPermissions.SalesBasic,
            ["COMPARE_BY_CATEGORY"] = ReportingPermissions.SalesAdvanced,
            ["COMPARE_BY_CASHIER"] = ReportingPermissions.SalesBasic,
            ["SALES_HEATMAP"] = ReportingPermissions.SalesBasic,
        });
    }

    [Theory]
    [InlineData("COMPARE_BY_DAY", LastYearRule.SameWeekday364)]
    [InlineData("COMPARE_BY_CATEGORY", LastYearRule.SameDates)]
    [InlineData("COMPARE_BY_CASHIER", LastYearRule.SameDates)]
    public void Regla_del_año_pasado_de_cada_comparativo(string code, LastYearRule rule)
    {
        var calculation = ReportCatalog.Find(code)!.Calculation.ShouldBeOfType<ComparisonCalculation>();
        calculation.Rule.ShouldBe(rule);
    }

    [Theory]
    [InlineData("COMPARE_BY_DAY")]
    [InlineData("COMPARE_BY_CATEGORY")]
    [InlineData("COMPARE_BY_CASHIER")]
    public void El_margen_y_sus_bases_son_columnas_de_costo(string code)
    {
        var columns = ReportCatalog.Find(code)!.Columns;
        columns.Where(c => c.Key.Contains("margin", StringComparison.Ordinal) || c.Key.EndsWith("_cost", StringComparison.Ordinal) || c.Key.EndsWith("_base", StringComparison.Ordinal))
            .ShouldAllBe(c => c.IsCost);
        columns.Count(c => c.Style == ReportColumnStyle.Variation).ShouldBe(6);
        columns.Where(c => c.Style == ReportColumnStyle.PointChange).Select(c => c.Key).ShouldBe(["margin_vs_prev", "margin_vs_ly"]);
    }

    [Fact]
    public void ABC_por_utilidad_oculta_todo_lo_que_depende_del_costo()
    {
        var report = ReportCatalog.Find("ABC_BY_PROFIT")!;
        report.VisibleColumns(includeCost: false).Select(c => c.Key).ShouldBe(["sku", "product_name", "category_name", "net_sales"]);
    }

    [Fact]
    public void El_mapa_de_calor_tiene_los_siete_dias_de_lunes_a_domingo()
    {
        var report = ReportCatalog.Find("SALES_HEATMAP")!;
        var heat = report.Columns.Where(c => c.Style == ReportColumnStyle.Heat).ToList();
        heat.Select(c => c.Label).ShouldBe(["Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado", "Domingo"]);
        heat.ShouldAllBe(c => c.Type == ReportColumnType.Money && c.Total);
        // isodow: 1 = lunes … 7 = domingo.
        report.Sql.ShouldContain("h.dow = 1), 0) AS mon");
        report.Sql.ShouldContain("h.dow = 7), 0) AS sun");
        report.Sql.ShouldContain("completed_local");
        report.Sql.ShouldContain("isodow FROM s.business_date");
    }
}
