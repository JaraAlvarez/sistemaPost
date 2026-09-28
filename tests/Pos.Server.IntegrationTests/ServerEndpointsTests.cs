using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Server.Host.Middleware;

namespace Pos.Server.IntegrationTests;

public class ServerEndpointsTests(PosServerFactory factory) : IClassFixture<PosServerFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_checks_responden_Healthy(string path)
    {
        var response = await _client.GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetString().ShouldBe("Healthy");
        body.GetProperty("checks").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task System_info_informa_version_entorno_y_hora_de_Colombia()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/api/v1/system/info", Ct);

        body.GetProperty("product").GetString().ShouldBe("PosSupermercado");
        body.GetProperty("version").GetString().ShouldStartWith("0.1.0");
        body.GetProperty("environment").GetString().ShouldBe("Development");
        body.GetProperty("businessTimeZone").GetString().ShouldBe("America/Bogota");
        body.GetProperty("runningAsWindowsService").GetBoolean().ShouldBeFalse();

        var businessTime = body.GetProperty("businessTime").GetDateTimeOffset();
        businessTime.Offset.ShouldBe(TimeSpan.FromHours(-5));
        body.GetProperty("businessDate").GetString().ShouldBe(businessTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task OpenAPI_documenta_la_API_en_desarrollo()
    {
        var document = await _client.GetStringAsync("/openapi/v1.json", Ct);

        document.ShouldContain("/api/v1/system/info");
    }

    [Fact]
    public async Task Sin_cabecera_se_genera_un_correlation_id()
    {
        var response = await _client.GetAsync("/health/live", Ct);

        var id = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        id.Length.ShouldBe(32);
    }

    [Fact]
    public async Task Correlation_id_valido_se_respeta()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "caja-01.venta_123");

        var response = await _client.SendAsync(request, Ct);

        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single().ShouldBe("caja-01.venta_123");
    }

    [Theory]
    [InlineData("con espacios")]
    [InlineData("<script>")]
    public async Task Correlation_id_invalido_se_reemplaza(string invalid)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, invalid);

        var response = await _client.SendAsync(request, Ct);

        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single().ShouldNotBe(invalid);
    }

    [Fact]
    public void Correlation_id_demasiado_largo_es_invalido()
    {
        CorrelationIdMiddleware.IsValid(new string('a', CorrelationIdMiddleware.MaxLength)).ShouldBeTrue();
        CorrelationIdMiddleware.IsValid(new string('a', CorrelationIdMiddleware.MaxLength + 1)).ShouldBeFalse();
    }
}
