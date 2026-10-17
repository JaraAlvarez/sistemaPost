using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Host.Downloads;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// "Descargas" del portal (Fase 13-B, D13B-05/06): el cliente, soporte y el superadministrador ven la última versión publicada según el
/// manifiesto de actualizaciones (simulado aquí) con el enlace del instalador en /updates, un mensaje amable si no hay manifiesto, y el
/// manual de instalación como página (con el texto siempre codificado).
/// </summary>
public class DownloadsTests(DownloadsCloudFixture cloud) : IClassFixture<DownloadsCloudFixture>
{
    private FakeManifestHandler Manifest => cloud.Factory.Manifest;

    [Fact]
    public async Task El_cliente_ve_la_ultima_version_y_el_enlace_del_instalador_en_updates()
    {
        Manifest.Respond(HttpStatusCode.OK, SignedManifest("1.2.3", "Arreglos en la caja.\nNuevo reporte de ventas."));
        using var browser = await CustomerBrowserAsync();

        using var response = await browser.GetAsync(new Uri("/descargas", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(Ct);
        html.ShouldContain("1.2.3");
        html.ShouldContain("Descargar instalador");
        html.ShouldContain("href=\"/updates/BusinessPost-Setup-1.2.3.exe\"");
        html.ShouldContain("Nuevo reporte de ventas.");
        html.ShouldContain("href=\"descargas\""); // Entrada del menú (Personal → Descargas).

        // Sin Downloads:ManifestUrl, el servidor lee el manifiesto en /updates del mismo sitio del portal.
        Manifest.LastRequest.ShouldBe(new Uri("https://licencias.test/updates/stable.json"));

        // "Mi suscripción" lleva a Descargas.
        var subscription = await PageTextAsync(browser, "/mi-suscripcion");
        subscription.ShouldContain("Descargar BusinessPost");
    }

    [Fact]
    public async Task Sin_manifiesto_o_con_una_version_invalida_muestra_el_mensaje_amable()
    {
        using var browser = await CustomerBrowserAsync();

        Manifest.Respond(HttpStatusCode.NotFound, "no existe");
        var html = await PageTextAsync(browser, "/descargas");
        html.ShouldContain("Aún no hay una versión publicada para descargar.");
        html.ShouldNotContain("Descargar instalador");

        // La versión termina en el enlace: solo números y puntos.
        Manifest.Respond(HttpStatusCode.OK, SignedManifest("1.2.3\"><script>alert(1)</script>", "x"));
        var raw = await browser.GetStringAsync(new Uri("/descargas", UriKind.Relative), Ct);
        raw.ShouldNotContain("<script>alert(1)</script>");
        WebUtility.HtmlDecode(raw).ShouldContain("Aún no hay una versión publicada para descargar.");

        Manifest.Respond(HttpStatusCode.OK, "esto no es JSON");
        html = await PageTextAsync(browser, "/descargas");
        html.ShouldContain("Aún no hay una versión publicada para descargar.");
    }

    [Fact]
    public async Task El_manual_se_muestra_como_pagina_con_imprimir()
    {
        Manifest.Respond(HttpStatusCode.NotFound, string.Empty);
        using var browser = await CustomerBrowserAsync();

        using var response = await browser.GetAsync(new Uri("/descargas/manual", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(Ct);
        html.ShouldContain("<h2>Manual de instalación de BusinessPost</h2>");
        html.ShouldContain("<strong>Ejecutar de todas formas</strong>");
        WebUtility.HtmlDecode(html).ShouldContain("Es la única caja");
        html.ShouldContain("Imprimir / Guardar como PDF");
        html.ShouldNotContain("Descargar manual (PDF)");
    }

    [Fact]
    public void El_manual_codifica_el_HTML_y_solo_acepta_enlaces_http()
    {
        var html = SafeMarkdown.ToHtml(
            "# Título <b>x</b>\n\nTexto <script>alert(1)</script> **negrita** `<i>código</i>`\n\n" +
            "- [malo](javascript:alert(1))\n- [bueno](https://businesspost.co/ayuda?a=1&b=2)\n\n<img src=x onerror=alert(1)>");

        html.ShouldContain("<h1>Título &lt;b&gt;x&lt;/b&gt;</h1>");
        html.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;");
        html.ShouldContain("<strong>negrita</strong>");
        html.ShouldContain("<code>&lt;i&gt;código&lt;/i&gt;</code>");
        html.ShouldContain("<li>malo");
        html.ShouldNotContain("javascript:");
        html.ShouldContain("href=\"https://businesspost.co/ayuda?a=1&amp;b=2\"");
        html.ShouldContain("&lt;img src=x onerror=alert(1)&gt;");
        html.ShouldNotContain("<img");
        html.ShouldNotContain("<script");

        // El manual real está incrustado y se convierte sin errores.
        InstallManual.ToHtml().ShouldContain("<h3>1. Antes de empezar</h3>");
    }

    [Fact]
    public void El_manual_solo_acepta_capturas_png_de_imagenes_instalacion_en_su_propia_linea()
    {
        var html = SafeMarkdown.ToHtml(
            "![Paso 1: Bienvenida <b>](imagenes/instalacion/01-bienvenida.png)\n\n" +
            "![fuera](imagenes/instalacion/../../secreto.png)\n\n" +
            "![remota](https://malo.example/x.png)\n\n" +
            "![script](javascript:alert(1))\n\n" +
            "![svg](imagenes/instalacion/dibujo.svg)\n\n" +
            "![mayusculas](imagenes/instalacion/Paso.PNG)\n\n" +
            "Texto con ![en linea](imagenes/instalacion/02-equipo.png) dentro.",
            imageBaseUrl: "descargas/manual/imagenes/");

        html.ShouldContain("<figure class=\"bp-manual-figura\"><img src=\"descargas/manual/imagenes/01-bienvenida.png\" alt=\"Paso 1: Bienvenida &lt;b&gt;\" loading=\"lazy\"><figcaption>Paso 1: Bienvenida &lt;b&gt;</figcaption></figure>");
        html.Split("<img").Length.ShouldBe(2); // Solo la primera es imagen; las demás quedan como texto.
        html.ShouldNotContain("secreto.png\"");
        html.ShouldNotContain("src=\"https://malo.example");
        html.ShouldNotContain("javascript:");
        html.ShouldNotContain("02-equipo.png\"");

        // Sin ruta de imágenes, solo el pie de foto.
        SafeMarkdown.ToHtml("![Pie](imagenes/instalacion/01-bienvenida.png)").ShouldBe("<figure class=\"bp-manual-figura\"><figcaption>Pie</figcaption></figure>\n");

        // El manual real trae sus capturas incrustadas.
        var manual = InstallManual.ToHtml();
        manual.ShouldContain("<img src=\"descargas/manual/imagenes/");
        foreach (var name in System.Text.RegularExpressions.Regex.Matches(manual, @"descargas/manual/imagenes/([a-z0-9-]+\.png)").Select(m => m.Groups[1].Value))
        {
            using var image = InstallManual.OpenImage(name);
            image.ShouldNotBeNull($"Falta la captura incrustada {name}");
        }

        InstallManual.OpenImage("../manual-instalacion.md").ShouldBeNull();
        InstallManual.OpenImage("no-existe.png").ShouldBeNull();
    }

    [Fact]
    public async Task Las_capturas_del_manual_se_sirven_como_png_solo_con_sesion()
    {
        Manifest.Respond(HttpStatusCode.NotFound, string.Empty);
        using var browser = await CustomerBrowserAsync();

        var page = await browser.GetStringAsync(new Uri("/descargas/manual", UriKind.Relative), Ct);
        var src = System.Text.RegularExpressions.Regex.Match(page, @"src=""(descargas/manual/imagenes/[a-z0-9-]+\.png)""").Groups[1].Value;
        src.ShouldNotBeEmpty();

        using var image = await browser.GetAsync(new Uri("/" + src, UriKind.Relative), Ct);
        image.StatusCode.ShouldBe(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        var bytes = await image.Content.ReadAsByteArrayAsync(Ct);
        bytes.Take(4).ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        using var missing = await browser.GetAsync(new Uri("/descargas/manual/imagenes/no-existe.png", UriKind.Relative), Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var traversal = await browser.GetAsync(new Uri("/descargas/manual/imagenes/..%2Fmanual-instalacion.md", UriKind.Relative), Ct);
        traversal.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var anonymous = cloud.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://licencias.test/"), AllowAutoRedirect = false,
        });
        using var denied = await anonymous.GetAsync(new Uri("/" + src, UriKind.Relative), Ct);
        denied.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sin_sesion_pide_ingresar_y_el_permiso_es_de_cliente_soporte_y_superadministrador()
    {
        using var anonymous = cloud.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://licencias.test/"), AllowAutoRedirect = false,
        });
        using var response = await anonymous.GetAsync(new Uri("/descargas", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().ShouldContain("/cuenta/ingresar");

        CloudPermissions.All.ShouldContain(CloudPermissions.DownloadView);
        CloudPermissions.RoleHas(PortalRoles.Customer, CloudPermissions.DownloadView).ShouldBeTrue();
        CloudPermissions.RoleHas(PortalRoles.Support, CloudPermissions.DownloadView).ShouldBeTrue();
        CloudPermissions.RoleHas(PortalRoles.Superadmin, CloudPermissions.DownloadView).ShouldBeTrue();
        CloudPermissions.RoleHas(PortalRoles.Reseller, CloudPermissions.DownloadView).ShouldBeFalse();
    }

    /// <summary>La página con las entidades HTML decodificadas (Blazor codifica las tildes).</summary>
    private static async Task<string> PageTextAsync(HttpClient browser, string path) =>
        WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri(path, UriKind.Relative), Ct));

    private static string SignedManifest(string version, string notes)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            product = "BusinessPost",
            channel = "stable",
            version,
            packageUrl = $"BusinessPost-{version}.zip",
            packageSha256 = new string('a', 64),
            packageSize = 1234,
            minimumTerminalVersion = "1.0.0",
            publishedAt = new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.Zero),
            notes,
        });
        return JsonSerializer.Serialize(new { payload = Base64Url.EncodeToString(payload), kid = "upd-test", signature = "firma-de-prueba" });
    }

    /// <summary>Un cliente del portal (rol Cliente) con su sesión en un "navegador" (cookie), como en PortalAccessTests.</summary>
    private async Task<HttpClient> CustomerBrowserAsync()
    {
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin);
        var (api, user) = await PortalApi.CreateUserAsync(cloud.Factory, cloud.Superadmin, PortalRoles.Customer, customer.AccountId);
        api.Dispose();
        var browser = cloud.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://licencias.test/"), AllowAutoRedirect = false, HandleCookies = true,
        });

        var login = new Uri("/cuenta/ingresar?returnUrl=%2Fdescargas", UriKind.Relative);
        var page = await browser.GetStringAsync(login, Ct);
        using var afterPassword = await BrowserForms.PostFormAsync(browser, login, page,
            new() { ["Input.Email"] = user.Email, ["Input.Password"] = user.Password });
        afterPassword.StatusCode.ShouldBe(HttpStatusCode.Redirect, await afterPassword.Content.ReadAsStringAsync(Ct));
        var next = afterPassword.Headers.Location!;
        if (next.ToString().Contains("segundo-factor", StringComparison.Ordinal))
        {
            page = await browser.GetStringAsync(next, Ct);
            using var afterCode = await BrowserForms.PostFormAsync(browser, next, page, new() { ["Input.Code"] = user.NextCode() });
            afterCode.StatusCode.ShouldBe(HttpStatusCode.Redirect, await afterCode.Content.ReadAsStringAsync(Ct));
        }

        return browser;
    }
}

