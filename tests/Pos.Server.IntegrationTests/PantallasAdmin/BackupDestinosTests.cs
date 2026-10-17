using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Backup.Contracts;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.PantallasAdmin;

/// <summary>
/// Pantalla de destinos de los backups (docs/fases/pantallas-faltantes-propuesta.md §1): resumen con la carpeta LOCAL y el último backup,
/// comprobación de la carpeta antes de guardar (existe o se puede crear, se puede escribir, no es la LOCAL, red por UNC), rechazo al
/// guardar un destino activo que no sirve, auditoría y permisos.
/// </summary>
public class BackupDestinosTests
{
    private static object Destination(string kind, string? path, bool active = true, int keepDaily = 7, string name = "USB de la tienda") => new
    {
        kind, name, path, volumeLabel = (string?)null, endpoint = (string?)null, region = (string?)null, bucket = (string?)null, prefix = (string?)null,
        accessKey = (string?)null, secretKey = (string?)null, onScheduled = false, onNightly = true, onClosing = true, onManual = true,
        keepDaily, keepWeekly = 4, keepMonthly = 12, isActive = active,
    };

    private static Task<BackupFolderCheckDto> CheckAsync(HttpClient client, string kind, string? path) =>
        PostAsync<BackupFolderCheckDto>(client, "/api/v1/backups/destinations/check-folder", new { kind, path });

