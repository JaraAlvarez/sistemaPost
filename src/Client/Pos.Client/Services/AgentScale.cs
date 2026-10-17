using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Modules.Organization.Contracts;

namespace Pos.Client.Services;

/// <summary>
/// Lectura del agente (<c>GET /scale/weight</c>). <c>Status</c>: OK, UNSTABLE, ZERO, NEGATIVE, OVERLOAD o ERROR; <c>Unavailable</c> = el agente
/// o la báscula no respondieron (sin báscula configurada en el agente, cable suelto, agente apagado). <c>GrossKg</c> = lo que marca la
/// báscula; <c>WeightKg</c> = bruto menos la tara manual (<c>TareKg</c>); <c>ScaleNet</c> = la báscula ya descontó una tara; <c>At</c> =
/// cuándo se leyó (viajan al servidor con la línea).
/// </summary>
public sealed record ScaleRead(
    string Status, string Message, decimal? WeightKg, decimal TareKg, bool Stable, string Display, bool Unavailable = false, decimal? GrossKg = null,
    bool ScaleNet = false, DateTimeOffset? At = null, Guid? ReadingId = null, bool EmptiedSinceLastAccepted = true, SignedScaleReading? SignedReading = null)
{
    public bool IsOk => Status == "OK" && WeightKg > 0m;

    public static ScaleRead Down(string message) => new("ERROR", message, null, 0m, false, "--", Unavailable: true);
}

/// <summary>Lectura firmada por el agente (D3): se reenvía al servidor tal cual.</summary>
public sealed record SignedScaleReading(string Payload, string Signature, Guid KeyId);

/// <summary>Clave pública que el agente creó al vincular la caja.</summary>
public sealed record ScalePairing(Guid TerminalId, Guid KeyId, string PublicKey, string Algorithm);

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

    /// <summary>
    /// La caja tiene una báscula configurada y activa, y la función "Báscula y PLU" está encendida (Fase 7-C parte A): apagada, la caja no lee
    /// la báscula (la configuración se conserva).
    /// </summary>
    public bool Enabled => Config is { Configured: true, Enabled: true, FeatureEnabled: true };


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

        if (Config is not { Configured: true, FeatureEnabled: true } config)
        {
            return null;
        }

        var aviso = await PushAsync(config);
        return aviso ?? (config is { Enabled: true, ReadingKeyId: null }
            ? "Báscula no vinculada: vincule con F1 → Vincular báscula (con un supervisor). Mientras tanto el servidor no acepta sus pesos."
            : null);
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

    /// <summary>
    /// "Probar báscula": abre en el agente una prueba con esta configuración (sin guardarla ni cambiar la báscula en uso ni su tara) y lee
    /// una vez.
    /// </summary>
    public static Task<ScaleRead> TestAsync(object settings) => SendAsync(HttpMethod.Post, "scale/test", settings);

    /// <summary>La caja agregó la línea con esta lectura: la siguiente pesada exige ver la báscula vacía (D1).</summary>
    public static Task AcceptedAsync(Guid readingId) => FireAsync("scale/accepted", new { readingId });

    /// <summary>Se eliminó la última pesada para volver a pesar (BN1).</summary>
    public static Task ReleaseAcceptedAsync() => FireAsync("scale/accepted", new { release = true });

    /// <summary>
    /// Vincula el agente de ESTE computador con la caja (D3): crea la clave que firma las lecturas y devuelve la pública para registrarla en
    /// el servidor. Nulo y el mensaje si el agente no respondió.
    /// </summary>
    public static async Task<(ScalePairing? Pairing, string? Error)> PairAsync(Guid terminalId)
    {
        try
        {
            using var response = await Agent.PostAsJsonAsync("scale/pairing", new { terminalId }, ApiClient.Json);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<ScalePairing>(ApiClient.Json), null)
                : (null, await ProblemAsync(response));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, "No se pudo hablar con el agente de esta caja para vincular la báscula.");
        }
    }

    /// <summary>
    /// BK1: el servidor ya registró la clave pendiente: el agente la activa. <c>Outdated</c> = el agente es de una versión anterior (no
    /// tiene la confirmación, BQ9); <c>Error</c> = no se pudo activar (se puede reintentar); ambos nulos/false = quedó activa.
    /// </summary>
    public static async Task<(bool Outdated, string? Error)> CommitPairingAsync(Guid keyId)
    {
        try
        {
            using var response = await Agent.PostAsJsonAsync("scale/pairing/commit", new { keyId }, ApiClient.Json);
            if (response.IsSuccessStatusCode)
            {
                return (false, null);
            }

            return response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.MethodNotAllowed
                ? (true, "Actualice el programa de la caja (agente de BusinessPost): esta versión no puede vincular la báscula.")
                : (false, await ProblemAsync(response));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, "No se pudo hablar con el agente de esta caja.");
        }
    }

    /// <summary>Otra lectura de la prueba abierta.</summary>
    public static Task<ScaleRead> TestReadAsync() => SendAsync(HttpMethod.Get, "scale/test", null);

    /// <summary>Tara de la prueba (no toca la báscula en uso).</summary>
    public static Task<ScaleRead> TestTareAsync(bool clear) => SendAsync(HttpMethod.Post, "scale/test/tare", new { clear });

    /// <summary>Cierra la prueba en el agente.</summary>
    public static async Task TestStopAsync()
    {
        try
        {
            using var response = await Agent.PostAsJsonAsync("scale/test/stop", new { }, ApiClient.Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // El agente no responde: la prueba se cierra sola al minuto.
        }
    }

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

    private static async Task FireAsync(string path, object body)
    {
        try
        {
            using var response = await Agent.PostAsJsonAsync(path, body, ApiClient.Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // El agente no responde: el servidor igual exige el retorno a cero con la lectura firmada.
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
