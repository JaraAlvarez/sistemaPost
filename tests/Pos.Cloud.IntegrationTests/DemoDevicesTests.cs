using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Licensing.Application;
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
    public async Task Un_inicio_anterior_a_la_compilacion_o_a_la_salida_del_registro_no_es_creible()
    {
        var client = cloud.Factory.CreateClient();
        var ring = await RingAsync(client);
        var seed = Guid.NewGuid().ToString("N");
        var built = DateTimeOffset.UtcNow.AddDays(-3);

        // Reloj atrasado al instalar (2019): cuenta desde la compilación que manda la tienda.
        var device = DeviceFingerprint.FromHardware($"PLACA-{seed}", $"DISCO-{seed}", $"MAQUINA-{seed}");
        (await RegisterAsync(client, ring, device, new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero), built)).StartedAt
            .ShouldBe(built, TimeSpan.FromMilliseconds(1));

        // Sin fecha de compilación: desde la salida del registro (⚙️ Licensing:DemoDevices:Since).
        var other = DeviceFingerprint.FromHardware($"PLACA2-{seed}", $"DISCO2-{seed}", $"MAQUINA2-{seed}");
        (await RegisterAsync(client, ring, other, new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero))).StartedAt
            .ShouldBe(new DemoDevicesOptions().Since);
    }

    [Fact]
    public async Task Con_la_placa_generica_basta_el_disco()
    {
        var client = cloud.Factory.CreateClient();
        var ring = await RingAsync(client);
        var seed = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.UtcNow.AddDays(-5);
        var first = DeviceFingerprint.FromHardware("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", $"DISCO-{seed}", $"MAQUINA-{seed}");
        (await RegisterAsync(client, ring, first, started)).StartedAt.ShouldBe(started, TimeSpan.FromMilliseconds(1));

        // Windows reinstalado (otro MachineGuid): mismo disco, mismo equipo aunque la placa no cuente.
        var reinstalled = DeviceFingerprint.FromHardware("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", $"DISCO-{seed}", "OTRA-" + seed);
        (await RegisterAsync(client, ring, reinstalled, null)).StartedAt.ShouldBe(started, TimeSpan.FromMilliseconds(1));

        // Otro PC del mismo modelo (misma placa genérica, otro disco): su propia demo.
        var sibling = DeviceFingerprint.FromHardware("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", $"DISCO-OTRO-{seed}", $"MAQUINA-OTRA-{seed}");
        (await RegisterAsync(client, ring, sibling, null)).StartedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Soporte_reinicia_la_demo_del_equipo_y_el_token_lo_dice()
    {
        var client = cloud.Factory.CreateClient();
        var ring = await RingAsync(client);
        var seed = Guid.NewGuid().ToString("N");
        var device = DeviceFingerprint.FromHardware($"PLACA-{seed}", $"DISCO-{seed}", $"MAQUINA-{seed}");
        var old = DateTimeOffset.UtcNow.AddDays(-12);
        (await RegisterAsync(client, ring, device, old)).Reset.ShouldBeFalse();

        var code = DemoDeviceIdentity.SupportCode(device);
        var found = await PortalApi.GetAsync<List<DemoDeviceDto>>(cloud.Superadmin, $"/admin/demo-devices?code={code}");
        var row = found.ShouldHaveSingleItem();
        row.SupportCode.ShouldBe(code);

        // Soporte (sin el permiso) no puede; el superadministrador sí, con motivo, y queda auditado.
        var (supportClient, _) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, PortalRoles.Support);
        using (var denied = await supportClient.PostAsJsonAsync($"/admin/demo-devices/{row.Id}/reset", new { reason = "Reloj atrasado" }, PortalApi.Json, Ct))
        {
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var reset = await cloud.Superadmin.PostAsJsonAsync($"/admin/demo-devices/{row.Id}/reset", new { reason = "Reloj atrasado al instalar" }, PortalApi.Json, Ct))
        {
            reset.IsSuccessStatusCode.ShouldBeTrue(await reset.Content.ReadAsStringAsync(Ct));
        }

        var after = await RegisterAsync(client, ring, device, old); // la tienda sigue recordando el inicio viejo
        after.Reset.ShouldBeTrue();
        after.StartedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));

        await using var connection = new NpgsqlConnection(cloud.Factory.Database.SuperuserConnectionString);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'DEMO_DEVICE_RESET' AND entity_id = @id", new { id = row.Id }))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Hay_cupo_diario_de_equipos_nuevos_por_ip_y_limpieza_de_los_vistos_una_vez()
    {
        var ip = IPAddress.Parse($"203.0.113.{Random.Shared.Next(1, 250)}");
        var seed = Guid.NewGuid().ToString("N");
        var limit = new DemoDevicesOptions().NewDevicesPerIpPerDay;
        for (var i = 0; i <= limit; i++)
        {
            await using var scope = cloud.Factory.Services.CreateAsyncScope();
            var device = DeviceFingerprint.FromHardware($"P{i}-{seed}", $"D{i}-{seed}", $"M{i}-{seed}");
            var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(
                new RegisterDemoDeviceCommand(new DemoDeviceRequest(device.ToString(), null), ip), Ct);
            if (i < limit)
            {
                result.IsSuccess.ShouldBeTrue();
            }
            else
            {
                result.Error.Code.ShouldBe(LicenseErrorCodes.TooManyRequests);
            }
        }

        // Los vistos una sola vez hace más de 90 días se borran; los demás quedan.
        await using var connection = new NpgsqlConnection(cloud.Factory.Database.SuperuserConnectionString);
        var staleId = await connection.ExecuteScalarAsync<Guid>(
            "UPDATE licensing.demo_devices SET last_seen_at = now() - interval '91 days' WHERE registered_ip = @ip::inet AND board_hash = @board RETURNING id",
            new { ip = ip.ToString(), board = DeviceFingerprint.FromHardware($"P0-{seed}", null, null).Board });
        await using (var scope = cloud.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new PurgeStaleDemoDevicesCommand(), Ct)).Value.ShouldBeGreaterThanOrEqualTo(1);
        }

        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM licensing.demo_devices WHERE id = @staleId", new { staleId })).ShouldBe(0);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM licensing.demo_devices WHERE registered_ip = @ip::inet", new { ip = ip.ToString() })).ShouldBe(limit - 1);
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

    private static async Task<DemoClaims> RegisterAsync(
        HttpClient client, LicenseKeyRing ring, DeviceFingerprint device, DateTimeOffset? localStart, DateTimeOffset? buildDate = null)
    {
        using var response = await client.PostAsJsonAsync(
            LicensingRoutes.DemoDevices, new DemoDeviceRequest(device.ToString(), localStart, buildDate), PortalApi.Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<DemoDeviceResponse>(PortalApi.Json, Ct);
        var verification = DemoToken.Verify(body!.Token, ring);
        verification.IsValid.ShouldBeTrue();
        LicenseToken.Verify(body.Token, ring).IsValid.ShouldBeFalse(); // no sirve como token de licencia
        return verification.Claims!;
    }
}
