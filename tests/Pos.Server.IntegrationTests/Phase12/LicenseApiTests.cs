using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pos.Licensing.Contracts;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Licensing.Application;
using Pos.Modules.Licensing.Contracts;
using Pos.Modules.Licensing.Infrastructure;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase12;

/// <summary>
/// Servidor de licencias en memoria que habla el contrato real (<c>Pos.Licensing.Contracts</c>: rutas, DTOs, token Ed25519 y códigos
/// <c>LICENSE.*</c>) por HTTP. El servidor real de la nube se prueba con el simulador en la Fase 12-A.
/// </summary>
internal sealed class FakeLicenseCloud : HttpMessageHandler
{
    public static readonly string ValidKey = LicenseKey.Generate();

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public LicenseSigningKey Key { get; } = LicenseSigningKey.Generate();

    public string SubscriptionStatus { get; set; } = SubscriptionStatuses.Active;

    public CheckinAuditSeal? LastSeal { get; private set; }

    /// <summary>Enlace de pago en línea que devuelve la nube (Fase 12-C); <c>null</c> = licencia al día.</summary>
    public string? PaymentUrl { get; set; }

    public int Checkins { get; private set; }

    /// <summary>Primer inicio de la demo que la nube conoce para el equipo (demo por equipo); <c>null</c> = la nube no responde ese registro.</summary>
    public DateTimeOffset? DemoStartedAt { get; set; }

    public int DemoRequests { get; private set; }

    /// <summary>Soporte reinició la demo de este equipo en el portal.</summary>
    public bool DemoReset { get; set; }

    /// <summary>Última fecha de compilación que mandó la tienda.</summary>
    public DateTimeOffset? LastBuildDate { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        if (path == LicensingRoutes.Activations)
        {
            var activation = JsonSerializer.Deserialize<ActivationRequest>(body, Web)!;
            return LicenseKey.TryNormalize(activation.LicenseKey, out var key) && key == ValidKey
                ? Token(activation.InstallationId, activation.Fingerprint, activation.DeviceRole, activation.OrganizationNit)
                : Problem(LicenseErrorCodes.KeyInvalid);
        }

        if (path == LicensingRoutes.DemoDevices && DemoStartedAt is { } demoStart)
        {
            var demo = JsonSerializer.Deserialize<DemoDeviceRequest>(body, Web)!;
            DemoRequests++;
            LastBuildDate = demo.BuildDate;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new DemoDeviceResponse(DemoToken.Sign(new DemoClaims { DeviceFingerprint = demo.Fingerprint, StartedAt = demoStart, IssuedAt = DateTimeOffset.UtcNow, Reset = DemoReset }, Key)),
                    options: Web),
            };
        }

        if (path == LicensingRoutes.Checkins)
        {
            var checkin = JsonSerializer.Deserialize<CheckinRequest>(body, Web)!;
            Checkins++;
            LastSeal = checkin.AuditSeal ?? LastSeal;
            var claims = LicenseToken.Verify(checkin.Token, new LicenseKeyRing([Key.PublicKey])).Claims!;
            return Token(claims.InstallationId, checkin.Fingerprint, claims.DeviceRole, claims.OrganizationNit);
        }

        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Token(Guid installation, string fingerprint, string role, string nit)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new LicenseClaims
        {
            LicenseId = Guid.CreateVersion7(), OrganizationNit = nit, OrganizationName = "Supermercado La Economía SAS", InstallationId = installation,
            DeviceFingerprint = fingerprint, DeviceRole = role, Edition = LicenseEditions.SingleTerminal, SubscriptionStatus = SubscriptionStatus,
            IssuedAt = now, ValidUntil = now.AddDays(30), GraceDays = 7, RefreshAfter = now.AddDays(1),
            Messages = SubscriptionStatus == SubscriptionStatuses.Suspended
                ? [new LicenseMessage("SUBSCRIPTION_SUSPENDED", "CRITICAL", "La suscripción está suspendida.")]
                : [],
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                new LicenseTokenResponse(
                    LicenseToken.Sign(claims, Key), Key.Kid, SubscriptionStatus, claims.ValidUntil, 7, claims.RefreshAfter, claims.Messages, PaymentUrl),
                options: Web),
        };
    }

    private static HttpResponseMessage Problem(string code) => new(HttpStatusCode.UnprocessableEntity)
    {
        Content = JsonContent.Create(new { title = "Rechazado", code, detail = "La clave de licencia no existe o fue revocada." }),
    };
}

