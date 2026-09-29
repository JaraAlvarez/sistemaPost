using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Sales.Application;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase7;

/// <summary>Criterios de la Fase 7: existencias y vencidos, ventas pendientes y cierre, anulación, cambios y garantía.</summary>
public class SalesFlowTests
{
    [Fact]
    public async Task Sin_existencias_no_se_vende_ajuste_rapido_autorizado_y_lote_vencido_con_autorizacion()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();

        // 50 arroces: pedir 51 no se puede (RN-SAL-17); la línea no queda en la venta.
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{sale.Id}/lines", new { code = "ARROZ-500", quantity = 51 })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.INSUFFICIENT_STOCK");
        (await GetAsync<SaleDto>(shop.Cashier, "/api/v1/sales/current")).Lines.ShouldBeEmpty();

        // El producto está en la mano del cliente: el supervisor autoriza un ajuste rápido desde la caja (entrada real al kardex).
        var terminal = (await GetAsync<List<TerminalDto>>(shop.Owner, "/api/v1/organization/terminals")).Single();
        var adjustment = await shop.AuthorizedAsync<AdjustmentDto>(HttpMethod.Post, "/api/v1/inventory/quick-adjustments",
            new { warehouseId = terminal.WarehouseId, productId = shop.Rice, quantity = 1m, reason = "Compra sin registrar, producto en góndola" },
            expected: HttpStatusCode.Created);
        adjustment.Status.ShouldBe("POSTED");
        sale = await shop.ScanAsync(sale.Id, "ARROZ-500", 51);
        sale.Lines.Single().Quantity.ShouldBe(51m);

        // Lote vencido: el yogurt del lote L-A venció ayer; venderlo exige autorización (RN-SAL-18) y queda registrada.
        await shop.Catalog.ExecuteAsync("UPDATE inventory.inventory_lots SET expiry_date = current_date - 1 WHERE lot_number = 'L-A'");
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{sale.Id}/lines", new { productId = shop.Yogurt, quantity = 1 })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.EXPIRED_LOT_REQUIRES_AUTHORIZATION");
        sale = await shop.AuthorizedAsync<SaleDto>(HttpMethod.Post, $"/api/v1/sales/{sale.Id}/lines/expired", new { productId = shop.Yogurt, quantity = 1 });
        sale.Lines.Single(l => l.ProductId == shop.Yogurt).ExpiredLotAuthorized.ShouldBeTrue();

        // Al cobrar, el kardex descuenta con FEFO (primero el lote vencido) y fija el costo de cada línea.
        var receipt = await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 200_000m)]);
        receipt.Sale.Status.ShouldBe("COMPLETED");
        (await shop.StockAsync(shop.Rice)).ShouldBe(0m);
        (await shop.Purchasing.LotsAsync(shop.Yogurt)).Single(l => l.LotNumber == "L-A").Quantity.ShouldBe(9m);
        (await shop.Catalog.ScalarAsync<string>("SELECT summary FROM audit.audit_log WHERE action = 'SALE_EXPIRED_LOT_AUTHORIZED'"))!.ShouldContain("YOG-1");
        (await shop.Catalog.ScalarAsync<string>("SELECT summary FROM audit.audit_log WHERE action = 'INVENTORY_QUICK_ADJUSTMENT'"))!.ShouldContain("ARROZ-500");
    }

    [Fact]
    public async Task Suspender_recuperar_cancelar_y_la_caja_no_cierra_con_ventas_pendientes()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var session = await shop.OpenSessionAsync();

        var first = await shop.StartAsync();
        await shop.ScanAsync(first.Id, "ARROZ-500");
        var held = await PostAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{first.Id}/hold", new { label = "Señora del bolso rojo" });
        held.Status.ShouldBe("ON_HOLD");

        var second = await shop.StartAsync();
        await shop.ScanAsync(second.Id, "7702004003508");
        (await GetAsync<List<SaleSummaryDto>>(shop.Cashier, "/api/v1/sales/held")).Single().HoldLabel.ShouldBe("Señora del bolso rojo");

        // RN-CSH-03: no se inicia el cierre con ventas en curso o suspendidas.
        await shop.Cashier.PostAsync($"/api/v1/cash/sessions/{session.Id}/start-closing", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "CASH.OPEN_SALES");

        // No se recupera mientras haya otra venta en curso; cancelar exige autorización y motivo.
        await shop.Cashier.PostAsync($"/api/v1/sales/{first.Id}/resume", null, Ct).ShouldFailWithAsync(HttpStatusCode.Conflict, "SALES.OPEN_SALE_EXISTS");
        var cancelled = await shop.AuthorizedAsync<SaleDto>(HttpMethod.Post, $"/api/v1/sales/{second.Id}/cancel", new { reason = "El cliente desistió" });
        cancelled.Status.ShouldBe("CANCELLED");
        cancelled.Number.ShouldBeNull();

        var resumed = await PostAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{first.Id}/resume");
        resumed.Status.ShouldBe("OPEN");
        (await shop.CompleteAsync(first.Id, [Pay(shop.Cash, 3_000m)])).Sale.Number.ShouldNotBeNull();

        // Eliminar una línea la deja registrada como anulada; una venta vacía no se cobra.
        var third = await shop.StartAsync();
        third = await shop.ScanAsync(third.Id, "ARROZ-500");
        third = await PostAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{third.Id}/lines/{third.Lines[0].Id}/void");
        third.Lines.Single().Status.ShouldBe("VOIDED");
        third.Total.ShouldBe(0m);
        await shop.Cashier.PostAsJsonAsync($"/api/v1/sales/{third.Id}/complete", new { payments = new[] { Pay(shop.Cash, 1_000m) } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.EMPTY_SALE");
        await shop.AuthorizedAsync<SaleDto>(HttpMethod.Post, $"/api/v1/sales/{third.Id}/cancel", new { reason = "Venta vacía" });

        (await PostAsync<CashSessionDto>(shop.Cashier, $"/api/v1/cash/sessions/{session.Id}/start-closing")).Status.ShouldBe("CLOSING");
        await shop.Cashier.PostAsJsonAsync("/api/v1/sales", new { }, Json, Ct).ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.NO_OPEN_CASH_SESSION");
    }

    [Fact]
    public async Task Anular_una_venta_por_error_devuelve_inventario_y_dinero_solo_con_la_jornada_abierta()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var session = await shop.OpenSessionAsync();

        var sale = await shop.StartAsync();
        await shop.ScanAsync(sale.Id, "ARROZ-500", 3);
        var done = (await shop.CompleteAsync(sale.Id, [Pay(shop.Debit, 4_000m, "APR-1"), Pay(shop.Cash, 5_000m)])).Sale;
        (await shop.StockAsync(shop.Rice)).ShouldBe(47m);

        // La cajera no anula sola: autoriza el supervisor; el comprobante queda anulado y el tiquete dice ANULADA.
        var voided = await shop.AuthorizedAsync<SaleReceiptDto>(HttpMethod.Post, $"/api/v1/sales/{done.Id}/void", new { reason = "Se cobró dos veces" });
        voided.Sale.Status.ShouldBe("VOIDED");
        voided.DocumentStatus.ShouldBe("VOIDED");
        voided.TicketText.ShouldContain("VENTA ANULADA");
        (await shop.StockAsync(shop.Rice)).ShouldBe(50m);
        var x = await GetAsync<CashReportDto>(shop.Supervisor, $"/api/v1/cash/sessions/{session.Id}/report-x");
        x.Totals.Single(t => t.Code == "EFECTIVO").Expected.ShouldBe(100_000m);
        x.Totals.Single(t => t.Code == "DEBITO").Expected.ShouldBe(0m);
        (await shop.Catalog.ScalarAsync<string>("SELECT severity FROM audit.audit_log WHERE action = 'SALE_VOIDED'")).ShouldBe("CRITICAL");

        // No se anula dos veces.
        await RawAsync(shop.Supervisor, HttpMethod.Post, $"/api/v1/sales/{done.Id}/void", new { reason = "Otra vez" })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.VOID_NOT_ALLOWED");

        // Una venta de una jornada ya cerrada no se anula: el reporte Z es definitivo (D7-10).
        var other = await shop.StartAsync();
        await shop.ScanAsync(other.Id, "ARROZ-500");
        var kept = (await shop.CompleteAsync(other.Id, [Pay(shop.Cash, 3_000m)])).Sale;
        await PostAsync<CashSessionDto>(shop.Cashier, $"/api/v1/cash/sessions/{session.Id}/start-closing");
        await PostAsync<CashReportDto>(shop.Cashier, $"/api/v1/cash/sessions/{session.Id}/close", new
        {
            count = new[] { new { paymentMethodId = shop.Cash, amount = 103_000m } },
        });
        await shop.OpenSessionAsync();
        await RawAsync(shop.Supervisor, HttpMethod.Post, $"/api/v1/sales/{kept.Id}/void", new { reason = "Error de ayer" })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.VOID_NOT_ALLOWED");
    }

    [Fact]
    public async Task Cambio_de_mercancia_por_igual_o_mayor_valor_sin_devolver_dinero_y_garantia_solo_del_propietario()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var session = await shop.OpenSessionAsync();

        var sale = await shop.StartAsync();
        await shop.ScanAsync(sale.Id, "ARROZ-500", 2);
        await shop.ScanAsync(sale.Id, "7702004003508", 2);
        var original = (await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 11_000m)])).Sale;
        var riceLine = original.Lines.Single(l => l.ProductId == shop.Rice);
        var sodaLine = original.Lines.Single(l => l.ProductId == shop.Soda);

        // La cajera busca la venta por su número y el supervisor autoriza el cambio: 1 arroz vuelve a la venta y 1 gaseosa a averías.
        (await GetAsync<List<SaleSummaryDto>>(shop.Cashier, $"/api/v1/sales?number={original.Number}")).Single().Id.ShouldBe(original.Id);
        var body = new
        {
            originalSaleId = original.Id, reason = "Gaseosa sin gas y arroz de más",
            lines = new object[]
            {
                new { saleLineId = riceLine.Id, quantity = 1m, destination = "ReturnToStock" },
                new { saleLineId = sodaLine.Id, quantity = 1m, destination = "SendToDamaged" },
            },
        };
        var grant = await shop.GrantAsync(await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/exchanges", body));
        var started = await SendAsync<ExchangeStartedDto>(shop.Cashier, HttpMethod.Post, "/api/v1/exchanges", body, HttpStatusCode.Created, grant: grant);
        started.Exchange.CreditTotal.ShouldBe(5_500m);
        started.Sale.ExchangeCredit.ShouldBe(5_500m);

        // Llevar menos que el crédito no se permite: no se devuelve dinero (D7-11).
        var replacement = await shop.ScanAsync(started.Sale.Id, "7702004003508");
        await shop.Cashier.PostAsJsonAsync($"/api/v1/sales/{replacement.Id}/complete", new { payments = Array.Empty<object>() }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.EXCHANGE_BELOW_CREDIT");

        // Lleva 3 gaseosas ($7.500): el crédito cubre $5.500 y paga la diferencia en efectivo.
        replacement = await shop.ScanAsync(replacement.Id, "7702004003508", 2);
        replacement.AmountDue.ShouldBe(2_000m);
        var receipt = await shop.CompleteAsync(replacement.Id, [Pay(shop.Cash, 2_000m)]);
        receipt.Sale.Payments.Single(p => p.MethodKind == "EXCHANGE_CREDIT").Applied.ShouldBe(5_500m);
        var exchange = await GetAsync<ExchangeDto>(shop.Cashier, $"/api/v1/exchanges/{started.Exchange.Id}");
        exchange.Status.ShouldBe("COMPLETED");
        exchange.Number.ShouldNotBeNull();
        (await GetAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{original.Id}")).ReturnStatus.ShouldBe("PARTIAL");

        // Inventario: arroz 50 − 2 + 1; gaseosas 40 − 2 − 3 en la venta y 1 en averías.
        (await shop.StockAsync(shop.Rice)).ShouldBe(49m);
        (await shop.StockAsync(shop.Soda)).ShouldBe(35m);
        (await shop.Catalog.ScalarAsync<decimal>(
                $"SELECT b.quantity FROM inventory.stock_balances b JOIN org.warehouses w ON w.id = b.warehouse_id WHERE w.kind = 'DAMAGED' AND b.product_id = '{shop.Soda}' AND b.lot_id IS NULL"))
            .ShouldBe(1m);

        // En la caja no salió dinero: el efectivo esperado sube con lo cobrado ($11.000 + $2.000).
        var x = await GetAsync<CashReportDto>(shop.Supervisor, $"/api/v1/cash/sessions/{session.Id}/report-x");
        x.Totals.Single(t => t.Code == "EFECTIVO").Expected.ShouldBe(113_000m);
        x.Totals.ShouldNotContain(t => t.Code == "CAMBIO");

        // Excepción de garantía: solo el propietario (ni el supervisor puede autorizarla) y sale en efectivo del cajón.
        var refund = new
        {
            originalSaleId = original.Id, reason = "Producto defectuoso, garantía",
            lines = new object[] { new { saleLineId = riceLine.Id, quantity = 1m, destination = "Discard" } },
        };
        await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/exchanges/warranty-refund", refund)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");
        var owner = factory.CreateClient();
        await SecurityScenario.PosLoginAsync(owner, "100", "4826");
        var refunded = await PostAsync<RefundReceiptDto>(owner, "/api/v1/exchanges/warranty-refund", refund);
        refunded.Refund.Kind.ShouldBe("WARRANTY_REFUND");
        refunded.Refund.CreditTotal.ShouldBe(3_000m);
        refunded.OpenDrawer.ShouldBeTrue();
        x = await GetAsync<CashReportDto>(shop.Supervisor, $"/api/v1/cash/sessions/{session.Id}/report-x");
        x.Totals.Single(t => t.Code == "EFECTIVO").Expected.ShouldBe(110_000m);
        (await GetAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{original.Id}")).ReturnStatus.ShouldBe("PARTIAL");
        (await shop.Catalog.ScalarAsync<string>("SELECT severity FROM audit.audit_log WHERE action = 'WARRANTY_REFUND'")).ShouldBe("CRITICAL");

        // Lo ya cambiado no se vuelve a cambiar.
        var twice = new
        {
            originalSaleId = original.Id, reason = "Otra vez el arroz",
            lines = new object[] { new { saleLineId = riceLine.Id, quantity = 1m, destination = "ReturnToStock" } },
        };
        var againGrant = await shop.GrantAsync(await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/exchanges", twice));
        await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/exchanges", twice, againGrant)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.EXCHANGE_QUANTITY_EXCEEDED");
    }
}
