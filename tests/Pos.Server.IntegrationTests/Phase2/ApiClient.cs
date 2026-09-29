using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Modules.Organization.Contracts;

namespace Pos.Server.IntegrationTests.Phase2;

/// <summary>Atajos HTTP para las pruebas de la Fase 2.</summary>
public static class ApiClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static object SetupBody(string nit = "900123456", string checkDigit = "8") => new
    {
        company = new
        {
            legalName = "Supermercado La Economía SAS",
            tradeName = "La Economía",
            personType = "Legal",
            identificationType = "NIT",
            identificationNumber = nit,
            checkDigit,
            taxRegime = "48",
            fiscalResponsibilities = new[] { "O-15", "R-99-PN" },
            municipalityCode = "05001",
            address = "Calle 50 # 45-20",
            phone = "6045551234",
            email = "contacto@laeconomia.co",
        },
        branch = new { code = "S01", name = "Centro", municipalityCode = "05001", address = "Calle 50 # 45-20" },
        terminal = new { code = "C01", name = "Caja 1" },
    };

    public static async Task<SetupResultDto> SetupAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/setup", SetupBody(), Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SetupResultDto>(Json, Ct))!;
    }

    public static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    /// <summary>Código de error (<c>code</c>) de una respuesta ProblemDetails.</summary>
    public static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        return json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static async Task ShouldFailWithAsync(this Task<HttpResponseMessage> call, HttpStatusCode status, string code)
    {
        var response = await call;
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        (await ErrorCodeAsync(response)).ShouldBe(code);
    }
}
