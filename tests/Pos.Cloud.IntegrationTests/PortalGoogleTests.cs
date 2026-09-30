using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Pos.Cloud.Abstractions;
using Pos.Cloud.PortalIdentity.Application;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// Google simulado: responde el intercambio del código (token) y la información del usuario (userinfo) como lo haría Google.
/// Cada "código" de autorización se registra con la identidad que Google devolvería para él.
/// </summary>
public sealed class FakeGoogle : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Dictionary<string, object>> _identities = new();

    /// <summary>Registra una identidad de Google y devuelve el código de autorización que la representa.</summary>
    public string Issue(string email, bool verified)
    {
        var code = Guid.NewGuid().ToString("N");
        _identities[code] = new Dictionary<string, object>
        {
            ["sub"] = Guid.NewGuid().ToString("N"),
            ["email"] = email,
            ["email_verified"] = verified,
            ["name"] = "Usuario de Google",
        };
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        if (url == GoogleDefaults.TokenEndpoint)
        {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            form["client_secret"].ToString().ShouldBe(GoogleCloudServerFactory.ClientSecret);
            return _identities.ContainsKey(form["code"].ToString())
                ? Json(new { access_token = form["code"].ToString(), token_type = "Bearer", expires_in = 3600 })
                : new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        if (url == GoogleDefaults.UserInformationEndpoint && request.Headers.Authorization?.Parameter is { } token
            && _identities.TryGetValue(token, out var identity))
        {
            return Json(identity);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
}

/// <summary>Servidor con el ingreso con Google configurado (Google simulado) y el doble factor OPCIONAL (lo predeterminado).</summary>
public sealed class GoogleCloudServerFactory : CloudServerFactory
{
    public const string ClientSecret = "secreto-de-prueba-google";

    public FakeGoogle Google { get; } = new();

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("Portal:RequireTotp", "false"),
        new("Portal:Google:ClientId", "cliente-de-prueba.apps.googleusercontent.com"),
        new("Portal:Google:ClientSecret", ClientSecret),
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.Configure<GoogleOptions>(GoogleDefaults.AuthenticationScheme, options => options.BackchannelHttpHandler = Google));
    }
}

public sealed class GoogleCloudFixture : IAsyncLifetime
{
    public GoogleCloudServerFactory Factory { get; } = new();

    public HttpClient Superadmin { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Factory.StartAsync();
        (Superadmin, _) = await PortalApi.BootstrapSuperadminAsync(Factory);
    }

    public async ValueTask DisposeAsync()
    {
        Superadmin?.Dispose();
        await Factory.DisposeAsync();
    }
}

/// <summary>
/// Ingreso con Google (ADR-0062): solo un correo VERIFICADO de un usuario ACTIVO entra, con la misma sesión del portal (cookie
/// <c>__Host-pos-portal</c>) y auditoría con el método; nunca se crean usuarios. El doble factor es opcional: la contraseña entra
/// sin TOTP salvo para quien lo activó.
/// </summary>
public class PortalGoogleTests(GoogleCloudFixture cloud) : IClassFixture<GoogleCloudFixture>
{
    private static readonly Uri Base = new("https://licencias.test/");

