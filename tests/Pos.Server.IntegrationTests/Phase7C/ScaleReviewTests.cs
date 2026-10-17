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
        using var agent = await ScaleAgentDouble.PairAsync(shop);
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        var lines = $"/api/v1/sales/{sale.Id}/lines";
        object Line(decimal weightKg, object scale) => new { code = "TOMATE", weightKg, weightSource = "SCALE", scale };

        // BN10: una pantalla vieja (sin la cabecera de versión) que envía un peso recibe CLIENT.OUTDATED ("recargue"); sin peso sigue funcionando.
        var outdated = await RawAsync(shop.Cashier, HttpMethod.Post, lines, new { code = "TOMATE", weightKg = 0.7m, weightSource = "SCALE" });
        (await SecurityScenario.ProblemAsync(outdated)).GetProperty("detail").GetString()!.ShouldContain("Ctrl+F5");
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

        // BB4: una lectura vieja o del futuro frente al reloj del servidor (⚙️ 5 min) no vale.
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(0.5m, agent.Read(0.5m, at: DateTimeOffset.UtcNow.AddMinutes(-10)).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_CLOCK_SKEW");
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(0.5m, agent.Read(0.5m, at: DateTimeOffset.UtcNow.AddMinutes(10)).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_CLOCK_SKEW");

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

        // BB4: el reloj del agente se atrasó 3 minutos: el orden va por la secuencia y la caja sigue pesando (sin quedar trabada).
        agent.ClockOffset = TimeSpan.FromMinutes(-3);
        agent.Empty();
        sale = await PostAsync<SaleDto>(shop.Cashier, lines, Line(0.820m, agent.Read(0.820m).Body));
        sale.Lines[^1].Quantity.ShouldBe(0.800m);
        agent.ClockOffset = TimeSpan.Zero;

        // M10: la cantidad de una línea pesada no se edita (se elimina y se vuelve a pesar).
        var weighed = sale.Lines[^1];
        await RawAsync(shop.Cashier, HttpMethod.Patch, $"{lines}/{weighed.Id}", new { quantity = 5m })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.WEIGHED_LINE_QUANTITY");
        (await GetAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{sale.Id}")).Lines.Single(l => l.Id == weighed.Id).Quantity.ShouldBe(0.800m);

        // BN1: eliminar la última pesada libera el retorno a cero (el servidor lo dice y la caja libera el agente): se vuelve a pesar.
        (await GetAsync<ScaleInterlockDto>(shop.Cashier, "/api/v1/sales/scale/interlock")).AwaitingEmpty.ShouldBeTrue();
        sale = await PostAsync<SaleDto>(shop.Cashier, $"{lines}/{weighed.Id}/void", null);
        (await GetAsync<ScaleInterlockDto>(shop.Cashier, "/api/v1/sales/scale/interlock")).AwaitingEmpty.ShouldBeFalse();
        sale = await PostAsync<SaleDto>(shop.Cashier, lines, Line(0.820m, agent.Read(0.820m).Body));
        sale.Lines[^1].Quantity.ShouldBe(0.800m);

        // BN1: eliminar una pesada que NO es la última de la caja no libera nada.
        await PostAsync<SaleDto>(shop.Cashier, $"{lines}/{first.Id}/void", null);
        (await GetAsync<ScaleInterlockDto>(shop.Cashier, "/api/v1/sales/scale/interlock")).AwaitingEmpty.ShouldBeTrue();

        // El retorno a cero vive en el servidor por caja: otra venta (o recargar la pantalla) no lo salta.
        await PostAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{sale.Id}/hold", new { label = "Pendiente" });
        var next = await shop.StartAsync();
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{next.Id}/lines", Line(0.640m, agent.Read(0.640m).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_NOT_EMPTIED");

        // El agente se reinició (otro arranque): basta con ver la báscula vacía desde que arrancó. Una lectura que el arranque anterior
        // firmó y nadie usó (R2) nunca vale después (BK2).
        agent.Empty();
        var (beforeRestart, _) = agent.Read(0.640m);
        agent.Restart();
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{next.Id}/lines", Line(0.640m, agent.Read(0.640m).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_NOT_EMPTIED");
        agent.Empty();
        (await PostAsync<SaleDto>(shop.Cashier, $"/api/v1/sales/{next.Id}/lines", Line(0.640m, agent.Read(0.640m).Body))).Lines[^1].Quantity.ShouldBe(0.620m);
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{next.Id}/lines", Line(0.640m, beforeRestart))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_READING_STALE");

        // BQ3: un arranque con número menor al último aceptado (aunque nunca tuvo pesadas y no está retirado) tampoco vale.
        agent.Empty();
        await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{next.Id}/lines",
                Line(0.640m, agent.Read(0.640m, bootCounter: agent.BootCounter - 1, bootId: Guid.NewGuid()).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_BOOT_REGRESSED");
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM sales.scale_readings")).ShouldBe(5);
    }

    [Fact]
    public async Task Solo_se_vincula_desde_la_caja_con_supervisor_y_el_cambio_queda_a_la_vista()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var terminal = (await GetAsync<List<TerminalDto>>(shop.Owner, "/api/v1/organization/terminals")).Single();
        await SendAsync<TerminalScaleDto>(shop.Owner, HttpMethod.Put, $"/api/v1/organization/terminals/{terminal.Id}/scale", new
        {
            enabled = true, driver = "TOLEDO_8217", port = "COM3", stableWaitSeconds = 3, emptyWaitSeconds = 5, allowManualWeight = true,
        });

        // BK3: báscula activa sin vincular: el Tablero lo avisa. BQ10: con la función "Báscula y PLU" apagada, no.
        (await GetAsync<List<ScaleKeyChangeDto>>(shop.Owner, "/api/v1/organization/terminals/scale-key-changes")).Single()
            .ShouldBe(new ScaleKeyChangeDto(terminal.Id, terminal.Code, null, Linked: false));
        var features = (await GetAsync<BusinessFeaturesDto>(shop.Owner, "/api/v1/organization/business-features")).EnabledFeatures;
        await SendAsync<BusinessFeaturesDto>(shop.Owner, HttpMethod.Put, "/api/v1/organization/business-features",
            new { enabledFeatures = features.Where(f => f != "SCALE").ToArray() });
        (await GetAsync<List<ScaleKeyChangeDto>>(shop.Owner, "/api/v1/organization/terminals/scale-key-changes")).ShouldBeEmpty();
        await SendAsync<BusinessFeaturesDto>(shop.Owner, HttpMethod.Put, "/api/v1/organization/business-features", new { enabledFeatures = features });

        using var old = new ScaleAgentDouble(terminal.Id);
        await old.RegisterKeyAsync(shop);
        var url = $"/api/v1/organization/terminals/{old.TerminalId}/scale/reading-key";
        using var stranger = new ScaleAgentDouble(old.TerminalId);

        // BB2: desde la administración (otro equipo o un token robado del administrador) no se registra una clave propia, aun con el permiso.
        await RawAsync(shop.Owner, HttpMethod.Put, url, stranger.KeyBody).ShouldFailWithAsync(HttpStatusCode.Forbidden, "ORGANIZATION.SCALE_LINK_TERMINAL");

        // En la caja, el cajero no tiene el permiso: necesita la autorización del supervisor.
        await RawAsync(shop.Cashier, HttpMethod.Put, url, stranger.KeyBody).ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.AUTHORIZATION_REQUIRED");
        var denied = await RawAsync(shop.Cashier, HttpMethod.Put, url, new { keyId = Guid.NewGuid(), publicKey = "AAAA" });
        await RawAsync(shop.Cashier, HttpMethod.Put, url, new { keyId = Guid.NewGuid(), publicKey = "AAAA" }, await shop.GrantAsync(denied))
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "ORGANIZATION.INVALID_SCALE_KEY");

        // BK1: los intentos fallidos (sin supervisor, clave inválida) no cambiaron la clave: la caja sigue pesando con la vigente.
        ScaleAgentDouble.UseCurrentClient(shop.Cashier);
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        var lines = $"/api/v1/sales/{sale.Id}/lines";
        (await PostAsync<SaleDto>(shop.Cashier, lines, new { code = "TOMATE", weightKg = 0.3m, weightSource = "SCALE", scale = old.Read(0.3m).Body }))
            .Lines[^1].Quantity.ShouldBe(0.3m);

        // Volver a vincular (con supervisor) rota la clave: la anterior deja de valer.
        using var renewed = new ScaleAgentDouble(old.TerminalId);
        await renewed.RegisterKeyAsync(shop);
        renewed.Empty();
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, new { code = "TOMATE", weightKg = 0.5m, weightSource = "SCALE", scale = old.Read(0.5m).Body })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "SALES.SCALE_READING_REQUIRED");
        (await PostAsync<SaleDto>(shop.Cashier, lines, new { code = "TOMATE", weightKg = 0.5m, weightSource = "SCALE", scale = renewed.Read(0.5m).Body }))
            .Lines[^1].Quantity.ShouldBe(0.5m);

        // Auditado (con quién autorizó) y visible en el Tablero: "La clave de la báscula de la caja X cambió".
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'SCALE_KEY_LINKED' AND authorized_by IS NOT NULL")).ShouldBe(2);
        (await GetAsync<List<ScaleKeyChangeDto>>(shop.Owner, "/api/v1/organization/terminals/scale-key-changes")).Single().PosTerminalId.ShouldBe(old.TerminalId);
    }

    [Fact]
    public async Task Vincular_desde_otro_equipo_de_caja_no_vale_aunque_tenga_sesion_de_caja()
    {
        // BB2: Multicaja con dos equipos emparejados (caja 1 y caja 2). Desde la sesión de caja del equipo de la caja 2 (con el permiso del
        // Propietario) no se puede vincular la báscula de la caja 1; desde el equipo de la caja 1, sí.
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var caja1 = scenario.Setup.PosTerminalId;
        var caja2 = (await PostAsync<TerminalDto>(scenario.Owner, $"/api/v1/organization/branches/{scenario.Setup.BranchId}/terminals",
            new { code = "C02", name = "Caja 2" }, HttpStatusCode.Created)).Id;
        await SendAsync<TerminalScaleDto>(scenario.Owner, HttpMethod.Put, $"/api/v1/organization/terminals/{caja1}/scale", new
        {
            enabled = true, driver = "CAS", port = "COM4", stableWaitSeconds = 3, emptyWaitSeconds = 5, allowManualWeight = true,
        });
        using var agent = new ScaleAgentDouble(caja1);
        var url = $"/api/v1/organization/terminals/{caja1}/scale/reading-key";

        var (otherDevice, _) = await scenario.PairTerminalAsync(caja2, ip: "192.168.1.22", hostname: "CAJA-02", fingerprint: 'b');
        await SecurityScenario.PosLoginAsync(otherDevice, OwnerPosCode, OwnerPin);
        await RawAsync(otherDevice, HttpMethod.Put, url, agent.KeyBody).ShouldFailWithAsync(HttpStatusCode.Forbidden, "ORGANIZATION.SCALE_LINK_TERMINAL");

        var (ownDevice, _) = await scenario.PairTerminalAsync(caja1, ip: "192.168.1.21", hostname: "CAJA-01", fingerprint: 'a');
        await SecurityScenario.PosLoginAsync(ownDevice, OwnerPosCode, OwnerPin);
        (await SendAsync<TerminalScaleDto>(ownDevice, HttpMethod.Put, url, agent.KeyBody)).ReadingKeyId.ShouldBe(agent.KeyId);
    }

    [Fact]
    public async Task Reinstalar_el_agente_y_volver_a_vincular_destraba_la_caja()
    {
        // BS1 de punta a punta: la caja pesa varias veces, se reinstala el agente (número de arranque 1), el servidor pide volver a
        // vincular, se vincula con el supervisor (clave nueva) y la caja vuelve a pesar.
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        using var agent = await ScaleAgentDouble.PairAsync(shop);
        ScaleAgentDouble.UseCurrentClient(shop.Cashier);
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        var lines = $"/api/v1/sales/{sale.Id}/lines";
        object Line(decimal weightKg, object scale) => new { code = "TOMATE", weightKg, weightSource = "SCALE", scale };
        agent.Restart();
        agent.Restart();
        await PostAsync<SaleDto>(shop.Cashier, lines, Line(0.7m, agent.Read(0.7m).Body));

        agent.Reinstall();
        var rejected = await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(0.5m, agent.Read(0.5m).Body));
        (await SecurityScenario.ProblemAsync(rejected)).GetProperty("detail").GetString()!.ShouldContain("vuelva a vincular la báscula (F1");
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(0.5m, agent.Read(0.5m).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_BOOT_REGRESSED");

        // El agente reinstalado (otro arranque, número 1) se vincula con una clave nueva: antes de vaciar la báscula no pesa; después sí.
        using var relinked = ScaleAgentDouble.SameAgentWithNewKey(agent);
        await relinked.RegisterKeyAsync(shop);
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(0.5m, relinked.Read(0.5m).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_NOT_EMPTIED");
        relinked.Empty();
        (await PostAsync<SaleDto>(shop.Cashier, lines, Line(0.5m, relinked.Read(0.5m).Body))).Lines[^1].Quantity.ShouldBe(0.5m);
        (await shop.Catalog.ScalarAsync<long>($"SELECT last_boot_counter FROM sales.scale_interlocks WHERE pos_terminal_id = '{agent.TerminalId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Volver_a_vincular_sin_retirar_el_producto_no_salta_el_retorno_a_cero()
    {
        // Ronda 7: se pesa, se vuelve a vincular (mismo agente, clave nueva) sin retirar el producto y se pesa otra vez: SCALE_NOT_EMPTIED.
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        using var agent = await ScaleAgentDouble.PairAsync(shop);
        ScaleAgentDouble.UseCurrentClient(shop.Cashier);
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        var lines = $"/api/v1/sales/{sale.Id}/lines";
        object Line(decimal weightKg, object scale) => new { code = "TOMATE", weightKg, weightSource = "SCALE", scale };
        await PostAsync<SaleDto>(shop.Cashier, lines, Line(0.7m, agent.Read(0.7m).Body));

        using var relinked = ScaleAgentDouble.SameAgentWithNewKey(agent);
        await relinked.RegisterKeyAsync(shop);
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(0.7m, relinked.Read(0.7m).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_NOT_EMPTIED");

        relinked.Empty();
        (await PostAsync<SaleDto>(shop.Cashier, lines, Line(0.7m, relinked.Read(0.7m).Body))).Lines[^1].Quantity.ShouldBe(0.7m);
    }

    [Fact]
    public async Task Reiniciar_bascula_con_supervisor_y_la_misma_lectura_dos_veces_a_la_vez_da_409()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        using var agent = await ScaleAgentDouble.PairAsync(shop);
        ScaleAgentDouble.UseCurrentClient(shop.Cashier);
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        var lines = $"/api/v1/sales/{sale.Id}/lines";
        object Line(decimal weightKg, object scale) => new { code = "TOMATE", weightKg, weightSource = "SCALE", scale };

        // BB9: la misma lectura enviada dos veces al mismo tiempo: una entra y la otra es un 409 (nunca un 500).
        var body = Line(0.7m, agent.Read(0.7m).Body);
        var both = await Task.WhenAll(RawAsync(shop.Cashier, HttpMethod.Post, lines, body), RawAsync(shop.Cashier, HttpMethod.Post, lines, body));
        both.Select(r => r.StatusCode).OrderBy(s => s).ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM sales.scale_readings")).ShouldBe(1);

        // BB5: la bandeja quedó en la báscula y no puede quedar vacía: "Reiniciar báscula" con el supervisor libera la espera (auditado).
        await RawAsync(shop.Cashier, HttpMethod.Post, lines, Line(0.9m, agent.Read(0.9m).Body))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.SCALE_NOT_EMPTIED");
        (await shop.AuthorizedAsync<ScaleInterlockDto>(HttpMethod.Post, "/api/v1/sales/scale/reset")).AwaitingEmpty.ShouldBeFalse();
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'SALE_SCALE_RESET' AND authorized_by IS NOT NULL")).ShouldBe(1);
        (await PostAsync<SaleDto>(shop.Cashier, lines, Line(0.9m, agent.Read(0.9m).Body))).Lines[^1].Quantity.ShouldBe(0.9m);
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
