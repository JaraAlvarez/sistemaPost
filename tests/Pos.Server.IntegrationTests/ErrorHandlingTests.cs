using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Pos.Server.Host.Middleware;

namespace Pos.Server.IntegrationTests;

public class ErrorHandlingTests(PosServerFactory factory) : IClassFixture<PosServerFactory>
{
    private const string ProblemJson = "application/problem+json";

    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Error_inesperado_devuelve_500_con_codigo_y_correlation_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dev/errors/unexpected");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "prueba-500");

        var response = await _client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe(ProblemJson);
        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single().ShouldBe("prueba-500");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("SYSTEM.UNEXPECTED");
        problem.GetProperty("correlationId").GetString().ShouldBe("prueba-500");
        problem.GetProperty("status").GetInt32().ShouldBe(500);
    }

    [Fact]
    public async Task Error_inesperado_queda_en_el_log_con_su_correlation_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dev/errors/unexpected");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "prueba-log-001");
        await _client.SendAsync(request, Ct);

        var logText = await ReadLogsUntilAsync("prueba-log-001");

        logText.ShouldContain("prueba-log-001");
        logText.ShouldContain("Error no controlado");
        logText.ShouldContain("InvalidOperationException");
    }

    [Fact]
    public async Task Violacion_de_regla_de_dominio_devuelve_422_con_su_codigo()
    {
        var response = await _client.GetAsync("/api/v1/dev/errors/domain", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("DEV.SAMPLE_RULE");
        problem.GetProperty("detail").GetString().ShouldBe("Regla de negocio de ejemplo incumplida.");
    }

    [Fact]
    public async Task Result_fallido_se_traduce_a_ProblemDetails()
    {
        var response = await _client.GetAsync("/api/v1/dev/errors/not-found", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe(ProblemJson);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("DEV.SAMPLE_NOT_FOUND");
        problem.TryGetProperty("correlationId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Ruta_inexistente_devuelve_404_como_ProblemDetails()
    {
        var response = await _client.GetAsync("/api/v1/no-existe", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe(ProblemJson);
    }

    [Fact]
    public async Task Comando_valido_pasa_por_el_despachador()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/dev/echo", new { text = "hola" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("text").GetString().ShouldBe("HOLA");
        body.GetProperty("length").GetInt32().ShouldBe(4);
    }

    [Fact]
    public async Task Comando_invalido_devuelve_400_con_errores_por_campo()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/dev/echo", new { text = "" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("VALIDATION.FAILED");
        var fieldError = problem.GetProperty("errors").EnumerateArray().Single();
        fieldError.GetProperty("field").GetString().ShouldBe("Text");
        fieldError.GetProperty("code").GetString().ShouldBe("DEV.ECHO_TEXT_REQUIRED");
    }

    [Fact]
    public async Task Regla_de_negocio_del_handler_devuelve_422()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/dev/echo", new { text = "fallar" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("DEV.ECHO_REJECTED");
    }

    [Fact]
    public async Task JSON_mal_formado_devuelve_400_con_codigo()
    {
        using var content = new StringContent("{ esto no es json", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/v1/dev/echo", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("REQUEST.INVALID");
    }

    private async Task<string> ReadLogsUntilAsync(string expected)
    {
        var text = string.Empty;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            text = string.Concat(Directory.Exists(factory.LogsDirectory)
                ? Directory.GetFiles(factory.LogsDirectory, "server-*.log").Select(ReadShared)
                : []);
            if (text.Contains(expected, StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(100, Ct);
        }

        return text;
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
