using System.Net;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pos.SharedKernel.Measurement;
using Pos.Terminal.Agent.Api;
using Pos.Terminal.Agent.Scales;
using static Pos.Terminal.Agent.Tests.AgentFactory;

namespace Pos.Terminal.Agent.Tests;

/// <summary>
/// Revisiones de la báscula: lecturas firmadas por el agente (D3), retorno a cero por el BRUTO (D1, BB1) con su vigilante (BB8), orden
/// por secuencia y no por la hora (BB4), vincular solo desde la interfaz (BB6), simulador sin firma (BB7), clave protegida (BB3), la
/// prueba que comparte el puerto con la venta (BN3) y que se cierra sola (BN4).
/// </summary>
public class ScaleSigningTests
{
    private static readonly ScaleSettings Simulated = new() { Driver = "SIMULATED", TimeoutMs = 300 };
    private static readonly ScaleSettings Toledo = new() { Driver = "TOLEDO_8217", Port = "COM5", TimeoutMs = 300 };
    private static readonly ScaleSettings Cas = new() { Driver = "CAS", Port = "COM6", TimeoutMs = 300 };
    private static readonly Guid Terminal = Guid.CreateVersion7();

    private static async Task<ScaleWeightDto> WeightAsync(HttpClient client)
    {
        var response = await client.GetAsync("/scale/weight", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ScaleWeightDto>(Json, Ct))!;
    }

    private static Task<HttpResponseMessage> PairAsync(HttpClient client, bool fromUi = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/scale/pairing") { Content = Body(new ScalePairingRequest(Terminal)) };
        if (fromUi)
        {
            request.Headers.Add("Origin", UiOrigin);
        }

        return client.SendAsync(request, Ct);
    }

