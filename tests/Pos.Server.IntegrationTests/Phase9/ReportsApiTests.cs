using System.Net;
using System.Text.Json;
using Pos.Modules.Reporting.Application;
using Pos.Modules.Reporting.Contracts;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase9;

/// <summary>
/// Verificación de coherencia de la Fase 9 (propuesta §10): todos los reportes del catálogo se ejecutan, las exportaciones y el tablero
/// responden, los permisos se aplican y se cumplen las igualdades de cuadre de la §0. Las pruebas funcionales de cada reporte las hace
/// el propietario con http/fase-09.http.
/// </summary>
public class ReportsApiTests
{
    private static decimal Total(ReportResultDto report, string key) =>
        report.Totals[key] is JsonElement { ValueKind: JsonValueKind.Number } value ? value.GetDecimal() : 0m;

    [Fact]
    public async Task Todos_los_reportes_se_ejecutan_y_cuadran_entre_si()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();

        var first = await shop.StartAsync();
        await shop.AddAsync(first.Id, shop.Rice, 2);
        first = await shop.AddAsync(first.Id, shop.Soda, 3);
        await shop.CompleteAsync(first.Id, [Pay(shop.Cash, 50_000m)]);
        var second = await shop.StartAsync();
        second = await shop.AddAsync(second.Id, shop.Rice, 1);
        await shop.CompleteAsync(second.Id, [Pay(shop.Debit, second.Total, "0001")]);

        // Catálogo: el propietario ve todos; la cajera ninguno y no puede ejecutarlos (403).
        (await GetAsync<List<ReportInfoDto>>(shop.Owner, "/api/v1/reports")).Count.ShouldBe(ReportCatalog.All.Count);
        (await GetAsync<List<ReportInfoDto>>(shop.Cashier, "/api/v1/reports")).ShouldBeEmpty();
        await RawAsync(shop.Cashier, HttpMethod.Get, "/api/v1/reports/SALES_DAILY").ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");

        // Cada reporte del catálogo se ejecuta sin error contra el escenario.
        var reports = new Dictionary<string, ReportResultDto>();
        foreach (var definition in ReportCatalog.All)
        {
            reports[definition.Code] = await GetAsync<ReportResultDto>(shop.Owner, $"/api/v1/reports/{definition.Code}");
        }

        // Igualdades de cuadre (§0).
        var daily = reports["SALES_DAILY"];
        Total(daily, "tickets").ShouldBe(2m);
        Total(daily, "total").ShouldBe(first.Total + second.Total);
        Total(reports["SALES_BY_PAYMENT"], "applied").ShouldBe(Total(daily, "total"));
        Total(reports["SALES_BY_PRODUCT"], "net_sales").ShouldBe(Total(daily, "net_sales"));
        Total(reports["SALES_BY_CASHIER"], "total").ShouldBe(Total(daily, "total"));
        Total(reports["TAXES_SALES"], "amount").ShouldBe(Total(daily, "taxes"));
        Total(reports["SALES_BOOK"], "total").ShouldBe(Total(daily, "total"));
        var profit = reports["PROFIT_BY_DAY"];
        Total(profit, "net_sales").ShouldBe(Total(daily, "subtotal"));
        Total(profit, "profit").ShouldBe(Total(profit, "net_sales") - Total(profit, "cost"));
        Total(reports["PROFIT_BY_PRODUCT"], "profit").ShouldBe(Total(profit, "profit"));
        var today = Phase5.PurchasingScenario.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var asOf = await GetAsync<ReportResultDto>(shop.Owner, $"/api/v1/reports/INVENTORY_VALUATION?asOf={today}");
        Total(asOf, "total_value").ShouldBe(Total(reports["INVENTORY_VALUATION"], "total_value"));

        // Exportaciones (auditadas) y validaciones.
        foreach (var (format, type) in new[] { ("csv", "text/csv"), ("xlsx", "application/vnd.openxmlformats"), ("pdf", "application/pdf") })
        {
            var file = await RawAsync(shop.Owner, HttpMethod.Get, $"/api/v1/reports/PROFIT_BY_PRODUCT?format={format}");
            file.StatusCode.ShouldBe(HttpStatusCode.OK);
            file.Content.Headers.ContentType!.ToString().ShouldStartWith(type);
            (await file.Content.ReadAsByteArrayAsync(Ct)).Length.ShouldBeGreaterThan(100);
        }

        await RawAsync(shop.Owner, HttpMethod.Get, "/api/v1/reports/NO_EXISTE").ShouldFailWithAsync(HttpStatusCode.NotFound, "REPORTING.NOT_FOUND");
        await RawAsync(shop.Owner, HttpMethod.Get, "/api/v1/reports/SALES_DAILY?from=2025-01-01&to=2026-12-31")
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "REPORTING.RANGE_TOO_LARGE");

        var dashboard = await GetAsync<DashboardDto>(shop.Owner, "/api/v1/reports/dashboard");
        dashboard.Today.Tickets.ShouldBe(2);
        dashboard.OpenCashSessions.ShouldBe(1);
        dashboard.TopProducts.Count.ShouldBe(2);
    }
}
