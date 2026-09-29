using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pos.Cloud.Licensing.Application;
using Pos.Cloud.PortalIdentity.Application;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// Ingreso al portal (L-08): contraseña + TOTP obligatorio (el código se calcula como una app autenticadora), cambio de la
/// contraseña temporal, cierre de sesión y bloqueo; permisos por rol aplicados igual en la API interna y en el pipeline.
/// </summary>
public class PortalAccessTests(CloudFixture cloud) : IClassFixture<CloudFixture>
{
    [Fact]
    public async Task Ingreso_con_contrasena_y_TOTP_con_enrolamiento_cambio_de_contrasena_y_salida()
    {
        var email = $"soporte-{Guid.NewGuid():N}@licencias.co";
        var temporary = await PortalApi.PostAsync<TemporaryPasswordDto>(cloud.Superadmin, "/admin/users",
            new { email, displayName = "Soporte Uno", role = "SUPPORT" });
        var anonymous = cloud.Factory.CreateClient();

        // Contraseña incorrecta: misma respuesta que un usuario inexistente.
        (await PortalApi.SendAsync(anonymous, HttpMethod.Post, "/admin/auth/login", new { email, password = "incorrecta-12345" }))
            .ShouldBe((HttpStatusCode.Unauthorized, "PORTAL.INVALID_CREDENTIALS"));
        (await PortalApi.SendAsync(anonymous, HttpMethod.Post, "/admin/auth/login", new { email = "nadie@licencias.co", password = "incorrecta-12345" }))
            .ShouldBe((HttpStatusCode.Unauthorized, "PORTAL.INVALID_CREDENTIALS"));

        // Paso 1: contraseña correcta → token PENDIENTE que aún no abre la API interna.
        var challenge = await PortalApi.PostAsync<LoginChallengeDto>(anonymous, "/admin/auth/login", new { email, password = temporary.TemporaryPassword });
        challenge.Stage.ShouldBe("ENROLLMENT_REQUIRED");
        var pending = cloud.Factory.CreateClient();
        pending.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", challenge.Token);
        (await PortalApi.SendAsync(pending, HttpMethod.Get, "/admin/dashboard")).Status.ShouldBe(HttpStatusCode.Forbidden);

        // Enrolamiento: secreto Base32 + enlace otpauth:// (el QR); un código incorrecto no lo confirma.
        var enrollment = await PortalApi.PostAsync<TotpEnrollmentDto>(anonymous, "/admin/auth/totp/enrollment", new { token = challenge.Token });
        var secret = Authenticator.FromBase32(enrollment.Secret);
        secret.Length.ShouldBe(20);
        var wrong = Authenticator.Code(secret, Authenticator.CurrentStep + 5);
        (await PortalApi.SendAsync(anonymous, HttpMethod.Post, "/admin/auth/totp", new { token = challenge.Token, code = wrong }))
            .ShouldBe((HttpStatusCode.Unauthorized, "PORTAL.INVALID_TOTP"));

        var step = Authenticator.CurrentStep;
        var code = Authenticator.Code(secret, step);
        var session = await PortalApi.PostAsync<PortalSessionDto>(anonymous, "/admin/auth/totp", new { token = challenge.Token, code });
        session.Token.ShouldNotBe(challenge.Token);
        session.User.MustChangePassword.ShouldBeTrue();
        session.User.Role.ShouldBe("SUPPORT");

        // El token pendiente ya no sirve (el token de la sesión se renueva al completar el segundo factor).
        (await PortalApi.SendAsync(anonymous, HttpMethod.Post, "/admin/auth/totp", new { token = challenge.Token, code }))
            .ShouldBe((HttpStatusCode.Unauthorized, "PORTAL.SESSION_INVALID"));

        // Con la contraseña temporal no hay permisos hasta cambiarla.
        var client = cloud.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        (await PortalApi.SendAsync(client, HttpMethod.Get, "/admin/dashboard")).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await PortalApi.SendAsync(client, HttpMethod.Post, "/admin/auth/password", new { currentPassword = temporary.TemporaryPassword, newPassword = "corta1" }))
            .ShouldBe((HttpStatusCode.BadRequest, "PORTAL.WEAK_PASSWORD"));
        (await PortalApi.SendAsync(client, HttpMethod.Post, "/admin/auth/password", new { currentPassword = temporary.TemporaryPassword, newPassword = PortalApi.NewPassword }))
            .Status.ShouldBe(HttpStatusCode.NoContent);
        (await PortalApi.SendAsync(client, HttpMethod.Get, "/admin/dashboard")).Status.ShouldBe(HttpStatusCode.OK);

        var me = await client.GetFromJsonAsync<MeResponse>("/admin/auth/me", PortalApi.Json, Ct);
        me!.Me.Email.ShouldBe(email);
        me.Me.Permissions.ShouldContain("licensing.dashboard.view");

        // Segundo ingreso: TOTP_REQUIRED; el MISMO código (mismo paso) no sirve dos veces.
        var again = await PortalApi.PostAsync<LoginChallengeDto>(anonymous, "/admin/auth/login", new { email, password = PortalApi.NewPassword });
        again.Stage.ShouldBe("TOTP_REQUIRED");
        (await PortalApi.SendAsync(anonymous, HttpMethod.Post, "/admin/auth/totp", new { token = again.Token, code }))
            .ShouldBe((HttpStatusCode.Unauthorized, "PORTAL.INVALID_TOTP"));
        var second = await PortalApi.PostAsync<PortalSessionDto>(anonymous, "/admin/auth/totp",
            new { token = again.Token, code = Authenticator.Code(secret, step + 1) });
        second.User.MustChangePassword.ShouldBeFalse();