    /// <summary>Prepara una clave pendiente (sin confirmar).</summary>
    private static async Task<ScalePairingDto> PreparedAsync(HttpClient client)
    {
        var response = await PairAsync(client);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ScalePairingDto>(Json, Ct))!;
    }

    private static Task<HttpResponseMessage> CommitAsync(HttpClient client, Guid keyId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/scale/pairing/commit") { Content = Body(new ScalePairingCommitRequest(keyId)) };
        request.Headers.Add("Origin", UiOrigin);
        return client.SendAsync(request, Ct);
    }

    /// <summary>Vincula del todo: prepara la clave y, como si el servidor la hubiera registrado, la confirma.</summary>
    private static async Task<ScalePairingDto> PairedAsync(HttpClient client)
    {
        var pairing = await PreparedAsync(client);
        (await CommitAsync(client, pairing.KeyId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return pairing;
    }

    private static bool Verifies(ScalePairingDto pairing, SignedScaleReading signed)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pairing.PublicKey), out _);
        return key.VerifyData(Encoding.UTF8.GetBytes(signed.Payload), Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static async Task AcceptAsync(HttpClient client, Guid readingId) =>
        (await client.PostAsync("/scale/accepted", Body(new ScaleAcceptedRequest(readingId)), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static ScaleReadingPayload Payload(ScaleWeightDto reading) => ScaleReadingPayload.TryParse(reading.SignedReading.ShouldNotBeNull().Payload).ShouldNotBeNull();

    [Fact]
    public async Task Vincular_crea_una_clave_protegida_y_cada_lectura_llega_firmada_con_ella()
    {
        await using var rig = await Rig.StartAsync(Toledo, pair: false);
        rig.Device.Set(0.700m);

        // Sin vincular: el peso se lee, pero sin firma (el servidor no lo aceptaría).
        (await WeightAsync(rig.Client)).SignedReading.ShouldBeNull();

        var pairing = await PairedAsync(rig.Client);
        pairing.TerminalId.ShouldBe(Terminal);

        // BB3: la clave privada va cifrada con DPAPI de la cuenta del agente y el archivo solo lo leen SYSTEM, Administradores y esa cuenta.
        var file = Path.Combine(rig.Agent.Root, "keys", "scale-signing.json");
        File.ReadAllText(file).ShouldContain("dpapi-user:");
        File.Exists(file + ".tmp").ShouldBeFalse();
        if (OperatingSystem.IsWindows())
        {
            OnlyAgentAndAdministrators(file).ShouldBeTrue();
            OnlyAgentAndAdministrators(Path.GetDirectoryName(file)!).ShouldBeTrue();
        }

        var first = await WeightAsync(rig.Client);
        var second = await WeightAsync(rig.Client);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pairing.PublicKey), out _);
        foreach (var reading in new[] { first, second })
        {
            var signed = reading.SignedReading.ShouldNotBeNull();
            signed.KeyId.ShouldBe(pairing.KeyId);
            key.VerifyData(Encoding.UTF8.GetBytes(signed.Payload), Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation).ShouldBeTrue();
            var payload = Payload(reading);
            (payload.TerminalId, payload.GrossKg, payload.NetKg, payload.Stable, payload.Status).ShouldBe((Terminal, 0.700m, 0.700m, true, "OK"));
            payload.ReadingId.ShouldBe(reading.ReadingId!.Value);
        }

        // Cada lectura es única (nonce) y la secuencia del arranque siempre crece.
        first.ReadingId.ShouldNotBe(second.ReadingId);
        (Payload(first).BootId, Payload(second).Seq > Payload(first).Seq).ShouldBe((Payload(second).BootId, true));

        // Volver a vincular rota la clave: la nueva firma no se verifica con la anterior.
        (await PairedAsync(rig.Client)).KeyId.ShouldNotBe(pairing.KeyId);
        var third = (await WeightAsync(rig.Client)).SignedReading!;
        key.VerifyData(Encoding.UTF8.GetBytes(third.Payload), Convert.FromBase64String(third.Signature), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation).ShouldBeFalse();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool OnlyAgentAndAdministrators(string path)
    {
        var rules = (Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                : new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
            .Cast<FileSystemAccessRule>().ToList();
        using var me = WindowsIdentity.GetCurrent();
        var allowed = new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), me.User! };
        return rules.Count > 0 && rules.All(r => allowed.Contains(r.IdentityReference) && !r.IsInherited);
    }

    [Fact]
    public async Task Vincular_solo_desde_la_interfaz_y_la_bascula_simulada_nunca_se_firma()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateClient();
        (await client.PutAsync("/scale/config", Body(Simulated), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // BB6: un programa del equipo (sin el Origin de la interfaz de la caja) no puede cambiar la clave.
        var local = await PairAsync(client, fromUi: false);
        local.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(local)).ShouldBe(LocalRequestGuard.OriginNotAllowed);

        // BB7: aun vinculado, el peso del simulador no se firma (el servidor no aceptaría un peso inventado).
        await PairedAsync(client);
        (await client.PostAsync("/scale/simulator", Body(new ScaleSimulatorRequest(0.7m)), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var reading = await WeightAsync(client);
        reading.WeightKg.ShouldBe(0.7m);
        reading.SignedReading.ShouldBeNull();
    }

    [Fact]
    public async Task Vincular_sin_confirmar_deja_la_caja_pesando_con_la_clave_vigente()
    {
        // BK1: el agente prepara la clave nueva, pero el registro en el servidor falla o se cancela (no llega la confirmación).
        var folder = TempDirectory();
        var settings = new Dictionary<string, string?> { ["Agent:ScaleConfigFile"] = Path.Combine(folder, "scale.json") };
        try
        {
            ScalePairingDto current;
            await using (var rig = await Rig.StartAsync(Toledo, settings, pair: false))
            {
                rig.Device.Set(0.7m);
                current = await PairedAsync(rig.Client);

                var abandoned = await PreparedAsync(rig.Client);
                var reading = (await WeightAsync(rig.Client)).SignedReading!;
                (reading.KeyId, Verifies(current, reading)).ShouldBe((current.KeyId, true));

                // Otro intento reemplaza la pendiente anterior: confirmar la primera ya no vale.
                var retry = await PreparedAsync(rig.Client);
                (await CommitAsync(rig.Client, abandoned.KeyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
                (await WeightAsync(rig.Client)).SignedReading!.KeyId.ShouldBe(current.KeyId);

                // Una página sin el Origin de la interfaz no puede confirmar.
                var local = await rig.Client.PostAsync("/scale/pairing/commit", Body(new ScalePairingCommitRequest(retry.KeyId)), Ct);
                local.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                _ = await PreparedAsync(rig.Client);
            }

            // Reinicio del agente con una pendiente sin confirmar: se descarta y sigue la vigente.
            await using var again = await Rig.StartAsync(Toledo, settings, pair: false);
            again.Device.Set(0.7m);
            var after = (await WeightAsync(again.Client)).SignedReading!;
            (after.KeyId, Verifies(current, after)).ShouldBe((current.KeyId, true));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task La_clave_del_formato_anterior_se_borra_al_arrancar()
    {
        // BK3: el archivo viejo (DPAPI de máquina con entropía fija, fuera de "keys") se borra y la caja queda sin vincular.
        var folder = TempDirectory();
        Directory.CreateDirectory(folder);
        var legacy = Path.Combine(folder, "scale-signing.json");
        File.WriteAllText(legacy, "{\"terminalId\":\"" + Terminal + "\",\"keyId\":\"" + Guid.NewGuid() + "\",\"privateKey\":\"dpapi:AAAA\"}");
        try
        {
            await using var rig = await Rig.StartAsync(Toledo, new Dictionary<string, string?> { ["Agent:ScaleConfigFile"] = Path.Combine(folder, "scale.json") }, pair: false);
            File.Exists(legacy).ShouldBeFalse();
            (await rig.Client.GetFromJsonAsync<ScaleStatusDto>("/scale/status", Json, Ct))!.PairedTerminalId.ShouldBeNull();
            rig.Device.Set(0.7m);
            (await WeightAsync(rig.Client)).SignedReading.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Bascula_generica_con_tara_fisica_marcada_como_neta_no_cuenta_como_vacia()
    {
        // BK4: la báscula genérica envía "NET 0.000 kg" con la tara de su tecla: no es el bruto y no cuenta como vacía.
        var port = new ScriptedContinuous { Frame = "  1.200 kg" };
        await using var rig = await Rig.StartAsync(new ScaleSettings { Driver = "GENERIC", Port = "COM7", TimeoutMs = 300 }, factory: new ContinuousPorts(port));
        await AcceptAsync(rig.Client, (await WeightAsync(rig.Client)).ReadingId!.Value);

        port.Frame = "NET   0.000 kg";
        var net = await WeightAsync(rig.Client);
        (net.ScaleNet, net.EmptiedSinceLastAccepted).ShouldBe((true, false));

        port.Frame = "      0.000 kg";
        (await WeightAsync(rig.Client)).EmptiedSinceLastAccepted.ShouldBeTrue();
    }

    [Fact]
    public async Task La_clave_sobrevive_a_un_reinicio_del_agente()
    {
        var folder = TempDirectory();
        var settings = new Dictionary<string, string?> { ["Agent:ScaleConfigFile"] = Path.Combine(folder, "scale.json") };
        try
        {
            ScalePairingDto pairing;
            Guid firstBoot;
            await using (var first = await Rig.StartAsync(Toledo, settings, pair: false))
            {
                first.Device.Set(0.5m);
                pairing = await PairedAsync(first.Client);
                firstBoot = Payload(await WeightAsync(first.Client)).BootId;
            }

            await using var second = await Rig.StartAsync(Toledo, settings, pair: false);
            second.Device.Set(0.5m);
            (await second.Client.GetFromJsonAsync<ScaleStatusDto>("/scale/status", Json, Ct))!.PairedTerminalId.ShouldBe(Terminal);
            var again = await WeightAsync(second.Client);
            again.SignedReading!.KeyId.ShouldBe(pairing.KeyId);

            // Otro arranque: otro identificador (el servidor sabe que la secuencia empezó de nuevo).
            Payload(again).BootId.ShouldNotBe(firstBoot);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Despues_de_una_pesada_aceptada_la_siguiente_exige_ver_la_bascula_vacia_por_el_bruto()
    {
        // CAS continuo (envía el peso también cuando se mueve), para ver que una lectura vacía inestable y con deriva negativa cuenta.
        await using var rig = await Rig.StartAsync(Cas);
        rig.Device.Set(1.200m);

        var meat = await WeightAsync(rig.Client);
        meat.EmptiedSinceLastAccepted.ShouldBeTrue();
        await AcceptAsync(rig.Client, meat.ReadingId!.Value);

        // La carne sigue en la báscula, o le ponen el tomate encima (otro peso): no vale.
        (await WeightAsync(rig.Client)).EmptiedSinceLastAccepted.ShouldBeFalse();
        rig.Device.Set(1.800m);
        var stacked = await WeightAsync(rig.Client);
        stacked.EmptiedSinceLastAccepted.ShouldBeFalse();
        Payload(stacked).EmptiedSinceLastAccepted.ShouldBeFalse();

        // Retiran todo: la báscula marca -0,010 kg moviéndose (deriva negativa dentro de la banda): cuenta como vacía (BB5).
        rig.Device.Set(-0.010m, stable: false);
        (await WeightAsync(rig.Client)).Status.ShouldBe(ScaleWeightDto.Unstable);
        rig.Device.Set(0.800m);
        var tomato = await WeightAsync(rig.Client);
        tomato.EmptiedSinceLastAccepted.ShouldBeTrue();
        Payload(tomato).LastEmptySeq!.Value.ShouldBeGreaterThan(Payload(meat).Seq);
    }

    [Fact]
    public async Task Con_tara_manual_el_neto_en_cero_no_cuenta_como_bascula_vacia()
    {
        // BB1: A encima → Ins → escanear → Esc → escanear: el neto da 0, pero el producto sigue en la báscula.
        await using var rig = await Rig.StartAsync(Toledo);
        rig.Device.Set(0.700m);
        await AcceptAsync(rig.Client, (await WeightAsync(rig.Client)).ReadingId!.Value);

        var tare = await rig.Client.PostAsync("/scale/tare", Body(new ScaleTareRequest()), Ct);
        var tared = (await tare.Content.ReadFromJsonAsync<ScaleWeightDto>(Json, Ct))!;
        (tared.Status, tared.WeightKg, tared.EmptiedSinceLastAccepted).ShouldBe((ScaleWeightDto.ZeroWeight, (decimal?)0m, false));
        Payload(await WeightAsync(rig.Client)).EmptiedSinceLastAccepted.ShouldBeFalse();

        // Esc quita la tara: sigue sin estar vacía (el bruto es el del producto).
        await rig.Client.PostAsync("/scale/tare", Body(new ScaleTareRequest(Clear: true)), Ct);
        (await WeightAsync(rig.Client)).EmptiedSinceLastAccepted.ShouldBeFalse();

        // Se retira el producto: ahora sí.
        rig.Device.Set(0m);
        (await WeightAsync(rig.Client)).EmptiedSinceLastAccepted.ShouldBeTrue();
    }

    [Fact]
    public async Task Con_la_tara_de_la_bascula_la_trama_neta_no_cuenta_como_bascula_vacia()
    {
        // BB1: la báscula (tecla de tara física) envía "00.000N": no trae el bruto real, así que no se marca vacía.
        var port = new ScriptedToledo { Frame = "\u000200.700" };
        await using var rig = await Rig.StartAsync(Toledo, factory: new ScriptedPorts(port));
        await AcceptAsync(rig.Client, (await WeightAsync(rig.Client)).ReadingId!.Value);

        port.Frame = "\u000200.000N";
        var net = await WeightAsync(rig.Client);
        (net.Status, net.ScaleNet, net.EmptiedSinceLastAccepted).ShouldBe((ScaleWeightDto.ZeroWeight, true, false));

        port.Frame = "\u000200.000";
        (await WeightAsync(rig.Client)).EmptiedSinceLastAccepted.ShouldBeTrue();
    }

    [Fact]
    public async Task Eliminar_la_ultima_pesada_libera_el_retorno_a_cero_en_el_agente()
    {
        await using var rig = await Rig.StartAsync(Toledo);
        rig.Device.Set(0.900m);
        await AcceptAsync(rig.Client, (await WeightAsync(rig.Client)).ReadingId!.Value);
        (await WeightAsync(rig.Client)).EmptiedSinceLastAccepted.ShouldBeFalse();

        (await rig.Client.PostAsync("/scale/accepted", Body(new ScaleAcceptedRequest(Release: true)), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await WeightAsync(rig.Client)).EmptiedSinceLastAccepted.ShouldBeTrue();
        var unknown = await rig.Client.PostAsync("/scale/accepted", Body(new ScaleAcceptedRequest(Guid.NewGuid())), Ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Un_paso_rapido_por_cero_lo_ve_el_vigilante_aunque_la_caja_no_lea()
    {
        await using var rig = await Rig.StartAsync(Toledo);
        rig.Device.Set(1.200m);
        await AcceptAsync(rig.Client, (await WeightAsync(rig.Client)).ReadingId!.Value);

        // La caja no lee: el producto se retira un momento (menos de un segundo) y llega el siguiente.
        rig.Device.Set(0m);
        await Task.Delay(ScaleEmptyMonitor.Interval * 3, Ct);
        rig.Device.Set(1.200m);

        (await WeightAsync(rig.Client)).EmptiedSinceLastAccepted.ShouldBeTrue();
    }

    [Fact]
    public async Task Cambiar_A_por_B_antes_de_que_la_caja_confirme_la_linea_no_deja_el_aviso_sin_motivo()
    {
        // BB8: el vigilante empieza con la lectura (no espera la confirmación de la línea): el cambio A → B pasa por la báscula vacía.
        await using var rig = await Rig.StartAsync(Toledo);
        rig.Device.Set(1.200m);
        var a = await WeightAsync(rig.Client);
        rig.Device.Set(0m);
        await Task.Delay(ScaleEmptyMonitor.Interval * 3, Ct);
        rig.Device.Set(0.800m);

        // La caja confirma la línea de A después del cambio: la lectura de B ya cuenta como "vacía después de A".
        await AcceptAsync(rig.Client, a.ReadingId!.Value);
        (await WeightAsync(rig.Client)).EmptiedSinceLastAccepted.ShouldBeTrue();
        ScaleService.MonitorAfterAccepted.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Un_salto_del_reloj_del_agente_no_traba_las_lecturas()
    {
        // BB4: la hora es la del reloj del equipo (sin "máximo guardado"); el orden va por la secuencia.
        await using var rig = await Rig.StartAsync(Toledo);
        rig.Device.Set(1.000m);
        var before = await WeightAsync(rig.Client);
        await AcceptAsync(rig.Client, before.ReadingId!.Value);

        rig.Clock.Offset = TimeSpan.FromHours(-1);
        rig.Device.Set(0m);
        await WeightAsync(rig.Client);
        rig.Device.Set(0.600m);
        var after = await WeightAsync(rig.Client);

        after.At.ShouldBeLessThan(before.At - TimeSpan.FromMinutes(59));
        Payload(after).Seq.ShouldBeGreaterThan(Payload(before).Seq);
        after.EmptiedSinceLastAccepted.ShouldBeTrue();
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
        await using var rig = await Rig.StartAsync(Toledo, factory: ports, pair: false);
        var client = rig.Client;

        // Venta, prueba con la misma configuración, venta y prueba otra vez: nunca "puerto ocupado" y el puerto se abre una sola vez.
        (await WeightAsync(client)).WeightKg.ShouldBe(0.700m);
        (await client.PostAsync("/scale/test", Body(Toledo), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WeightAsync(client)).WeightKg.ShouldBe(0.700m);
        (await client.GetAsync("/scale/test", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WeightAsync(client)).WeightKg.ShouldBe(0.700m);
        ports.Opens.ShouldBe(1);

        // Al detener la prueba la venta sigue con su conexión.
        (await client.PostAsync("/scale/test/stop", Body(new { }), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await WeightAsync(client)).WeightKg.ShouldBe(0.700m);
        ports.Opens.ShouldBe(1);

        // Misma COM con otra velocidad: la prueba toma el puerto y la venta avisa que está en prueba (sin pelear por el puerto).
        (await client.PostAsync("/scale/test", Body(Toledo with { BaudRate = 19200 }), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var busy = await client.GetAsync("/scale/weight", Ct);
        (await CodeAsync(busy)).ShouldBe(ScaleOutcome.InTest);
        (await client.PostAsync("/scale/test/stop", Body(new { }), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await WeightAsync(client)).WeightKg.ShouldBe(0.700m);
    }

    /// <summary>Agente con una báscula "real" (Toledo en COM5 o CAS en COM6) hecha con el simulador por dentro, vinculado y con reloj movible.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private Rig(AgentFactory agent, WebApplicationFactory<Program> host, SimulatedScale device, MovableClock clock)
        {
            Agent = agent;
            Host = host;
            Device = device;
            Clock = clock;
            Client = host.CreateClient();
        }

        public AgentFactory Agent { get; }

        public WebApplicationFactory<Program> Host { get; }

        public SimulatedScale Device { get; }

        public MovableClock Clock { get; }

        public HttpClient Client { get; }

        public static async Task<Rig> StartAsync(
            ScaleSettings settings, IDictionary<string, string?>? options = null, bool pair = true, IScaleConnectionFactory? factory = null)
        {
            var device = new SimulatedScale();
            var clock = new MovableClock();
            var protocol = settings.Driver == "CAS" ? SimulatedProtocol.Cas : SimulatedProtocol.Toledo8217;
            var agent = new AgentFactory(options);
            var host = agent.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IScaleConnectionFactory>(factory ?? new DevicePorts(device, protocol));
                s.AddSingleton<TimeProvider>(clock);
            }));
            var rig = new Rig(agent, host, device, clock);
            (await rig.Client.PutAsync("/scale/config", Body(settings), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            if (pair)
            {
                await PairedAsync(rig.Client);
            }

            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Agent.DisposeAsync();
        }
    }

    /// <summary>Reloj del sistema con un desfase que la prueba puede mover (salto del reloj del equipo).</summary>
    private sealed class MovableClock : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
    }

    /// <summary>Báscula simulada en cualquier puerto (Toledo por pedido, CAS continuo).</summary>
    private sealed class DevicePorts(SimulatedScale device, SimulatedProtocol protocol) : IScaleConnectionFactory
    {
        public IScaleConnection Open(ScaleTarget target) => new SimulatedScaleConnection(device, protocol, continuous: protocol == SimulatedProtocol.Cas);
    }

    /// <summary>Toledo que responde a "W" con la trama que diga la prueba (p. ej. neta "00.000N" con la tara de la báscula).</summary>
    private sealed class ScriptedToledo : IScaleConnection
    {
        private readonly Queue<byte> _pending = new();

        public string Frame { get; set; } = "\u000200.000";

        public void Write(ReadOnlySpan<byte> data)
        {
            lock (_pending)
            {
                foreach (var b in Encoding.ASCII.GetBytes(Frame + "\r"))
                {
                    _pending.Enqueue(b);
                }
            }
        }

        public int Read(byte[] buffer, int timeoutMs)
        {
            lock (_pending)
            {
                var n = 0;
                while (n < buffer.Length && _pending.Count > 0)
                {
                    buffer[n++] = _pending.Dequeue();
                }

                return n;
            }
        }

        public void DiscardInput()
        {
            lock (_pending)
            {
                _pending.Clear();
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedPorts(ScriptedToledo port) : IScaleConnectionFactory
    {
        public IScaleConnection Open(ScaleTarget target) => port;
    }

    /// <summary>Báscula en modo continuo que envía siempre la trama que diga la prueba.</summary>
    private sealed class ScriptedContinuous : IScaleConnection
    {
        public string Frame { get; set; } = "0.000 kg";

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public int Read(byte[] buffer, int timeoutMs)
        {
            Thread.Sleep(5);
            var bytes = Encoding.ASCII.GetBytes(Frame + "\r\n");
            bytes.CopyTo(buffer, 0);
            return bytes.Length;
        }

        public void DiscardInput()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ContinuousPorts(ScriptedContinuous port) : IScaleConnectionFactory
    {
        public IScaleConnection Open(ScaleTarget target) => port;
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
