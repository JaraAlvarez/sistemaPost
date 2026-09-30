using System.Net;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Pos.Cloud.PortalIdentity.Application;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// Formularios de acceso del navegador (/cuenta) con el doble factor obligatorio y sin Google configurado.
/// <para>
/// Regresión del "HTTP ERROR 400" de producción: la sesión PENDIENTE (tras la contraseña, 5 minutos) era una identidad, así que
/// el token antifalsificación del formulario del código quedaba atado a ella. Al vencer antes de enviar el código (escanear el
/// QR e instalar la aplicación toma más de 5 minutos), Blazor respondía un 400 SIN cuerpo que solo se registraba en Debug.
/// </para>
/// </summary>
public class PortalAccountFormTests(CloudFixture cloud) : IClassFixture<CloudFixture>
{
    private static readonly Uri Base = new("https://licencias.test/");

    [Fact]
    public async Task Si_la_sesion_pendiente_vence_antes_de_enviar_el_codigo_se_ve_el_aviso_y_no_un_400()
    {
        var email = $"enrolar-{Guid.NewGuid():N}@licencias.co";
        var temporary = await PortalApi.PostAsync<TemporaryPasswordDto>(cloud.Superadmin, "/admin/users", new { email, displayName = "Enrolar", role = "SUPPORT" });
        var cookies = new CookieContainerHandler();
        using var browser = cloud.Factory.CreateDefaultClient(Base, cookies);

        var login = new Uri("cuenta/ingresar?returnUrl=%2F", UriKind.Relative);
        var loginPage = await browser.GetStringAsync(login, Ct);
        using var posted = await BrowserForms.PostFormAsync(browser, login, loginPage,
            new() { ["Input.Email"] = email, ["Input.Password"] = temporary.TemporaryPassword });
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var enroll = posted.Headers.Location!;
        enroll.PathAndQuery.ShouldBe("/cuenta/activar-doble-factor?returnUrl=%2F");

        using var enrollResponse = await browser.GetAsync(enroll, Ct);
        enrollResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var enrollPage = await enrollResponse.Content.ReadAsStringAsync(Ct);
        WebUtility.HtmlDecode(enrollPage).ShouldContain("Código QR del doble factor");

        // La cookie de la sesión pendiente vence en el navegador mientras el usuario configura su aplicación.
        cookies.Container.GetCookies(Base)["__Host-pos-portal"]!.Expired = true;
        using var submitted = await BrowserForms.PostFormAsync(browser, enroll, enrollPage, new() { ["Input.Code"] = "123456" });
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK);
        WebUtility.HtmlDecode(await submitted.Content.ReadAsStringAsync(Ct)).ShouldContain("La sesión venció o fue cerrada");
    }

    [Fact]
    public async Task Un_formulario_de_acceso_con_token_antifalsificacion_invalido_vuelve_al_ingreso_con_aviso()
    {
        var cookies = new CookieContainerHandler();
        using var browser = cloud.Factory.CreateDefaultClient(Base, cookies);
        var login = new Uri("cuenta/ingresar", UriKind.Relative);
        var page = await browser.GetStringAsync(login, Ct);

        using var posted = await BrowserForms.PostFormAsync(browser, login, page, new()
        {
            ["__RequestVerificationToken"] = "token-de-otra-sesion",
            ["Input.Email"] = cloud.SuperadminUser.Email,
            ["Input.Password"] = cloud.SuperadminUser.Password,
        });
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect, "Antes: 400 sin cuerpo (\"HTTP ERROR 400\" en el navegador).");
        posted.Headers.Location!.ToString().ShouldBe("/cuenta/ingresar?vencido=1");
        WebUtility.HtmlDecode(await browser.GetStringAsync(posted.Headers.Location, Ct)).ShouldContain("La página de acceso venció");
    }

    [Fact]
    public async Task Sin_Google_configurado_no_hay_boton_ni_rutas_de_Google()
    {
        using var browser = cloud.Factory.CreateDefaultClient(Base, new CookieContainerHandler());
        var page = await browser.GetStringAsync(new Uri("cuenta/ingresar", UriKind.Relative), Ct);
        page.ShouldNotContain("Ingresar con Google");
        WebUtility.HtmlDecode(page).ShouldContain("Si activó el doble factor");

        using var challenge = await BrowserForms.PostFormAsync(browser, new Uri("cuenta/google", UriKind.Relative), page, []);
        challenge.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var callback = await browser.GetAsync(new Uri("signin-google?state=x&code=y", UriKind.Relative), Ct);
        callback.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
