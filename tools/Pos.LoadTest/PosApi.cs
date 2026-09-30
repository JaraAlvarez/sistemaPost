using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pos.LoadTest;

/// <summary>Error de la API con su código estable (<c>code</c> del ProblemDetails).</summary>
public sealed class ApiException(HttpStatusCode status, string? code, string body) : Exception($"{(int)status} {code}: {body}")
{
    public HttpStatusCode Status { get; } = status;

    public string? Code { get; } = code;
}

/// <summary>Cliente HTTP mínimo de la API del POS (JSON sin tipos del servidor) que mide cada llamada.</summary>
public sealed class PosApi : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public PosApi(Uri server, Metrics? metrics = null)
    {
        _http = HandlerFactory is { } factory ? new HttpClient(factory(), disposeHandler: true) : new HttpClient();
        _http.BaseAddress = server;
        _http.Timeout = TimeSpan.FromMinutes(5);
        Metrics = metrics;
    }

    public Metrics? Metrics { get; }

    /// <summary>Con <c>false</c> no se miden las llamadas (calentamiento: la primera vez se carga el código).</summary>
    public bool Recording { get; set; } = true;

    /// <summary>Solo para la prueba de coherencia: dirige las llamadas al servidor en memoria (TestServer).</summary>
    public static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    public void UseDevice(string deviceId, string secret)
    {
        _http.DefaultRequestHeaders.Add("X-Device-Id", deviceId);
        _http.DefaultRequestHeaders.Add("X-Device-Secret", secret);
    }

    public async Task LoginAsync(string username, string password, CancellationToken ct) =>
        Token((await SendAsync(HttpMethod.Post, "api/v1/auth/login", new { username, password }, null, ct))!["token"]!.GetValue<string>());

    public async Task PosLoginAsync(string posCode, string pin, CancellationToken ct) =>
        Token((await SendAsync(HttpMethod.Post, "api/v1/auth/pos-login", new { posCode, pin }, null, ct))!["token"]!.GetValue<string>());

    public Task<JsonNode?> GetAsync(string path, CancellationToken ct, string? metric = null) => SendAsync(HttpMethod.Get, path, null, metric, ct);

    public Task<JsonNode?> PostAsync(string path, object? body, CancellationToken ct, string? metric = null) => SendAsync(HttpMethod.Post, path, body, metric, ct);

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body, string? metric, CancellationToken ct, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        var watch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            Record(metric, watch, ok: false);
            throw;
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            Record(metric, watch, response.IsSuccessStatusCode);
            if (!response.IsSuccessStatusCode)
            {
                string? code = null;
                try
                {
                    code = JsonNode.Parse(text)?["code"]?.GetValue<string>();
                }
                catch (JsonException)
                {
                    // Cuerpo que no es JSON.
                }

                throw new ApiException(response.StatusCode, code, text.Length > 400 ? text[..400] : text);
            }

            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
    }

    /// <summary>Descarga (p. ej. un reporte en Excel) y devuelve los bytes.</summary>
    public async Task<long> DownloadAsync(string path, string metric, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var response = await _http.GetAsync(path, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        Record(metric, watch, response.IsSuccessStatusCode);
        return response.IsSuccessStatusCode ? bytes.LongLength : throw new ApiException(response.StatusCode, null, "descarga");
    }

    public async Task<JsonNode?> UploadAsync(string path, string fileName, byte[] content, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", fileName);
        using var response = await _http.PostAsync(path, form, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return response.IsSuccessStatusCode ? JsonNode.Parse(text) : throw new ApiException(response.StatusCode, null, text);
    }

    public void Dispose() => _http.Dispose();

    private void Token(string token) => _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private void Record(string? metric, Stopwatch watch, bool ok)
    {
        if (metric is not null && Recording)
        {
            Metrics?.Add(metric, watch.Elapsed.TotalMilliseconds, ok);
        }
    }
}
