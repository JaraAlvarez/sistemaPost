using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Pos.Terminal.Agent.Scales;

namespace Pos.Terminal.Agent.Api;

/// <summary><c>POST /scale/tare</c>: toma la tara con el peso actual; con <c>clear</c> la quita.</summary>
public sealed record ScaleTareRequest(bool Clear = false);

/// <summary><c>POST /scale/simulator</c> (solo desarrollo): lo que "tiene encima" la báscula simulada.</summary>
public sealed record ScaleSimulatorRequest(decimal WeightKg, bool Stable = true, bool Overload = false, bool Silent = false);

/// <summary>
/// Báscula de la caja (Fase 7-C parte B), solo <c>localhost</c> como el resto del agente: <c>GET /scale/status</c>,
/// <c>GET /scale/weight</c> (una lectura), <c>GET /scale/stream</c> (lecturas seguidas como Server-Sent Events), <c>POST /scale/tare</c>,
/// <c>PUT /scale/config</c> (aplica y guarda), <c>POST /scale/test</c> (aplica sin guardar y lee una vez) y, solo en desarrollo,
/// <c>POST /scale/simulator</c>.
/// </summary>
public static class ScaleEndpoints
{
    public const string InvalidScale = "AGENT.INVALID_SCALE";
    public const string SimulatorDisabled = "SCALE.SIMULATOR_DISABLED";

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
        app.MapPost("/scale/test", TestAsync).WithSummary("Aplica una configuración sin guardarla y lee el peso una vez (\"Probar báscula\")");
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

        if (await scale.ConfigureAsync(settings! with { Enabled = true }, persist: false, ct) is { } error)
        {
            return AgentEndpoints.Problem(HttpStatusCode.BadRequest, InvalidScale, error);
        }

        return ToResult(await scale.ReadAsync(ct));
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
