using System.Net;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase7C;

/// <summary>
/// Correcciones de las revisiones de la báscula (Fase 7-C): lecturas firmadas por el agente y sin repetir (D3), retorno a cero en el
/// servidor por caja (D1, BN1), una sola tara (M7), origen del peso obligatorio (M9), línea pesada fija (M10), pantalla vieja (BN10) y
/// peso mínimo de la caja (M6).
/// </summary>
public class ScaleReviewTests
{
    [Fact]
    public async Task Ventas_con_bascula_lecturas_firmadas_retorno_a_cero_tara_unica_y_linea_pesada_fija()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.Catalog.ExecuteAsync($"UPDATE catalog.products SET tare_grams = 20 WHERE id = '{shop.Tomato}'");
        using var agent = await ScaleAgentDouble.PairAsync(shop.Owner);
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        var lines = $"/api/v1/sales/{sale.Id}/lines";
        object Line(decimal weightKg, object scale) => new { code = "TOMATE", weightKg, weightSource = "SCALE", scale };

        // BN10: una pantalla vieja (sin la cabecera de versión) que envía un peso recibe "recargue"; sin peso sigue funcionando.
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, new { code = "TOMATE", weightKg = 0.7m, weightSource = "SCALE" })
            .ShouldFailWithAsync(HttpStatusCode.Conflict, PosClientBuild.OutdatedCode);
        await PostAsync<SaleDto>(shop.Cashier, lines, new { code = "ARROZ-500" });
        ScaleAgentDouble.UseCurrentClient(shop.Cashier);

        // M9/D3: sin origen no se supone la báscula; SCALE exige la lectura firmada por el agente de ESTA caja.
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, new { code = "TOMATE", weightKg = 2.5m })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "SALES.WEIGHT_SOURCE_REQUIRED");
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, new { code = "TOMATE", weightKg = 2.5m, weightSource = "SCALE" })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "SALES.SCALE_READING_REQUIRED");
        using (var forger = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
        {
            await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(2.5m, agent.Read(2.5m, key: forger).Body))
                .ShouldFailWithAsync(HttpStatusCode.BadRequest, "SALES.SCALE_READING_REQUIRED");
        }

        var (_, honest) = agent.Read(0.300m);
        var tampered = new { payload = honest.Format().Replace("|0.300|0.000|0.300|", "|2.500|0.000|2.500|", StringComparison.Ordinal), signature = "AAAA" };
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(2.5m, tampered)).ShouldFailWithAsync(HttpStatusCode.BadRequest, "SALES.SCALE_READING_REQUIRED");
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(2.5m, agent.Read(2.5m, terminalId: Guid.NewGuid()).Body))
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "SALES.SCALE_READING_REQUIRED");
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(2.5m, agent.Read(2.5m, stable: false).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_WEIGHT_UNSTABLE");
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(2.5m, agent.Read(0.3m).Body))
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "SALES.INVALID_SCALE_READING");

        // M7: el cajero tomó la tara manual de una bandeja de 50 g: se descuenta esa y NO además la bolsa del producto (20 g).
        var (meat, _) = agent.Read(1.250m, tareKg: 0.050m);
        sale = await PostAsync<SaleDto>(shop.Cashier, lines, Line(1.200m, meat));
        var first = sale.Lines[^1];
        first.Quantity.ShouldBe(1.200m);
        (await shop.Catalog.ScalarAsync<decimal>($"SELECT scale_gross_kg FROM sales.sale_lines WHERE id = '{first.Id}'")).ShouldBe(1.250m);
        (await shop.Catalog.ScalarAsync<string>($"SELECT payload FROM sales.scale_readings WHERE sale_line_id = '{first.Id}'"))!.ShouldContain("|1.250|0.050|1.200|");

        // D3: la misma lectura firmada no vale dos veces (nonce).
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(1.200m, meat)).ShouldFailWithAsync(HttpStatusCode.Conflict, "SALES.SCALE_READING_REUSED");

        // D1: sin ver la báscula vacía no se pesa otra cosa, ni con el mismo peso ni con otro (el tomate encima de la carne).
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(1.250m, agent.Read(1.250m).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_NOT_EMPTIED");
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(1.950m, agent.Read(1.950m).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_NOT_EMPTIED");

        // La báscula quedó vacía (lo vio el agente, aunque fuera un paso rápido por cero): el mismo peso vale.
        agent.Empty();
        sale = await PostAsync<SaleDto>(shop.Cashier, lines, Line(1.250m, agent.Read(1.250m).Body));
        sale.Lines[^1].Quantity.ShouldBe(1.230m);

        // D3: una lectura muy anterior a la última aceptada (reloj del agente) no vale.
        agent.Empty();
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(0.5m, agent.Read(0.5m, at: agent.Clock.AddMinutes(-5)).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_READING_STALE");
        sale = await PostAsync<SaleDto>(shop.Cashier, lines, Line(0.820m, agent.Read(0.820m).Body));
        sale.Lines[^1].Quantity.ShouldBe(0.800m);

        // M10: la cantidad de una línea pesada no se edita (se elimina y se vuelve a pesar).
        var weighed = sale.Lines[^1];
        await RawAsync(shop.Cashier, HttpMethod.Patch, $"{lines}/{weighed.Id}", new { quantity = 5m })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.WEIGHED_LINE_QUANTITY");
        (await GetAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{sale.Id}")).Lines.Single(l => l.Id == weighed.Id).Quantity.ShouldBe(0.800m);

        // BN1: eliminar la última pesada libera el retorno a cero: se vuelve a pesar sin retirar el producto.
        sale = await PostAsync<SaleDto>(shop.Cashier, $"{lines}/{weighed.Id}/void", null);
        sale = await PostAsync<SaleDto>(shop.Cashier, lines, Line(0.820m, agent.Read(0.820m).Body));
        sale.Lines[^1].Quantity.ShouldBe(0.800m);

        // El retorno a cero vive en el servidor por caja: otra venta (o recargar la pantalla) no lo salta.
        await PostAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{sale.Id}/hold", new { label = "Pendiente" });
        var next = await shop.StartAsync();
        (await shop.Catalog.ScalarAsync<bool>($"SELECT awaiting_empty FROM sales.scale_interlocks WHERE pos_terminal_id = '{agent.TerminalId}'")).ShouldBeTrue();
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{next.Id}/lines", Line(0.640m, agent.Read(0.640m).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_NOT_EMPTIED");
        agent.Empty();
        (await PostAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{next.Id}/lines", Line(0.640m, agent.Read(0.640m).Body))).Lines[^1].Quantity.ShouldBe(0.620m);
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM sales.scale_readings")).ShouldBe(5);
    }

    [Fact]
    public async Task Volver_a_vincular_rota_la_clave_y_la_anterior_deja_de_valer()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        using var old = await ScaleAgentDouble.PairAsync(shop.Owner);
        using var renewed = new ScaleAgentDouble(old.TerminalId);
        await renewed.RegisterKeyAsync(shop.Owner);
        ScaleAgentDouble.UseCurrentClient(shop.Cashier);
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        var lines = $"/api/v1/sales/{sale.Id}/lines";

        await RawAsync(shop.Cashier, HttpMethod.Post, lines, new { code = "TOMATE", weightKg = 0.5m, weightSource = "SCALE", scale = old.Read(0.5m).Body })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "SALES.SCALE_READING_REQUIRED");
        (await PostAsync<SaleDto>(shop.Cashier, lines, new { code = "TOMATE", weightKg = 0.5m, weightSource = "SCALE", scale = renewed.Read(0.5m).Body }))
            .Lines[^1].Quantity.ShouldBe(0.5m);

        // Solo quien administra las cajas vincula la báscula; una clave que no es P-256 se rechaza.
        await RawAsync(shop.Cashier, HttpMethod.Put, $"/api/v1/organization/terminals/{old.TerminalId}/scale/reading-key", new { keyId = Guid.NewGuid(), publicKey = "AAAA" })
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");
        await RawAsync(shop.Owner, HttpMethod.Put, $"/api/v1/organization/terminals/{old.TerminalId}/scale/reading-key", new { keyId = Guid.NewGuid(), publicKey = "AAAA" })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "ORGANIZATION.INVALID_SCALE_KEY");
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
