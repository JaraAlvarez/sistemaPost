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
        (await anonymous.GetAsync("/api/v1/system/updates/manifest", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await anonymous.GetAsync("/api/v1/system/updates", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

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
