using System.Net;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase15;

/// <summary>
/// Verificación de coherencia de la Fase 15: el servidor sirve la interfaz (Blazor WebAssembly) con su política de contenido, las rutas de
/// la interfaz caen en index.html y la API conserva sus 404. El recorrido en pantalla se probó en el navegador (informe §2).
/// </summary>
public class InterfaceTests
{
    [Fact]
    public async Task El_servidor_sirve_la_interfaz_con_su_CSP_sin_tapar_la_API()
    {
        await using var factory = new PosServerFactory();
        var client = factory.CreateClient();

        foreach (var route in new[] { "/", "/caja", "/admin/reportes" })
        {
            var page = await client.GetAsync(route, Ct);
            page.StatusCode.ShouldBe(HttpStatusCode.OK, route);
            (await page.Content.ReadAsStringAsync(Ct)).ShouldContain("_framework/blazor.webassembly.js", Case.Sensitive, route);
            page.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("'wasm-unsafe-eval'");
        }

        var css = await client.GetAsync("/css/app.css", Ct);
        css.StatusCode.ShouldBe(HttpStatusCode.OK);
        css.Content.Headers.ContentType!.MediaType.ShouldBe("text/css");
        (await client.GetAsync("/_framework/blazor.webassembly.js", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.GetAsync("/api/v1/no-existe", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/instalacion", Ct)).Headers.GetValues("Content-Security-Policy").Single().ShouldContain("'unsafe-inline'");
    }
}
