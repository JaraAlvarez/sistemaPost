using System.Net;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase7;

/// <summary>
/// Fase 7-C: la báscula de la caja se guarda en el servidor y la caja registra el peso que le entrega el agente (aquí, los pesos que
/// devolvería la báscula simulada del agente): tara del producto, una línea por pesada, peso digitado con autorización y tiquete.
/// </summary>
public class ScaleSalesTests
{
    /// <summary>Lectura del agente que acompaña una línea de báscula (sin tara manual, estable).</summary>
    public static object Reading(decimal grossKg, decimal tareKg = 0m, bool net = false, bool stable = true, bool emptiedSinceLast = false) =>
        new { grossKg, tareKg, net, stable, readAt = DateTimeOffset.UtcNow, emptiedSinceLast };

    [Fact]
    public async Task Bascula_de_la_caja_y_venta_con_peso_leido_y_digitado()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var terminal = (await GetAsync<List<TerminalDto>>(shop.Owner, "/api/v1/organization/terminals")).Single();

        // Sin configurar: la caja trabaja sin báscula.
        (await GetAsync<TerminalScaleDto>(shop.Cashier, $"/api/v1/organization/terminals/{terminal.Id}/scale")).Configured.ShouldBeFalse();
        var scale = await SendAsync<TerminalScaleDto>(shop.Owner, HttpMethod.Put, $"/api/v1/organization/terminals/{terminal.Id}/scale", new
        {
            enabled = true, driver = "TOLEDO_8217", port = "COM3", brand = "Torrey", model = "PCR-40", stableWaitSeconds = 3, emptyWaitSeconds = 5,
            allowManualWeight = true,
        });
        (scale.Configured, scale.Driver, scale.Port, scale.Parity).ShouldBe((true, "TOLEDO_8217", (string?)"COM3", (string?)null));
        (await GetAsync<TerminalScaleDto>(shop.Cashier, $"/api/v1/organization/terminals/{terminal.Id}/scale")).Brand.ShouldBe("Torrey");
        await RawAsync(shop.Cashier, HttpMethod.Put, $"/api/v1/organization/terminals/{terminal.Id}/scale", new { enabled = true, driver = "CAS", port = "COM4" })
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");

        // El tomate se vende por báscula con bolsa de 20 g.
        await shop.Catalog.ExecuteAsync($"UPDATE catalog.products SET tare_grams = 20 WHERE id = '{shop.Tomato}'");
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();

        // La báscula marca 0,720 kg: se registran 0,700 kg (menos la bolsa); otra pesada va en otra línea.
        sale = await PostAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{sale.Id}/lines", new { code = "TOMATE", weightKg = 0.720m, weightSource = "SCALE", scale = Reading(0.720m) });
        sale = await PostAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{sale.Id}/lines", new { code = "TOMATE", weightKg = 0.520m, weightSource = "SCALE", scale = Reading(0.520m) });
        var weighed = sale.Lines.Where(l => l.ProductId == shop.Tomato).ToList();
        weighed.Select(l => l.Quantity).ShouldBe([0.700m, 0.500m]);
        weighed.ShouldAllBe(l => l.Source == "SCALE_LIVE" && l.WeightSource == WeightSources.Scale);

        // Rechazos: menos que la tara, producto que no es de peso y peso digitado por la ruta normal.
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{sale.Id}/lines", new { code = "TOMATE", weightKg = 0.015m, weightSource = "SCALE", scale = Reading(0.015m) })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.WEIGHT_TOO_LOW");
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{sale.Id}/lines", new { code = "ARROZ-500", weightKg = 1m, weightSource = "SCALE" })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.NOT_WEIGHABLE");
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{sale.Id}/lines", new { code = "TOMATE", weightKg = 1m, weightSource = "MANUAL" })
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "SALES.MANUAL_WEIGHT_ROUTE");

        // La báscula no responde: el peso digitado necesita autorización del supervisor y queda auditado y marcado.
        sale = await shop.AuthorizedAsync<SaleDto>(HttpMethod.Post, $"/api/v1/sales/{sale.Id}/lines/manual-weight", new { code = "TOMATE", weightKg = 0.400m });
        var typed = sale.Lines[^1];
        (typed.Quantity, typed.Source, typed.WeightSource).ShouldBe((0.400m, "MANUAL_WEIGHT", (string?)WeightSources.Manual));
        (await shop.Catalog.ScalarAsync<string>("SELECT summary FROM audit.audit_log WHERE action = 'SALE_MANUAL_WEIGHT'"))!.ShouldContain("0,400 kg");

        // El tiquete muestra el peso y el precio por kilo; el kardex descuenta los kilos.
        var receipt = await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 100_000m)]);
        receipt.TicketText.ShouldContain("0,700 kg x $4.980/kg");
        receipt.TicketText.ShouldContain("0,400 kg x $4.980/kg (digitado)");
        (await shop.StockAsync(shop.Tomato)).ShouldBe(20m - 1.6m);
    }
}
