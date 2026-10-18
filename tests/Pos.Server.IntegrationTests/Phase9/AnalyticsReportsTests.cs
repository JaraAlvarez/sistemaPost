using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Modules.Reporting.Contracts;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase9;

/// <summary>
/// Reportes de análisis (docs/fases/mejora-reportes-propuesta.md) con ventas reales: el ABC cuadra con "Ventas por producto", el
/// comparativo refleja ventas movidas al periodo anterior y al año pasado (364 días en el comparativo por día, mismas fechas en
/// categoría y cajero), el mapa de calor cuadra con "Ventas por hora", los costos se ocultan sin permiso y los archivos se generan.
/// Con devoluciones, ventas anuladas, líneas eliminadas y productos que solo tienen devolución, todo cuadra con SALES_DAILY,
/// SALES_BY_PRODUCT y PROFIT_BY_CASHIER.
/// </summary>
public class AnalyticsReportsTests
{
    private static decimal Total(ReportResultDto report, string key) =>
        report.Totals.TryGetValue(key, out var value) && value is JsonElement { ValueKind: JsonValueKind.Number } number ? number.GetDecimal() : 0m;

    private static JsonElement Cell(IReadOnlyDictionary<string, object?> row, string key) => (JsonElement)row[key]!;

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Task<ReportResultDto> RunAsync(HttpClient client, string code, DateOnly from, DateOnly to) =>
        GetAsync<ReportResultDto>(client, $"/api/v1/reports/{code}?from={Iso(from)}&to={Iso(to)}");

    [Fact]
    public async Task Los_reportes_de_analisis_cuadran_con_los_reportes_base_y_respetan_los_permisos()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        var today = Phase5.PurchasingScenario.Today;

        var current = await shop.StartAsync();
        await shop.AddAsync(current.Id, shop.Rice, 4);
        current = await shop.AddAsync(current.Id, shop.Soda, 3);
        await shop.CompleteAsync(current.Id, [Pay(shop.Cash, 50_000m)]);
        var sale364 = await SellAsync(shop, shop.Rice, 1);
        var yesterday = await SellAsync(shop, shop.Soda, 2);
        var sameDateLastYear = await SellAsync(shop, shop.Rice, 2);

        // ------------------------------------------------------------------ ABC con todo en el día: cuadra con "Ventas por producto".
        var abc = await RunAsync(shop.Owner, "ABC_BY_SALES", today, today);
        var byProduct = await RunAsync(shop.Owner, "SALES_BY_PRODUCT", today, today);
        abc.Group.ShouldBe("Análisis");
        Total(abc, "net_sales").ShouldBe(Total(byProduct, "net_sales"));
        abc.Rows.Count.ShouldBe(2);
        abc.Columns.Select(c => c.Key).ShouldNotContain("abc_base");
        abc.Columns.Single(c => c.Key == "abc_class").Style.ShouldBe(ReportColumnStyle.AbcClass);
        Cell(abc.Rows[0], "abc_class").GetString().ShouldBe("A");
        Cell(abc.Rows[0], "net_sales").GetDecimal().ShouldBeGreaterThanOrEqualTo(Cell(abc.Rows[1], "net_sales").GetDecimal());
        Cell(abc.Rows[1], "abc_cumulative").GetDecimal().ShouldBe(100m);
        Total(abc, "abc_share").ShouldBe(100m, 0.02m);

        var abcProfit = await RunAsync(shop.Owner, "ABC_BY_PROFIT", today, today);
        var profitByProduct = await RunAsync(shop.Owner, "PROFIT_BY_PRODUCT", today, today);
        Total(abcProfit, "profit").ShouldBe(Total(profitByProduct, "profit"));
        Cell(abcProfit.Rows[0], "abc_class").GetString().ShouldBe("A");

        // ------------------------------------------------------------------ Mapa de calor: cuadra con "Ventas por hora".
        var heatmap = await RunAsync(shop.Owner, "SALES_HEATMAP", today, today);
        var byHour = await RunAsync(shop.Owner, "SALES_BY_HOUR", today, today);
        Total(heatmap, "total").ShouldBe(Total(byHour, "total"));
        var weekday = new[] { "sun", "mon", "tue", "wed", "thu", "fri", "sat" }[(int)today.DayOfWeek];
        Total(heatmap, weekday).ShouldBe(Total(byHour, "total"));
        heatmap.Columns.Count(c => c.Style == ReportColumnStyle.Heat).ShouldBe(7);

