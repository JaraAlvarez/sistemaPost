using System.Net;
using System.Text.Json;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Billing.Domain;
using Pos.Modules.Reporting.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase11B.ElectronicBilling;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase11B;

/// <summary>
/// Reporte de conciliación de facturación electrónica del catálogo de la Fase 9 (§6 flujo 6): por día y sucursal, ventas contra
/// facturas aceptadas, pendientes, en contingencia, rechazadas y canceladas, con los valores y las diferencias; permisos y exportación.
/// </summary>
public class FiscalReconciliationReportTests
{
    private static decimal Total(ReportResultDto report, string key) =>
        report.Totals[key] is JsonElement { ValueKind: JsonValueKind.Number } value ? value.GetDecimal() : 0m;

    [Fact]
    public async Task El_reporte_concilia_ventas_contra_documentos_por_estado_con_totales_y_diferencias()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();

        // Modo OFF: todas las ventas con comprobante interno, nada por conciliar.
        var internalSale = await SellAsync(shop);
        var off = await GetAsync<ReportResultDto>(shop.Owner, "/api/v1/reports/FISCAL_RECONCILIATION");
        (Total(off, "sales"), Total(off, "internal_receipts"), Total(off, "unreconciled")).ShouldBe((1m, 1m, 0m));

        await EnableAsync(shop);

        // Aceptada.
        var accepted = await SellAsync(shop);
        await ProcessAsync(shop);

        // Rechazada por la "DIAN".
        factory.Fiscal.RejectedIdentifications.Add(FiscalParty.FinalConsumerIdentification);
        var rejected = await SellAsync(shop);
        await ProcessAsync(shop);
        factory.Fiscal.RejectedIdentifications.Clear();

        // En contingencia (sin Internet).
        factory.Fiscal.Offline = true;
        var contingency = await SellAsync(shop);
        await ProcessAsync(shop);
        factory.Fiscal.Offline = false;

        // Pendiente (aún no se envía) y una anulada antes de enviarla (factura cancelada).
        var pending = await SellAsync(shop);
        var voided = await SellAsync(shop);
        await shop.AuthorizedAsync<SaleReceiptDto>(HttpMethod.Post, $"/api/v1/sales/{voided.Sale.Id}/void", new { reason = "Error de digitación" });

        (await ForSourceAsync(shop, rejected.Sale.Id)).Status.ShouldBe("REJECTED");
        (await ForSourceAsync(shop, contingency.Sale.Id)).Status.ShouldBe("CONTINGENCY");
        (await ForSourceAsync(shop, pending.Sale.Id)).Status.ShouldBe("PENDING");
        (await ForSourceAsync(shop, voided.Sale.Id)).Status.ShouldBe("CANCELLED");

        var report = await GetAsync<ReportResultDto>(shop.Owner, "/api/v1/reports/FISCAL_RECONCILIATION");
        report.Group.ShouldBe("Impuestos");
        var row = report.Rows.ShouldHaveSingleItem();
        ((JsonElement)row["branch_name"]!).GetString().ShouldNotBeNullOrWhiteSpace();
        (Total(report, "sales"), Total(report, "internal_receipts"), Total(report, "accepted"), Total(report, "pending"), Total(report, "contingency"),
                Total(report, "rejected"), Total(report, "without_document"), Total(report, "voided"), Total(report, "cancelled"))
            .ShouldBe((5m, 1m, 1m, 1m, 1m, 1m, 0m, 1m, 1m));
        Total(report, "sales_total").ShouldBe(internalSale.Sale.Total + accepted.Sale.Total + rejected.Sale.Total + contingency.Sale.Total + pending.Sale.Total);
        Total(report, "accepted_total").ShouldBe(accepted.Sale.Total);
        Total(report, "unreconciled").ShouldBe(3m);
        Total(report, "unreconciled_total").ShouldBe(rejected.Sale.Total + contingency.Sale.Total + pending.Sale.Total);
        Total(report, "amount_difference").ShouldBe(0m);

        // Cuadra con la conciliación del módulo de facturación.
        var day = (await GetAsync<List<FiscalReconciliationDayDto>>(shop.Owner, "/api/v1/billing/reconciliation")).Single(d => d.SalesCompleted > 0);
        (day.Accepted, day.Pending, day.Contingency, day.Rejected, day.Cancelled, day.AcceptedTotal).ShouldBe(
            ((int)Total(report, "accepted"), (int)Total(report, "pending"), (int)Total(report, "contingency"), (int)Total(report, "rejected"),
                (int)Total(report, "cancelled"), Total(report, "accepted_total")));

        // Permisos del catálogo (la cajera no lo ve) y exportación.
        (await GetAsync<List<ReportInfoDto>>(shop.Cashier, "/api/v1/reports")).ShouldNotContain(r => r.Code == "FISCAL_RECONCILIATION");
        await RawAsync(shop.Cashier, HttpMethod.Get, "/api/v1/reports/FISCAL_RECONCILIATION").ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");
        var file = await RawAsync(shop.Owner, HttpMethod.Get, "/api/v1/reports/FISCAL_RECONCILIATION?format=csv");
        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await file.Content.ReadAsStringAsync(Ct)).ShouldContain("Facturas aceptadas");
    }
}
