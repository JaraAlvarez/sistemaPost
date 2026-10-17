using System.Net;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Infrastructure.Persistence;
using Pos.Server.Host.Updates;
using Pos.Updates.Contracts;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase13;

/// <summary>
/// Verificación de coherencia de la Fase 13 (servidor): estado del actualizador, "instalar ahora", historial auditado una sola vez y el
/// paquete de la versión instalada que el servidor ofrece a sus cajas.
/// </summary>
public class UpdateApiTests
{
    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public async Task Estado_instalar_ahora_historial_auditado_y_paquete_para_las_cajas()
    {
        await using var factory = new PosServerFactory();
        var owner = await OwnerClientAsync(factory);

        // Lo que deja el actualizador (servicio aparte) en la carpeta de datos.
        UpdateFiles.WriteState(factory.DataRoot, new UpdaterState("1.3.0", "1.4.0", "Mejoras de caja", true, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow.AddHours(3)));
        UpdateFiles.AppendHistory(factory.DataRoot, new UpdateHistoryEntry(Guid.CreateVersion7(), DateTimeOffset.UtcNow, UpdateOutcomes.Failed, "1.2.0", "1.3.0", "No arrancó."));
        UpdateFiles.AppendHistory(factory.DataRoot, new UpdateHistoryEntry(Guid.CreateVersion7(), DateTimeOffset.UtcNow, UpdateOutcomes.RolledBack, "1.2.0", "1.3.0", "Se volvió a 1.2.0."));

        await RunAuditorAsync(factory);
        await RunAuditorAsync(factory); // idempotente: no duplica
        await using (var connection = new NpgsqlConnection(factory.ConnectionString))
        {
            var actions = (await connection.QueryAsync<string>("SELECT action FROM audit.audit_log WHERE module = 'system' ORDER BY occurred_at")).ToList();
            actions.ShouldBe([UpdateOutcomes.Failed, UpdateOutcomes.RolledBack]);
        }

        var status = await GetAsync<UpdateStatusDto>(owner, "/api/v1/system/updates");
        status.AvailableVersion.ShouldBe("1.4.0");
        status.ReadyToInstall.ShouldBeTrue();
        status.History.Count.ShouldBe(2);

        (await owner.PostAsync("/api/v1/system/updates/install-now", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        File.Exists(Path.Combine(UpdateFiles.Directory(factory.DataRoot), UpdateFiles.InstallNowRequest)).ShouldBeTrue();
        (await GetAsync<UpdateStatusDto>(owner, "/api/v1/system/updates")).InstallRequested.ShouldBeTrue();

        // Las cajas descargan del servidor la versión instalada (sin sesión: el paquete va firmado).
        var anonymous = factory.CreateClient();
        (await anonymous.GetAsync("/api/v1/system/updates/manifest", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await File.WriteAllTextAsync(Path.Combine(UpdateFiles.Directory(factory.DataRoot), UpdateFiles.InstalledManifest), "{\"payload\":\"x\"}", Ct);
        using (var manifest = await anonymous.GetAsync("/api/v1/system/updates/manifest", Ct))
        {
            manifest.StatusCode.ShouldBe(HttpStatusCode.OK);
            // ALTA 4: "instalar ahora" del propietario también llega a las cajas (las que estaban apagadas lo reciben al encender).
            // Ronda 3 (6): con la hora del pedido.
            DateTimeOffset.Parse(manifest.Headers.GetValues(TerminalVersions.InstallNowHeader).Single(), System.Globalization.CultureInfo.InvariantCulture)
                .ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
        }

        // Ronda 3 (4): la caja informa su versión real al pedir el manifiesto; el servidor la guarda normalizada en org.devices y el aviso
        // de cajas desactualizadas la usa contra la versión mínima del manifiesto instalado.
        await using (var connection = new NpgsqlConnection(factory.ConnectionString))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO org.devices (id, company_id, node_id, kind, hostname, machine_fingerprint_hash, app_version, paired_at, status, created_at,
                    created_by, paired_by, credential_hash)
                SELECT gen_random_uuid(), (SELECT id FROM org.companies LIMIT 1), (SELECT id FROM org.nodes LIMIT 1), 'TERMINAL', 'CAJA-PRUEBA',
                       repeat('a', 64), '1.0.0', now(), 'ACTIVE', now(), gen_random_uuid(), gen_random_uuid(), repeat('b', 64)
                """);
        }

        using (var informe = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/updates/manifest"))
        {
            informe.Headers.Add(TerminalVersions.TerminalVersionHeader, "1.3.0-beta.2+abc123");
            informe.Headers.Add(TerminalVersions.TerminalHostHeader, "caja-prueba");
            (await anonymous.SendAsync(informe, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await using (var connection = new NpgsqlConnection(factory.ConnectionString))
        {
            (await connection.ExecuteScalarAsync<string>("SELECT app_version FROM org.devices WHERE hostname = 'CAJA-PRUEBA'")).ShouldBe("1.3.0");
        }

        var payload = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new UpdateManifest("BusinessPost", UpdateChannels.Stable, "1.4.0", "p.zip", new string('a', 64), 1, "1.4.0", DateTimeOffset.UtcNow, "",
                DateTimeOffset.UtcNow.AddDays(90), 10),
            WebJson));
        await File.WriteAllTextAsync(Path.Combine(UpdateFiles.Directory(factory.DataRoot), UpdateFiles.InstalledManifest),
            UpdateSigning.Serialize(new SignedUpdateManifest(payload, "kid", "firma")), Ct);
        var conMinimo = await GetAsync<UpdateStatusDto>(owner, "/api/v1/system/updates");
        conMinimo.MinimumTerminalVersion.ShouldBe("1.4.0");
        conMinimo.OutdatedTerminals!.ShouldBe(["CAJA-PRUEBA (1.3.0)"]);
        await File.WriteAllTextAsync(Path.Combine(UpdateFiles.Directory(factory.DataRoot), UpdateFiles.InstalledManifest), "{\"payload\":\"x\"}", Ct);

        (await anonymous.GetAsync("/api/v1/system/updates", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // ALTA 4: aviso del Tablero con las cajas por debajo de la versión mínima que exige esta versión.
        TerminalVersions.Outdated([("CAJA-1", "1.3.0"), ("CAJA-2", "1.4.0"), ("CAJA-3", null)], "1.4.0")
            .ShouldBe(["CAJA-1 (1.3.0)", "CAJA-3 (versión desconocida)"]);
        TerminalVersions.Outdated([("CAJA-1", "1.3.0")], null).ShouldBeEmpty();
        (await GetAsync<UpdateStatusDto>(owner, "/api/v1/system/updates")).OutdatedTerminals.ShouldBeEmpty("El manifiesto de prueba no trae versión mínima.");

        // El asistente inicial mínimo se sirve también fuera de desarrollo.
        (await (await anonymous.GetAsync("/instalacion", Ct)).Content.ReadAsStringAsync(Ct)).ShouldContain("Configuración inicial");
    }

    private static async Task RunAuditorAsync(PosServerFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var auditor = scope.ServiceProvider.GetServices<IDatabaseReadyHook>().OfType<UpdateHistoryAuditor>().Single();
        await auditor.RunAsync(scope.ServiceProvider, Ct);
    }
}
