using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase7;

/// <summary>Criterios de aceptación de la Fase 7 (propuesta §14): venta, cobro, existencias, descuentos, anulación y cierre.</summary>
public class SalesApiTests
{
    [Fact]
    public async Task Venta_completa_con_bascula_descuento_autorizado_pagos_combinados_redondeo_e_idempotencia()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);

        // Sin jornada abierta no se vende (RN-SAL-01); desde el backoffice tampoco (se vende desde una caja).
        await shop.Cashier.PostAsJsonAsync("/api/v1/sales", new { }, Json, Ct).ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.NO_OPEN_CASH_SESSION");
        await shop.Owner.PostAsJsonAsync("/api/v1/sales", new { }, Json, Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "SALES.TERMINAL_REQUIRED");
        var session = await shop.OpenSessionAsync();

        var sale = await shop.StartAsync();
        sale.Status.ShouldBe("OPEN");
        sale.CashSessionId.ShouldBe(session.Id);
        sale.Customer.Name.ShouldBe("Consumidor final");
        await shop.Cashier.PostAsJsonAsync("/api/v1/sales", new { }, Json, Ct).ShouldFailWithAsync(HttpStatusCode.Conflict, "SALES.OPEN_SALE_EXISTS");

        // Escaneo por SKU dos veces (suma en la misma línea), código de barras y etiqueta de báscula por peso.
        await shop.ScanAsync(sale.Id, "ARROZ-500");
        sale = await shop.ScanAsync(sale.Id, "ARROZ-500");
        sale.Lines.Count.ShouldBe(1);
        sale.Lines[0].Quantity.ShouldBe(2m);
        sale = await shop.ScanAsync(sale.Id, "7702004003508", quantity: 2);
        sale = await shop.ScanAsync(sale.Id, Phase4.CatalogScenario.Ean13("200012301250"));
        var tomato = sale.Lines.Single(l => l.ProductId == shop.Tomato);
        tomato.Source.ShouldBe("SCALE_WEIGHT");
        tomato.Quantity.ShouldBe(1.25m);
        tomato.Total.ShouldBe(6_225m);
        tomato.TaxTotal.ShouldBe(0m);
        sale.Total.ShouldBe(6_000m + 5_000m + 6_225m);
        (await GetAsync<SaleDto>(shop.Cashier, "/api/v1/sales/current")).Id.ShouldBe(sale.Id);

        // Precio con impuestos incluidos: base + IVA = lo exhibido.
        var rice = sale.Lines.Single(l => l.ProductId == shop.Rice);
        (rice.TaxBase + rice.TaxTotal).ShouldBe(rice.Total);

        // La cajera no descuenta sin autorización (Fase 7, pregunta 4): el supervisor autoriza con su código y PIN.
        var discountUrl = $"/api/v1/sales/{sale.Id}/discounts";
        var discount = new { lineId = rice.Id, percent = 10m, reason = "Empaque averiado" };
        sale = await shop.AuthorizedAsync<SaleDto>(HttpMethod.Post, discountUrl, discount);
        sale.Lines.Single(l => l.Id == rice.Id).LineDiscount.ShouldBe(600m);
        sale.Total.ShouldBe(16_625m);

        // Una autorización es de un solo uso.
        var reused = await RawAsync(shop.Cashier, HttpMethod.Post, discountUrl, discount);
        reused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Un medio sin cambio no excede el saldo; el efectivo cubre el resto redondeado a $50 y da el cambio.
        await shop.Cashier.PostAsJsonAsync($"/api/v1/sales/{sale.Id}/complete", new { payments = new[] { Pay(shop.Transfer, 20_000m, "TRF-9") } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.NON_CASH_OVERPAYMENT");
        await shop.Cashier.PostAsJsonAsync($"/api/v1/sales/{sale.Id}/complete", new { payments = new[] { Pay(shop.Transfer, 10_000m) } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "SALES.REFERENCE_REQUIRED");
        var receipt = await shop.CompleteAsync(sale.Id, [Pay(shop.Transfer, 10_000m, "TRF-9"), Pay(shop.Cash, 10_000m)], key: "caja1-venta1");
        var done = receipt.Sale;
        done.Status.ShouldBe("COMPLETED");
        done.Number.ShouldNotBeNull();
        done.RoundingAdjustment.ShouldBe(25m);
        done.Total.ShouldBe(16_650m);
        done.ChangeTotal.ShouldBe(3_350m);
        done.Payments.Single(p => p.MethodCode == "EFECTIVO").Applied.ShouldBe(6_650m);
        receipt.OpenDrawer.ShouldBeTrue();
        receipt.DocumentType.ShouldBe("INTERNAL_RECEIPT");
        receipt.DocumentStatus.ShouldBe("NOT_REQUIRED");
        receipt.TicketText.ShouldContain("TIQUETE DE VENTA");
        receipt.TicketText.ShouldContain("No es factura");
        receipt.TicketText.Split('\n').ShouldAllBe(line => line.Length <= 42);

        // Idempotencia: repetir con la misma clave devuelve la misma venta; con otra clave, conflicto.
        (await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 20_000m)], key: "caja1-venta1")).Sale.Number.ShouldBe(done.Number);
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{sale.Id}/complete", new { payments = new[] { Pay(shop.Cash, 20_000m) } })
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "SALES.ALREADY_COMPLETED");

        // Kardex y costo de la línea, caja por medio y comprobante interno.
        (await shop.StockAsync(shop.Rice)).ShouldBe(48m);
        (await shop.StockAsync(shop.Tomato)).ShouldBe(18.75m);
        var stored = await GetAsync<SaleDto>(shop.Supervisor, $"/api/v1/sales/{sale.Id}");
        stored.Lines.ShouldAllBe(l => l.Total > 0m);
        (await shop.Catalog.ScalarAsync<decimal>($"SELECT unit_cost FROM sales.sale_lines WHERE sale_id = '{sale.Id}' AND product_id = '{shop.Rice}'")).ShouldBe(1_000m);
        var x = await GetAsync<CashReportDto>(shop.Supervisor, $"/api/v1/cash/sessions/{session.Id}/report-x");
        x.Totals.Single(t => t.Code == "EFECTIVO").Expected.ShouldBe(100_000m + 6_650m);
        x.Totals.Single(t => t.Code == "TRANSFERENCIA").Expected.ShouldBe(10_000m);
        var documents = await GetAsync<List<FiscalDocumentDto>>(shop.Owner, "/api/v1/billing/documents");
        documents.Single().SourceNumber.ShouldBe(done.Number);

        // Reimpresión: marcada COPIA.
        (await PostAsync<SaleReceiptDto>(shop.Cashier, $"/api/v1/sales/{sale.Id}/reprint")).TicketText.ShouldContain("COPIA");
    }
}
