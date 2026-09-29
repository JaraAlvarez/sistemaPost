using System.Net;
using Pos.Cloud.Licensing.Application;
using Pos.License.Simulator;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.IntegrationTests;

/// <summary>§11: suspender, renovar y reactivar desde el portal se reflejan en el siguiente check-in del POS.</summary>
public class SubscriptionLifecycleTests(CloudFixture cloud) : IClassFixture<CloudFixture>
{
    [Fact]
    public async Task Suspender_reactivar_renovar_y_cancelar_llegan_al_POS_en_su_siguiente_checkin()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var pos = cloud.NewPos(customer.Nit);
        await pos.RefreshPublicKeysAsync(Ct);
        (await pos.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();
        pos.Claims!.SubscriptionStatus.ShouldBe(SubscriptionStatuses.Trial);
        var trialEnd = pos.Claims.ValidUntil;

        // Suspender (falta de pago): el POS lo recibe y pasa a restringido.
        await Admin("suspend", customer.SubscriptionId, new { reason = "Falta de pago de octubre" });
        var suspended = await pos.CheckinAsync(cancellationToken: Ct);
        suspended.Succeeded.ShouldBeTrue(suspended.ErrorMessage);
        suspended.Claims!.SubscriptionStatus.ShouldBe(SubscriptionStatuses.Suspended);
        suspended.Messages!.ShouldContain(m => m.Code == "SUBSCRIPTION_SUSPENDED" && m.Severity == "CRITICAL" && m.Text.Contains("Falta de pago de octubre"));
        pos.Mode.ShouldBe(SimulatedLicenseMode.Restricted);

        // Suspendida no activa instalaciones nuevas.
        var branch = cloud.NewPos(customer.Nit);
        await branch.RefreshPublicKeysAsync(Ct);
        (await branch.ActivateAsync(customer.License.Key, Ct)).ErrorCode.ShouldBe(LicenseErrorCodes.SubscriptionInactive);

        // Reactivar: vuelve al estado por fechas (prueba).
        await Admin("reactivate", customer.SubscriptionId, new { reason = "Acuerdo de pago firmado" });
        var reactivated = await pos.CheckinAsync(cancellationToken: Ct);
        reactivated.Claims!.SubscriptionStatus.ShouldBe(SubscriptionStatuses.Trial);
        pos.Mode.ShouldBe(SimulatedLicenseMode.Licensed);

        // Renovar (pago de 2 meses): activa y con la vigencia extendida desde hoy (venía de prueba).
        await Admin("renew", customer.SubscriptionId, new { paymentReference = "CONSIGNACION-778", periods = 2 });
        var renewed = await pos.CheckinAsync(cancellationToken: Ct);
        renewed.Claims!.SubscriptionStatus.ShouldBe(SubscriptionStatuses.Active);
        renewed.Claims.ValidUntil.ShouldBeGreaterThan(trialEnd);
        renewed.Claims.ValidUntil.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(55));
        renewed.Claims.Messages.ShouldNotContain(m => m.Code == "TRIAL");

        // Cambio de edición: el siguiente token lo lleva.
        await Admin("change-edition", customer.SubscriptionId, new { edition = "SINGLE", reason = "Cerró las demás cajas" }, HttpStatusCode.UnprocessableEntity);
        (await pos.CheckinAsync(cancellationToken: Ct)).Claims!.Edition.ShouldBe(LicenseEditions.MultiTerminal);

        // Cancelar revoca también la licencia vigente: el siguiente check-in recibe LICENSE.REVOKED y el POS queda restringido.
        await Admin("cancel", customer.SubscriptionId, new { reason = "El cliente cerró el negocio" });
        var cancelled = await pos.CheckinAsync(cancellationToken: Ct);
        cancelled.ErrorCode.ShouldBe(LicenseErrorCodes.LicenseRevoked);
        pos.Mode.ShouldBe(SimulatedLicenseMode.Restricted);