/// <summary>Responde el manifiesto de actualizaciones que la prueba fije (sin red).</summary>
public sealed class FakeManifestHandler : HttpMessageHandler
{
    private volatile Tuple<HttpStatusCode, string> _response = Tuple.Create(HttpStatusCode.NotFound, string.Empty);

    public Uri? LastRequest { get; private set; }

    public void Respond(HttpStatusCode status, string body) => _response = Tuple.Create(status, body);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request.RequestUri;
        var (status, body) = _response;
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

/// <summary>Servidor con el manifiesto simulado y sin memoria del manifiesto (cada página lo lee de nuevo).</summary>
public sealed class DownloadsCloudServerFactory : CloudServerFactory
{
    public FakeManifestHandler Manifest { get; } = new();

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("Downloads:CacheMinutes", "0")];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(DownloadCatalog.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Manifest));
    }
}

/// <summary>Un servidor con descargas simuladas por clase de pruebas, con el superadministrador ya dentro.</summary>
public sealed class DownloadsCloudFixture : IAsyncLifetime
{
    public DownloadsCloudServerFactory Factory { get; } = new();

    public HttpClient Superadmin { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Factory.StartAsync();
        (Superadmin, _) = await PortalApi.BootstrapSuperadminAsync(Factory);
    }

    public async ValueTask DisposeAsync()
    {
        Superadmin.Dispose();
        await Factory.DisposeAsync();
    }
}
