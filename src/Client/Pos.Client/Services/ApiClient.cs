using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Client.Services;

/// <summary>
/// Error de la API (ProblemDetails): el mensaje ya viene en español. <see cref="Permission"/>, <see cref="Action"/> y <see cref="TargetId"/>
/// llegan cuando hace falta la autorización de un supervisor (<c>AUTH.AUTHORIZATION_REQUIRED</c>).
/// </summary>
public sealed class ApiException(HttpStatusCode status, string? code, string message, string? permission, string? action, Guid? targetId)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    public string? Code { get; } = code;

    public string? Permission { get; } = permission;

    public string? Action { get; } = action;

    public Guid? TargetId { get; } = targetId;

    public bool NeedsSupervisor => Code == "AUTH.AUTHORIZATION_REQUIRED" && Permission is not null;
}

/// <summary>Cliente de la API del servidor de la tienda: sesión, credencial del equipo y autorizaciones de supervisor.</summary>
public sealed class ApiClient(HttpClient http, SessionState session)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Se dispara con 401: la sesión venció o fue cerrada.</summary>
    public event Action? Unauthorized;

    public Task<T> GetAsync<T>(string path) => SendAsync<T>(HttpMethod.Get, path, null);

    public Task<T> PostAsync<T>(string path, object? body = null, Guid? grant = null, string? idempotencyKey = null) =>
        SendAsync<T>(HttpMethod.Post, path, body, grant, idempotencyKey);

    public Task<T> PutAsync<T>(string path, object? body) => SendAsync<T>(HttpMethod.Put, path, body);

    public Task<T> PatchAsync<T>(string path, object? body) => SendAsync<T>(HttpMethod.Patch, path, body);

    public async Task PostAsync(string path, object? body = null, Guid? grant = null) => await SendAsync<JsonElement?>(HttpMethod.Post, path, body, grant);

    /// <summary>Descarga un archivo (reportes en Excel, CSV o PDF): nombre, tipo y contenido.</summary>
    public async Task<(string FileName, string ContentType, byte[] Content)> DownloadAsync(string path)
    {
        using var request = Request(HttpMethod.Get, path, null, null, null);
        using var response = await http.SendAsync(request);
        await EnsureAsync(response);
        var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName ?? "archivo";
        return (name.Trim('"'), response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", await response.Content.ReadAsByteArrayAsync());
    }

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, Guid? grant = null, string? idempotencyKey = null)
    {
        using var request = Request(method, path, body, grant, idempotencyKey);
        using var response = await http.SendAsync(request);
        await EnsureAsync(response);
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
        {
            return default!;
        }

        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object? body, Guid? grant, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (session.Token is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (session.Device is { } device)
        {
            request.Headers.Add("X-Device-Id", device.DeviceId.ToString());
            request.Headers.Add("X-Device-Secret", device.DeviceSecret);
        }

        if (grant is { } g)
        {
            request.Headers.Add("X-Authorization-Grant", g.ToString());
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private async Task EnsureAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized && session.Token is not null)
        {
            Unauthorized?.Invoke();
        }

        string? code = null, permission = null, action = null;
        Guid? target = null;
        var message = $"El servidor respondió {(int)response.StatusCode}.";
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
            code = Text(problem, "code");
            permission = Text(problem, "permission");
            action = Text(problem, "action");
            target = Guid.TryParse(Text(problem, "targetId"), out var id) ? id : null;
            message = Text(problem, "detail") ?? Text(problem, "title") ?? message;
            if (problem.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var details = errors.EnumerateObject().SelectMany(e => e.Value.ValueKind == JsonValueKind.Array ? e.Value.EnumerateArray().Select(v => v.ToString()) : [e.Value.ToString()]);
                message = $"{message} {string.Join(" ", details)}";
            }
        }
        catch (JsonException)
        {
            // Respuesta sin ProblemDetails.
        }

        throw new ApiException(response.StatusCode, code, message, permission, action, target);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
