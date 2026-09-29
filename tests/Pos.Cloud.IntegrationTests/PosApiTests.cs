using System.Net;
using System.Net.Http.Json;
using Pos.License.Simulator;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.IntegrationTests;

/// <summary>API del POS (/v1): activación, check-in y liberación con códigos de error estables (contrato con la Fase 12-B).</summary>
public class PosApiTests(CloudFixture cloud) : IClassFixture<CloudFixture>
{
    private static string Fingerprint(string seed) => DeviceFingerprint.FromHardware($"placa-{seed}", $"disco-{seed}", $"maq-{seed}").ToString();

    [Fact]
    public async Task Activar_devuelve_un_token_Ed25519_con_kid_verificable_con_las_claves_publicadas()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var pos = cloud.NewPos(customer.Nit);
        var published = await pos.RefreshPublicKeysAsync(Ct);

        var result = await pos.ActivateAsync(customer.License.Key, Ct);

        result.Succeeded.ShouldBeTrue(result.ErrorMessage);
        result.StatusCode.ShouldBe(200);
        result.TokenStatus.ShouldBe(LicenseTokenStatus.Valid);
        var active = published.ShouldHaveSingleItem();
        active.Status.ShouldBe("ACTIVE");
        active.Kid.ShouldBe(cloud.Factory.PublicKey.Kid);
        LicenseToken.Verify(pos.Token, pos.TrustedKeys).Kid.ShouldBe(active.Kid);

        var claims = result.Claims!;
        claims.LicenseId.ShouldBe(customer.License.LicenseId);
        claims.OrganizationNit.ShouldBe(customer.Nit);
        claims.InstallationId.ShouldBe(pos.Identity.InstallationId);
        claims.DeviceFingerprint.ShouldBe(pos.Identity.Fingerprint);
        claims.Edition.ShouldBe(LicenseEditions.MultiTerminal);
        claims.SubscriptionStatus.ShouldBe(SubscriptionStatuses.Trial);
        claims.Messages.ShouldContain(m => m.Code == "TRIAL");
        claims.Messages.ShouldContain(m => m.Code == "UPDATE_AVAILABLE");
        pos.Mode.ShouldBe(SimulatedLicenseMode.Licensed);

