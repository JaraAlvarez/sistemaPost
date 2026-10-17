using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSec.Cryptography;
using Npgsql;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.Sync.Application;
using Pos.Sync.Contracts;

namespace Pos.Cloud.IntegrationTests;

/// <summary>Nube con su clave de sincronización configurada (para abrir paquetes .possync cargados en el portal).</summary>
public sealed class SyncPackagesCloudServerFactory : CloudServerFactory
{
    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"pos-sync-key-{Guid.CreateVersion7():N}.txt");

    public SyncPackagesCloudServerFactory()
    {
        var (key, publicKey) = SyncPackage.GenerateKeyPair();
        using (key)
        {
            File.WriteAllText(_keyFile, SyncPackage.ExportPrivate(key));
        }

        CloudPublicKey = publicKey;
    }

    public string CloudPublicKey { get; }

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
        [new("Sync:PrivateKeyPath", _keyFile), new("Sync:MaxUploadsPer10Minutes", "1000")];

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        File.Delete(_keyFile);
    }
}

public sealed class SyncPackagesCloudFixture : IAsyncLifetime
{
    public SyncPackagesCloudServerFactory Factory { get; } = new();

    public HttpClient Superadmin { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Factory.StartAsync();
        (Superadmin, _) = await PortalApi.BootstrapSuperadminAsync(Factory);
    }

    public async ValueTask DisposeAsync()
    {
        Superadmin.Dispose();
        await Factory.DisposeAsync();
    }
}