        // Todo quedó en el historial de la suscripción (solo inserción).
        var detail = await PortalApi.GetAsync<OrganizationDetailDto>(cloud.Superadmin, $"/admin/organizations/{customer.OrganizationId}");
        detail.Events.Select(e => e.Type).ShouldBe(["CREATED", "SUSPENDED", "REACTIVATED", "RENEWED", "CANCELLED"], ignoreOrder: true);
        detail.Events.ShouldContain(e => e.Type == "RENEWED" && e.PaymentReference == "CONSIGNACION-778");
    }

    [Fact]
    public async Task Regenerar_y_revocar_la_clave_se_reflejan_en_el_checkin()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin, trial: false);
        var pos = cloud.NewPos(customer.Nit);
        await pos.RefreshPublicKeysAsync(Ct);
        (await pos.ActivateAsync(customer.License.Key, Ct)).Claims!.SubscriptionStatus.ShouldBe(SubscriptionStatuses.Active);

        var regenerated = await PortalApi.PostAsync<GeneratedLicenseDto>(cloud.Superadmin,
            $"/admin/licenses/{customer.License.LicenseId}/regenerate", new { reason = "La clave se publicó por error" });
        regenerated.Key.ShouldNotBe(customer.License.Key);

        // El POS no escribe la clave nueva: su check-in trae el nuevo lic.
        var moved = await pos.CheckinAsync(cancellationToken: Ct);
        moved.Succeeded.ShouldBeTrue(moved.ErrorMessage);
        moved.Claims!.LicenseId.ShouldBe(regenerated.LicenseId);

        // La clave anterior ya no activa nada (misma respuesta que una clave inexistente).
        var other = cloud.NewPos(customer.Nit);
        await other.RefreshPublicKeysAsync(Ct);
        (await other.ActivateAsync(customer.License.Key, Ct)).ErrorCode.ShouldBe(LicenseErrorCodes.KeyInvalid);
        (await other.ActivateAsync(regenerated.Key, Ct)).Succeeded.ShouldBeTrue();

        // Revocar sin reemplazo: los POS reciben LICENSE.REVOKED en su siguiente check-in.
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/licenses/{regenerated.LicenseId}/revoke", new { reason = "Contrato terminado" }))
            .Status.ShouldBe(HttpStatusCode.NoContent);
        var revoked = await pos.CheckinAsync(cancellationToken: Ct);
        revoked.StatusCode.ShouldBe(403);
        revoked.ErrorCode.ShouldBe(LicenseErrorCodes.LicenseRevoked);
    }

    [Fact]
    public async Task Liberar_el_equipo_desde_el_portal_permite_activar_el_PC_nuevo()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var oldPc = cloud.NewPos(customer.Nit);
        await oldPc.RefreshPublicKeysAsync(Ct);
        (await oldPc.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();

        // El PC nuevo de la MISMA sucursal (misma instalación) no puede activarse mientras el anterior siga activo.
        var newPc = new SimulatedPos(cloud.Factory.CreateClient(), oldPc.Identity);
        newPc.ChangeHardware("placa-nueva", "disco-nuevo", "maquina-nueva");
        await newPc.RefreshPublicKeysAsync(Ct);
        (await newPc.ActivateAsync(customer.License.Key, Ct)).ErrorCode.ShouldBe(LicenseErrorCodes.InstallationActiveOnOtherDevice);

        var installation = (await PortalApi.GetAsync<List<InstallationDto>>(cloud.Superadmin, $"/admin/installations?organizationId={customer.OrganizationId}")).ShouldHaveSingleItem();
        var activation = installation.Activations.Single(a => a.Status == "ACTIVE");
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/activations/{activation.Id}/release", new { reason = "El equipo se dañó" }))
            .Status.ShouldBe(HttpStatusCode.NoContent);

        (await newPc.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();
        (await oldPc.CheckinAsync(cancellationToken: Ct)).ErrorCode.ShouldBe(LicenseErrorCodes.ReactivationRequired);
    }

    private async Task Admin(string action, Guid subscription, object body, HttpStatusCode expected = HttpStatusCode.NoContent) =>
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/subscriptions/{subscription}/{action}", body)).Status.ShouldBe(expected, action);
}