/// <summary>Reloj que la prueba puede atrasar (RN-LIC-05).</summary>
internal sealed class ShiftableTime : TimeProvider
{
    public TimeSpan Shift { get; set; }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
}

/// <summary>
/// Caja Única conectada al servidor de licencias en memoria, con una huella fija y el reloj desplazable. La clave de la nube y la huella
/// se INYECTAN como servicios (no por configuración): así la prueba vale igual en Debug y en Release, donde la configuración de
/// desarrollo no existe (R1).
/// </summary>
public class LicenseServerFactory : PosServerFactory
{
    internal static readonly DeviceFingerprint Fingerprint = DeviceFingerprint.FromHardware("PLACA-1", "DISCO-1", "MAQUINA-1");

    internal FakeLicenseCloud Cloud { get; } = new();

    internal ShiftableTime Time { get; } = new();

    protected override string Edition => "SINGLE";

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("Pos:Audit:Interval", "00:00:00.500"),
        new("Pos:Audit:SafetyHorizon", "00:00:02"),
        new("Pos:Licensing:ServerUrl", "https://licencias.test/"),
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time));
            services.Replace(ServiceDescriptor.Singleton<ILicenseKeys>(new EmbeddedLicenseKeys([Cloud.Key.PublicKey.X])));
            services.Replace(ServiceDescriptor.Singleton<IDeviceIdentity>(new WindowsDeviceIdentity(Fingerprint.ToString())));
            services.Replace(ServiceDescriptor.Singleton<ILicenseCloud>(new LicenseCloudClient(
                new HttpClient(Cloud) { BaseAddress = new Uri("https://licencias.test/") }, NullLogger<LicenseCloudClient>.Instance)));
        });
    }
}

/// <summary>
/// Verificación de coherencia de la Fase 12-B (§12): demostración, activación, restricción en el backend con las ventas de la jornada
/// abierta disponibles, reactivación, reloj atrasado y sello de auditoría enviado en el check-in.
/// </summary>
public class LicenseApiTests
{
    private const string PaymentUrl = "https://licencias.test/pagar/Q7wX2kLm9pR4tY6uZ3vB8n";

    [Fact]
    public async Task Demostracion_activacion_restriccion_reactivacion_y_reloj_atrasado()
    {
        await using var factory = new LicenseServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);

        var status = await GetAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license");
        status.State.ShouldBe(LicenseStates.Demo);
        status.DaysLeft.ShouldBe(30);
        (await GetAsync<MeDto>(shop.Cashier, "/api/v1/auth/me")).License.ShouldNotBeNull().State.ShouldBe(LicenseStates.Demo);

        // En demostración todo funciona y el tiquete lo dice.
        await shop.OpenSessionAsync();
        var demoSale = await shop.StartAsync();
        demoSale = await shop.AddAsync(demoSale.Id, shop.Rice, 1);
        (await shop.CompleteAsync(demoSale.Id, [Pay(shop.Cash, 5_000m)])).TicketText.ShouldContain("DEMOSTRACIÓN");

