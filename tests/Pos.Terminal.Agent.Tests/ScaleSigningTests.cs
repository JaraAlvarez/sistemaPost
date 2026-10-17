using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pos.SharedKernel.Measurement;
using Pos.Terminal.Agent.Api;
using Pos.Terminal.Agent.Scales;
using static Pos.Terminal.Agent.Tests.AgentFactory;

namespace Pos.Terminal.Agent.Tests;

/// <summary>
/// Segunda revisión de la báscula: lecturas firmadas por el agente (D3), retorno a cero (D1) con su vigilante, la prueba que comparte el
/// puerto con la venta (BN3) y que se cierra sola (BN4).
/// </summary>
public class ScaleSigningTests
{
    private static readonly ScaleSettings Simulated = new() { Driver = "SIMULATED", TimeoutMs = 300 };
    private static readonly Guid Terminal = Guid.CreateVersion7();

    private static async Task<HttpClient> ConfiguredAsync(AgentFactory agent, bool pair = true)
    {
        var client = agent.CreateClient();
        (await client.PutAsync("/scale/config", Body(Simulated), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        if (pair)
        {
            (await client.PostAsync("/scale/pairing", Body(new ScalePairingRequest(Terminal)), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        return client;
    }

    private static async Task PutWeightAsync(HttpClient client, decimal kg, bool stable = true) =>
        (await client.PostAsync("/scale/simulator", Body(new ScaleSimulatorRequest(kg, stable)), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

    private static async Task<ScaleWeightDto> WeightAsync(HttpClient client)
    {
        var response = await client.GetAsync("/scale/weight", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ScaleWeightDto>(Json, Ct))!;
    }

    private static async Task AcceptAsync(HttpClient client, Guid readingId) =>
        (await client.PostAsync("/scale/accepted", Body(new ScaleAcceptedRequest(readingId)), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task Vincular_crea_una_clave_y_cada_lectura_llega_firmada_con_ella()
    {
        await using var agent = new AgentFactory();
        var client = await ConfiguredAsync(agent, pair: false);
        await PutWeightAsync(client, 0.700m);

        // Sin vincular: el peso se lee, pero sin firma (el servidor no lo aceptaría).
        (await WeightAsync(client)).SignedReading.ShouldBeNull();

        var pairing = (await (await client.PostAsync("/scale/pairing", Body(new ScalePairingRequest(Terminal)), Ct))
            .Content.ReadFromJsonAsync<ScalePairingDto>(Json, Ct))!;
        pairing.TerminalId.ShouldBe(Terminal);
        File.Exists(Path.Combine(agent.Root, "scale-signing.json")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(agent.Root, "scale-signing.json")).ShouldContain("dpapi:");

        var first = await WeightAsync(client);
        var second = await WeightAsync(client);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pairing.PublicKey), out _);
        foreach (var reading in new[] { first, second })
        {
            var signed = reading.SignedReading.ShouldNotBeNull();
            signed.KeyId.ShouldBe(pairing.KeyId);
            key.VerifyData(Encoding.UTF8.GetBytes(signed.Payload), Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation).ShouldBeTrue();
            var payload = ScaleReadingPayload.TryParse(signed.Payload).ShouldNotBeNull();
            (payload.TerminalId, payload.GrossKg, payload.NetKg, payload.Stable, payload.Status).ShouldBe((Terminal, 0.700m, 0.700m, true, "OK"));
            payload.ReadingId.ShouldBe(reading.ReadingId!.Value);
        }

        // Cada lectura es única (nonce) y la hora del agente siempre avanza.
        first.ReadingId.ShouldNotBe(second.ReadingId);
        second.At.ShouldBeGreaterThan(first.At);

        // Volver a vincular rota la clave: la nueva firma no se verifica con la anterior.
        var rotated = (await (await client.PostAsync("/scale/pairing", Body(new ScalePairingRequest(Terminal)), Ct))
            .Content.ReadFromJsonAsync<ScalePairingDto>(Json, Ct))!;
        rotated.KeyId.ShouldNotBe(pairing.KeyId);
        var third = (await WeightAsync(client)).SignedReading!;
        key.VerifyData(Encoding.UTF8.GetBytes(third.Payload), Convert.FromBase64String(third.Signature), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation).ShouldBeFalse();
    }

    [Fact]
    public async Task La_clave_sobrevive_a_un_reinicio_del_agente()
    {
        var folder = TempDirectory();
        var settings = new Dictionary<string, string?> { ["Agent:ScaleConfigFile"] = Path.Combine(folder, "scale.json") };
        try
        {
            ScalePairingDto pairing;
            await using (var first = new AgentFactory(settings))
            {
                var client = await ConfiguredAsync(first, pair: false);
                pairing = (await (await client.PostAsync("/scale/pairing", Body(new ScalePairingRequest(Terminal)), Ct))
                    .Content.ReadFromJsonAsync<ScalePairingDto>(Json, Ct))!;
            }

            await using var second = new AgentFactory(settings);
            var again = second.CreateClient();
            (await again.GetFromJsonAsync<ScaleStatusDto>("/scale/status", Json, Ct))!.PairedTerminalId.ShouldBe(Terminal);
            (await WeightAsync(again)).SignedReading!.KeyId.ShouldBe(pairing.KeyId);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Despues_de_una_pesada_aceptada_la_siguiente_exige_ver_la_bascula_vacia()
    {
        // CAS continuo (envía el peso también cuando se mueve), para ver que una lectura vacía inestable cuenta.
        var device = new SimulatedScale();
        await using var agent = new AgentFactory();
        await using var host = agent.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IScaleConnectionFactory>(new CasPorts(device))));
        var client = host.CreateClient();
        (await client.PutAsync("/scale/config", Body(new ScaleSettings { Driver = "CAS", Port = "COM6", TimeoutMs = 300 }), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync("/scale/pairing", Body(new ScalePairingRequest(Terminal)), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        device.Set(1.200m);

        var meat = await WeightAsync(client);
        meat.EmptiedSinceLastAccepted.ShouldBeTrue();
        await AcceptAsync(client, meat.ReadingId!.Value);

        // La carne sigue en la báscula, o le ponen el tomate encima (otro peso): no vale (D1, sin la tolerancia de ±5 g).
        (await WeightAsync(client)).EmptiedSinceLastAccepted.ShouldBeFalse();
        device.Set(1.800m);
        var stacked = await WeightAsync(client);
        stacked.EmptiedSinceLastAccepted.ShouldBeFalse();
        ScaleReadingPayload.TryParse(stacked.SignedReading!.Payload)!.EmptiedSinceLastAccepted.ShouldBeFalse();

        // Retiran todo (la lectura vacía todavía se mueve) y ponen el tomate: vale, con la hora de la báscula vacía en la firma.
        device.Set(0.004m, stable: false);
        (await WeightAsync(client)).Status.ShouldBe(ScaleWeightDto.Unstable);
        device.Set(0.800m);
        var tomato = await WeightAsync(client);
        tomato.EmptiedSinceLastAccepted.ShouldBeTrue();
        var payload = ScaleReadingPayload.TryParse(tomato.SignedReading!.Payload)!;
        payload.LastEmptyAt.ShouldNotBeNull();
        payload.LastEmptyAt!.Value.ShouldBeGreaterThan(meat.At);
    }

    /// <summary>Báscula CAS en modo continuo en cualquier puerto.</summary>
    private sealed class CasPorts(SimulatedScale device) : IScaleConnectionFactory
    {
        public IScaleConnection Open(ScaleTarget target) => new SimulatedScaleConnection(device, SimulatedProtocol.Cas, continuous: true);
    }

    [Fact]
    public async Task Eliminar_la_ultima_pesada_libera_el_retorno_a_cero_en_el_agente()
    {
        await using var agent = new AgentFactory();
        var client = await ConfiguredAsync(agent);
        await PutWeightAsync(client, 0.900m);
        await AcceptAsync(client, (await WeightAsync(client)).ReadingId!.Value);
        (await WeightAsync(client)).EmptiedSinceLastAccepted.ShouldBeFalse();

        (await client.PostAsync("/scale/accepted", Body(new ScaleAcceptedRequest(Release: true)), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await WeightAsync(client)).EmptiedSinceLastAccepted.ShouldBeTrue();
        var unknown = await client.PostAsync("/scale/accepted", Body(new ScaleAcceptedRequest(Guid.NewGuid())), Ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Un_paso_rapido_por_cero_lo_ve_el_vigilante_aunque_la_caja_no_lea()
    {
        await using var agent = new AgentFactory();
        var client = await ConfiguredAsync(agent);
        await PutWeightAsync(client, 1.200m);
        await AcceptAsync(client, (await WeightAsync(client)).ReadingId!.Value);

        // La caja no lee: el producto se retira un momento (menos de un segundo) y llega el siguiente.
        await PutWeightAsync(client, 0m);
        await Task.Delay(ScaleEmptyMonitor.Interval * 3, Ct);
        await PutWeightAsync(client, 1.200m);

        (await WeightAsync(client)).EmptiedSinceLastAccepted.ShouldBeTrue();
    }

    [Fact]
    public async Task La_prueba_se_cierra_sola_sin_lecturas()
    {
        await using var agent = new AgentFactory(new Dictionary<string, string?> { ["Agent:ScaleTestIdleTimeout"] = "00:00:00.300" });
        var client = agent.CreateClient();
        (await client.PostAsync("/scale/test", Body(Simulated), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<ScaleStatusDto>("/scale/status", Json, Ct))!.Testing.ShouldBeTrue();

        // Nadie vuelve a llamar al agente: el temporizador la cierra (y libera el puerto).
        await Task.Delay(TimeSpan.FromMilliseconds(1200), Ct);

        (await client.GetFromJsonAsync<ScaleStatusDto>("/scale/status", Json, Ct))!.Testing.ShouldBeFalse();
        var read = await client.GetAsync("/scale/test", Ct);
        read.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(read)).ShouldBe(ScaleOutcome.TestNotStarted);
    }

    [Fact]
    public async Task Prueba_y_venta_en_el_mismo_puerto_comparten_la_conexion_sin_cerrarla()
    {
        var device = new SimulatedScale();
        device.Set(0.700m);
        var ports = new ExclusivePorts(device);
        await using var agent = new AgentFactory();
        await using var host = agent.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IScaleConnectionFactory>(ports)));
        var client = host.CreateClient();
        var real = new ScaleSettings { Driver = "TOLEDO_8217", Port = "COM5", TimeoutMs = 300 };
        (await client.PutAsync("/scale/config", Body(real), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Venta, prueba con la misma configuración, venta y prueba otra vez: nunca "puerto ocupado" y el puerto se abre una sola vez.
        (await WeightAsync(client)).WeightKg.ShouldBe(0.700m);
        (await client.PostAsync("/scale/test", Body(real), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WeightAsync(client)).WeightKg.ShouldBe(0.700m);
        (await client.GetAsync("/scale/test", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WeightAsync(client)).WeightKg.ShouldBe(0.700m);
        ports.Opens.ShouldBe(1);

        // Al detener la prueba la venta sigue con su conexión.
        (await client.PostAsync("/scale/test/stop", Body(new { }), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await WeightAsync(client)).WeightKg.ShouldBe(0.700m);
        ports.Opens.ShouldBe(1);

        // Misma COM con otra velocidad: la prueba toma el puerto y la venta avisa que está en prueba (sin pelear por el puerto).
        (await client.PostAsync("/scale/test", Body(real with { BaudRate = 19200 }), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var busy = await client.GetAsync("/scale/weight", Ct);
        (await CodeAsync(busy)).ShouldBe(ScaleOutcome.InTest);
        (await client.PostAsync("/scale/test/stop", Body(new { }), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await WeightAsync(client)).WeightKg.ShouldBe(0.700m);
    }

    /// <summary>Puertos COM como en Windows: un puerto abierto no se puede abrir otra vez hasta cerrarlo.</summary>
    private sealed class ExclusivePorts(SimulatedScale device) : IScaleConnectionFactory
    {
        private readonly HashSet<string> _open = [];

        public int Opens { get; private set; }

        public IScaleConnection Open(ScaleTarget target)
        {
            lock (_open)
            {
                if (!_open.Add(target.Port))
                {
                    throw new UnauthorizedAccessException($"El puerto {target.Port} está ocupado.");
                }

                Opens++;
            }

            return new Port(this, target.Port, new SimulatedScaleConnection(device, SimulatedProtocol.Toledo8217, continuous: false));
        }

        private void Close(string port)
        {
            lock (_open)
            {
                _open.Remove(port);
            }
        }

        private sealed class Port(ExclusivePorts owner, string name, SimulatedScaleConnection inner) : IScaleConnection
        {
            public void Write(ReadOnlySpan<byte> data) => inner.Write(data);

            public int Read(byte[] buffer, int timeoutMs) => inner.Read(buffer, timeoutMs);

            public void DiscardInput() => inner.DiscardInput();

            public void Dispose()
            {
                inner.Dispose();
                owner.Close(name);
            }
        }
    }
}
