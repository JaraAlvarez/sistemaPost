using System.Net;
using Pos.Cloud.Abstractions;
using Pos.Cloud.PortalIdentity.Application;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// N2 y PN6: con un ingreso de hace más de 10 minutos, las acciones delicadas sobre OTRO usuario (restablecer, cambiar el rol, desactivar,
/// desbloquear, crear un superadministrador) piden confirmar con la contraseña o el código TOTP. PN7: esta clase tiene su propio servidor
/// con reloj adelantable y lo deja en la hora real al terminar cada prueba (antes se adelantaba el reloj de otro fixture compartido).
/// En este servidor el doble factor es opcional y el superadministrador no lo tiene activo.
/// </summary>
public sealed class PortalAdminReauthTests(ShiftableClockCloudFixture cloud) : IClassFixture<ShiftableClockCloudFixture>, IDisposable
{
    public void Dispose() => cloud.Factory.Clock.Reset();

    [Fact]
    public async Task Restablecer_a_otro_usuario_con_un_ingreso_antiguo_pide_confirmar_la_identidad()
    {
        var (_, support) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "SUPPORT");
        var id = await IdOfAsync(support.Email);
        using var admin = await OldSignInAsync();
        var url = $"/admin/users/{id}/reset-password";

        (await PortalApi.SendAsync(admin, HttpMethod.Post, url)).Code.ShouldBe("PORTAL.REAUTH_REQUIRED");
        (await PortalApi.SendAsync(admin, HttpMethod.Post, url, new { confirmation = "otra-contrasena-123" })).Code.ShouldBe("PORTAL.INVALID_CONFIRMATION");
        (await PortalApi.SendAsync(admin, HttpMethod.Post, url, new { confirmation = PortalApi.NewPassword })).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cambiar_el_rol_desactivar_y_desbloquear_con_un_ingreso_antiguo_piden_confirmar_la_identidad()
    {
        var (_, support) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "SUPPORT");
        var id = await IdOfAsync(support.Email);
        using var admin = await OldSignInAsync();
        var url = $"/admin/users/{id}";

        // Cambiar solo el nombre no es delicado.
        (await PortalApi.SendAsync(admin, HttpMethod.Put, url, new { displayName = "Soporte renombrado", role = "SUPPORT", isActive = true }))
            .Status.ShouldBe(HttpStatusCode.NoContent);

        // Desactivar: confirmar con la contraseña.
        (await PortalApi.SendAsync(admin, HttpMethod.Put, url, new { displayName = "Soporte renombrado", role = "SUPPORT", isActive = false }))
            .Code.ShouldBe("PORTAL.REAUTH_REQUIRED");
        (await PortalApi.SendAsync(admin, HttpMethod.Put, url,
            new { displayName = "Soporte renombrado", role = "SUPPORT", isActive = false, confirmation = PortalApi.NewPassword })).Status.ShouldBe(HttpStatusCode.NoContent);

        // Desbloquear: igual.
        (await PortalApi.SendAsync(admin, HttpMethod.Post, $"{url}/unlock")).Code.ShouldBe("PORTAL.REAUTH_REQUIRED");
        (await PortalApi.SendAsync(admin, HttpMethod.Post, $"{url}/unlock", new { confirmation = PortalApi.NewPassword })).Status.ShouldBe(HttpStatusCode.NoContent);

        // Convertirlo en superadministrador o crear uno: siempre el TOTP del que actúa (aquí no lo tiene activo).
        (await PortalApi.SendAsync(admin, HttpMethod.Put, url,
            new { displayName = "Soporte renombrado", role = "SUPERADMIN", isActive = false, confirmation = PortalApi.NewPassword })).Code.ShouldBe("PORTAL.ACTOR_TOTP_REQUIRED");
        (await PortalApi.SendAsync(admin, HttpMethod.Post, "/admin/users",
            new { email = $"super-{Guid.NewGuid():N}@licencias.co", displayName = "Otro", role = "SUPERADMIN", confirmation = PortalApi.NewPassword }))
            .Code.ShouldBe("PORTAL.ACTOR_TOTP_REQUIRED");

