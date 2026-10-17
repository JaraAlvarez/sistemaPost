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

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("Sync:PrivateKeyPath", _keyFile)];

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
            .Error.Code.ShouldBe(SyncErrors.UnknownInstallation.Code);
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

    private async Task<Pos.SharedKernel.Results.Result<UploadResultDto>> UploadAsync(byte[] package)
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
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new UploadPackageCommand(package), Ct);
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
