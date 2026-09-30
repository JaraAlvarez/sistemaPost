using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Pos.License.Simulator;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.IntegrationTests;

/// <summary>Servidor de la nube bajo una ruta de un dominio existente (<c>Cloud:PathBase</c>), p. ej. <c>https://dominio/businesspost/</c>.</summary>
public sealed class PathBaseCloudServerFactory : CloudServerFactory
{
    // Sin barra inicial y con barra final a propósito: la configuración se normaliza a "/businesspost".
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("Cloud:PathBase", "businesspost/")];
}

public sealed class PathBaseCloudFixture : IAsyncLifetime
{
    public PathBaseCloudServerFactory Factory { get; } = new();

    public HttpClient Superadmin { get; private set; } = null!;

    public PortalCredentials SuperadminUser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Factory.StartAsync();
        (Superadmin, SuperadminUser) = await PortalApi.BootstrapSuperadminAsync(Factory);
    }

    public async ValueTask DisposeAsync()
    {
        Superadmin.Dispose();
        await Factory.DisposeAsync();
    }
}

/// <summary>
/// Con <c>Cloud:PathBase=/businesspost</c> responden la salud, la API del POS (/v1), la API interna (/admin) y el portal bajo la
/// ruta; el ingreso usa la cookie <c>__Secure-pos-portal</c> limitada a la ruta (no <c>__Host-</c>, que exige la ruta /).
/// </summary>
public class PathBaseTests(PathBaseCloudFixture cloud) : IClassFixture<PathBaseCloudFixture>
{
    private static readonly Uri Base = new("https://tutiendanueva.test/businesspost/");

    [Fact]
    public async Task Salud_claves_publicas_y_API_interna_responden_bajo_la_ruta()
    {
        using var client = Browser();
        using var health = await client.GetAsync(new Uri("health", UriKind.Relative), Ct);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await health.Content.ReadAsStringAsync(Ct)).ShouldContain("\"status\"");

        var keys = await client.GetFromJsonAsync<PublicKeysResponse>(new Uri("v1/public-keys", UriKind.Relative), PortalApi.Json, Ct);
        keys!.Keys.ShouldContain(k => k.Kid == cloud.Factory.PublicKey.Kid);