/// <summary>
/// Revisión S2/S3 en la nube: la sincronización en línea registra la clave de firma de la tienda; los paquetes cargados en el portal deben
/// venir firmados con ella (o quedan «sin verificar» si la tienda aún no registró clave); una instalación liberada no recibe paquetes; una
/// versión del futuro se acota a la hora del servidor.
/// </summary>
public class SyncSigningTests(SyncPackagesCloudFixture cloud) : IClassFixture<SyncPackagesCloudFixture>
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    [Fact]
    public async Task La_clave_de_la_tienda_se_registra_en_linea_y_los_paquetes_deben_venir_firmados_con_ella()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var pos = cloud.Factory.NewPos(customer.Nit);
        await pos.RefreshPublicKeysAsync(Ct);
        (await pos.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();
        var installation = pos.Identity.InstallationId;
        using var client = cloud.Factory.CreateClient();

        // Sin clave registrada: el paquete se acepta pero queda «sin verificar».
        using var storeKey = SyncSigning.GenerateKey();
        var first = await UploadAsync(SyncPackage.Seal(Batch(installation, Sale(Guid.CreateVersion7().ToString(), DateTimeOffset.UtcNow.AddMinutes(-5))),
            cloud.Factory.CloudPublicKey, storeKey));
        first.IsSuccess.ShouldBeTrue(first.IsFailure ? first.Error.Message : null);
        first.Value.Unverified.ShouldBeTrue();

        // Una clave con una prueba de posesión inválida se rechaza.
        var batch = Batch(installation, Sale(Guid.CreateVersion7().ToString(), DateTimeOffset.UtcNow.AddMinutes(-5)));
        (await SendAsync(client, batch, pos.Token!, pos.Identity.Fingerprint, SyncSigning.PublicOf(storeKey), "prueba-falsa")).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);

        // El lote en línea (autenticado con licencia y huella) registra la clave.
        var online = await SendAsync(client, batch, pos.Token!, pos.Identity.Fingerprint, SyncSigning.PublicOf(storeKey),
            SyncSigning.KeyProof(storeKey, installation, batch.BatchId));
        await PortalApi.EnsureAsync(online, HttpStatusCode.OK);
        (await ScalarAsync<string>($"SELECT public_key FROM sync.installation_keys WHERE installation_id = '{installation}'"))
            .ShouldBe(SyncSigning.PublicOf(storeKey));

        // Desde ahora: firmado con su clave → verificado; sin firma o con otra clave → rechazado.
        var verified = await UploadAsync(SyncPackage.Seal(Batch(installation, Sale(Guid.CreateVersion7().ToString(), DateTimeOffset.UtcNow.AddMinutes(-4))),
            cloud.Factory.CloudPublicKey, storeKey));
        verified.IsSuccess.ShouldBeTrue(verified.IsFailure ? verified.Error.Message : null);
        verified.Value.Unverified.ShouldBeFalse();
        (await UploadAsync(SyncPackage.Seal(Batch(installation, Sale("x", DateTimeOffset.UtcNow)), cloud.Factory.CloudPublicKey)))
            .Error.Code.ShouldBe(SyncErrors.WrongSigner.Code);
        using var attacker = SyncSigning.GenerateKey();
        (await UploadAsync(SyncPackage.Seal(Batch(installation, Sale("x", DateTimeOffset.UtcNow)), cloud.Factory.CloudPublicKey, attacker)))
            .Error.Code.ShouldBe(SyncErrors.WrongSigner.Code);
        (await ScalarAsync<long>($"SELECT count(*) FROM sync.batches WHERE installation_id = '{installation}' AND signature_status = 'UNVERIFIED'")).ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM sync.batches WHERE installation_id = '{installation}' AND signature_status = 'VERIFIED'")).ShouldBe(1);

        // Liberado el equipo, sus paquetes ya no se aplican.
        (await pos.DeactivateAsync("Cambio de computador", Ct)).Succeeded.ShouldBeTrue();
        (await UploadAsync(SyncPackage.Seal(Batch(installation, Sale("y", DateTimeOffset.UtcNow)), cloud.Factory.CloudPublicKey, storeKey)))
            .Error.Code.ShouldBe(SyncErrors.ReleasedInstallation.Code);
        (await UploadAsync(SyncPackage.Seal(Batch(Guid.CreateVersion7(), Sale("z", DateTimeOffset.UtcNow)), cloud.Factory.CloudPublicKey, storeKey)))
            .Error.Code.ShouldBe(SyncErrors.UnknownInstallation.Code);
    }

    [Fact]
    public async Task Rotar_la_clave_se_audita_y_la_anterior_se_acepta_siete_dias()
    {
        var (pos, _) = await ActivatedAsync();
        var installation = pos.Identity.InstallationId;
        using var first = SyncSigning.GenerateKey();
        using var second = SyncSigning.GenerateKey();
        await RegisterOnlineAsync(pos, first);
        await RegisterOnlineAsync(pos, second);
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.audit_log WHERE action = 'SYNC_KEY_ROTATED' AND entity_id = '{installation}'")).ShouldBe(1);

        // Un paquete exportado antes de rotar (firmado con la anterior) se acepta…
        var old = await UploadAsync(SyncPackage.Seal(Batch(installation, Sale(Guid.CreateVersion7().ToString(), DateTimeOffset.UtcNow)), cloud.Factory.CloudPublicKey, first));
        old.IsSuccess.ShouldBeTrue(old.IsFailure ? old.Error.Message : null);
        old.Value.Unverified.ShouldBeFalse();

        // …pero no después de 7 días.
        await ExecuteAsync($"UPDATE sync.installation_keys SET rotated_at = now() - interval '8 days' WHERE installation_id = '{installation}'");
        (await UploadAsync(SyncPackage.Seal(Batch(installation, Sale("w", DateTimeOffset.UtcNow)), cloud.Factory.CloudPublicKey, first)))
            .Error.Code.ShouldBe(SyncErrors.WrongSigner.Code);
    }

    [Fact]
    public async Task Los_paquetes_sin_verificar_se_verifican_o_se_descartan_cuando_hay_clave_y_la_clave_se_puede_reiniciar()
    {
        var (pos, customer) = await ActivatedAsync();
        var installation = pos.Identity.InstallationId;
        using var storeKey = SyncSigning.GenerateKey();

        // Antes de registrar clave: uno firmado (se podrá verificar) y uno sin firma (versión anterior: solo se puede descartar).
        var signedSale = Guid.CreateVersion7().ToString();
        var unsignedSale = Guid.CreateVersion7().ToString();
        var replacedSale = Guid.CreateVersion7().ToString();

        // Una venta que ya estaba (en línea, sin registrar clave) y que el paquete sin firma va a reemplazar.
        using (var client = cloud.Factory.CreateClient())
        {
            await PortalApi.EnsureAsync(await SendAsync(client, Batch(installation, Sale(replacedSale, DateTimeOffset.UtcNow.AddMinutes(-10))), pos.Token!,
                pos.Identity.Fingerprint), HttpStatusCode.OK);
        }

        (await UploadAsync(SyncPackage.Seal(Batch(installation, Sale(signedSale, DateTimeOffset.UtcNow.AddMinutes(-3))), cloud.Factory.CloudPublicKey, storeKey)))
            .Value.Unverified.ShouldBeTrue();
        var unsignedPackage = new SyncBatch(Guid.CreateVersion7(), installation, DateTimeOffset.UtcNow, "1.0.0",
            [Sale(unsignedSale, DateTimeOffset.UtcNow.AddMinutes(-3)), Sale(replacedSale, DateTimeOffset.UtcNow.AddMinutes(-3), "VOIDED")]);
        (await UploadAsync(SyncPackage.Seal(unsignedPackage, cloud.Factory.CloudPublicKey))).Value.Unverified.ShouldBeTrue();
        (await ScalarAsync<string>($"SELECT data->>'status' FROM sync.documents WHERE doc_id = '{replacedSale}'")).ShouldBe("VOIDED");
        var signedBatch = await ScalarAsync<Guid>($"SELECT batch_id FROM sync.documents WHERE doc_id = '{signedSale}'");
        var unsignedBatch = await ScalarAsync<Guid>($"SELECT batch_id FROM sync.documents WHERE doc_id = '{unsignedSale}'");

        // Sin clave registrada todavía no se puede verificar.
        (await AsSuperadminAsync(d => d.Send(new VerifySyncBatchCommand(signedBatch), Ct))).Error.Code.ShouldBe(SyncErrors.CannotVerify.Code);

        await RegisterOnlineAsync(pos, storeKey);
        var data = (await AsSuperadminAsync(d => d.Send(new GetStoreDataQuery(customer.OrganizationId, Today, Today), Ct))).Value;
        data.Installations.Single(i => i.InstallationId == installation).Unverified.ShouldBe(2);
        data.Installations.Single(i => i.InstallationId == installation).KeyRegistered.ShouldBeTrue();
        data.UnverifiedBatches!.Single(b => b.BatchId == signedBatch).MatchesKey.ShouldBeTrue();
        data.UnverifiedBatches!.Single(b => b.BatchId == unsignedBatch).HasSignature.ShouldBeFalse();

        (await AsSuperadminAsync(d => d.Send(new VerifySyncBatchCommand(signedBatch), Ct))).IsSuccess.ShouldBeTrue();
        (await ScalarAsync<string>($"SELECT signature_status FROM sync.batches WHERE id = '{signedBatch}'")).ShouldBe("VERIFIED");
        (await AsSuperadminAsync(d => d.Send(new VerifySyncBatchCommand(unsignedBatch), Ct))).Error.Code.ShouldBe(SyncErrors.CannotVerify.Code);

        // Descartar: lo que creó se borra y lo que reemplazó vuelve a su versión anterior.
        (await AsSuperadminAsync(d => d.Send(new DiscardSyncBatchCommand(unsignedBatch), Ct))).Value.ShouldBe(2);
        (await ScalarAsync<long>($"SELECT count(*) FROM sync.documents WHERE doc_id = '{unsignedSale}'")).ShouldBe(0);
        (await ScalarAsync<string>($"SELECT data->>'status' FROM sync.documents WHERE doc_id = '{replacedSale}'")).ShouldBe("COMPLETED");
        (await ScalarAsync<string>($"SELECT received_via FROM sync.documents WHERE doc_id = '{replacedSale}'")).ShouldBe("ONLINE");
        (await ScalarAsync<string>($"SELECT signature_status FROM sync.batches WHERE id = '{unsignedBatch}'")).ShouldBe("DISCARDED");
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.audit_log WHERE action = 'SYNC_BATCH_DISCARDED' AND entity_id = '{unsignedBatch}'")).ShouldBe(1);

        // Reiniciar la clave: sus paquetes vuelven a quedar sin verificar hasta que registre otra.
        (await AsSuperadminAsync(d => d.Send(new ResetSyncSigningKeyCommand(installation, "Se restauró la tienda en otro equipo"), Ct))).IsSuccess.ShouldBeTrue();
        (await ScalarAsync<object>($"SELECT public_key FROM sync.installation_keys WHERE installation_id = '{installation}'")).ShouldBe(DBNull.Value);
        (await UploadAsync(SyncPackage.Seal(Batch(installation, Sale(Guid.CreateVersion7().ToString(), DateTimeOffset.UtcNow)), cloud.Factory.CloudPublicKey, storeKey)))
            .Value.Unverified.ShouldBeTrue();

        // El mismo archivo descartado se puede volver a cargar (aquí, sin clave registrada tras el reinicio: queda sin verificar).
        var reloaded = await UploadAsync(SyncPackage.Seal(unsignedPackage, cloud.Factory.CloudPublicKey));
        reloaded.IsSuccess.ShouldBeTrue(reloaded.IsFailure ? reloaded.Error.Message : null);
        reloaded.Value.Duplicate.ShouldBeFalse();
        (await ScalarAsync<long>($"SELECT count(*) FROM sync.documents WHERE doc_id = '{unsignedSale}'")).ShouldBe(1);

        // Revisar exige que la instalación exista.
        (await AsSuperadminAsync(d => d.Send(new ResetSyncSigningKeyCommand(Guid.CreateVersion7(), "Instalación inventada"), Ct)))
            .Error.Code.ShouldBe(SyncErrors.UnknownInstallation.Code);
    }

    [Fact]
    public async Task La_nube_no_guarda_la_cedula_ni_el_costo_promedio_aunque_la_tienda_los_mande()
    {
        var (pos, _) = await ActivatedAsync();
        using var client = cloud.Factory.CreateClient();
        var now = DateTimeOffset.UtcNow.AddMinutes(-2);
        using var sale = JsonDocument.Parse("""{"number": "S-1", "status": "COMPLETED", "customer": "Laura", "customerIdentification": "1020304050", "total": 5}""");
        using var stock = JsonDocument.Parse("""{"sku": "A1", "quantity": 2, "averageCost": 1500, "totalValue": 3000}""");
        var saleId = Guid.CreateVersion7().ToString();
        var stockId = Guid.CreateVersion7().ToString();
        var batch = new SyncBatch(Guid.CreateVersion7(), pos.Identity.InstallationId, now, "1.3.0",
        [
            new SyncItem(SyncKinds.Sale, saleId, now, Today, sale.RootElement.Clone()),
            new SyncItem(SyncKinds.Stock, stockId, now, null, stock.RootElement.Clone()),
        ]);
        await PortalApi.EnsureAsync(await SendAsync(client, batch, pos.Token!, pos.Identity.Fingerprint), HttpStatusCode.OK);

        (await ScalarAsync<bool>($"SELECT data ? 'customerIdentification' FROM sync.documents WHERE doc_id = '{saleId}'")).ShouldBeFalse();
        (await ScalarAsync<string>($"SELECT data->>'customer' FROM sync.documents WHERE doc_id = '{saleId}'")).ShouldBe("Laura");
        (await ScalarAsync<bool>($"SELECT data ? 'averageCost' FROM sync.documents WHERE doc_id = '{stockId}'")).ShouldBeFalse();
        (await ScalarAsync<string>($"SELECT data->>'totalValue' FROM sync.documents WHERE doc_id = '{stockId}'")).ShouldBe("3000");
    }

    [Fact]
    public async Task Ventas_recientes_se_ordenan_por_la_fecha_de_la_venta_y_no_por_la_version()
    {
        var (pos, customer) = await ActivatedAsync();
        using var client = cloud.Factory.CreateClient();
        var now = DateTimeOffset.UtcNow;

        // La venta vieja se anuló después (versión más nueva), pero sigue siendo la más vieja.
        var oldSale = Sale(Guid.CreateVersion7().ToString(), now.AddMinutes(-1), "VOIDED");
        using var oldData = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            number = "S-VIEJA", status = "VOIDED", branch = "Principal", total = 1m, completedAt = now.AddDays(-2), businessDate = Today,
        }, SyncJson.Options));
        using var newData = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            number = "S-NUEVA", status = "COMPLETED", branch = "Principal", total = 2m, completedAt = now.AddHours(-1), businessDate = Today,
        }, SyncJson.Options));
        var batch = new SyncBatch(Guid.CreateVersion7(), pos.Identity.InstallationId, now, "1.0.0",
        [
            oldSale with { Data = oldData.RootElement.Clone() },
            new SyncItem(SyncKinds.Sale, Guid.CreateVersion7().ToString(), now.AddHours(-1), Today, newData.RootElement.Clone()),
        ]);
        await PortalApi.EnsureAsync(await SendAsync(client, batch, pos.Token!, pos.Identity.Fingerprint), HttpStatusCode.OK);

        var recent = (await AsSuperadminAsync(d => d.Send(new GetStoreDataQuery(customer.OrganizationId, Today, Today), Ct))).Value.RecentSales;
        recent.Select(r => r.Number).ShouldBe(["S-NUEVA", "S-VIEJA"]);
        recent[1].CompletedAt!.Value.ShouldBe(now.AddDays(-2), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void El_limite_de_cargas_por_usuario_corta_en_el_maximo_de_la_ventana()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var throttle = new SyncUploadThrottle(clock, maxUploads: 2);
        var user = Guid.CreateVersion7();
        throttle.TryAcquire(user).ShouldBeTrue();
        throttle.TryAcquire(user).ShouldBeTrue();
        throttle.TryAcquire(user).ShouldBeFalse();
        throttle.TryAcquire(Guid.CreateVersion7()).ShouldBeTrue(); // otro usuario
        clock.UtcNow = clock.UtcNow.AddMinutes(11);
        throttle.TryAcquire(user).ShouldBeTrue();
    }

    private sealed class MutableClock(DateTimeOffset now) : Pos.SharedKernel.Time.IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public TimeZoneInfo BusinessTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task Una_version_del_futuro_se_acota_y_no_congela_el_documento()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var pos = cloud.Factory.NewPos(customer.Nit);
        await pos.RefreshPublicKeysAsync(Ct);
        (await pos.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();
        using var client = cloud.Factory.CreateClient();
        var saleId = Guid.CreateVersion7().ToString();

        // Reloj de la tienda adelantado un año: la versión queda en la hora del servidor + 10 min.
        var future = Batch(pos.Identity.InstallationId, Sale(saleId, DateTimeOffset.UtcNow.AddYears(1)));
        await PortalApi.EnsureAsync(await SendAsync(client, future, pos.Token!, pos.Identity.Fingerprint), HttpStatusCode.OK);
        var stored = await ScalarAsync<DateTime>($"SELECT version FROM sync.documents WHERE doc_id = '{saleId}'");
        stored.ShouldBeLessThan(DateTime.UtcNow.AddMinutes(11));

        // Un cambio real posterior (más de 10 min después, según la tienda) igual se aplica.
        await ExecuteAsync($"UPDATE sync.documents SET version = version - interval '1 hour' WHERE doc_id = '{saleId}'");
        var later = Batch(pos.Identity.InstallationId, Sale(saleId, DateTimeOffset.UtcNow, "VOIDED"));
        var ack = (await (await SendAsync(client, later, pos.Token!, pos.Identity.Fingerprint)).Content.ReadFromJsonAsync<SyncAck>(SyncJson.Options, Ct))!;
        ack.Applied.ShouldBe(1);
        (await ScalarAsync<string>($"SELECT data->>'status' FROM sync.documents WHERE doc_id = '{saleId}'")).ShouldBe("VOIDED");
    }

    private Task<Pos.SharedKernel.Results.Result<UploadResultDto>> UploadAsync(byte[] package) =>
        AsSuperadminAsync(d => d.Send(new UploadPackageCommand(package), Ct));

    private async Task<T> AsSuperadminAsync<T>(Func<IDispatcher, Task<T>> action)
    {
        await using var scope = cloud.Factory.Services.CreateAsyncScope();
        // La sesión activa del superadministrador (la abrió el fixture): el verificador de permisos la consulta.
        var session = await ScalarAsync<Guid>(
            "SELECT s.id FROM portal.portal_sessions s JOIN portal.portal_users u ON u.id = s.user_id WHERE u.role = 'SUPERADMIN' AND s.stage = 'ACTIVE' " +
            "AND s.revoked_at IS NULL ORDER BY s.created_at DESC LIMIT 1");
        var superadminId = await ScalarAsync<Guid>($"SELECT user_id FROM portal.portal_sessions WHERE id = '{session}'");
        var user = scope.ServiceProvider.GetRequiredService<PortalUserContext>();
        user.Set(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(PortalClaims.Stage, PortalClaims.ActiveStage), new Claim(PortalClaims.UserId, superadminId.ToString()),
                new Claim(PortalClaims.SessionId, session.ToString()),
                new Claim(PortalClaims.Role, PortalRoles.Superadmin), new Claim(ClaimTypes.Name, "Superadministrador"),
            ],
            "Test")));
        return await action(scope.ServiceProvider.GetRequiredService<IDispatcher>());
    }

    private async Task<(Pos.License.Simulator.SimulatedPos Pos, LicensedCustomer Customer)> ActivatedAsync()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var pos = cloud.Factory.NewPos(customer.Nit);
        await pos.RefreshPublicKeysAsync(Ct);
        (await pos.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();
        return (pos, customer);
    }

    private async Task RegisterOnlineAsync(Pos.License.Simulator.SimulatedPos pos, Key key)
    {
        using var client = cloud.Factory.CreateClient();
        var batch = Batch(pos.Identity.InstallationId, Sale(Guid.CreateVersion7().ToString(), DateTimeOffset.UtcNow.AddMinutes(-5)));
        await PortalApi.EnsureAsync(await SendAsync(client, batch, pos.Token!, pos.Identity.Fingerprint, SyncSigning.PublicOf(key),
            SyncSigning.KeyProof(key, pos.Identity.InstallationId, batch.BatchId)), HttpStatusCode.OK);
    }

    private static SyncBatch Batch(Guid installation, SyncItem item) => new(Guid.CreateVersion7(), installation, DateTimeOffset.UtcNow, "1.0.0", [item]);

    private static SyncItem Sale(string id, DateTimeOffset version, string status = "COMPLETED")
    {
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            number = "S-1", status, branch = "Principal", terminal = "CAJA-01", cashier = "Cajera", total = 12_500m, completedAt = version, businessDate = Today,
        }, SyncJson.Options));
        return new SyncItem(SyncKinds.Sale, id, version, Today, data.RootElement.Clone());
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, SyncBatch batch, string token, string fingerprint, string? signingKey = null, string? proof = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SyncRoutes.Batches) { Content = JsonContent.Create(batch, options: SyncJson.Options) };
        request.Headers.Authorization = new AuthenticationHeaderValue(SyncRoutes.AuthorizationScheme, token);
        request.Headers.Add(SyncRoutes.FingerprintHeader, fingerprint);
        if (signingKey is not null)
        {
            request.Headers.Add(SyncRoutes.SigningKeyHeader, signingKey);
            request.Headers.Add(SyncRoutes.SigningKeyProofHeader, proof);
        }

        return await client.SendAsync(request, Ct);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(cloud.Factory.Database.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(cloud.Factory.Database.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}

internal static class SyncPackagesCloudServerFactoryExtensions
{
    public static Pos.License.Simulator.SimulatedPos NewPos(this CloudServerFactory factory, string nit) =>
        new(factory.CreateClient(), Pos.License.Simulator.SimulatorIdentity.Create(
            nit, Pos.Licensing.Contracts.DeviceRoles.StoreServer, board: $"PLACA-{Guid.NewGuid():N}", disk: $"DISCO-{Guid.NewGuid():N}",
            machine: $"MAQ-{Guid.NewGuid():N}"));
}
