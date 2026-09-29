using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Organization.Contracts;

namespace Pos.Server.IntegrationTests.Phase2;

/// <summary>Atajos HTTP para las pruebas de las Fases 2 y 3.</summary>
public static class ApiClient
{
    public const string OwnerUsername = "dueno";
    public const string OwnerPassword = "Sup3rmercado-Seguro";
    public const string OwnerPosCode = "100";
    public const string OwnerPin = "4826";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static object SetupBody(string nit = "900123456", string checkDigit = "8", string ownerPassword = OwnerPassword) => new
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
        owner = new { username = OwnerUsername, displayName = "Dueño de la tienda", password = ownerPassword, posCode = OwnerPosCode, pin = OwnerPin },
        terminal = new { code = "C01", name = "Caja 1" },
    };

    /// <summary>Ejecuta el asistente inicial y deja el cliente autenticado como Propietario.</summary>
    public static async Task<SetupResultDto> SetupAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/setup", SetupBody(), Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var setup = (await response.Content.ReadFromJsonAsync<SetupResultDto>(Json, Ct))!;
        await LoginAsync(client, OwnerUsername, OwnerPassword);
        return setup;
    }

    /// <summary>Cliente autenticado como Propietario; ejecuta el asistente si hace falta.</summary>
    public static async Task<HttpClient> OwnerClientAsync(PosServerFactory factory)
    {
        var client = factory.CreateClient();
        var status = await GetAsync<SetupStatusDto>(client, "/api/v1/setup/status");
        if (!status.IsCompleted)
        {
            await SetupAsync(client);
        }
        else
        {
            await LoginAsync(client, OwnerUsername, OwnerPassword);
        }

        return client;
    }

    /// <summary>Entra al backoffice y pone el token en el cliente.</summary>
    public static async Task<LoginResultDto> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password }, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var login = (await response.Content.ReadFromJsonAsync<LoginResultDto>(Json, Ct))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return login;
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