    [Fact]
    public async Task Un_correo_verificado_de_un_usuario_activo_entra_con_Google_sin_cambiar_la_contrasena_temporal()
    {
        var (email, _) = await NewUserAsync();
        var (browser, cookies) = Browser();
        using (browser)
        {
            var login = await browser.GetStringAsync(new Uri("cuenta/ingresar?returnUrl=%2Finstalaciones", UriKind.Relative), Ct);
            login.ShouldContain("Continuar con Google");

            // Mayúsculas distintas a las registradas: el correo se compara sin distinguirlas.
            using var complete = await SignInWithGoogleAsync(browser, login, cloud.Factory.Google.Issue(email.ToUpperInvariant(), verified: true));
            complete.StatusCode.ShouldBe(HttpStatusCode.OK);
            var html = await complete.Content.ReadAsStringAsync(Ct);
            html.ShouldContain("http-equiv=\"refresh\" content=\"0;url=/instalaciones\"");
            var cookie = complete.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-pos-portal=", StringComparison.Ordinal));
            cookie.ShouldContain("samesite=strict", Case.Insensitive);
            cookies.Container.GetCookies(Base)["__Secure-pos-google"].ShouldBeNull("La cookie temporal de Google se borra al completar.");

            // Con Google, la contraseña temporal no obliga a pasar por "Mi cuenta": el portal abre.
            using var page = await browser.GetAsync(new Uri("instalaciones", UriKind.Relative), Ct);
            page.StatusCode.ShouldBe(HttpStatusCode.OK, page.Headers.Location?.ToString());
        }

        var audit = await PortalApi.GetAsync<List<CloudAuditEntryDto>>(cloud.Superadmin, $"/admin/audit?action=PORTAL_LOGIN_SUCCEEDED&text={Uri.EscapeDataString(email)}");
        audit.ShouldContain(e => e.Summary!.Contains("método GOOGLE", StringComparison.Ordinal) && e.Summary.Contains("agente: Pruebas-Google", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Correo_sin_verificar_desconocido_deshabilitado_o_bloqueado_no_entra()
    {
        var (unverified, _) = await NewUserAsync();
        var (disabled, disabledId) = await NewUserAsync();
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Put, $"/admin/users/{disabledId}",
            new { displayName = "Deshabilitado", role = "SUPPORT", resellerAccountId = (Guid?)null, isActive = false })).Status.ShouldBe(HttpStatusCode.NoContent);
        var (locked, _) = await NewUserAsync();
        using (var api = cloud.Factory.CreateClient())
        {
            for (var i = 0; i < 5; i++)
            {
                await PortalApi.SendAsync(api, HttpMethod.Post, "/admin/auth/login", new { email = locked, password = "incorrecta-12345" });
            }
        }

        var unknown = $"nadie-{Guid.NewGuid():N}@gmail.com";
        foreach (var (email, verified, expected) in new[]
                 {
                     (unverified, false, "rechazado"), (unknown, true, "rechazado"), (disabled, true, "rechazado"), (locked, true, "bloqueado"),
                 })
        {
            var (browser, _) = Browser();
            using (browser)
            {
                var login = await browser.GetStringAsync(new Uri("cuenta/ingresar", UriKind.Relative), Ct);
                using var complete = await SignInWithGoogleAsync(browser, login, cloud.Factory.Google.Issue(email, verified));
                complete.StatusCode.ShouldBe(HttpStatusCode.Redirect, email);
                complete.Headers.Location!.ToString().ShouldBe($"/cuenta/ingresar?google={expected}", email);
                var setCookies = complete.Headers.TryGetValues("Set-Cookie", out var set) ? set : [];
                setCookies.ShouldNotContain(c => c.StartsWith("__Host-pos-portal=", StringComparison.Ordinal), email);

                var notice = WebUtility.HtmlDecode(await browser.GetStringAsync(complete.Headers.Location, Ct));
                notice.ShouldContain(expected == "bloqueado" ? "bloqueado temporalmente" : "No fue posible ingresar con esa cuenta de Google");
            }
        }

        // Nunca se crean usuarios; el intento del correo desconocido queda en la auditoría.
        var users = await PortalApi.GetAsync<List<PortalUserDto>>(cloud.Superadmin, "/admin/users");
        users.ShouldNotContain(u => u.Email == unknown);
        var audit = await PortalApi.GetAsync<List<CloudAuditEntryDto>>(cloud.Superadmin, $"/admin/audit?action=PORTAL_LOGIN_FAILED&text={Uri.EscapeDataString(unknown)}");
        audit.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Sin_TOTP_obligatorio_la_contrasena_entra_directo_y_quien_activo_su_TOTP_debe_darlo()
    {
        var email = $"respaldo-{Guid.NewGuid():N}@licencias.co";
        var temporary = await PortalApi.PostAsync<TemporaryPasswordDto>(cloud.Superadmin, "/admin/users", new { email, displayName = "Respaldo", role = "SUPPORT" });
        using var anonymous = cloud.Factory.CreateClient();

        // Contraseña sin TOTP: la sesión queda ACTIVA de una vez (con la temporal, solo para cambiarla).
        var first = await PortalApi.PostAsync<LoginChallengeDto>(anonymous, "/admin/auth/login", new { email, password = temporary.TemporaryPassword });
        first.Stage.ShouldBe("ACTIVE");
        using var client = cloud.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Token);
        (await PortalApi.SendAsync(client, HttpMethod.Get, "/admin/dashboard")).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await PortalApi.SendAsync(client, HttpMethod.Post, "/admin/auth/password", new { currentPassword = temporary.TemporaryPassword, newPassword = PortalApi.NewPassword }))
            .Status.ShouldBe(HttpStatusCode.NoContent);
        (await PortalApi.SendAsync(client, HttpMethod.Get, "/admin/dashboard")).Status.ShouldBe(HttpStatusCode.OK);

        // Activa su TOTP (opcional) desde su cuenta: desde ahí el ingreso con contraseña lo pide.
        var enrollment = await PortalApi.PostAsync<TotpEnrollmentDto>(client, "/admin/auth/me/totp/enrollment", new { });
        var user = new PortalCredentials(email, PortalApi.NewPassword) { TotpSecret = Authenticator.FromBase32(enrollment.Secret) };
        (await PortalApi.SendAsync(client, HttpMethod.Post, "/admin/auth/me/totp", new { code = "000000" })).Code.ShouldBe("PORTAL.INVALID_TOTP");
        (await PortalApi.SendAsync(client, HttpMethod.Post, "/admin/auth/me/totp", new { code = user.NextCode() })).Status.ShouldBe(HttpStatusCode.NoContent);
        (await PortalApi.SendAsync(client, HttpMethod.Get, "/admin/dashboard")).Status.ShouldBe(HttpStatusCode.OK, "La sesión en curso sigue abierta.");

        var second = await PortalApi.PostAsync<LoginChallengeDto>(anonymous, "/admin/auth/login", new { email, password = PortalApi.NewPassword });
        second.Stage.ShouldBe("TOTP_REQUIRED");
        (await PortalApi.SendAsync(anonymous, HttpMethod.Post, "/admin/auth/totp", new { token = second.Token, code = "000000" })).Code.ShouldBe("PORTAL.INVALID_TOTP");

        // Lo desactiva con un código vigente (la sesión en curso sigue abierta): vuelve a entrar solo con la contraseña.
        (await PortalApi.SendAsync(client, HttpMethod.Post, "/admin/auth/me/totp/disable", new { code = "000000" })).Code.ShouldBe("PORTAL.INVALID_TOTP");
        (await PortalApi.SendAsync(client, HttpMethod.Post, "/admin/auth/me/totp/disable", new { code = user.NextCode() })).Status.ShouldBe(HttpStatusCode.NoContent);
        (await PortalApi.PostAsync<LoginChallengeDto>(anonymous, "/admin/auth/login", new { email, password = PortalApi.NewPassword })).Stage.ShouldBe("ACTIVE");
    }

