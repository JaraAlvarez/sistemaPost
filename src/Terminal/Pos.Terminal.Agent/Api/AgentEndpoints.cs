using System.IO.Ports;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Pos.Printing;
using Pos.Terminal.Agent.Printing;

namespace Pos.Terminal.Agent.Api;

/// <summary><c>POST /print</c>: el tiquete del servidor y la impresora de la caja (tal como las devuelve el servidor).</summary>
public sealed record PrintRequest(TicketDocument? Ticket, ReceiptPrinterSettings? Printer);

/// <summary><c>POST /drawer/open</c>: la impresora a la que está conectado el cajón.</summary>
public sealed record DrawerRequest(ReceiptPrinterSettings? Printer);

/// <summary><c>POST /test-page</c>: la impresora y, opcional, el nombre de la caja para imprimirlo.</summary>
public sealed record TestPageRequest(ReceiptPrinterSettings? Printer, string? TerminalName);

public sealed record AgentStatusDto(
    string Service,
    string Version,
    string Machine,
    DateTimeOffset StartedAt,
    int Port,
    IReadOnlyList<string> Connections,
    string FileOutputDirectory,
    IReadOnlyList<string> SerialPorts,
    PrintOutcome? LastJob);

/// <summary>
/// API local del agente (solo <c>localhost</c>): <c>POST /print</c>, <c>POST /drawer/open</c>, <c>GET /status</c> y
/// <c>POST /test-page</c>. Los errores son ProblemDetails con <c>code</c>, como en el servidor.
/// </summary>
public static class AgentEndpoints
{
    public const string UnsupportedMediaType = "AGENT.UNSUPPORTED_MEDIA_TYPE";
    public const string RequestTooLarge = "AGENT.REQUEST_TOO_LARGE";
    public const string InvalidRequest = "AGENT.INVALID_REQUEST";
    public const string InvalidDocument = "AGENT.INVALID_DOCUMENT";
    public const string InvalidPrinter = "AGENT.INVALID_PRINTER";

    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    /// <summary>JSON de la API: el mismo formato del servidor (camelCase, enumeraciones como texto).</summary>
    public static JsonSerializerOptions Json { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions json)
    {
        ArgumentNullException.ThrowIfNull(json);
        json.Converters.Add(new JsonStringEnumConverter());
        json.AllowOutOfOrderMetadataProperties = true;
        json.MaxDepth = 16;
        return json;
    }

    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/status", Status).WithSummary("Estado del agente, conexiones disponibles, puertos serie del equipo y último trabajo");
        app.MapPost("/print", PrintAsync).WithSummary("Imprime el tiquete neutro (Pos.Printing) en la impresora indicada");
        app.MapPost("/drawer/open", OpenDrawerAsync).WithSummary("Abre el cajón conectado a la impresora indicada");
        app.MapPost("/test-page", TestPageAsync).WithSummary("Página de prueba: tildes, estilos, código de barras, QR, corte y pulso del cajón");
    }

    public static IResult Problem(HttpStatusCode status, string code, string detail) =>
        TypedResults.Problem(
            title: status == HttpStatusCode.ServiceUnavailable ? "Impresora no disponible" : "No se pudo atender la petición",
            detail: detail,
            statusCode: (int)status,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static Ok<AgentStatusDto> Status(PrintService printing, IOptions<AgentOptions> options)
    {
        string[] serialPorts;
        try
        {
            serialPorts = [.. SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            serialPorts = [];
        }

        string[] connections = OperatingSystem.IsWindows() ? ["FILE", "NETWORK", "WINDOWS_SPOOLER", "SERIAL"] : ["FILE", "NETWORK", "SERIAL"];
        return TypedResults.Ok(new AgentStatusDto(
            AgentInfo.ServiceName, AgentInfo.Version, Environment.MachineName, StartedAt, options.Value.Port, connections,
            options.Value.ResolveFileOutputDirectory(), serialPorts, printing.LastJob));
    }

    private static async Task<IResult> PrintAsync(HttpContext http, PrintService printing, IOptions<AgentOptions> options, CancellationToken ct)
    {
        var (request, rejection) = await ReadAsync<PrintRequest>(http, options.Value, ct);
        if (rejection is not null)
        {
            return rejection;
        }

        if (TicketValidator.Validate(request!.Ticket) is { } problem)
        {
            return Problem(HttpStatusCode.BadRequest, InvalidDocument, problem);
        }

        var printer = PrinterSettingsResolver.TryResolve(request.Printer, options.Value, out var error);
        return printer is null
            ? Problem(HttpStatusCode.BadRequest, InvalidPrinter, error)
            : ToResult(await printing.PrintAsync(request.Ticket!, printer, ct));
    }

    private static async Task<IResult> OpenDrawerAsync(HttpContext http, PrintService printing, IOptions<AgentOptions> options, CancellationToken ct)
    {
        var (request, rejection) = await ReadAsync<DrawerRequest>(http, options.Value, ct);
        if (rejection is not null)
        {
            return rejection;
        }

        var printer = PrinterSettingsResolver.TryResolve(request!.Printer, options.Value, out var error);
        return printer is null ? Problem(HttpStatusCode.BadRequest, InvalidPrinter, error) : ToResult(await printing.OpenDrawerAsync(printer, ct));
    }

    private static async Task<IResult> TestPageAsync(HttpContext http, PrintService printing, IOptions<AgentOptions> options, CancellationToken ct)
    {
        var (request, rejection) = await ReadAsync<TestPageRequest>(http, options.Value, ct);
        if (rejection is not null)
        {
            return rejection;
        }

        if (request!.TerminalName is { Length: > 60 })
        {
            return Problem(HttpStatusCode.BadRequest, InvalidRequest, "El nombre de la caja supera 60 caracteres.");
        }

        var printer = PrinterSettingsResolver.TryResolve(request.Printer, options.Value, out var error);
        return printer is null
            ? Problem(HttpStatusCode.BadRequest, InvalidPrinter, error)
            : ToResult(await printing.PrintTestPageAsync(printer, request.TerminalName, ct));
    }

    /// <summary>
    /// Lee el cuerpo JSON con el límite de tamaño (también sin <c>Content-Length</c>). Exigir <c>application/json</c> hace que un
    /// navegador tenga que pedir permiso CORS antes de enviar la petición.
    /// </summary>
    private static async Task<(T? Value, IResult? Rejection)> ReadAsync<T>(HttpContext http, AgentOptions options, CancellationToken ct)
        where T : class
    {
        if (!http.Request.HasJsonContentType())
        {
            return (null, Problem(HttpStatusCode.UnsupportedMediaType, UnsupportedMediaType, "El cuerpo se envía como application/json."));
        }

        var limit = options.MaxRequestBytes;
        if (http.Request.ContentLength > limit)
        {
            return (null, TooLarge(limit));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    return (null, TooLarge(limit));
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, TooLarge(limit));
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), Json);
            return value is null ? (null, Problem(HttpStatusCode.BadRequest, InvalidRequest, "El cuerpo está vacío.")) : (value, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return (null, Problem(HttpStatusCode.BadRequest, InvalidRequest, $"JSON inválido: {ex.Message}"));
        }
    }

    private static IResult TooLarge(int limit) =>
        Problem(HttpStatusCode.RequestEntityTooLarge, RequestTooLarge, $"La petición supera {limit / 1024} KB.");

    private static IResult ToResult(PrintOutcome outcome) => outcome switch
    {
        { Succeeded: true } => TypedResults.Ok(outcome),
        { ErrorCode: PrintOutcome.PrinterUnavailable } => Problem(HttpStatusCode.ServiceUnavailable, outcome.ErrorCode, outcome.Error!),
        _ => Problem(HttpStatusCode.Conflict, outcome.ErrorCode!, outcome.Error!),
    };
}
