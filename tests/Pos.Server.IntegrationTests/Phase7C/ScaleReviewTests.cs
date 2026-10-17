using System.Net;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;
using static Pos.Server.IntegrationTests.Phase7.ScaleSalesTests;

namespace Pos.Server.IntegrationTests.Phase7C;

/// <summary>
/// Correcciones de la revisión de la báscula (Fase 7-C): no cobrar el peso del producto anterior (A2), una sola tara (M7), el origen del
/// peso es obligatorio (M9), la línea pesada no se edita (M10) y el peso mínimo de la caja se guarda (M6).
/// </summary>
public class ScaleReviewTests
{
    [Fact]
    public async Task Ventas_con_bascula_peso_anterior_tara_unica_origen_obligatorio_y_linea_pesada_fija()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.Catalog.ExecuteAsync($"UPDATE catalog.products SET tare_grams = 20 WHERE id = '{shop.Tomato}'");
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        var lines = $"/api/v1/sales/{sale.Id}/lines";

        // M9: con peso y sin origen no se supone la báscula; SCALE exige la lectura del agente, estable y coherente.
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, new { code = "TOMATE", weightKg = 2.5m })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "SALES.WEIGHT_SOURCE_REQUIRED");
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, new { code = "TOMATE", weightKg = 2.5m, weightSource = "SCALE" })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "SALES.SCALE_READING_REQUIRED");
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, new { code = "TOMATE", weightKg = 2.5m, weightSource = "SCALE", scale = Reading(2.5m, stable: false) })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_WEIGHT_UNSTABLE");
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, new { code = "TOMATE", weightKg = 2.5m, weightSource = "SCALE", scale = Reading(0.3m) })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "SALES.INVALID_SCALE_READING");

        // M7: el cajero tomó la tara manual de una bandeja de 50 g: se descuenta esa y NO además la bolsa del producto (20 g).
        sale = await PostAsync<SaleDto>(shop.Cashier, lines, new { code = "TOMATE", weightKg = 1.200m, weightSource = "SCALE", scale = Reading(1.250m, tareKg: 0.050m) });
        sale.Lines[^1].Quantity.ShouldBe(1.200m);
        (await shop.Catalog.ScalarAsync<decimal>($"SELECT scale_gross_kg FROM sales.sale_lines WHERE id = '{sale.Lines[^1].Id}'")).ShouldBe(1.250m);

        // A2: sigue el mismo producto en la báscula (1,250 kg bruto) y registran otro: se rechaza.
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, new { code = "TOMATE", weightKg = 1.252m, weightSource = "SCALE", scale = Reading(1.252m) })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_WEIGHT_REPEATED");

        // Si la caja vio la báscula vacía entre las dos pesadas, el mismo peso vale (dos paquetes iguales); con otro peso también.
        sale = await PostAsync<SaleDto>(shop.Cashier, lines, new { code = "TOMATE", weightKg = 1.250m, weightSource = "SCALE", scale = Reading(1.250m, emptiedSinceLast: true) });
        sale.Lines[^1].Quantity.ShouldBe(1.230m);
        sale = await PostAsync<SaleDto>(shop.Cashier, lines, new { code = "TOMATE", weightKg = 0.820m, weightSource = "SCALE", scale = Reading(0.820m) });
        sale.Lines[^1].Quantity.ShouldBe(0.800m);

        // M10: la cantidad de una línea pesada no se edita (se elimina y se vuelve a pesar).
        var weighed = sale.Lines[^1];
        await RawAsync(shop.Cashier, HttpMethod.Patch, $"{lines}/{weighed.Id}", new { quantity = 5m })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.WEIGHED_LINE_QUANTITY");
        (await GetAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{sale.Id}")).Lines.Single(l => l.Id == weighed.Id).Quantity.ShouldBe(0.800m);

        // Eliminada la línea, volver a pesar el mismo producto con el mismo peso vale.
        sale = await PostAsync<SaleDto>(shop.Cashier, $"{lines}/{weighed.Id}/void", null);
        sale = await PostAsync<SaleDto>(shop.Cashier, lines, new { code = "TOMATE", weightKg = 0.820m, weightSource = "SCALE", scale = Reading(0.820m) });
        sale.Lines[^1].Quantity.ShouldBe(0.800m);
    }

    [Fact]
    public async Task El_peso_minimo_de_la_bascula_se_guarda_con_la_caja()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var terminal = (await GetAsync<List<TerminalDto>>(shop.Owner, "/api/v1/organization/terminals")).Single();
        var url = $"/api/v1/organization/terminals/{terminal.Id}/scale";
        object Config(int? minWeightGrams) => new
        {
            enabled = true, driver = "CAS", port = "COM4", stableWaitSeconds = 3, emptyWaitSeconds = 5, allowManualWeight = true, minWeightGrams,
        };

        (await SendAsync<TerminalScaleDto>(shop.Owner, HttpMethod.Put, url, Config(null))).MinWeightGrams.ShouldBe(20);
        (await SendAsync<TerminalScaleDto>(shop.Owner, HttpMethod.Put, url, Config(40))).MinWeightGrams.ShouldBe(40);
        (await GetAsync<TerminalScaleDto>(shop.Cashier, url)).MinWeightGrams.ShouldBe(40);
        await RawAsync(shop.Owner, HttpMethod.Put, url, Config(5000)).ShouldFailWithAsync(HttpStatusCode.BadRequest, "ORGANIZATION.INVALID_SCALE");
    }
}