    [Fact]
    public async Task En_el_navegador_la_contrasena_sin_TOTP_crea_la_sesion_y_lleva_a_cambiar_la_temporal()
    {
        var email = $"navegador-{Guid.NewGuid():N}@licencias.co";
        var temporary = await PortalApi.PostAsync<TemporaryPasswordDto>(cloud.Superadmin, "/admin/users", new { email, displayName = "Navegador", role = "SUPPORT" });
        var (browser, _) = Browser();
        using (browser)
        {
            var login = new Uri("cuenta/ingresar", UriKind.Relative);
            var page = await browser.GetStringAsync(login, Ct);
            using var response = await BrowserForms.PostFormAsync(browser, login, page,
                new() { ["_handler"] = "login", ["Input.Email"] = email, ["Input.Password"] = temporary.TemporaryPassword });
            response.StatusCode.ShouldBe(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync(Ct));
            response.Headers.Location!.AbsolutePath.ShouldBe("/");
            response.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith("__Host-pos-portal=", StringComparison.Ordinal));

            // La contraseña temporal (ingreso con contraseña) sí obliga a cambiarla.
            using var home = await browser.GetAsync(new Uri("instalaciones", UriKind.Relative), Ct);
            home.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            home.Headers.Location!.ToString().ShouldBe("/mi-cuenta");
        }
    }

    private async Task<(string Email, Guid Id)> NewUserAsync()
    {
        var email = $"google-{Guid.NewGuid():N}@licencias.co";
        var created = await PortalApi.PostAsync<TemporaryPasswordDto>(cloud.Superadmin, "/admin/users", new { email, displayName = "Usuario Google", role = "SUPPORT" });
        return (email, created.UserId);
    }

    private (HttpClient Browser, CookieContainerHandler Cookies) Browser()
    {
        var cookies = new CookieContainerHandler();
        var browser = cloud.Factory.CreateDefaultClient(Base, cookies);
        browser.DefaultRequestHeaders.UserAgent.ParseAdd("Pruebas-Google/1.0");
        return (browser, cookies);
    }

    /// <summary>
    /// El recorrido del navegador: botón "Continuar con Google" (POST) → Google (se simula la vuelta con <paramref name="code"/>) →
    /// <c>/signin-google</c> → <c>/cuenta/google/completar</c>. Devuelve la respuesta de este último.
    /// </summary>
    private static async Task<HttpResponseMessage> SignInWithGoogleAsync(HttpClient browser, string loginPage, string code)
    {
        using var challenge = await BrowserForms.PostFormAsync(browser, new Uri("cuenta/google", UriKind.Relative), loginPage, []);
        challenge.StatusCode.ShouldBe(HttpStatusCode.Redirect, await challenge.Content.ReadAsStringAsync(Ct));
        var google = challenge.Headers.Location!;
        google.GetLeftPart(UriPartial.Path).ShouldBe(GoogleDefaults.AuthorizationEndpoint);
        var query = QueryHelpers.ParseQuery(google.Query);
        query["redirect_uri"].ToString().ShouldBe("https://licencias.test/signin-google");
        query["scope"].ToString().Split(' ').ShouldBe(["openid", "email", "profile"], ignoreOrder: true);

        using var callback = await browser.GetAsync(
            new Uri($"signin-google?state={Uri.EscapeDataString(query["state"].ToString())}&code={code}", UriKind.Relative), Ct);
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await callback.Content.ReadAsStringAsync(Ct));
        callback.Headers.Location!.ToString().ShouldStartWith("/cuenta/google/completar?returnUrl=");
        return await browser.GetAsync(callback.Headers.Location, Ct);
    }
}
