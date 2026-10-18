using System.Net;
using System.Net.Http.Json;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// Demo por equipo (docs/fases/fase-demo-por-equipo.md): la nube guarda el primer inicio de la demo por huella (2 de 3) y lo devuelve
/// firmado. Reinstalar Windows o cambiar el disco no la reinicia; otro equipo empieza la suya.
/// </summary>
public class DemoDevicesTests(CloudFixture cloud) : IClassFixture<CloudFixture>
{
    [Fact]
    public async Task El_primer_inicio_de_la_demo_se_recuerda_por_equipo_y_va_firmado()
    {
        var client = cloud.Factory.CreateClient();
        var ring = await RingAsync(client);
        var seed = Guid.NewGuid().ToString("N");
        var original = DeviceFingerprint.FromHardware($"PLACA-{seed}", $"DISCO-{seed}", $"MAQUINA-{seed}");
        var started = DateTimeOffset.UtcNow.AddDays(-12);

        var first = await RegisterAsync(client, ring, original, started);
        first.StartedAt.ShouldBe(started, TimeSpan.FromMilliseconds(1));
        DeviceFingerprint.TryParse(first.DeviceFingerprint, out var signed).ShouldBeTrue();
        signed!.Matches(original).ShouldBeTrue();

        // Windows reinstalado (otro MachineGuid) y datos borrados: la tienda cree que la demo empieza hoy; la nube devuelve el inicio real.
        var reinstalled = DeviceFingerprint.FromHardware($"PLACA-{seed}", $"DISCO-{seed}", "MAQUINA-NUEVA-" + seed);
        (await RegisterAsync(client, ring, reinstalled, DateTimeOffset.UtcNow)).StartedAt.ShouldBe(started, TimeSpan.FromMilliseconds(1));

        // Disco cambiado después (placa + MachineGuid nuevo siguen coincidiendo con lo último visto).
        var newDisk = DeviceFingerprint.FromHardware($"PLACA-{seed}", "DISCO-NUEVO-" + seed, "MAQUINA-NUEVA-" + seed);
        (await RegisterAsync(client, ring, newDisk, null)).StartedAt.ShouldBe(started, TimeSpan.FromMilliseconds(1));

        // Un inicio futuro no cuenta; uno anterior lo adelanta.
        (await RegisterAsync(client, ring, original, DateTimeOffset.UtcNow.AddDays(5))).StartedAt.ShouldBe(started, TimeSpan.FromMilliseconds(1));
        var earlier = started.AddDays(-3);
        (await RegisterAsync(client, ring, original, earlier)).StartedAt.ShouldBe(earlier, TimeSpan.FromMilliseconds(1));

        // Otro equipo (placa y disco distintos) empieza su propia demo.
        var other = DeviceFingerprint.FromHardware("OTRA-PLACA-" + seed, "OTRO-DISCO-" + seed, $"MAQUINA-{seed}");
        (await RegisterAsync(client, ring, other, null)).StartedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Una_huella_invalida_se_rechaza_con_codigo_estable()
    {
        var client = cloud.Factory.CreateClient();
        using var response = await client.PostAsJsonAsync(LicensingRoutes.DemoDevices, new DemoDeviceRequest("fp1.-.-.-", null), PortalApi.Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PortalApi.ErrorCodeAsync(response)).ShouldBe(LicenseErrorCodes.FingerprintInvalid);
    }

    private static async Task<LicenseKeyRing> RingAsync(HttpClient client)
    {
        var keys = await client.GetFromJsonAsync<PublicKeysResponse>(LicensingRoutes.PublicKeys, PortalApi.Json, Ct);
        return new LicenseKeyRing(keys!.Keys.Select(k => LicensePublicKey.TryParse(k.X, out var key) ? key : null).OfType<LicensePublicKey>());
    }

    private static async Task<DemoClaims> RegisterAsync(HttpClient client, LicenseKeyRing ring, DeviceFingerprint device, DateTimeOffset? localStart)
    {
        using var response = await client.PostAsJsonAsync(LicensingRoutes.DemoDevices, new DemoDeviceRequest(device.ToString(), localStart), PortalApi.Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<DemoDeviceResponse>(PortalApi.Json, Ct);
        var verification = DemoToken.Verify(body!.Token, ring);
        verification.IsValid.ShouldBeTrue();
        LicenseToken.Verify(body.Token, ring).IsValid.ShouldBeFalse(); // no sirve como token de licencia
        return verification.Claims!;
    }
}