        // Un token alterado no verifica (ni en el POS ni en el servidor).
        var parts = pos.Token!.Split('.');
        var tampered = $"{parts[0]}.{parts[1][..^2]}AA.{parts[2]}";
        LicenseToken.Verify(tampered, pos.TrustedKeys).Status.ShouldNotBe(LicenseTokenStatus.Valid);
        var response = await cloud.Factory.CreateClient().PostAsJsonAsync(LicensingRoutes.Checkins,
            new CheckinRequest(tampered, pos.Identity.Fingerprint, "1.0.0", 1, DateTimeOffset.UtcNow), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PortalApi.ErrorCodeAsync(response)).ShouldBe(LicenseErrorCodes.TokenInvalid);
    }

    [Fact]
    public async Task Los_errores_de_activacion_tienen_codigo_estable()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin, maxInstallations: 1);
        var client = cloud.Factory.CreateClient();
        var installation = Guid.CreateVersion7();

        async Task<(HttpStatusCode, string?)> Activate(string key, Guid installationId, string fingerprint, string role, string nit)
        {
            using var response = await client.PostAsJsonAsync(LicensingRoutes.Activations,
                new ActivationRequest(key, installationId, fingerprint, role, "1.0.0", nit), Ct);
            return (response.StatusCode, await PortalApi.ErrorCodeAsync(response));
        }

        (await Activate("POS-12345", installation, Fingerprint("a"), DeviceRoles.StoreServer, customer.Nit))
            .ShouldBe((HttpStatusCode.BadRequest, LicenseErrorCodes.KeyFormatInvalid));
        (await Activate(LicenseKey.Generate(), installation, Fingerprint("a"), DeviceRoles.StoreServer, customer.Nit))
            .ShouldBe((HttpStatusCode.Forbidden, LicenseErrorCodes.KeyInvalid));
        (await Activate(customer.License.Key, installation, "fp1.-.-.-", DeviceRoles.StoreServer, customer.Nit))
            .ShouldBe((HttpStatusCode.BadRequest, LicenseErrorCodes.FingerprintInvalid));
        (await Activate(customer.License.Key, installation, Fingerprint("a"), DeviceRoles.StoreServer, "900123456-8"))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, LicenseErrorCodes.NitMismatch));
        (await Activate(customer.License.Key, installation, Fingerprint("a"), "CAJA", customer.Nit)).Item1.ShouldBe(HttpStatusCode.BadRequest);

        // Correcta; repetirla desde el mismo equipo (aunque cambió el disco: 2 de 3) es idempotente.
        (await Activate(customer.License.Key, installation, Fingerprint("a"), DeviceRoles.StoreServer, customer.Nit)).ShouldBe((HttpStatusCode.OK, null));
        var sameMachineNewDisk = DeviceFingerprint.FromHardware("placa-a", "disco-NUEVO", "maq-a").ToString();
        (await Activate(customer.License.Key, installation, sameMachineNewDisk, DeviceRoles.StoreServer, customer.Nit)).ShouldBe((HttpStatusCode.OK, null));

        // La misma instalación en OTRO equipo: hay que liberar el anterior desde el portal.
        (await Activate(customer.License.Key, installation, Fingerprint("b"), DeviceRoles.StoreServer, customer.Nit))
            .ShouldBe((HttpStatusCode.Conflict, LicenseErrorCodes.InstallationActiveOnOtherDevice));

        // Otra sucursal con el máximo de 1 instalación.
        (await Activate(customer.License.Key, Guid.CreateVersion7(), Fingerprint("c"), DeviceRoles.StoreServer, customer.Nit))
            .ShouldBe((HttpStatusCode.Conflict, LicenseErrorCodes.InstallationsExceeded));

        // Caja Única solo se instala en un equipo que hace todo.
        var single = await LicensedCustomer.CreateAsync(cloud.Superadmin, edition: LicenseEditions.SingleTerminal);
        (await Activate(single.License.Key, Guid.CreateVersion7(), Fingerprint("d"), DeviceRoles.StoreServer, single.Nit))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, LicenseErrorCodes.EditionMismatch));
        (await Activate(single.License.Key, Guid.CreateVersion7(), Fingerprint("d"), DeviceRoles.AllInOne, single.Nit)).ShouldBe((HttpStatusCode.OK, null));

        // Una instalación de otra empresa no se puede "robar" con otra clave.
        (await Activate(single.License.Key, installation, Fingerprint("a"), DeviceRoles.AllInOne, single.Nit))
            .ShouldBe((HttpStatusCode.Conflict, LicenseErrorCodes.InstallationOfOtherLicense));
    }

    [Fact]
    public async Task Checkin_y_liberacion_desde_el_POS()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var pos = cloud.NewPos(customer.Nit);
        await pos.RefreshPublicKeysAsync(Ct);
        (await pos.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();
        var first = pos.Claims!;

        var checkin = await pos.CheckinAsync(4, Ct);
        checkin.Succeeded.ShouldBeTrue(checkin.ErrorMessage);
        checkin.Claims!.IssuedAt.ShouldBeGreaterThanOrEqualTo(first.IssuedAt);
        checkin.Claims.RefreshAfter.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(23));

        // Otro PC con la copia del token: ningún componente coincide.
        var copy = SimulatedPos.Restore(cloud.Factory.CreateClient(), pos.Export());
        copy.ChangeHardware("otra-placa", "otro-disco", "otra-maquina");
        (await copy.CheckinAsync(cancellationToken: Ct)).ErrorCode.ShouldBe(LicenseErrorCodes.ReactivationRequired);

        // Liberar desde el POS: el token ya no sirve y la sucursal puede activar otro equipo con su clave.
        var saved = pos.Token!;
        (await pos.DeactivateAsync("Cambio de computador", Ct)).Succeeded.ShouldBeTrue();
        pos.Token.ShouldBeNull();
        using var afterRelease = await cloud.Factory.CreateClient().PostAsJsonAsync(LicensingRoutes.Checkins,
            new CheckinRequest(saved, pos.Identity.Fingerprint, "1.0.0", 1, DateTimeOffset.UtcNow), Ct);
        afterRelease.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PortalApi.ErrorCodeAsync(afterRelease)).ShouldBe(LicenseErrorCodes.ReactivationRequired);

        pos.ChangeHardware("placa-nueva", "disco-nuevo", "maquina-nueva");
        (await pos.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();

        // El historial de check-ins (incluidos los rechazos) queda en la ficha de la empresa.
        var detail = await PortalApi.GetAsync<Pos.Cloud.Licensing.Application.OrganizationDetailDto>(cloud.Superadmin, $"/admin/organizations/{customer.OrganizationId}");
        detail.RecentCheckins.ShouldContain(c => c.Result == "REJECTED" && c.RejectionCode == LicenseErrorCodes.ReactivationRequired);
        detail.RecentCheckins.ShouldContain(c => c.Result == "TOKEN_ISSUED" && c.ActiveTerminals == 4);
        detail.Installations.ShouldHaveSingleItem().Devices.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Las_claves_publicas_y_la_API_del_POS_son_anonimas_pero_la_API_interna_no()
    {
        var client = cloud.Factory.CreateClient();

        (await client.GetAsync(new Uri(LicensingRoutes.PublicKeys, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var (status, code) = await PortalApi.SendAsync(client, HttpMethod.Get, "/admin/dashboard");
        status.ShouldBe(HttpStatusCode.Unauthorized);
        code.ShouldBe("AUTH.REQUIRED");

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token-inventado");
        (await PortalApi.SendAsync(client, HttpMethod.Get, "/admin/dashboard")).Status.ShouldBe(HttpStatusCode.Unauthorized);

        // Cabeceras de seguridad.
        using var health = await cloud.Factory.CreateClient().GetAsync(new Uri("/health", UriKind.Relative), Ct);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        health.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        health.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        health.Headers.Contains("Content-Security-Policy").ShouldBeTrue();
    }
}
