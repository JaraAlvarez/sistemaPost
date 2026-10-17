using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Pos.Terminal.Agent.Scales;

namespace Pos.Terminal.Agent.Api;

/// <summary><c>POST /scale/tare</c>: toma la tara con el peso actual; con <c>clear</c> la quita.</summary>
public sealed record ScaleTareRequest(bool Clear = false);

/// <summary><c>GET /scale/pairing/pending</c>: clave pendiente de confirmar (si hay) y la vigente.</summary>
public sealed record ScalePendingDto(Guid? PendingKeyId, Guid? KeyId);

/// <summary><c>POST /scale/simulator</c> (solo desarrollo): lo que "tiene encima" la báscula simulada.</summary>
public sealed record ScaleSimulatorRequest(decimal WeightKg, bool Stable = true, bool Overload = false, bool Silent = false);

/// <summary>
/// Báscula de la caja (Fase 7-C parte B), solo <c>localhost</c> como el resto del agente: <c>GET /scale/status</c>,
/// <c>GET /scale/weight</c> (una lectura), <c>GET /scale/stream</c> (lecturas seguidas como Server-Sent Events), <c>POST /scale/tare</c>,
/// <c>PUT /scale/config</c> (aplica y guarda), la prueba "Probar báscula" (<c>POST /scale/test</c> abre una sesión de prueba aparte
/// y lee una vez; <c>GET /scale/test</c> vuelve a leer, <c>POST /scale/test/tare</c> y <c>POST /scale/test/stop</c>; nunca cambia la
/// báscula en uso ni su tara) y, solo en desarrollo, <c>POST /scale/simulator</c>.
/// </summary>
public static class ScaleEndpoints
{
    public const string InvalidScale = "AGENT.INVALID_SCALE";
    public const string SimulatorDisabled = "SCALE.SIMULATOR_DISABLED";

    /// <summary>La clave pendiente no existe (otra vinculación la reemplazó o ya se confirmó).</summary>
    public const string PairingNotPending = "SCALE.PAIRING_NOT_PENDING";

    /// <summary>Pausa entre lecturas del flujo en vivo.</summary>
    public static readonly TimeSpan StreamInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Duración máxima de un flujo (la pantalla abre otro si sigue pesando).</summary>
    public const int MaxStreamSeconds = 300;