    [Fact]
    public async Task La_carpeta_se_comprueba_antes_de_guardar_y_los_cambios_se_auditan()
    {
        await using var factory = new SingleFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var owner = shop.Owner;

        // Resumen: carpeta LOCAL fija y sin backups todavía (el último backup y su resultado aparecen al haber alguno).
        var overview = await GetAsync<BackupOverviewDto>(owner, "/api/v1/backups/overview");
        (overview.LastRun, overview.LastSuccessful).ShouldBe(((BackupRunDto?)null, (BackupRunDto?)null));
        overview.LocalDirectory.ShouldEndWith(Path.Combine("backups", "local"));
        var local = overview.LocalDirectory;
        (await GetAsync<List<BackupDestinationDto>>(owner, "/api/v1/backups/destinations")).ShouldHaveSingleItem().Kind.ShouldBe("LOCAL");

        // Comprobar: una carpeta nueva sirve si existe su superior (no se crea nada); sin superior no; la LOCAL no; la red exige UNC de
        // OTRO equipo; S3 no es carpeta.
        var usb = Path.Combine(factory.DataRoot, "usb", "BackupsTienda");
        (await CheckAsync(owner, "EXTERNAL", usb)).Message.ShouldContain("ni su carpeta superior");
        Directory.CreateDirectory(Path.GetDirectoryName(usb)!);
        var ok = await CheckAsync(owner, "EXTERNAL", usb);
        ok.Ok.ShouldBeTrue(ok.Message);
        Directory.Exists(usb).ShouldBeFalse();
        (await CheckAsync(owner, "NETWORK", @"\\localhost\backups")).Message.ShouldContain("este mismo equipo");
        (await CheckAsync(owner, "EXTERNAL", @"\\NAS\backups")).Ok.ShouldBeFalse();
        (await CheckAsync(owner, "EXTERNAL", local)).ShouldSatisfyAllConditions(c => c.Ok.ShouldBeFalse(), c => c.Message.ShouldContain("carpeta LOCAL"));
        (await CheckAsync(owner, "EXTERNAL", "Backups")).Ok.ShouldBeFalse();
        await RawAsync(owner, HttpMethod.Post, "/api/v1/backups/destinations/check-folder", new { kind = "NETWORK", path = usb })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "BACKUP.NETWORK_PATH_REQUIRED");
        await RawAsync(owner, HttpMethod.Post, "/api/v1/backups/destinations/check-folder", new { kind = "S3", path = usb })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "BACKUP.FOLDER_KIND");

        // Guardar: ruta relativa o red sin UNC no pasan la validación; una ruta que es un archivo no pasa la comprobación.
        await RawAsync(owner, HttpMethod.Post, "/api/v1/backups/destinations", Destination("EXTERNAL", "Backups"))
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "VALIDATION.FAILED");
        await RawAsync(owner, HttpMethod.Post, "/api/v1/backups/destinations", Destination("NETWORK", usb, name: "NAS"))
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "VALIDATION.FAILED");
        var file = Path.Combine(factory.DataRoot, "no-es-carpeta.txt");
        await File.WriteAllTextAsync(file, "x", Ct);
        await RawAsync(owner, HttpMethod.Post, "/api/v1/backups/destinations", Destination("EXTERNAL", file))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BACKUP.FOLDER_INVALID");
        await RawAsync(owner, HttpMethod.Post, "/api/v1/backups/destinations", Destination("EXTERNAL", local))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BACKUP.FOLDER_INVALID");
        (await AuditCountAsync(shop)).ShouldBe(0);

        // Un destino que sirve se guarda y se audita.
        var created = await PostAsync<BackupDestinationDto>(owner, "/api/v1/backups/destinations", Destination("EXTERNAL", usb), HttpStatusCode.Created);
        (created.Kind, created.Path, created.IsActive).ShouldBe(("EXTERNAL", usb, true));
        (await AuditCountAsync(shop)).ShouldBe(1);

        // Disco retirado (aquí: la carpeta ya no se puede usar): cambiar solo la retención no vuelve a probar la carpeta…
        await File.WriteAllTextAsync(usb, "ocupa el lugar de la carpeta", Ct);
        (await SendAsync<BackupDestinationDto>(owner, HttpMethod.Put, $"/api/v1/backups/destinations/{created.Id}", Destination("EXTERNAL", usb, keepDaily: 14)))
            .KeepDaily.ShouldBe(14);
        // …pero cambiar la carpeta o reactivar el destino sí.
        await RawAsync(owner, HttpMethod.Put, $"/api/v1/backups/destinations/{created.Id}", Destination("EXTERNAL", file))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BACKUP.FOLDER_INVALID");
        (await SendAsync<BackupDestinationDto>(owner, HttpMethod.Put, $"/api/v1/backups/destinations/{created.Id}", Destination("EXTERNAL", usb, active: false)))
            .IsActive.ShouldBeFalse();
        await RawAsync(owner, HttpMethod.Put, $"/api/v1/backups/destinations/{created.Id}", Destination("EXTERNAL", usb))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BACKUP.FOLDER_INVALID");
        File.Delete(usb);
        (await SendAsync<BackupDestinationDto>(owner, HttpMethod.Put, $"/api/v1/backups/destinations/{created.Id}", Destination("EXTERNAL", usb)))
            .IsActive.ShouldBeTrue();
        (await AuditCountAsync(shop)).ShouldBe(4);
        (await shop.Catalog.ScalarAsync<string>(
                "SELECT severity FROM audit.audit_log WHERE action = 'BACKUP_DESTINATION_CHANGED' ORDER BY occurred_at DESC LIMIT 1"))
            .ShouldBe("WARNING");
    }

    [Fact]
    public async Task Un_disco_externo_en_el_mismo_volumen_que_la_carpeta_LOCAL_no_sirve()
    {
        await using var factory = new SingleFactory(allowSameVolume: false);
        var shop = await SalesScenario.CreateAsync(factory);
        var mismoDisco = Directory.CreateDirectory(Path.Combine(factory.DataRoot, "mismo-disco")).FullName;

        (await CheckAsync(shop.Owner, "EXTERNAL", mismoDisco)).Message.ShouldContain("mismo disco");
        await RawAsync(shop.Owner, HttpMethod.Post, "/api/v1/backups/destinations", Destination("EXTERNAL", mismoDisco))
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BACKUP.FOLDER_INVALID");
        (await AuditCountAsync(shop)).ShouldBe(0);
    }

    [Fact]
    public async Task Solo_quien_configura_los_destinos_comprueba_y_guarda_carpetas()
    {
        await using var factory = new SingleFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var usb = Path.Combine(factory.DataRoot, "usb");

        foreach (var client in new[] { shop.Supervisor, shop.Cashier })
        {
            await RawAsync(client, HttpMethod.Post, "/api/v1/backups/destinations/check-folder", new { kind = "EXTERNAL", path = usb })
                .ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");
            await RawAsync(client, HttpMethod.Post, "/api/v1/backups/destinations", Destination("EXTERNAL", usb))
                .ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");
        }

        await RawAsync(shop.Cashier, HttpMethod.Get, "/api/v1/backups/overview").ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");
        (await AuditCountAsync(shop)).ShouldBe(0);
    }

    /// <summary>
    /// Caja Única: el cajero y el supervisor entran con PIN sin registrar la caja. Las carpetas de la prueba están en el mismo disco que la
    /// LOCAL: por defecto se permite (solo fuera de producción) y una prueba aparte comprueba la regla de otro disco.
    /// </summary>
    private sealed class SingleFactory(bool allowSameVolume = true) : PosServerFactory
    {
        protected override string Edition => "SINGLE";

        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
            [new("Pos:Backup:AllowSameVolumeDestinations", allowSameVolume ? "true" : "false")];
    }

    private static async Task<long> AuditCountAsync(SalesScenario shop) =>
        await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'BACKUP_DESTINATION_CHANGED'");
}
