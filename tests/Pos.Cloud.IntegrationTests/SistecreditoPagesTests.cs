using System.Net;
using System.Text;
using Pos.Cloud.Host.Hosting;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// Direcciones públicas que exige Sistecrédito (ADR-0064): la página de vuelta del cliente y el aviso de confirmación. El POS no las
/// necesita (confirma consultando, D7B-03): la nube responde rápido, sin scripts y sin guardar nada.
/// </summary>
public class SistecreditoPagesTests(CloudFixture cloud) : IClassFixture<CloudFixture>
{
    [Fact]
    public async Task La_pagina_de_respuesta_es_amable_sin_datos_y_sin_scripts()
    {
        var client = cloud.Factory.CreateClient();
        using var response = await client.GetAsync(new Uri("sistecredito/respuesta?transactionId=abc&documento=1234567890", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        var html = await response.Content.ReadAsStringAsync(Ct);
        html.ShouldContain("Tu pago con Sistecrédito se está procesando");
        html.ShouldContain("Vuelve a la caja");
        html.ShouldNotContain("<script", Case.Insensitive);
        html.ShouldNotContain("1234567890");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.ShouldContain("default-src 'none'");
        csp.ShouldNotContain("script-src");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task La_confirmacion_acepta_el_aviso_con_200_por_POST_y_por_GET()
    {
        var client = cloud.Factory.CreateClient();
        using var json = new StringContent(
            """{"transactionId":"6f1c-99","transactionStatus":"Approved","documentNumber":"1234567890","name":"Cliente"}""", Encoding.UTF8, "application/json");
        using var posted = await client.PostAsync(new Uri("sistecredito/confirmacion", UriKind.Relative), json, Ct);
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await posted.Content.ReadAsStringAsync(Ct)).ShouldContain("\"received\":true");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["transactionId"] = "77", ["status"] = "Rejected" });
        (await client.PostAsync(new Uri("sistecredito/confirmacion", UriKind.Relative), form, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(new Uri("sistecredito/confirmacion?transactionId=88&transactionStatus=Pending", UriKind.Relative), Ct)).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        // Cuerpo vacío o que no es JSON: igual 200 (no se le pide nada al remitente).
        using var garbage = new StringContent("<xml/>", Encoding.UTF8, "text/plain");
        (await client.PostAsync(new Uri("sistecredito/confirmacion", UriKind.Relative), garbage, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task La_confirmacion_rechaza_cuerpos_grandes()
    {
        var client = cloud.Factory.CreateClient();
        using var big = new StringContent(new string('a', SistecreditoEndpoints.MaxBodyBytes + 10), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("sistecredito/confirmacion", UriKind.Relative), big, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public void Solo_se_registran_el_id_y_el_estado_saneados()
    {
        SistecreditoEndpoints.Extract("""{"data":{"transactionId":"abc-1","transactionStatus":"Approved"},"documentNumber":"1"}""", "application/json")
            .ShouldBe(("abc-1", "Approved"));
        SistecreditoEndpoints.Extract("transactionId=9&status=Pending", "application/x-www-form-urlencoded").ShouldBe(("9", "Pending"));
        SistecreditoEndpoints.Extract("no es json", "application/json").ShouldBe((null, null));
        SistecreditoEndpoints.Sanitize("abc\r\nFALSO: línea inyectada").ShouldBe("abcFALSOlneainyectada");
        SistecreditoEndpoints.Sanitize(new string('x', 200)).Length.ShouldBe(64);
        SistecreditoEndpoints.Sanitize(null).ShouldBe("(sin dato)");
    }

    [Fact]
    public async Task Las_paginas_de_Sistecredito_tienen_limite_por_IP()
    {
        await using var factory = new LowIpLimitCloudServerFactory();
        var client = await factory.StartAsync();
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync(new Uri("sistecredito/respuesta", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await client.GetAsync(new Uri("sistecredito/confirmacion", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }
}