        // Clave mal escrita y clave inexistente: códigos estables en español.
        await RawAsync(shop.Owner, HttpMethod.Post, "/api/v1/license/activate", new { licenseKey = "POS-123" })
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, LicenseErrorCodes.KeyFormatInvalid);
        await RawAsync(shop.Owner, HttpMethod.Post, "/api/v1/license/activate", new { licenseKey = LicenseKey.Generate() })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, LicenseErrorCodes.KeyInvalid);
        await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/license/activate", new { licenseKey = FakeLicenseCloud.ValidKey })
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");

        status = await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/activate", new { licenseKey = FakeLicenseCloud.ValidKey.ToLowerInvariant() });
        status.State.ShouldBe(LicenseStates.Valid);
        status.LicenseKeyPrefix.ShouldBe(LicenseKey.VisiblePrefix(FakeLicenseCloud.ValidKey));
        status.OrganizationNit.ShouldBe("900123456-8");

        status.PaymentUrl.ShouldBeNull();
        (await shop.Owner.GetAsync("/api/v1/license/payment-qr", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Jornada abierta con una venta en curso; la nube suspende la suscripción y envía el enlace de pago (Fase 12-C).
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 2);
        factory.Cloud.SubscriptionStatus = SubscriptionStatuses.Suspended;
        factory.Cloud.PaymentUrl = PaymentUrl;
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct); // horizonte seguro del sello de auditoría
        status = await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/check");
        status.State.ShouldBe(LicenseStates.Restricted);
        status.Notices.ShouldContain(n => n.Code == "SUBSCRIPTION_SUSPENDED");
        factory.Cloud.LastSeal.ShouldNotBeNull(); // Ancla externa de la auditoría (ADR-0048).

        // Restringida, el enlace de pago se ve en la licencia y en la sesión del dueño (no en la de la caja) y su QR se dibuja en SVG.
        status.PaymentUrl.ShouldBe(PaymentUrl);
        (await GetAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license")).PaymentUrl.ShouldBe(PaymentUrl);
        (await GetAsync<MeDto>(shop.Owner, "/api/v1/auth/me")).License.ShouldNotBeNull().PaymentUrl.ShouldBe(PaymentUrl);
        (await GetAsync<MeDto>(shop.Cashier, "/api/v1/auth/me")).License.ShouldNotBeNull().PaymentUrl.ShouldBeNull();
        using (var qr = await shop.Owner.GetAsync("/api/v1/license/payment-qr?url=https://otro.test/", Ct))
        {
            qr.StatusCode.ShouldBe(HttpStatusCode.OK);
            qr.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");
            (await qr.Content.ReadAsStringAsync(Ct)).ShouldContain("<svg");
        }

        (await shop.Cashier.GetAsync("/api/v1/license/payment-qr", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Restringida: se vende y se cobra en la jornada abierta, se consulta y se exporta; no se administra.
        (await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 10_000m)])).TicketText.ShouldNotContain("DEMOSTRACIÓN");
        await RawAsync(shop.Owner, HttpMethod.Post, "/api/v1/catalog/categories", new { name = "Nueva", sortOrder = 0 })
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "LICENSE.RESTRICTED");
        (await shop.Owner.GetAsync("/api/v1/reports/SALES_DAILY", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // La nube reactiva (pago aplicado): todo vuelve a funcionar y el enlace de pago desaparece.
        factory.Cloud.SubscriptionStatus = SubscriptionStatuses.Active;
        factory.Cloud.PaymentUrl = null;
        status = await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/check");
        status.State.ShouldBe(LicenseStates.Valid);
        status.PaymentUrl.ShouldBeNull();
        (await GetAsync<MeDto>(shop.Owner, "/api/v1/auth/me")).License.ShouldNotBeNull().PaymentUrl.ShouldBeNull();
        (await RawAsync(shop.Owner, HttpMethod.Post, "/api/v1/catalog/categories", new { name = "Nueva", sortOrder = 0 })).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Reloj atrasado 3 días: restringida hasta que un check-in confirma la hora confiable.
        await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/check");
        factory.Time.Shift = TimeSpan.FromDays(-3);
        await RefreshAsync(factory);
        (await GetAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license")).State.ShouldBe(LicenseStates.Restricted);
        (await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/check")).State.ShouldBe(LicenseStates.Valid);

        var checkins = await GetAsync<List<LicenseCheckinDto>>(shop.Owner, "/api/v1/license/checkins");
        checkins.Count(c => c.Kind == "ACTIVATION").ShouldBe(1); // la activación rechazada se revierte con su transacción
        checkins.ShouldContain(c => c.Kind == "MANUAL" && c.Succeeded);

        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        var actions = (await connection.QueryAsync<string>("SELECT action FROM audit.audit_log WHERE module = 'licensing'")).ToList();
        actions.ShouldContain("LICENSE_ACTIVATED");
        actions.ShouldContain("LICENSE_STATE_CHANGED");
        actions.ShouldContain("LICENSE_CLOCK_ROLLBACK");
    }

    /// <summary>
    /// R2: si el reloj se adelantó por error, la marca de tiempo sube; al corregirlo la licencia queda restringida por "reloj atrasado"
    /// y el siguiente check-in (hora confiable firmada por la nube) baja la marca y la deja vigente otra vez.
    /// </summary>
    [Fact]
    public async Task Reloj_adelantado_por_error_y_corregido_vuelve_a_vigente_con_el_checkin()
    {
        await using var factory = new LicenseServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        (await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/activate", new { licenseKey = FakeLicenseCloud.ValidKey })).State
            .ShouldBe(LicenseStates.Valid);

        // Con el reloj adelantado las sesiones web vencen: el estado se lee con el mismo comando del proceso de fondo.
        factory.Time.Shift = TimeSpan.FromDays(10);
        (await RefreshAsync(factory)).State.ShouldNotBe(LicenseStates.Restricted);

        factory.Time.Shift = TimeSpan.Zero;
        await RefreshAsync(factory);
        var restricted = await GetAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license");
        restricted.State.ShouldBe(LicenseStates.Restricted);
        restricted.Reason.ShouldContain("reloj");

        (await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/check")).State.ShouldBe(LicenseStates.Valid);
        await RefreshAsync(factory);
        (await GetAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license")).State.ShouldBe(LicenseStates.Valid);

        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        var maxObserved = await connection.ExecuteScalarAsync<DateTime>("SELECT max_observed_utc FROM licensing.license_state");
        maxObserved.ShouldBeLessThan(DateTime.UtcNow.AddHours(1));
        (await connection.ExecuteScalarAsync<string?>("SELECT state_mac FROM licensing.license_state")).ShouldNotBeNull();
    }

    /// <summary>H4: editar a mano el estado guardado rompe la firma: restringida hasta el check-in (el recálculo no la vuelve a firmar).</summary>
    [Fact]
    public async Task Editar_el_estado_en_la_base_de_datos_restringe_hasta_el_checkin()
    {
        await using var factory = new LicenseServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/activate", new { licenseKey = FakeLicenseCloud.ValidKey });

        await using (var connection = new NpgsqlConnection(factory.ConnectionString))
        {
            await connection.ExecuteAsync("UPDATE licensing.license_state SET clock_offset_seconds = clock_offset_seconds + 600");
        }

        await RefreshAsync(factory);
        var tampered = await GetAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license");
        tampered.State.ShouldBe(LicenseStates.Restricted);
        tampered.Reason.ShouldContain("no cuadran con su firma");
        await RefreshAsync(factory);
        (await GetAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license")).State.ShouldBe(LicenseStates.Restricted);

        (await PostAsync<LicenseStatusDto>(shop.Owner, "/api/v1/license/check")).State.ShouldBe(LicenseStates.Valid);
    }

    /// <summary>
    /// R1: en Release las claves y la huella de desarrollo NO se leen de la configuración aunque el entorno sea Development (el entorno lo
    /// cambia cualquier administrador de Windows).
    /// </summary>
    [Fact]
    public void En_Release_las_claves_de_desarrollo_se_ignoran_aunque_el_entorno_sea_Development()
    {
        using var key = LicenseSigningKey.Generate();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pos:Licensing:DevelopmentTrustedKeys:0"] = key.PublicKey.X,
            ["Pos:Licensing:DevelopmentFingerprint"] = LicenseServerFactory.Fingerprint.ToString(),
        }).Build();
        var development = new HostingEnvironment { EnvironmentName = Environments.Development };

        // Compilación Release (debugBuild: false) con el entorno Development: no se habilitan y no se leen.
        LicensingDevelopment.Enabled(development, debugBuild: false).ShouldBeFalse();
        var release = LicensingInfrastructureRegistration.Read(configuration, developmentOverrides: false);
        release.DevelopmentTrustedKeys.ShouldBeEmpty();
        release.DevelopmentFingerprint.ShouldBeNull();
        new EmbeddedLicenseKeys(release.DevelopmentTrustedKeys).Ring.Kids.ShouldNotContain(key.PublicKey.Kid);

        // Ni siquiera en Debug valen en producción.
        LicensingDevelopment.Enabled(new HostingEnvironment { EnvironmentName = Environments.Production }, debugBuild: true).ShouldBeFalse();

#if DEBUG
        LicensingDevelopment.CompiledIn.ShouldBeTrue();
        LicensingInfrastructureRegistration.Read(configuration, developmentOverrides: true).DevelopmentTrustedKeys.ShouldHaveSingleItem();
#else
        // Este binario es Release: la puerta no existe y, aunque se pida, la configuración no se lee.
        LicensingDevelopment.CompiledIn.ShouldBeFalse();
        LicensingInfrastructureRegistration.Read(configuration, developmentOverrides: true).DevelopmentTrustedKeys.ShouldBeEmpty();
#endif
    }

    /// <summary>El proceso en segundo plano recalcula cada minuto; la prueba lo fuerza con el mismo comando.</summary>
    private static async Task<LicenseStatusDto> RefreshAsync(LicenseServerFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<Pos.Application.Abstractions.Messaging.IDispatcher>()
            .Send(new RefreshLicenseCommand(), Ct);
        result.IsSuccess.ShouldBeTrue();
        return result.Value;
    }
}
