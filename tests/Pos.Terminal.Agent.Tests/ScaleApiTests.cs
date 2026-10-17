using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Terminal.Agent.Api;
using Pos.Terminal.Agent.Scales;
using static Pos.Terminal.Agent.Tests.AgentFactory;

namespace Pos.Terminal.Agent.Tests;

/// <summary>Endpoints de la báscula con la báscula simulada (habla Toledo 8217 dentro del agente; no se instalan puertos virtuales).</summary>
public class ScaleApiTests
{
    private static readonly ScaleSettings Simulated = new() { Driver = "SIMULATED", TimeoutMs = 300 };

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<HttpClient> ConfiguredAsync(AgentFactory agent)
    {
        var client = agent.CreateClient();
        var response = await client.PutAsync("/scale/config", Body(Simulated), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return client;
    }

    private static async Task PutWeightAsync(HttpClient client, decimal kg, bool stable = true, bool overload = false, bool silent = false) =>
        (await client.PostAsync("/scale/simulator", Body(new ScaleSimulatorRequest(kg, stable, overload, silent)), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

    private static async Task<ScaleWeightDto> WeightAsync(HttpClient client)
    {
        var response = await client.GetAsync("/scale/weight", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ScaleWeightDto>(Json, Ct))!;
    }

    [Fact]
    public async Task Sin_bascula_configurada_lo_dice_y_lista_los_drivers()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateClient();

        var response = await client.GetAsync("/scale/weight", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(response)).ShouldBe(ScaleOutcome.NotConfigured);
        (await client.GetAsync("/scale/stream", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var status = (await client.GetFromJsonAsync<ScaleStatusDto>("/scale/status", Json, Ct))!;
        status.Configured.ShouldBeFalse();
        status.SimulatorAvailable.ShouldBeTrue();
        status.Drivers.Select(d => d.Code).ShouldBe([ScaleDrivers.Toledo8217Code, ScaleDrivers.CasCode, ScaleDrivers.GenericCode]);
        status.Drivers[0].ShouldBe(new ScaleDriverInfo(ScaleDrivers.Toledo8217Code, "Toledo 8217 (Mettler Toledo y compatibles)", 9600, 7, "EVEN", 1, "REQUEST", "W"));
    }

    [Fact]
    public async Task Lee_el_peso_estable_en_kg_y_gramos()
    {
        await using var agent = new AgentFactory();
        var client = await ConfiguredAsync(agent);
        await PutWeightAsync(client, 0.700m);

        var weight = await WeightAsync(client);

        (weight.Status, weight.WeightKg, weight.Grams, weight.Stable, weight.Display).ShouldBe((ScaleWeightDto.Ok, (decimal?)0.700m, (int?)700, true, "0,700 kg"));
        var status = (await client.GetFromJsonAsync<ScaleStatusDto>("/scale/status", Json, Ct))!;
        status.Configured.ShouldBeTrue();
        status.Simulated.ShouldBeTrue();
        status.LastReading!.WeightKg.ShouldBe(0.700m);
    }

    [Theory]
    [InlineData(1.2, false, false, ScaleWeightDto.Unstable)]
    [InlineData(0, true, false, ScaleWeightDto.ZeroWeight)]
    [InlineData(-0.05, true, false, ScaleWeightDto.NegativeWeight)]
    [InlineData(40, true, true, ScaleWeightDto.OverloadWeight)]
    public async Task Inestable_cero_negativo_y_sobrepeso(decimal kg, bool stable, bool overload, string expected)
    {
        await using var agent = new AgentFactory();
        var client = await ConfiguredAsync(agent);
        await PutWeightAsync(client, kg, stable, overload);

        var weight = await WeightAsync(client);

        weight.Status.ShouldBe(expected);
        weight.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Tara_por_software_descuenta_la_bandeja_y_se_puede_quitar()
    {
        await using var agent = new AgentFactory();
        var client = await ConfiguredAsync(agent);
        await PutWeightAsync(client, 0.050m);

        var tare = await client.PostAsync("/scale/tare", Body(new ScaleTareRequest()), Ct);
        tare.StatusCode.ShouldBe(HttpStatusCode.OK, await tare.Content.ReadAsStringAsync(Ct));
        var afterTare = (await tare.Content.ReadFromJsonAsync<ScaleWeightDto>(Json, Ct))!;
        (afterTare.TareKg, afterTare.WeightKg, afterTare.Status).ShouldBe((0.050m, (decimal?)0m, ScaleWeightDto.ZeroWeight));

        await PutWeightAsync(client, 0.750m);
        var net = await WeightAsync(client);
        (net.WeightKg, net.GrossKg, net.TareKg).ShouldBe(((decimal?)0.700m, (decimal?)0.750m, 0.050m));

        // Sin la bandeja la báscula marca menos que la tara: negativo.
        await PutWeightAsync(client, 0m);
        (await WeightAsync(client)).Status.ShouldBe(ScaleWeightDto.NegativeWeight);

        var cleared = await client.PostAsync("/scale/tare", Body(new ScaleTareRequest(Clear: true)), Ct);
        (await cleared.Content.ReadFromJsonAsync<ScaleWeightDto>(Json, Ct))!.TareKg.ShouldBe(0m);
    }

    [Fact]
    public async Task No_toma_la_tara_con_el_peso_en_movimiento()
    {
        await using var agent = new AgentFactory();
        var client = await ConfiguredAsync(agent);
        await PutWeightAsync(client, 0.3m, stable: false);

        var response = await client.PostAsync("/scale/tare", Body(new ScaleTareRequest()), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(response)).ShouldBe(ScaleOutcome.TareUnstable);
    }

    [Fact]
    public async Task Si_la_bascula_no_responde_devuelve_503()
    {
        await using var agent = new AgentFactory();
        var client = await ConfiguredAsync(agent);
        await PutWeightAsync(client, 1m, silent: true);

        var response = await client.GetAsync("/scale/weight", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await CodeAsync(response)).ShouldBe(ScaleOutcome.Unavailable);
        (await client.GetFromJsonAsync<ScaleStatusDto>("/scale/status", Json, Ct))!.LastError!.ShouldContain("no respondió");
    }

    [Fact]
    public async Task La_configuracion_se_guarda_y_sobrevive_a_un_reinicio()
    {
        var folder = TempDirectory();
        var file = Path.Combine(folder, "scale.json");
        try
        {
            await using (var first = new AgentFactory(new Dictionary<string, string?> { ["Agent:ScaleConfigFile"] = file }))
            {
                await ConfiguredAsync(first);
                File.Exists(file).ShouldBeTrue();
            }

            await using var second = new AgentFactory(new Dictionary<string, string?> { ["Agent:ScaleConfigFile"] = file });
            var status = (await second.CreateClient().GetFromJsonAsync<ScaleStatusDto>("/scale/status", Json, Ct))!;
            status.Configured.ShouldBeTrue();
            status.Driver.ShouldBe(ScaleDrivers.SimulatedCode);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Probar_aplica_sin_guardar_y_deshabilitar_quita_la_bascula()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateClient();
        await PutWeightAsync(client, 2.5m);

        var test = await client.PostAsync("/scale/test", Body(Simulated), Ct);
        test.StatusCode.ShouldBe(HttpStatusCode.OK, await test.Content.ReadAsStringAsync(Ct));
        (await test.Content.ReadFromJsonAsync<ScaleWeightDto>(Json, Ct))!.WeightKg.ShouldBe(2.5m);
        File.Exists(agent.ScaleConfigFile).ShouldBeFalse();

        (await client.PutAsync("/scale/config", Body(Simulated with { Enabled = false }), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/scale/weight", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Configuracion_invalida_y_simulador_apagado_fuera_de_desarrollo()
    {
        await using var agent = new AgentFactory(new Dictionary<string, string?> { ["Agent:EnableScaleSimulator"] = "false" });
        var client = agent.CreateClient();

        var badPort = await client.PutAsync("/scale/config", Body(new ScaleSettings { Driver = "CAS", Port = "LPT1" }), Ct);
        badPort.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(badPort)).ShouldBe(ScaleEndpoints.InvalidScale);

        var simulated = await client.PutAsync("/scale/config", Body(Simulated), Ct);
        simulated.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(simulated)).ShouldBe(ScaleEndpoints.InvalidScale);

        var simulator = await client.PostAsync("/scale/simulator", Body(new ScaleSimulatorRequest(1m)), Ct);
        simulator.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(simulator)).ShouldBe(ScaleEndpoints.SimulatorDisabled);
    }

    [Fact]
    public async Task Un_puerto_que_no_existe_responde_503_con_mensaje_claro()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateClient();
        (await client.PutAsync("/scale/config", Body(new ScaleSettings { Driver = "TOLEDO_8217", Port = "COM239", TimeoutMs = 200 }), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await client.GetAsync("/scale/weight", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await CodeAsync(response)).ShouldBe(ScaleOutcome.Unavailable);
    }

    [Fact]
    public async Task El_flujo_en_vivo_envia_eventos_weight()
    {
        await using var agent = new AgentFactory();
        var client = await ConfiguredAsync(agent);
        await PutWeightAsync(client, 0.7m);

        var response = await client.GetAsync("/scale/stream?seconds=1", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.ShouldContain("event: weight");
        text.ShouldContain("\"status\":\"OK\"");
        text.ShouldContain("\"weightKg\":0.7");
    }

    [Fact]
    public async Task La_interfaz_de_caja_puede_configurar_con_PUT_y_una_pagina_cualquiera_no()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/scale/config");
        preflight.Headers.Add("Origin", UiOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        var response = await client.SendAsync(preflight, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Methods").Single().ShouldContain("PUT");

        using var web = new HttpRequestMessage(HttpMethod.Get, "/scale/weight");
        web.Headers.Add("Origin", "https://tienda-falsa.example");
        var rejected = await client.SendAsync(web, Ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(rejected)).ShouldBe(LocalRequestGuard.OriginNotAllowed);
    }
}