        var audit = await PortalApi.GetAsync<List<CloudAuditEntryDto>>(admin, $"/admin/audit?action=PORTAL_USER_UNLOCKED&text={Uri.EscapeDataString(support.Email)}");
        audit.ShouldContain(a => a.Summary!.Contains("dueno@licencias.co", StringComparison.Ordinal));
    }

    /// <summary>IL6: reactivar, desactivar a un usuario bloqueado por intentos y cambiar la cuenta asociada también piden confirmar.</summary>
    [Fact]
    public async Task Reactivar_desactivar_un_bloqueado_y_cambiar_la_cuenta_piden_confirmar_la_identidad()
    {
        var first = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var second = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var (_, customer) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "CUSTOMER", first.AccountId);
        var (_, support) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "SUPPORT");
        var customerUrl = $"/admin/users/{await IdOfAsync(customer.Email)}";
        var supportUrl = $"/admin/users/{await IdOfAsync(support.Email)}";

        // Bloqueado por intentos fallidos (sigue "activo").
        using (var anonymous = cloud.Factory.CreateClient())
        {
            for (var i = 0; i < 5; i++)
            {
                await PortalApi.SendAsync(anonymous, HttpMethod.Post, "/admin/auth/login", new { email = support.Email, password = "incorrecta-12345" });
            }
        }

        using var admin = await OldSignInAsync();

        object Support(bool active, string? confirmation = null) => new { displayName = "Soporte", role = "SUPPORT", isActive = active, confirmation };
        (await PortalApi.SendAsync(admin, HttpMethod.Put, supportUrl, Support(false))).Code.ShouldBe("PORTAL.REAUTH_REQUIRED");
        (await PortalApi.SendAsync(admin, HttpMethod.Put, supportUrl, Support(false, PortalApi.NewPassword))).Status.ShouldBe(HttpStatusCode.NoContent);
        (await PortalApi.SendAsync(admin, HttpMethod.Put, supportUrl, Support(true))).Code.ShouldBe("PORTAL.REAUTH_REQUIRED", "Reactivar también.");
        (await PortalApi.SendAsync(admin, HttpMethod.Put, supportUrl, Support(true, PortalApi.NewPassword))).Status.ShouldBe(HttpStatusCode.NoContent);

        object Customer(Guid account, string? confirmation = null) =>
            new { displayName = "Cliente", role = "CUSTOMER", resellerAccountId = account, isActive = true, confirmation };
        (await PortalApi.SendAsync(admin, HttpMethod.Put, customerUrl, Customer(first.AccountId))).Status.ShouldBe(HttpStatusCode.NoContent, "Solo el nombre.");
        (await PortalApi.SendAsync(admin, HttpMethod.Put, customerUrl, Customer(second.AccountId))).Code.ShouldBe("PORTAL.REAUTH_REQUIRED");
        (await PortalApi.SendAsync(admin, HttpMethod.Put, customerUrl, Customer(second.AccountId, PortalApi.NewPassword))).Status.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>IL6: sobre sí mismo no se cambia el rol ni el estado (con su propio error, no el de "restablecer").</summary>
    [Fact]
    public async Task Cambiarse_el_rol_o_desactivarse_da_su_propio_error()
    {
        var me = await IdOfAsync("dueno@licencias.co");
        foreach (var body in new object[]
                 {
                     new { displayName = "Dueño", role = "SUPPORT", isActive = true, confirmation = PortalApi.NewPassword },
                     new { displayName = "Dueño", role = "SUPERADMIN", isActive = false, confirmation = PortalApi.NewPassword },
                 })
        {
            (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Put, $"/admin/users/{me}", body)).Code.ShouldBe("PORTAL.CANNOT_CHANGE_SELF");
        }
    }

    /// <summary>Una sesión del superadministrador que sigue abierta (se usa cada pocos minutos) pero cuyo ingreso fue hace más de 10 minutos.</summary>
    private async Task<HttpClient> OldSignInAsync()
    {
        var admin = await PortalApi.SignInAsync(cloud.Factory, new PortalCredentials("dueno@licencias.co", PortalApi.NewPassword));
        for (var i = 0; i < 3; i++)
        {
            cloud.Factory.Clock.Advance(TimeSpan.FromMinutes(4));
            (await PortalApi.SendAsync(admin, HttpMethod.Get, "/admin/users")).Status.ShouldBe(HttpStatusCode.OK, "La sesión sigue abierta.");
        }

        return admin;
    }

    private async Task<Guid> IdOfAsync(string email) =>
        (await PortalApi.GetAsync<List<PortalUserDto>>(cloud.Superadmin, "/admin/users")).Single(u => u.Email == email).Id;
}