        // ------------------------------------------------------------------ Ventas movidas al periodo anterior y al año pasado.
        var lastYear364 = today.AddDays(-364);
        var lastYearSameDate = today.AddYears(-1);
        await MoveAsync(shop, yesterday.Id, today.AddDays(-1));
        await MoveAsync(shop, sale364.Id, lastYear364);
        await MoveAsync(shop, sameDateLastYear.Id, lastYearSameDate);

        async Task<decimal> DailyNet(DateOnly date) => Total(await RunAsync(shop.Owner, "SALES_DAILY", date, date), "net_sales");
        var currentNet = await DailyNet(today);
        var previousNet = await DailyNet(today.AddDays(-1));
        var net364 = await DailyNet(lastYear364);
        var netSameDate = await DailyNet(lastYearSameDate);
        (currentNet, previousNet, net364, netSameDate).ShouldBe((current.Total, yesterday.Total, sale364.Total, sameDateLastYear.Total));

        // Por día: el año pasado es 364 días antes (mismo día de la semana).
        var byDay = await RunAsync(shop.Owner, "COMPARE_BY_DAY", today, today);
        var day = byDay.Rows.ShouldHaveSingleItem();
        Cell(day, "business_date").GetString().ShouldBe(Iso(today));
        (Cell(day, "cur_net").GetDecimal(), Cell(day, "prev_net").GetDecimal(), Cell(day, "ly_net").GetDecimal())
            .ShouldBe((currentNet, previousNet, net364));
        (Cell(day, "cur_tickets").GetInt64(), Cell(day, "prev_tickets").GetInt64(), Cell(day, "ly_tickets").GetInt64()).ShouldBe((1L, 1L, 1L));
        Cell(day, "net_vs_prev").GetDecimal().ShouldBe(Math.Round((currentNet - previousNet) / previousNet * 100m, 2, MidpointRounding.AwayFromZero));
        Cell(day, "net_vs_ly").GetDecimal().ShouldBe(Math.Round((currentNet - net364) / net364 * 100m, 2, MidpointRounding.AwayFromZero));
        Cell(day, "cur_avg").GetDecimal().ShouldBe(current.Total);
        byDay.Parameters.Keys.ShouldContain(k => k.StartsWith("Año pasado (364 días", StringComparison.Ordinal));
        var profitToday = await RunAsync(shop.Owner, "PROFIT_BY_DAY", today, today);
        Cell(day, "cur_margin").GetDecimal().ShouldBe(Total(profitToday, "margin"), 0.02m);
        Cell(day, "margin_vs_prev").ValueKind.ShouldBe(JsonValueKind.Number);

        // Una semana completa: cada día lleva su fila (también los días sin ventas).
        var week = await RunAsync(shop.Owner, "COMPARE_BY_DAY", today.AddDays(-6), today);
        week.Rows.Count.ShouldBe(7);
        Total(week, "cur_net").ShouldBe(currentNet + previousNet);

        // Por cajero y por categoría: el año pasado son las mismas fechas.
        var byCashier = await RunAsync(shop.Owner, "COMPARE_BY_CASHIER", today, today);
        (Total(byCashier, "cur_net"), Total(byCashier, "prev_net"), Total(byCashier, "ly_net")).ShouldBe((currentNet, previousNet, netSameDate));
        byCashier.Rows.ShouldHaveSingleItem();
        Total(byCashier, "cur_tickets").ShouldBe(1m);

