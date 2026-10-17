using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Modules.Organization.Contracts;

namespace Pos.Client.Services;

/// <summary>
/// Lectura del agente (<c>GET /scale/weight</c>). <c>Status</c>: OK, UNSTABLE, ZERO, NEGATIVE, OVERLOAD o ERROR; <c>Unavailable</c> = el agente
/// o la báscula no respondieron (sin báscula configurada en el agente, cable suelto, agente apagado).
/// </summary>
public sealed record ScaleRead(string Status, string Message, decimal? WeightKg, decimal TareKg, bool Stable, string Display, bool Unavailable = false)
{
    public bool IsOk => Status == "OK" && WeightKg > 0m;

    public static ScaleRead Down(string message) => new("ERROR", message, null, 0m, false, "--", Unavailable: true);
}

/// <summary>Driver que ofrece el agente con sus valores de fábrica.</summary>
public sealed record ScaleDriverInfo(string Code, string Name, int BaudRate, int DataBits, string Parity, int StopBits, string ReadMode, string? RequestCommand);

/// <summary>Estado de la báscula en el agente: puertos serie del equipo y drivers disponibles.</summary>
public sealed record AgentScaleStatus(
    bool Configured, string? Driver, string? Description, bool Simulated, bool SimulatorAvailable, decimal TareKg, string? LastError,
    IReadOnlyList<ScaleDriverInfo> Drivers, IReadOnlyList<string> SerialPorts);

/// <summary>
/// Báscula conectada a la caja (Fase 7-C) a través del agente local (<c>http://localhost:5490</c>), como la impresora: la configuración
/// vive en el servidor (<c>/organization/terminals/{id}/scale</c>) y la caja se la entrega al agente al abrir; el agente lee el puerto serie.
/// </summary>
public sealed class AgentScale(ApiClient api)
{
    private static readonly HttpClient Agent = new() { BaseAddress = new Uri("http://localhost:5490/"), Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Báscula de la caja guardada en el servidor (null si no se pudo leer).</summary>
    public TerminalScaleDto? Config { get; private set; }

    /// <summary>La caja tiene una báscula configurada y activa.</summary>
    public bool Enabled => Config is { Configured: true, Enabled: true };

    /// <summary>Lee la báscula de la caja en el servidor y se la entrega al agente. Devuelve un aviso si el agente no la aceptó.</summary>
    public async Task<string?> LoadAsync(Guid terminalId)
    {
        try
        {
            Config = await api.GetAsync<TerminalScaleDto>($"api/v1/organization/terminals/{terminalId}/scale");
        }
        catch (ApiException)
        {
            Config = null;
            return null;
        }

        return Config is { Configured: true } config ? await PushAsync(config) : null;
    }

    /// <summary>Entrega la configuración al agente (<c>PUT /scale/config</c>); el agente la guarda en el equipo.</summary>
    public static async Task<string?> PushAsync(object settings)
    {
        try
        {
            using var response = await Agent.PutAsJsonAsync("scale/config", settings, ApiClient.Json);
            return response.IsSuccessStatusCode ? null : await ProblemAsync(response);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "No se pudo hablar con el agente de esta caja (servicio BusinessPost-TerminalAgent) para configurar la báscula.";
        }
    }

    public static Task<ScaleRead> ReadAsync() => SendAsync(HttpMethod.Get, "scale/weight", null);

    /// <summary>Aplica la configuración en el agente sin guardarla y lee una vez ("Probar báscula").</summary>
    public static Task<ScaleRead> TestAsync(object settings) => SendAsync(HttpMethod.Post, "scale/test", settings);

    /// <summary>Tara manual por software (bolsa o bandeja que no está en el producto); <paramref name="clear"/> la quita.</summary>
    public static Task<ScaleRead> TareAsync(bool clear) => SendAsync(HttpMethod.Post, "scale/tare", new { clear });

    public static async Task<AgentScaleStatus?> StatusAsync()
    {
        try
        {
            return await Agent.GetFromJsonAsync<AgentScaleStatus>("scale/status", ApiClient.Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static async Task<ScaleRead> SendAsync(HttpMethod method, string path, object? body)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: ApiClient.Json);
            }

            using var response = await Agent.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<ScaleRead>(ApiClient.Json) ?? ScaleRead.Down("La báscula no envió datos.");
            }

            var problem = await ProblemAsync(response);
            return response.StatusCode == HttpStatusCode.Conflict && !problem.Contains("configurada", StringComparison.Ordinal)
                ? new ScaleRead("ERROR", problem, null, 0m, false, "--")
                : ScaleRead.Down(problem);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ScaleRead.Down("No se pudo hablar con el agente de esta caja: la báscula no responde.");
        }
    }

    private static async Task<string> ProblemAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
            return problem.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text ? text : $"Error {(int)response.StatusCode}.";
        }
        catch (JsonException)
        {
            return $"Error {(int)response.StatusCode}.";
        }
    }
}
