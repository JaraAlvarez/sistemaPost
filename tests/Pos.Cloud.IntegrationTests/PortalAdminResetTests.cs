using System.Net;
using Pos.Cloud.Abstractions;
using Pos.Cloud.PortalIdentity.Application;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// N2/N3: restablecer la contraseña o el doble factor de OTRO usuario exige un ingreso reciente (o confirmar en el momento), el TOTP del
/// superadministrador si el afectado es otro superadministrador, nunca sobre sí mismo; y restablecer el TOTP también restablece la
/// contraseña (temporal) por defecto. Doble factor obligatorio en este servidor.
/// </summary>
public class PortalAdminResetTests(CloudFixture cloud) : IClassFixture<CloudFixture>
{
    [Fact]
    public async Task No_se_puede_restablecer_a_si_mismo_desde_la_administracion()
    {
        var me = await IdOfAsync(cloud.SuperadminUser.Email);
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/users/{me}/reset-password")).Code.ShouldBe("PORTAL.CANNOT_RESET_SELF");
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/users/{me}/reset-totp", new { reason = "Prueba de seguridad" }))
            .Code.ShouldBe("PORTAL.CANNOT_RESET_SELF");
    }

    [Fact]
    public async Task Restablecer_el_TOTP_tambien_restablece_la_contrasena_por_defecto()
    {
        var (_, support) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "SUPPORT");
        var id = await IdOfAsync(support.Email);

        var reset = await PortalApi.PostAsync<TotpResetDto>(cloud.Superadmin, $"/admin/users/{id}/reset-totp", new { reason = "Teléfono perdido" });
        reset.TemporaryPassword.ShouldNotBeNullOrWhiteSpace();
        var user = (await PortalApi.GetAsync<List<PortalUserDto>>(cloud.Superadmin, "/admin/users")).Single(u => u.Id == id);
        (user.TotpEnabled, user.MustChangePassword).ShouldBe((false, true));

        // La contraseña anterior ya no sirve; la temporal sí (y lleva a enrolar el TOTP de nuevo).
        using var anonymous = cloud.Factory.CreateClient();
        (await PortalApi.SendAsync(anonymous, HttpMethod.Post, "/admin/auth/login", new { email = support.Email, password = support.Password })).Status
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await PortalApi.PostAsync<LoginChallengeDto>(anonymous, "/admin/auth/login", new { email = support.Email, password = reset.TemporaryPassword }))
            .Stage.ShouldBe("ENROLLMENT_REQUIRED");

        // Explícitamente sin la contraseña: solo el doble factor (queda en la auditoría).
        var (_, other) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "SUPPORT");
        var onlyTotp = await PortalApi.PostAsync<TotpResetDto>(cloud.Superadmin, $"/admin/users/{await IdOfAsync(other.Email)}/reset-totp",
            new { reason = "Cambió de teléfono", alsoResetPassword = false });
        onlyTotp.TemporaryPassword.ShouldBeNull();
        var audit = await PortalApi.GetAsync<List<CloudAuditEntryDto>>(cloud.Superadmin, $"/admin/audit?action=PORTAL_TOTP_RESET&text={Uri.EscapeDataString(other.Email)}");
        audit.ShouldContain(a => a.Summary!.Contains("la contraseña NO se restableció", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Afectar_a_otro_superadministrador_exige_el_codigo_TOTP_del_que_actua()
    {
        var (_, target) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "SUPERADMIN");
        var id = await IdOfAsync(target.Email);
        var url = $"/admin/users/{id}/reset-password";

        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, url)).Code.ShouldBe("PORTAL.TOTP_CONFIRMATION_REQUIRED");
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, url, new { confirmation = cloud.SuperadminUser.Password }))
            .Code.ShouldBe("PORTAL.INVALID_CONFIRMATION", "Con otro superadministrador la contraseña no basta.");
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, url, new { confirmation = "000000" })).Code.ShouldBe("PORTAL.INVALID_CONFIRMATION");

        var reset = await PortalApi.PostAsync<TemporaryPasswordDto>(cloud.Superadmin, url, new { confirmation = cloud.SuperadminUser.NextCode() });
        reset.Email.ShouldBe(target.Email);

        var failures = await PortalApi.GetAsync<List<CloudAuditEntryDto>>(cloud.Superadmin, "/admin/audit?action=PORTAL_REAUTH_FAILED");
        failures.Count(a => a.Summary!.Contains(target.Email, StringComparison.Ordinal)).ShouldBe(2);
    }

    private async Task<Guid> IdOfAsync(string email) =>
        (await PortalApi.GetAsync<List<PortalUserDto>>(cloud.Superadmin, "/admin/users")).Single(u => u.Email == email).Id;
}