        var byCategory = await RunAsync(shop.Owner, "COMPARE_BY_CATEGORY", today, today);
        async Task<decimal> CategoryNet(DateOnly date) => Total(await RunAsync(shop.Owner, "SALES_BY_CATEGORY", date, date), "net_sales");
        (Total(byCategory, "cur_net"), Total(byCategory, "prev_net"), Total(byCategory, "ly_net"))
            .ShouldBe((await CategoryNet(today), await CategoryNet(today.AddDays(-1)), await CategoryNet(lastYearSameDate)));
        byCategory.Totals.TryGetValue("cur_tickets", out var categoryTickets).ShouldBeTrue();
        (categoryTickets is null or JsonElement { ValueKind: JsonValueKind.Null }).ShouldBeTrue("los tiquetes por categoría no se totalizan");

        // ------------------------------------------------------------------ Permisos: sin utilidad no hay ABC por utilidad ni margen.
        var role = await shop.Owner.PostAsJsonAsync(
            "/api/v1/identity/roles",
            new { code = "ANALISTA", name = "Analista", permissions = new[] { ReportingPermissions.SalesBasic, ReportingPermissions.SalesAdvanced, ReportingPermissions.Export } },
            Json,
            Ct);
        role.StatusCode.ShouldBe(HttpStatusCode.Created, await role.Content.ReadAsStringAsync(Ct));
        await shop.Security.CreateUserAsync("analista", "ANALISTA");
        var analyst = await shop.Security.LocalClientAsync("analista");