    public static void MapScaleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/scale/status", (ScaleService scale) => TypedResults.Ok(scale.Status()))
            .WithSummary("Báscula configurada, última lectura, tara, drivers disponibles y puertos serie del equipo");
        app.MapGet("/scale/weight", WeightAsync).WithSummary("Una lectura del peso (kg, estable, cero, negativo, sobrepeso)");
        app.MapGet("/scale/stream", Stream).WithSummary("Lecturas seguidas del peso como Server-Sent Events (evento \"weight\")");
        app.MapPost("/scale/tare", TareAsync).WithSummary("Tara por software con el peso estable actual; { \"clear\": true } la quita");
        app.MapPut("/scale/config", ConfigureAsync).WithSummary("Aplica y guarda la báscula de la caja (la envía la interfaz con lo guardado en el servidor)");
        app.MapPost("/scale/test", TestAsync).WithSummary("Abre una prueba con una configuración (sin guardarla ni tocar la báscula en uso) y lee el peso una vez");
        app.MapGet("/scale/test", TestReadAsync).WithSummary("Otra lectura de la prueba abierta (peso en vivo de \"Probar báscula\")");
        app.MapPost("/scale/test/tare", TestTareAsync).WithSummary("Tara de la prueba abierta; { \"clear\": true } la quita");
        app.MapPost("/scale/test/stop", TestStopAsync).WithSummary("Cierra la prueba: la báscula en uso sigue igual");
        app.MapPost("/scale/pairing", PairAsync)
            .WithSummary("Prepara una clave PENDIENTE para vincular la caja (la vigente sigue firmando) y devuelve la pública para registrarla en el servidor");
        app.MapGet("/scale/pairing/pending", (ScaleSigner signer) => TypedResults.Ok(new ScalePendingDto(signer.PendingKeyId, signer.KeyId)))
            .WithSummary("Clave pendiente de confirmar y clave vigente (para «Completar vinculación» sin volver a registrar)");
        app.MapPost("/scale/pairing/commit", CommitAsync)
            .WithSummary("El servidor ya registró la clave pendiente: el agente la activa y la guarda (sin esto, la pendiente se descarta)");
        app.MapPost("/scale/accepted", AcceptedAsync)
            .WithSummary("La caja agregó la línea con esta lectura (la siguiente pesada exige ver la báscula vacía) o, con release, eliminó la última");
        app.MapPost("/scale/simulator", SimulateAsync).WithSummary("Solo desarrollo: cambia el peso de la báscula simulada");
    }

    private static async Task<IResult> WeightAsync(ScaleService scale, CancellationToken ct) => ToResult(await scale.ReadAsync(ct));

    private static IResult Stream(ScaleService scale, int? seconds, CancellationToken ct)
    {
        if (scale.Status() is { Configured: false })
        {
            return ToResult(ScaleOutcome.Fail(ScaleOutcome.NotConfigured, "Esta caja no tiene una báscula configurada."));
        }

        var duration = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 60, 1, MaxStreamSeconds));
        return TypedResults.ServerSentEvents(ReadingsAsync(scale, duration, ct), eventType: "weight");
    }

    private static async IAsyncEnumerable<ScaleWeightDto> ReadingsAsync(ScaleService scale, TimeSpan duration, [EnumeratorCancellation] CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + duration;
        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < until)
        {
            var outcome = await scale.ReadAsync(ct);
            yield return outcome.Weight ?? new ScaleWeightDto(
                ScaleWeightDto.Error, outcome.Error ?? "Sin lectura.", null, null, 0m, false, false, false, false, null, ScaleService.Display(null), DateTimeOffset.UtcNow);
            await Task.Delay(StreamInterval, ct);
        }
    }

    private static async Task<IResult> TareAsync(HttpContext http, ScaleService scale, IOptions<AgentOptions> options, CancellationToken ct)
    {
        var (request, rejection) = await AgentEndpoints.ReadAsync<ScaleTareRequest>(http, options.Value, ct);
        return rejection ?? ToResult(await scale.TareAsync(request!.Clear, ct));
    }

    private static async Task<IResult> ConfigureAsync(HttpContext http, ScaleService scale, IOptions<AgentOptions> options, CancellationToken ct)
    {
        var (settings, rejection) = await AgentEndpoints.ReadAsync<ScaleSettings>(http, options.Value, ct);
        if (rejection is not null)
        {
            return rejection;
        }

        return await scale.ConfigureAsync(settings!, persist: true, ct) is { } error
            ? AgentEndpoints.Problem(HttpStatusCode.BadRequest, InvalidScale, error)
            : TypedResults.Ok(scale.Status());
    }

    private static async Task<IResult> TestAsync(HttpContext http, ScaleService scale, IOptions<AgentOptions> options, CancellationToken ct)
    {
        var (settings, rejection) = await AgentEndpoints.ReadAsync<ScaleSettings>(http, options.Value, ct);
        if (rejection is not null)
        {
            return rejection;
        }

        var (error, outcome) = await scale.TestStartAsync(settings!, ct);
        return error is not null ? AgentEndpoints.Problem(HttpStatusCode.BadRequest, InvalidScale, error) : ToResult(outcome!);
    }

    private static async Task<IResult> TestReadAsync(ScaleService scale, CancellationToken ct) => ToResult(await scale.TestReadAsync(ct));

    private static async Task<IResult> TestTareAsync(HttpContext http, ScaleService scale, IOptions<AgentOptions> options, CancellationToken ct)
    {
        var (request, rejection) = await AgentEndpoints.ReadAsync<ScaleTareRequest>(http, options.Value, ct);
        return rejection ?? ToResult(await scale.TestTareAsync(request!.Clear, ct));
    }

    private static async Task<IResult> TestStopAsync(ScaleService scale, CancellationToken ct)
    {
        await scale.TestStopAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// BB6: además de lo que exige todo el agente (equipo local y Host local, LocalRequestGuard), vincular solo se acepta desde la interfaz de
    /// la caja (Origin permitido): un programa del equipo o una página cualquiera no puede preparar ni activar una clave.
    /// </summary>
    private static IResult? FromUiOnly(HttpContext http) =>
        http.Request.Headers.Origin.ToString().Length == 0
            ? AgentEndpoints.Problem(HttpStatusCode.Forbidden, LocalRequestGuard.OriginNotAllowed, "La báscula se vincula desde la pantalla de la caja.")
            : null;

    private static async Task<IResult> PairAsync(HttpContext http, ScaleSigner signer, IOptions<AgentOptions> options, CancellationToken ct)
    {
        var (request, rejection) = await AgentEndpoints.ReadAsync<ScalePairingRequest>(http, options.Value, ct);
        if ((rejection ?? FromUiOnly(http)) is { } denied)
        {
            return denied;
        }

        if (request!.TerminalId == Guid.Empty)
        {
            return AgentEndpoints.Problem(HttpStatusCode.BadRequest, AgentEndpoints.InvalidRequest, "Indique la caja que se vincula.");
        }

        // BK1/BB6: la clave nueva queda PENDIENTE; la vigente sigue firmando hasta que el servidor confirme.
        try
        {
            return TypedResults.Ok(signer.Prepare(request.TerminalId));
        }
        catch (ObjectDisposedException)
        {
            return AgentEndpoints.Problem(HttpStatusCode.Conflict, PairingNotPending, "El agente se está cerrando: vuelva a vincular la báscula.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return AgentEndpoints.Problem(HttpStatusCode.ServiceUnavailable, ScaleOutcome.Unavailable, $"No se pudo guardar la clave de la báscula: {ex.Message}");
        }
    }

    private static async Task<IResult> CommitAsync(HttpContext http, ScaleSigner signer, IOptions<AgentOptions> options, CancellationToken ct)
    {
        var (request, rejection) = await AgentEndpoints.ReadAsync<ScalePairingCommitRequest>(http, options.Value, ct);
        if ((rejection ?? FromUiOnly(http)) is { } denied)
        {
            return denied;
        }

        // BQ5: otra vinculación reemplazó la pendiente, ya se confirmó o el agente se está cerrando: 409 (nunca 500).
        try
        {
            return signer.Commit(request!.KeyId)
                ? TypedResults.NoContent()
                : AgentEndpoints.Problem(HttpStatusCode.Conflict, PairingNotPending, "No hay una clave pendiente con ese identificador: vuelva a vincular la báscula.");
        }
        catch (ObjectDisposedException)
        {
            return AgentEndpoints.Problem(HttpStatusCode.Conflict, PairingNotPending, "El agente se está cerrando: vuelva a vincular la báscula.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException
                                       or System.Text.Json.JsonException or FormatException)
        {
            return AgentEndpoints.Problem(HttpStatusCode.ServiceUnavailable, ScaleOutcome.Unavailable, $"No se pudo activar la clave de la báscula: {ex.Message}");
        }
    }

    private static async Task<IResult> AcceptedAsync(HttpContext http, ScaleService scale, IOptions<AgentOptions> options, CancellationToken ct)
    {
        var (request, rejection) = await AgentEndpoints.ReadAsync<ScaleAcceptedRequest>(http, options.Value, ct);
        if (rejection is not null)
        {
            return rejection;
        }

        if (request!.Release)
        {
            scale.ReleaseAccepted();
            return TypedResults.NoContent();
        }

        return request.ReadingId is { } id && scale.MarkAccepted(id)
            ? TypedResults.NoContent()
            : AgentEndpoints.Problem(HttpStatusCode.NotFound, AgentEndpoints.InvalidRequest, "La lectura no es de esta báscula o ya es muy antigua.");
    }

    private static async Task<IResult> SimulateAsync(HttpContext http, ScaleService scale, SimulatedScale simulator, IOptions<AgentOptions> options, CancellationToken ct)
    {
        if (!scale.SimulatorAvailable)
        {
            return AgentEndpoints.Problem(HttpStatusCode.NotFound, SimulatorDisabled, "La báscula simulada solo está disponible en el agente de desarrollo.");
        }

        var (request, rejection) = await AgentEndpoints.ReadAsync<ScaleSimulatorRequest>(http, options.Value, ct);
        if (rejection is not null)
        {
            return rejection;
        }

        if (request!.WeightKg is < -1000m or > 1000m)
        {
            return AgentEndpoints.Problem(HttpStatusCode.BadRequest, AgentEndpoints.InvalidRequest, "El peso simulado va de -1000 a 1000 kg.");
        }

        simulator.Set(request.WeightKg, request.Stable, request.Overload, request.Silent);
        return TypedResults.NoContent();
    }

    private static IResult ToResult(ScaleOutcome outcome) => outcome switch
    {
        { Weight: { } weight } => TypedResults.Ok(weight),
        { ErrorCode: ScaleOutcome.Unavailable } => AgentEndpoints.Problem(HttpStatusCode.ServiceUnavailable, outcome.ErrorCode, outcome.Error!),
        _ => AgentEndpoints.Problem(HttpStatusCode.Conflict, outcome.ErrorCode!, outcome.Error!),
    };
}