        // Salir revoca la sesión en la BD: el token deja de servir de inmediato.
        (await PortalApi.SendAsync(client, HttpMethod.Post, "/admin/auth/logout")).Status.ShouldBe(HttpStatusCode.NoContent);
        (await PortalApi.SendAsync(client, HttpMethod.Get, "/admin/dashboard")).Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tras_varios_intentos_fallidos_el_usuario_queda_bloqueado()
    {
        var email = $"bloqueo-{Guid.NewGuid():N}@licencias.co";
        var temporary = await PortalApi.PostAsync<TemporaryPasswordDto>(cloud.Superadmin, "/admin/users",
            new { email, displayName = "Bloqueo", role = "SUPPORT" });
        var anonymous = cloud.Factory.CreateClient();

        for (var i = 0; i < 4; i++)
        {
            (await PortalApi.SendAsync(anonymous, HttpMethod.Post, "/admin/auth/login", new { email, password = "incorrecta-12345" }))
                .Code.ShouldBe("PORTAL.INVALID_CREDENTIALS");
        }

        (await PortalApi.SendAsync(anonymous, HttpMethod.Post, "/admin/auth/login", new { email, password = "incorrecta-12345" }))
            .Code.ShouldBe("PORTAL.USER_LOCKED");

        // Bloqueado, ni la contraseña correcta entra; el superadministrador lo desbloquea.
        (await PortalApi.SendAsync(anonymous, HttpMethod.Post, "/admin/auth/login", new { email, password = temporary.TemporaryPassword }))
            .Code.ShouldBe("PORTAL.USER_LOCKED");
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/users/{temporary.UserId}/unlock")).Status.ShouldBe(HttpStatusCode.NoContent);
        (await PortalApi.SendAsync(anonymous, HttpMethod.Post, "/admin/auth/login", new { email, password = temporary.TemporaryPassword }))
            .Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Soporte_atiende_pero_no_administra_y_el_superadministrador_puede_todo()
    {
        var (support, _) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "SUPPORT");
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var subscription = customer.SubscriptionId;

        // Lo que Soporte SÍ puede: ver, reactivar, extender la gracia, liberar equipos y ver la auditoría.
        foreach (var url in new[] { "/admin/dashboard", "/admin/accounts", "/admin/organizations", "/admin/subscriptions", "/admin/installations", "/admin/audit" })
        {
            (await PortalApi.SendAsync(support, HttpMethod.Get, url)).Status.ShouldBe(HttpStatusCode.OK, url);
        }

        (await PortalApi.SendAsync(support, HttpMethod.Post, $"/admin/subscriptions/{subscription}/extend-grace", new { days = 3, reason = "Pago en camino" }))
            .Status.ShouldBe(HttpStatusCode.NoContent);

        // Lo que NO puede: crear clientes, suspender, generar o regenerar claves, ver claves de firma, administrar usuarios.
        var denied = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, "/admin/accounts", new AccountInput("X", "DIRECT", null, null, null, null, null, null, null)),
            (HttpMethod.Post, $"/admin/subscriptions/{subscription}/suspend", new { reason = "Falta de pago" }),
            (HttpMethod.Post, $"/admin/subscriptions/{subscription}/renew", new { paymentReference = "PAGO-9", periods = 1 }),
            (HttpMethod.Post, $"/admin/licenses/{customer.License.LicenseId}/regenerate", new { reason = "Se perdió la clave" }),
            (HttpMethod.Post, "/admin/licenses", new { organizationId = customer.OrganizationId }),
            (HttpMethod.Get, "/admin/signing-keys", null),
            (HttpMethod.Get, "/admin/users", null),
            (HttpMethod.Post, "/admin/users", new { email = "x@licencias.co", displayName = "X", role = "SUPERADMIN" }),
        };
        foreach (var (method, url, body) in denied)
        {
            (await PortalApi.SendAsync(support, method, url, body)).ShouldBe((HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED"), $"{method} {url}");
        }

        // El superadministrador sí (y la suspensión se revierte con la reactivación, que también puede Soporte).
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/subscriptions/{subscription}/suspend", new { reason = "Falta de pago" }))
            .Status.ShouldBe(HttpStatusCode.NoContent);
        (await PortalApi.SendAsync(support, HttpMethod.Post, $"/admin/subscriptions/{subscription}/reactivate", new { reason = "Pagó por transferencia" }))
            .Status.ShouldBe(HttpStatusCode.NoContent);
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Get, "/admin/signing-keys")).Status.ShouldBe(HttpStatusCode.OK);
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Get, "/admin/users")).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task El_distribuidor_no_accede_a_la_API_interna()
    {
        // Desviación documentada: el rol RESELLER existe en el modelo (con su cuenta) pero en 12-A no tiene permisos ni pantallas;
        // la prueba "el distribuidor solo ve sus clientes" se reemplaza por esta.
        var reseller = await PortalApi.PostAsync<Guid>(cloud.Superadmin, "/admin/accounts",
            new AccountInput("Distribuidor del Caribe", "RESELLER", null, null, null, null, null, null, null));
        var (client, _) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, "RESELLER", reseller);

        var me = await client.GetFromJsonAsync<MeResponse>("/admin/auth/me", PortalApi.Json, Ct);
        me!.Me.Role.ShouldBe("RESELLER");
        me.Me.Permissions.ShouldBeEmpty();

        foreach (var url in new[] { "/admin/dashboard", "/admin/accounts", "/admin/organizations", "/admin/subscriptions", "/admin/installations", "/admin/audit", "/admin/signing-keys", "/admin/users" })
        {
            (await PortalApi.SendAsync(client, HttpMethod.Get, url)).ShouldBe((HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED"), url);
        }
    }

    private sealed record MeResponse(PortalMeDto Me, IReadOnlyList<PortalSessionInfoDto> Sessions);
}