        var visible = (await GetAsync<List<ReportInfoDto>>(analyst, "/api/v1/reports")).Where(r => r.Group == "Análisis").Select(r => r.Code).ToList();
        visible.ShouldBe(["ABC_BY_SALES", "COMPARE_BY_DAY", "COMPARE_BY_CATEGORY", "COMPARE_BY_CASHIER", "SALES_HEATMAP"], ignoreOrder: true);
        await RawAsync(analyst, HttpMethod.Get, "/api/v1/reports/ABC_BY_PROFIT").ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");
        var analystDay = await RunAsync(analyst, "COMPARE_BY_DAY", today, today);
        analystDay.Columns.Select(c => c.Key).ShouldNotContain(k => k.Contains("margin", StringComparison.Ordinal) || k.Contains("cost", StringComparison.Ordinal));
        analystDay.Rows[0].Keys.ShouldNotContain(k => k.Contains("margin", StringComparison.Ordinal) || k.EndsWith("_base", StringComparison.Ordinal));
        Total(analystDay, "cur_net").ShouldBe(currentNet);
        var analystCsv = await RawAsync(analyst, HttpMethod.Get, $"/api/v1/reports/COMPARE_BY_DAY?format=csv&from={Iso(today)}&to={Iso(today)}");
        analystCsv.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await analystCsv.Content.ReadAsStringAsync(Ct)).ShouldNotContain("Margen");

        // ------------------------------------------------------------------ Exportación: Excel y PDF (el comparativo ancho, con letra reducida).
        foreach (var code in new[] { "COMPARE_BY_DAY", "ABC_BY_SALES", "SALES_HEATMAP", "COMPARE_BY_CATEGORY" })
        {
            foreach (var (format, type) in new[] { ("xlsx", "application/vnd.openxmlformats"), ("pdf", "application/pdf") })
            {
                var file = await RawAsync(shop.Owner, HttpMethod.Get, $"/api/v1/reports/{code}?format={format}&from={Iso(today.AddDays(-6))}&to={Iso(today)}");
                file.StatusCode.ShouldBe(HttpStatusCode.OK, $"{code} {format}");
                file.Content.Headers.ContentType!.ToString().ShouldStartWith(type);
                (await file.Content.ReadAsByteArrayAsync(Ct)).Length.ShouldBeGreaterThan(500);
            }
        }

        await RawAsync(shop.Owner, HttpMethod.Get, "/api/v1/reports/ABC_BY_SALES?cutA=90&cutB=20")
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "REPORTING.INVALID_PARAMETER");
    }

    [Fact]
    public async Task Con_devoluciones_anuladas_y_lineas_eliminadas_el_ABC_y_los_comparativos_cuadran_con_los_reportes_base()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        var today = Phase5.PurchasingScenario.Today;
        var yesterday = today.AddDays(-1);

        // Venta con arroz y gaseosa; luego se devuelve (garantía) 1 arroz.
        var main = await shop.StartAsync();
        await shop.AddAsync(main.Id, shop.Rice, 3);
        main = await shop.AddAsync(main.Id, shop.Soda, 2);
        var mainSale = (await shop.CompleteAsync(main.Id, [Pay(shop.Cash, 50_000m)])).Sale;

        // Venta con una línea eliminada antes de cobrar: la línea no cuenta.
        var withVoidedLine = await shop.StartAsync();
        withVoidedLine = await shop.AddAsync(withVoidedLine.Id, shop.Rice, 5);
        withVoidedLine = await shop.AddAsync(withVoidedLine.Id, shop.Soda, 1);
        var riceLine = withVoidedLine.Lines.Single(l => l.ProductId == shop.Rice);
        withVoidedLine = await PostAsync<Pos.Modules.Sales.Contracts.SaleDto>(shop.Cashier, $"/api/v1/sales/{withVoidedLine.Id}/lines/{riceLine.Id}/void");
        await shop.CompleteAsync(withVoidedLine.Id, [Pay(shop.Cash, withVoidedLine.Total)]);

        // Venta anulada: no cuenta.
        var voided = await SellAsync(shop, shop.Rice, 7);
        await shop.AuthorizedAsync<Pos.Modules.Sales.Contracts.SaleReceiptDto>(HttpMethod.Post, $"/api/v1/sales/{voided.Id}/void", new { reason = "Error de digitación" });

        // Yogurt vendido "ayer" y devuelto hoy: hoy el yogurt solo tiene devolución.
        var yogurt = (await shop.CompleteAsync(
            (await shop.AddAsync((await shop.StartAsync()).Id, shop.Yogurt, 2)).Id, [Pay(shop.Cash, 50_000m)])).Sale;
        await MoveAsync(shop, yogurt.Id, yesterday);

        var owner = factory.CreateClient();
        await Phase3.SecurityScenario.PosLoginAsync(owner, "100", "4826");
        foreach (var (sale, productId) in new[] { (mainSale, shop.Rice), (yogurt, shop.Yogurt) })
        {
            var line = sale.Lines.Single(l => l.ProductId == productId);
            var refund = await RawAsync(owner, HttpMethod.Post, "/api/v1/exchanges/warranty-refund", new
            {
                originalSaleId = sale.Id, reason = "Producto defectuoso, garantía",
                lines = new object[] { new { saleLineId = line.Id, quantity = 1m, destination = "Discard" } },
            });
            refund.StatusCode.ShouldBe(HttpStatusCode.OK, await refund.Content.ReadAsStringAsync(Ct));
        }

        var daily = await RunAsync(shop.Owner, "SALES_DAILY", today, today);
        var byProduct = await RunAsync(shop.Owner, "SALES_BY_PRODUCT", today, today);
        Total(daily, "tickets").ShouldBe(2m);
        Total(daily, "voided_tickets").ShouldBe(1m);
        Total(daily, "refunds").ShouldBeGreaterThan(0m);
        Total(byProduct, "net_sales").ShouldBe(Total(daily, "net_sales"));

        // ABC: cuadra con "Ventas por producto"; el yogurt (solo devolución) queda con venta negativa, en C y sin participación.
        var abc = await RunAsync(shop.Owner, "ABC_BY_SALES", today, today);
        Total(abc, "net_sales").ShouldBe(Total(byProduct, "net_sales"));
        Total(abc, "quantity").ShouldBe(Total(byProduct, "quantity_sold") - Total(byProduct, "quantity_returned"));
        var yogurtRow = abc.Rows.Single(r => Cell(r, "net_sales").GetDecimal() < 0);
        Cell(yogurtRow, "quantity").GetDecimal().ShouldBe(-1m);
        (Cell(yogurtRow, "abc_class").GetString(), Cell(yogurtRow, "abc_share").GetDecimal()).ShouldBe(("C", 0m));
        foreach (var row in abc.Rows)
        {
            var sku = Cell(row, "sku").GetString();
            Cell(row, "net_sales").GetDecimal().ShouldBe(Cell(byProduct.Rows.Single(p => Cell(p, "sku").GetString() == sku), "net_sales").GetDecimal());
        }

        var abcProfit = await RunAsync(shop.Owner, "ABC_BY_PROFIT", today, today);
        var profitByProduct = await RunAsync(shop.Owner, "PROFIT_BY_PRODUCT", today, today);
        (Total(abcProfit, "net_sales"), Total(abcProfit, "cost"), Total(abcProfit, "profit"))
            .ShouldBe((Total(profitByProduct, "net_sales"), Total(profitByProduct, "cost"), Total(profitByProduct, "profit")));

        // Comparativo por día: hoy y ayer cuadran con el resumen diario; el margen, con la utilidad por día.
        var byDay = await RunAsync(shop.Owner, "COMPARE_BY_DAY", today, today);
        var day = byDay.Rows.ShouldHaveSingleItem();
        Cell(day, "cur_net").GetDecimal().ShouldBe(Total(daily, "net_sales"));
        Cell(day, "cur_tickets").GetInt64().ShouldBe(2L);
        Cell(day, "prev_net").GetDecimal().ShouldBe(Total(await RunAsync(shop.Owner, "SALES_DAILY", yesterday, yesterday), "net_sales"));
        Cell(day, "prev_net").GetDecimal().ShouldBe(yogurt.Total);
        Cell(day, "cur_margin").GetDecimal().ShouldBe(Total(await RunAsync(shop.Owner, "PROFIT_BY_DAY", today, today), "margin"), 0.02m);

        // Por cajero: la devolución resta al cajero de la venta original; cuadra con la utilidad por cajero.
        var byCashier = await RunAsync(shop.Owner, "COMPARE_BY_CASHIER", today, today);
        var profitByCashier = await RunAsync(shop.Owner, "PROFIT_BY_CASHIER", today, today);
        Total(byCashier, "cur_net").ShouldBe(Total(daily, "net_sales"));
        foreach (var row in byCashier.Rows.Where(r => Cell(r, "cur_tickets").GetInt64() > 0))
        {
            var name = Cell(row, "cashier_name").GetString();
            var profitRow = profitByCashier.Rows.Single(p => Cell(p, "cashier_name").GetString() == name);
            Cell(row, "cur_margin").GetDecimal().ShouldBe(Cell(profitRow, "margin").GetDecimal(), 0.02m);
        }

        Total(byCashier, "cur_margin").ShouldBe(Total(profitByCashier, "margin"), 0.02m);

        // Por categoría: cuadra con "Ventas por categoría".
        var byCategory = await RunAsync(shop.Owner, "COMPARE_BY_CATEGORY", today, today);
        Total(byCategory, "cur_net").ShouldBe(Total(await RunAsync(shop.Owner, "SALES_BY_CATEGORY", today, today), "net_sales"));
        Total(byCategory, "prev_net").ShouldBe(Total(await RunAsync(shop.Owner, "SALES_BY_CATEGORY", yesterday, yesterday), "net_sales"));

        // El rango del comparativo se topa a la tercera parte del máximo (122 días con 366).
        await RawAsync(shop.Owner, HttpMethod.Get, $"/api/v1/reports/COMPARE_BY_DAY?from={Iso(today.AddDays(-122))}&to={Iso(today)}")
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "REPORTING.RANGE_TOO_LARGE");
        (await RunAsync(shop.Owner, "COMPARE_BY_DAY", today.AddDays(-121), today)).Rows.Count.ShouldBe(122);

        // El catálogo publica la fecha de negocio de hoy (la pantalla resuelve today-N con ella).
        (await GetAsync<List<ReportInfoDto>>(shop.Owner, "/api/v1/reports")).ShouldAllBe(r => r.Today == today);
    }

    private static async Task<Pos.Modules.Sales.Contracts.SaleDto> SellAsync(SalesScenario shop, Guid product, decimal quantity)
    {
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, product, quantity);
        await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, sale.Total)]);
        return sale;
    }

    /// <summary>Lleva una venta (y por la vista, sus líneas) a otra fecha de negocio.</summary>
    private static Task MoveAsync(SalesScenario shop, Guid saleId, DateOnly date) =>
        shop.Catalog.ExecuteAsync($"UPDATE sales.sales SET business_date = '{Iso(date)}' WHERE id = '{saleId}'");
}