        using var admin = Browser();
        admin.DefaultRequestHeaders.Authorization = cloud.Superadmin.DefaultRequestHeaders.Authorization;
        (await PortalApi.SendAsync(admin, HttpMethod.Get, "admin/dashboard")).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task El_POS_simulado_activa_y_hace_checkin_con_una_URL_base_con_ruta()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        using var http = cloud.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = Base });
        var pos = new SimulatedPos(http, SimulatorIdentity.Create(
            customer.Nit, DeviceRoles.StoreServer, board: $"PLACA-{Guid.NewGuid():N}", disk: $"DISCO-{Guid.NewGuid():N}", machine: $"MAQ-{Guid.NewGuid():N}"));

        (await pos.RefreshPublicKeysAsync(Ct)).ShouldContain(k => k.Kid == cloud.Factory.PublicKey.Kid);
        var activation = await pos.ActivateAsync(customer.License.Key, Ct);
        activation.Succeeded.ShouldBeTrue($"{activation.ErrorCode}: {activation.ErrorMessage}");
        (await pos.CheckinAsync(2, Ct)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task El_ingreso_al_portal_funciona_bajo_la_ruta_con_la_cookie_limitada_a_ella()
    {
        using var browser = Browser();

        // Sin sesión: la página pide ingresar, dentro de la ruta.
        using (var home = await browser.GetAsync(new Uri("instalaciones", UriKind.Relative), Ct))
        {
            home.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            home.Headers.Location!.OriginalString.ShouldStartWith("/businesspost/cuenta/ingresar?returnUrl=");
        }

        // Paso 1: contraseña (formulario renderizado en el servidor con <base href> de la ruta).
        var login = new Uri("cuenta/ingresar?returnUrl=%2Finstalaciones", UriKind.Relative);
        var page = await browser.GetStringAsync(login, Ct);
        page.ShouldContain("<base href=\"/businesspost/\"");
        using var afterPassword = await BrowserForms.PostFormAsync(browser, login, page,
            new() { ["Input.Email"] = cloud.SuperadminUser.Email, ["Input.Password"] = cloud.SuperadminUser.Password });
        afterPassword.StatusCode.ShouldBe(HttpStatusCode.Redirect, await afterPassword.Content.ReadAsStringAsync(Ct));
        afterPassword.Headers.Location!.ToString().ShouldContain("/businesspost/cuenta/segundo-factor?returnUrl=%2Finstalaciones");
        var cookie = afterPassword.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Secure-pos-portal=", StringComparison.Ordinal));
        cookie.ShouldContain("path=/businesspost", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("httponly", Case.Insensitive);
        afterPassword.Headers.GetValues("Set-Cookie").ShouldNotContain(c => c.StartsWith("__Host-", StringComparison.Ordinal));

        // Paso 2: código TOTP → vuelve a la página pedida, dentro de la ruta.
        var secondFactor = afterPassword.Headers.Location!;
        page = await browser.GetStringAsync(secondFactor, Ct);
        using var afterCode = await BrowserForms.PostFormAsync(browser, secondFactor, page, new() { ["Input.Code"] = cloud.SuperadminUser.NextCode() });
        afterCode.StatusCode.ShouldBe(HttpStatusCode.Redirect, await afterCode.Content.ReadAsStringAsync(Ct));
        afterCode.Headers.Location!.ToString().ShouldEndWith("/businesspost/instalaciones");

        // Con la cookie la página abre; los enlaces del menú son relativos a la ruta.
        using var installations = await browser.GetAsync(new Uri("instalaciones", UriKind.Relative), Ct);
        installations.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await installations.Content.ReadAsStringAsync(Ct);
        html.ShouldContain("action=\"cuenta/salir\"");
        html.ShouldNotContain("href=\"/clientes\"");

        // Salir: borra la cookie de la ruta y vuelve al ingreso de la ruta.
        using var logout = await BrowserForms.PostFormAsync(browser, new Uri("cuenta/salir", UriKind.Relative), html, []);
        logout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        logout.Headers.Location!.OriginalString.ShouldBe("/businesspost/cuenta/ingresar");
        using var closed = await browser.GetAsync(new Uri("instalaciones", UriKind.Relative), Ct);
        closed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    private HttpClient Browser() =>
        cloud.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = Base, AllowAutoRedirect = false, HandleCookies = true });
}

/// <summary>Formularios renderizados en el servidor enviados como lo haría el navegador.</summary>
public static partial class BrowserForms
{
    /// <summary>Envía un formulario como el navegador: campos ocultos de la página (antifalsificación, _handler) + los dados.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, Uri url, string page, Dictionary<string, string> fields)
    {
        var form = new Dictionary<string, string>();
        foreach (Match input in HiddenInput().Matches(page))
        {
            var name = NameAttribute().Match(input.Value);
            var value = ValueAttribute().Match(input.Value);
            if (name.Success)
            {
                form.TryAdd(WebUtility.HtmlDecode(name.Groups[1].Value), value.Success ? WebUtility.HtmlDecode(value.Groups[1].Value) : string.Empty);
            }
        }

        foreach (var (key, value) in fields)
        {
            form[key] = value;
        }

        using var content = new FormUrlEncodedContent(form);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        return await client.PostAsync(url, content, Ct);
    }

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*>")]
    private static partial Regex HiddenInput();

    [GeneratedRegex("name=\"([^\"]*)\"")]
    private static partial Regex NameAttribute();

    [GeneratedRegex("value=\"([^\"]*)\"")]
    private static partial Regex ValueAttribute();
}
