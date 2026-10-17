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
