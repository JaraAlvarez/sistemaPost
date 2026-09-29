using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Licensing.Application;
using Pos.Infrastructure.Auditing;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.Database.Tests;

/// <summary>
/// Los casos de uso de la nube guardan y leen de verdad en PostgreSQL: conversiones de enumeraciones, <c>smallint</c> de la
/// gracia, navegaciones por campos, lecturas del portal (SQL) y el orden de guardado al regenerar la clave (FK diferida
/// <c>replaced_by</c> + <c>FlushAsync</c> dentro de la transacción del comando).
/// </summary>
public class LicensingPersistenceTests(CloudPostgresFixture postgres)
{
    private static readonly IPAddress Ip = IPAddress.Parse("190.25.10.20");

    [Fact]
    public async Task Flujo_completo_por_casos_de_uso_con_regeneracion_de_la_clave()
    {
        await using var harness = await CloudHarness.CreateAsync(postgres);
        var db = harness.Database;
        (await harness.SendAsync(new EnsureActiveSigningKeyCommand())).Value.ShouldBeTrue();

        var (nit, dv) = CloudSeed.NewNit();
        var account = (await harness.SendAsync(new CreateAccountCommand(
            new AccountInput("Supermercados La 80", "DIRECT", null, null, "Ana", "ana@la80.co", "3001234567", null, null)))).Value;
        var organization = (await harness.SendAsync(new CreateOrganizationCommand(account, "Supermercados La 80 SAS", nit, dv, "Medellín"))).Value;
        var subscription = (await harness.SendAsync(new CreateSubscriptionCommand(organization, "MULTI", "MONTHLY", Trial: true, TrialDays: 15))).Value;
        var license = (await harness.SendAsync(new GenerateLicenseCommand(organization))).Value;

        (await db.ScalarAsync<short>($"SELECT grace_days FROM licensing.subscriptions WHERE id = '{subscription}'")).ShouldBe((short)7);
        (await db.ScalarAsync<string>($"SELECT status FROM licensing.subscriptions WHERE id = '{subscription}'")).ShouldBe("TRIAL");
        (await db.ScalarAsync<string>($"SELECT type FROM licensing.subscription_events WHERE subscription_id = '{subscription}'")).ShouldBe("CREATED");
        (await db.ScalarAsync<string>($"SELECT key_prefix FROM licensing.licenses WHERE id = '{license.LicenseId}'")).ShouldBe(license.KeyPrefix);
        (await db.ScalarAsync<long>($"SELECT count(*) FROM licensing.licenses WHERE key_hash LIKE '%{license.Key[4..9]}%'")).ShouldBe(0);

        // Activación y check-in (el POS).
        var installationId = Guid.CreateVersion7();
        var fingerprint = DeviceFingerprint.FromHardware("PLACA-1", "DISCO-1", "MAQUINA-1").ToString();
        var activation = await harness.SendAsync(new ActivateLicenseCommand(
            new ActivationRequest(license.Key.ToLowerInvariant(), installationId, fingerprint, DeviceRoles.StoreServer, "1.0.0", $"{nit}-{dv}", "Principal", "SERVIDOR", "Windows 11"),
            Ip));
        activation.IsSuccess.ShouldBeTrue(activation.IsFailure ? activation.Error.Code : null);
        (await db.ScalarAsync<string>($"SELECT role FROM licensing.devices WHERE fingerprint = '{fingerprint}'")).ShouldBe("STORE_SERVER");

        var checkin = await harness.SendAsync(new CheckinCommand(
            new CheckinRequest(activation.Value.Token, fingerprint, "1.0.1", 3, DateTimeOffset.UtcNow), Ip));
        checkin.Value.Succeeded.ShouldBeTrue(checkin.Value.Error?.Code);
        (await db.ScalarAsync<string>("SELECT result || '|' || subscription_status || '|' || host(ip_address) FROM licensing.checkins"))
            .ShouldBe("TOKEN_ISSUED|TRIAL|190.25.10.20");
        (await db.ScalarAsync<int>($"SELECT active_terminals FROM licensing.installations WHERE installation_id = '{installationId}'")).ShouldBe(3);

        // Regenerar: la anterior queda revocada y apunta a la nueva; la instalación y su activación pasan a la nueva.
        var regenerated = await harness.SendAsync(new RegenerateLicenseCommand(license.LicenseId, "La clave se filtró por correo"));
        regenerated.IsSuccess.ShouldBeTrue(regenerated.IsFailure ? regenerated.Error.Code : null);
        var replacement = regenerated.Value.LicenseId;

        (await db.ScalarAsync<string>($"SELECT status || '|' || replaced_by FROM licensing.licenses WHERE id = '{license.LicenseId}'"))
            .ShouldBe($"REVOKED|{replacement}");
        (await db.ScalarAsync<string>($"SELECT status FROM licensing.licenses WHERE id = '{replacement}'")).ShouldBe("ACTIVE");
        (await db.ScalarAsync<Guid>($"SELECT license_id FROM licensing.installations WHERE installation_id = '{installationId}'")).ShouldBe(replacement);
        (await db.ScalarAsync<Guid>("SELECT license_id FROM licensing.activations WHERE status = 'ACTIVE'")).ShouldBe(replacement);

        // El POS sigue con su token anterior y recibe en el check-in el nuevo lic; la clave vieja ya no activa nada.
        var afterRegeneration = await harness.SendAsync(new CheckinCommand(
            new CheckinRequest(checkin.Value.Value!.Token, fingerprint, "1.0.1", 3, DateTimeOffset.UtcNow), Ip));
        afterRegeneration.Value.Succeeded.ShouldBeTrue(afterRegeneration.Value.Error?.Code);
        LicenseToken.Verify(afterRegeneration.Value.Value!.Token, await TrustedKeysAsync(harness)).Claims!.LicenseId.ShouldBe(replacement);
        var oldKey = await harness.SendAsync(new ActivateLicenseCommand(
            new ActivationRequest(license.Key, Guid.CreateVersion7(), fingerprint, DeviceRoles.StoreServer, "1.0.0", $"{nit}-{dv}"), Ip));
        oldKey.Error.Code.ShouldBe(LicenseErrorCodes.KeyInvalid);

        // Lecturas del portal (SQL a mano) sobre los datos reales.
        var detail = (await harness.SendAsync(new GetOrganizationQuery(organization))).Value;
        detail.Licenses.Count.ShouldBe(2);
        detail.Installations.ShouldHaveSingleItem().Activations.ShouldHaveSingleItem().LicenseId.ShouldBe(replacement);
        detail.RecentCheckins.Count.ShouldBe(2);
        detail.Organization.Subscription!.Status.ShouldBe(SubscriptionStatuses.Trial);
        (await harness.SendAsync(new ListInstallationsQuery(organization))).Value.ShouldHaveSingleItem().LastIp.ShouldBe("190.25.10.20");
        (await harness.SendAsync(new ListSubscriptionsQuery())).Value.ShouldHaveSingleItem().DaysLeft.ShouldBeInRange(14, 15);
        var dashboard = (await harness.SendAsync(new GetDashboardQuery())).Value;
        dashboard.ActiveInstallations.ShouldBe(1);
        dashboard.Organizations.ShouldBe(1);
        (await harness.SendAsync(new ListAccountsQuery())).Value.ShouldHaveSingleItem().Organizations.ShouldBe(1);
        (await harness.SendAsync(new GetPublicKeysQuery())).Value.Keys.ShouldHaveSingleItem().Status.ShouldBe("ACTIVE");

        // Todo quedó en la auditoría encadenada y verificable.
        var actions = await db.ListAsync<string>("SELECT action::text FROM audit.audit_log ORDER BY seq");
        actions.ShouldContain("SIGNING_KEY_ACTIVATED");
        actions.ShouldContain("ACCOUNT_CREATED");
        actions.ShouldContain("ORGANIZATION_CREATED");
        actions.ShouldContain("SUBSCRIPTION_CREATED");
        actions.ShouldContain("LICENSE_ISSUED");
        actions.ShouldContain("INSTALLATION_ACTIVATED");
        actions.ShouldContain("LICENSE_REGENERATED");
        (await harness.Services.GetRequiredService<AuditVerifier>().VerifyAsync(TestContext.Current.CancellationToken)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Suspender_renovar_extender_y_liberar_persisten_sus_estados_y_eventos()
    {
        await using var harness = await CloudHarness.CreateAsync(postgres);
        var db = harness.Database;
        (await harness.SendAsync(new EnsureActiveSigningKeyCommand())).Value.ShouldBeTrue();
        var (nit, dv) = CloudSeed.NewNit();
        var account = (await harness.SendAsync(new CreateAccountCommand(new AccountInput("Tienda", "DIRECT", null, null, null, null, null, null, null)))).Value;
        var organization = (await harness.SendAsync(new CreateOrganizationCommand(account, "Tienda SAS", nit, dv, null))).Value;
        var subscription = (await harness.SendAsync(new CreateSubscriptionCommand(organization, "SINGLE", "ANNUAL", Trial: false, PaymentReference: "PAGO-001"))).Value;
        var license = (await harness.SendAsync(new GenerateLicenseCommand(organization, MaxInstallations: 1))).Value;
        var fingerprint = DeviceFingerprint.FromHardware("P", "D", "M").ToString();
        var activation = await harness.SendAsync(new ActivateLicenseCommand(
            new ActivationRequest(license.Key, Guid.CreateVersion7(), fingerprint, DeviceRoles.AllInOne, "1.0.0", $"{nit}-{dv}"), Ip));
        activation.IsSuccess.ShouldBeTrue(activation.IsFailure ? activation.Error.Code : null);

        (await harness.SendAsync(new SuspendSubscriptionCommand(subscription, "Falta de pago de marzo"))).IsSuccess.ShouldBeTrue();
        (await harness.SendAsync(new ReactivateSubscriptionCommand(subscription, "Pagó en efectivo"))).IsSuccess.ShouldBeTrue();
        (await harness.SendAsync(new RenewSubscriptionCommand(subscription, "PAGO-002", 2))).IsSuccess.ShouldBeTrue();
        (await harness.SendAsync(new ExtendGraceCommand(subscription, 5, "El pago está en camino"))).IsSuccess.ShouldBeTrue();
        (await harness.SendAsync(new SetMaxInstallationsCommand(license.LicenseId, null))).IsSuccess.ShouldBeTrue();

        (await db.ListAsync<string>($"SELECT type::text FROM licensing.subscription_events WHERE subscription_id = '{subscription}' ORDER BY occurred_at, type"))
            .ShouldBe(["CREATED", "RENEWED", "SUSPENDED", "REACTIVATED", "RENEWED", "GRACE_EXTENDED"], ignoreOrder: true);
        (await db.ScalarAsync<short>($"SELECT grace_days FROM licensing.subscriptions WHERE id = '{subscription}'")).ShouldBe((short)12);
        (await db.ScalarAsync<string>($"SELECT edition || '|' || billing_period || '|' || status FROM licensing.subscriptions WHERE id = '{subscription}'"))
            .ShouldBe("SINGLE|ANNUAL|ACTIVE");
        (await db.ScalarAsync<bool>($"SELECT max_installations IS NULL FROM licensing.licenses WHERE id = '{license.LicenseId}'")).ShouldBeTrue();

        var activationId = await db.ScalarAsync<Guid>("SELECT id FROM licensing.activations");
        (await harness.SendAsync(new ReleaseActivationCommand(activationId, "El equipo se dañó"))).IsSuccess.ShouldBeTrue();
        (await db.ScalarAsync<string>($"SELECT status || '|' || released_by || '|' || release_reason FROM licensing.activations WHERE id = '{activationId}'"))
            .ShouldBe($"RELEASED|{SystemActor.Id}|El equipo se dañó");
        (await db.ScalarAsync<string>("SELECT status FROM licensing.installations")).ShouldBe("RELEASED");

        // El check-in con el token del equipo liberado se rechaza, y el rechazo también queda registrado (solo inserción).
        var rejected = await harness.SendAsync(new CheckinCommand(new CheckinRequest(activation.Value.Token, fingerprint, "1.0.0", 1, DateTimeOffset.UtcNow), Ip));
        rejected.Value.Error!.Code.ShouldBe(LicenseErrorCodes.ReactivationRequired);
        (await db.ScalarAsync<string>("SELECT result || '|' || rejection_code FROM licensing.checkins")).ShouldBe($"REJECTED|{LicenseErrorCodes.ReactivationRequired}");
    }

    private static async Task<LicenseKeyRing> TrustedKeysAsync(CloudHarness harness) =>
        await harness.Services.GetRequiredService<ITrustedSigningKeys>().GetAsync(TestContext.Current.CancellationToken);
}
