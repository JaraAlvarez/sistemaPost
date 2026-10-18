using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Reporting.Application;
using Pos.Modules.Reporting.Contracts;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Reporting.UnitTests;

/// <summary>Motor de reportes con un ejecutor falso: columnas ocultas y calculadas, totales, cálculos y costos sin permiso.</summary>
public class ReportEngineTests
{
    private static readonly DateOnly Today = new(2026, 10, 17);

    private static (ReportEngine Engine, FakeRunner Runner) Engine(IEnumerable<IReadOnlyDictionary<string, object?>> rows, params string[] permissions)
    {
        var runner = new FakeRunner(rows);
        return (new ReportEngine(new FakeInstallation(), new FakePermissions(permissions), new DefaultSettings(), runner, new FixedClock()), runner);
    }

    private static Dictionary<string, string?> Args(params (string Key, string? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private static object? Cell(ExecutedReport report, int row, string key) =>
        report.Rows[row][report.Columns.ToList().FindIndex(c => c.Key == key)];

    private static object? Total(ExecutedReport report, string key) => report.Totals[report.Columns.ToList().FindIndex(c => c.Key == key)];

    [Fact]
    public async Task ABC_lee_la_base_oculta_calcula_las_columnas_y_no_publica_la_base()
    {
        var (engine, runner) = Engine(
            [
                Row(("sku", "A1"), ("product_name", "Arroz"), ("net_sales", 700m), ("quantity", 7m), ("abc_base", 1000m)),
                Row(("sku", "B1"), ("product_name", "Gaseosa"), ("net_sales", 200m), ("quantity", 2m), ("abc_base", 1000m)),
                Row(("sku", "C1"), ("product_name", "Sal"), ("net_sales", 100m), ("quantity", 1m), ("abc_base", 1000m)),
                Row(("sku", "D1"), ("product_name", "Devuelto"), ("net_sales", -50m), ("quantity", -1m), ("abc_base", 1000m)),
            ],
            ReportingPermissions.SalesAdvanced);

        var result = await engine.ExecuteAsync("ABC_BY_SALES", Args(), Ct);

        result.IsSuccess.ShouldBeTrue();
        var report = result.Value;
        runner.Last!.Columns.Select(c => c.Key).ShouldContain("abc_base");
        runner.Last.Columns.ShouldNotContain(c => c.Computed);
        report.Columns.Select(c => c.Key).ShouldNotContain("abc_base");
        Enumerable.Range(0, 4).Select(i => Cell(report, i, AbcCalculation.ClassKey)).ShouldBe(["A", "A", "B", "C"]);
        Enumerable.Range(0, 4).Select(i => Cell(report, i, AbcCalculation.ShareKey)).ShouldBe([70m, 20m, 10m, 0m]);
        Enumerable.Range(0, 4).Select(i => Cell(report, i, AbcCalculation.CumulativeKey)).ShouldBe([70m, 90m, 100m, 100m]);
        Total(report, "net_sales").ShouldBe(950m);
        Total(report, AbcCalculation.ShareKey).ShouldBe(100m);
        report.Parameters.ShouldContain(p => p.Key == "Corte A (% acumulado, número entero)" && p.Value == "80");
    }

    [Fact]
    public async Task ABC_con_cortes_invalidos_es_un_error_de_parametro()
    {
        var (engine, runner) = Engine([], ReportingPermissions.SalesAdvanced);
        var result = await engine.ExecuteAsync("ABC_BY_SALES", Args(("cutA", "90"), ("cutB", "20")), Ct);
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("REPORTING.INVALID_PARAMETER");
        result.Error.Message.ShouldStartWith("Parámetro 'cutB'");
        runner.Last.ShouldBeNull();

        var badA = await engine.ExecuteAsync("ABC_BY_SALES", Args(("cutA", "0")), Ct);
        badA.Error.Message.ShouldStartWith("Parámetro 'cutA'");
    }

    [Fact]
    public async Task El_rango_de_un_comparativo_se_topa_a_la_tercera_parte_del_maximo()
    {
        var (engine, runner) = Engine([ComparisonRow()], ReportingPermissions.SalesBasic, ReportingPermissions.SalesAdvanced);

        // reporting.max_range_days = 366 por defecto → 122 días.
        var ok = await engine.ExecuteAsync("COMPARE_BY_CASHIER", Args(("from", "2026-06-18"), ("to", "2026-10-17")), Ct);
        ok.IsSuccess.ShouldBeTrue();

        var tooLarge = await engine.ExecuteAsync("COMPARE_BY_DAY", Args(("from", "2026-06-17"), ("to", "2026-10-17")), Ct);
        tooLarge.Error.Code.ShouldBe("REPORTING.RANGE_TOO_LARGE");
        tooLarge.Error.Message.ShouldContain("122 días");
        tooLarge.Error.Message.ShouldContain("tres periodos");

        // Los demás reportes conservan el máximo completo.
        (await engine.ExecuteAsync("ABC_BY_SALES", Args(("from", "2025-10-17"), ("to", "2026-10-17")), Ct)).IsSuccess.ShouldBeTrue();
        ComparisonCalculation.MaxDays(366).ShouldBe(122);
        ComparisonCalculation.MaxDays(2).ShouldBe(1);
    }

    [Fact]
    public void El_catalogo_publica_la_fecha_de_negocio_de_hoy() =>
        ReportCatalog.Find("COMPARE_BY_DAY")!.ToInfo(includeCost: false, Today).Today.ShouldBe(Today);

    [Fact]
    public async Task ABC_por_utilidad_exige_el_permiso_de_utilidad()
    {
        var (engine, _) = Engine([], ReportingPermissions.SalesAdvanced, ReportingPermissions.SalesBasic);
        var result = await engine.ExecuteAsync("ABC_BY_PROFIT", Args(), Ct);
        result.Error.ShouldBe(ReportingErrors.PermissionDenied);
    }

    [Fact]
    public async Task El_comparativo_por_dia_manda_los_periodos_a_364_dias_y_calcula_las_variaciones()
    {
        var (engine, runner) = Engine([ComparisonRow()], ReportingPermissions.SalesBasic, ReportingPermissions.ProfitView);

        var result = await engine.ExecuteAsync("COMPARE_BY_DAY", Args(("from", "2026-10-12"), ("to", "2026-10-18")), Ct);

        result.IsSuccess.ShouldBeTrue();
        var arguments = runner.Last!.Arguments.ToDictionary(a => a.Name, a => a.Value);
        arguments["prev_from"].ShouldBe(new DateOnly(2026, 10, 5));
        arguments["prev_to"].ShouldBe(new DateOnly(2026, 10, 11));
        arguments["ly_from"].ShouldBe(new DateOnly(2025, 10, 13));
        arguments["ly_to"].ShouldBe(new DateOnly(2025, 10, 19));
        arguments["prev_shift"].ShouldBe(7);
        arguments["ly_shift"].ShouldBe(364);

        var report = result.Value;
        report.Parameters.ShouldContain(p => p.Key == "Periodo anterior" && p.Value == "2026-10-05 a 2026-10-11");
        report.Columns.ShouldNotContain(c => c.Hidden);
        report.Columns.Count.ShouldBe(22);
        Cell(report, 0, "net_vs_prev").ShouldBe(20m);
        Cell(report, 0, "net_vs_ly").ShouldBeNull();
        Cell(report, 0, "tickets_vs_prev").ShouldBe(-50m);
        Cell(report, 0, "cur_avg").ShouldBe(6000m);
        Cell(report, 0, "prev_avg").ShouldBe(2500m);
        Cell(report, 0, "avg_vs_prev").ShouldBe(140m);
        Cell(report, 0, "cur_margin").ShouldBe(25m);
        Cell(report, 0, "prev_margin").ShouldBe(20m);
        Cell(report, 0, "margin_vs_prev").ShouldBe(5m);
        Cell(report, 0, "margin_vs_ly").ShouldBeNull();

        // Totales: sumas de las cifras y variaciones recalculadas sobre los totales (no la suma de las variaciones).
        Total(report, "cur_net").ShouldBe(12_000m);
        Total(report, "net_vs_prev").ShouldBe(20m);
        Total(report, "cur_avg").ShouldBe(6000m);
        Total(report, "cur_margin").ShouldBe(25m);
    }

    [Fact]
    public async Task Sin_permiso_de_costos_el_comparativo_no_publica_el_margen()
    {
        var (engine, _) = Engine([ComparisonRow()], ReportingPermissions.SalesBasic);

        var report = (await engine.ExecuteAsync("COMPARE_BY_DAY", Args(), Ct)).Value;

        report.Columns.ShouldNotContain(c => c.IsCost);
        report.Columns.Select(c => c.Key).ShouldNotContain("cur_margin");
        report.Columns.Select(c => c.Key).ShouldNotContain("cur_cost");
        report.Columns.Count.ShouldBe(17);
        report.Rows[0].Length.ShouldBe(17);
        report.Totals.Length.ShouldBe(17);
        Cell(report, 0, "net_vs_prev").ShouldBe(20m);
    }

    [Fact]
    public async Task Con_el_permiso_de_costos_del_inventario_tambien_se_ve_el_margen()
    {
        var (engine, _) = Engine([ComparisonRow()], ReportingPermissions.SalesBasic, InventoryPermissions.CostView);
        var report = (await engine.ExecuteAsync("COMPARE_BY_DAY", Args(), Ct)).Value;
        report.Columns.Select(c => c.Key).ShouldContain("cur_margin");
    }

    [Fact]
    public async Task El_comparativo_por_categoria_usa_las_mismas_fechas_del_año_pasado_y_no_totaliza_tiquetes()
    {
        var (engine, runner) = Engine([ComparisonRow(), ComparisonRow()], ReportingPermissions.SalesAdvanced);

        var report = (await engine.ExecuteAsync("COMPARE_BY_CATEGORY", Args(("from", "2026-10-01"), ("to", "2026-10-31")), Ct)).Value;

        var arguments = runner.Last!.Arguments.ToDictionary(a => a.Name, a => a.Value);
        arguments["ly_from"].ShouldBe(new DateOnly(2025, 10, 1));
        arguments["ly_to"].ShouldBe(new DateOnly(2025, 10, 31));
        report.Parameters.ShouldContain(p => p.Key == "Año pasado (mismas fechas)" && p.Value == "2025-10-01 a 2025-10-31");
        Total(report, "cur_tickets").ShouldBeNull();
        Total(report, "cur_avg").ShouldBeNull();
        Total(report, "cur_net").ShouldBe(24_000m);
        Total(report, "net_vs_prev").ShouldBe(20m);
    }

    [Fact]
    public async Task Los_reportes_sin_calculo_no_cambian()
    {
        var (engine, runner) = Engine(
            [Row(("hour", 10L), ("tickets", 2L), ("total", 5000m), ("average_ticket", 2500m))],
            ReportingPermissions.SalesBasic);
        var report = (await engine.ExecuteAsync("SALES_BY_HOUR", Args(), Ct)).Value;
        runner.Last!.Columns.Count.ShouldBe(4);
        Total(report, "total").ShouldBe(5000m);
        Total(report, "average_ticket").ShouldBe(2500m);
    }

    private static Dictionary<string, object?> ComparisonRow() => Row(
        ("business_date", Today), ("day_name", "sábado"),
        ("cur_net", 12_000m), ("cur_tickets", 2L), ("cur_total", 12_000m), ("cur_base", 10_000m), ("cur_cost", 7_500m),
        ("prev_net", 10_000m), ("prev_tickets", 4L), ("prev_total", 10_000m), ("prev_base", 8_000m), ("prev_cost", 6_400m),
        ("ly_net", 0m), ("ly_tickets", 0L), ("ly_total", 0m), ("ly_base", 0m), ("ly_cost", 0m));

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeRunner(IEnumerable<IReadOnlyDictionary<string, object?>> rows) : IReportQueryRunner
    {
        public ReportQuery? Last { get; private set; }

        public Task<Result<IReadOnlyList<object?[]>>> RunAsync(ReportQuery query, CancellationToken cancellationToken)
        {
            Last = query;
            IReadOnlyList<object?[]> result = [.. rows.Select(r => query.Columns.Select(c => r.GetValueOrDefault(c.Key)).ToArray())];
            return Task.FromResult(Result.Success(result));
        }
    }

    private sealed class FakeInstallation : IInstallationContext
    {
        public Guid NodeId { get; } = Guid.NewGuid();

        public NodeRole NodeRole => NodeRole.AllInOne;

        public bool IsSetupCompleted => true;

        public Guid? CompanyId { get; } = Guid.NewGuid();

        public Guid? BranchId { get; } = Guid.NewGuid();

        public Guid? SystemUserId => null;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakePermissions(string[] granted) : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(string permissionCode, Guid? branchId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(granted.Contains(permissionCode));
    }

    private sealed class DefaultSettings : ISettingsReader
    {
        public Task<ResolvedSetting<T>> ResolveAsync<T>(SettingDefinition<T> definition, SettingContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResolvedSetting<T>(definition.DefaultValue, SettingSource.Default, definition.DefaultValue, SettingSource.Default));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 17, 17, 0, 0, TimeSpan.Zero);

        public TimeZoneInfo BusinessTimeZone => BusinessTimeZones.Colombia;
    }
}
