using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Pos.Cloud.Host.Hosting;

/// <summary>
/// Direcciones públicas que exige la API de Sistecrédito al crear un cobro (<c>urlResponse</c> y <c>urlConfirmation</c>, ADR-0064) y que
/// Sistecrédito puede validar o llamar. El POS está en el local, sin dirección pública, así que NO depende de ellas: confirma el pago
/// CONSULTANDO el estado del cobro cada pocos segundos (D7B-03). Por eso aquí:
/// <list type="bullet">
/// <item><c>GET /sistecredito/respuesta</c>: página amable, estática y sin scripts, a la que Sistecrédito lleva al cliente al terminar.</item>
/// <item><c>GET|POST /sistecredito/confirmacion</c>: recibe el aviso, responde 200 de inmediato y NO guarda nada; solo registra (Information)
/// el id de la transacción y el estado, saneados. Límite por IP y del tamaño del cuerpo. Nunca se registran documentos, nombres ni el cuerpo.</item>
/// </list>
/// </summary>
internal static partial class SistecreditoEndpoints
{
    public const string ResponsePath = "/sistecredito/respuesta";
    public const string ConfirmationPath = "/sistecredito/confirmacion";

    /// <summary>Política de límite de peticiones por IP (la registra el host).</summary>
    public const string RateLimit = "sistecredito";

    /// <summary>Tamaño máximo del aviso (un aviso real es de unos cientos de bytes).</summary>
    public const int MaxBodyBytes = 16 * 1024;

    /// <summary>CSP de estas páginas: nada de scripts, ni formularios, ni marcos; solo los estilos en línea de la página.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly string[] IdFields = ["transactionId", "transaction_id", "idTransaction", "id", "_id"];
    private static readonly string[] StatusFields = ["transactionStatus", "transaction_status", "status", "state"];

    private const string ResponsePage = """
        <!DOCTYPE html>
        <html lang="es">
        <head>
        <meta charset="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <meta name="robots" content="noindex" />
        <title>Pago con Sistecrédito · BusinessPost</title>
        <style>
        body{margin:0;min-height:100vh;display:grid;place-items:center;font-family:Inter,"Segoe UI",system-ui,sans-serif;color:#1F2432;
          background:linear-gradient(160deg,#11157D,#2524C8 55%,#5B3DF5);padding:16px;box-sizing:border-box}
        main{background:#fff;border-radius:20px;max-width:440px;padding:32px 28px;box-shadow:0 30px 60px -20px rgba(10,6,50,.6);text-align:center}
        .ok{width:64px;height:64px;border-radius:50%;margin:0 auto 16px;display:grid;place-items:center;background:#ecfdf3;color:#16A34A;font-size:32px;font-weight:800}
        h1{font-size:22px;margin:0 0 10px}
        p{color:#6B7280;line-height:1.55;margin:0 0 10px;font-size:15px}
        small{color:#9CA3AF}
        </style>
        </head>
        <body>
        <main>
        <div class="ok" aria-hidden="true">✓</div>
        <h1>Tu pago con Sistecrédito se está procesando</h1>
        <p>Vuelve a la caja: allí se confirma el pago y se entrega tu factura.</p>
        <p>Ya puedes cerrar esta página.</p>
        <small>BusinessPost</small>
        </main>
        </body>
        </html>
        """;

    public static IEndpointRouteBuilder MapSistecreditoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/sistecredito").WithTags("Sistecrédito").AllowAnonymous().RequireRateLimiting(RateLimit);

        group.MapGet("/respuesta", () => Results.Content(ResponsePage, "text/html; charset=utf-8"))
            .WithSummary("Página de vuelta del cliente tras pagar con Sistecrédito (sin datos ni scripts)");

        group.MapMethods("/confirmacion", [HttpMethods.Get, HttpMethods.Post], ConfirmAsync)
            .WithSummary("Aviso de Sistecrédito: responde 200 y no guarda nada (el POS confirma consultando el estado, D7B-03)");
        return endpoints;
    }

    private static async Task<IResult> ConfirmAsync(HttpRequest request, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        string? id = null;
        string? status = null;
        foreach (var name in IdFields)
        {
            id ??= request.Query[name].FirstOrDefault();
        }

        foreach (var name in StatusFields)
        {
            status ??= request.Query[name].FirstOrDefault();
        }

        if (HttpMethods.IsPost(request.Method))
        {
            var body = await ReadLimitedAsync(request, cancellationToken);
            if (body is null)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var (bodyId, bodyStatus) = Extract(body, request.ContentType);
            id ??= bodyId;
            status ??= bodyStatus;
        }

        // Solo el id y el estado, saneados (nunca el cuerpo: puede traer el documento o el nombre del cliente).
        var transactionId = Sanitize(id);
        var transactionStatus = Sanitize(status);
        var logger = loggers.CreateLogger(typeof(SistecreditoEndpoints));
        LogConfirmation(logger, transactionId, transactionStatus);
        return Results.Ok(new { received = true });
    }

    /// <summary>Lee el cuerpo hasta <see cref="MaxBodyBytes"/>; <c>null</c> si es más grande (también sin Content-Length).</summary>
    private static async Task<string?> ReadLimitedAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
        {
            total += read;
        }

        return total > MaxBodyBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }

    internal static (string? Id, string? Status) Extract(string body, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        if (contentType?.Contains("form", StringComparison.OrdinalIgnoreCase) == true)
        {
            var form = QueryHelpers.ParseQuery(body.StartsWith('?') ? body : "?" + body);
            return (First(form, IdFields), First(form, StatusFields));
        }

        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            // Algunos avisos envuelven la transacción en "data".
            var scope = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;
            return (Scalar(scope, IdFields) ?? Scalar(root, IdFields), Scalar(scope, StatusFields) ?? Scalar(root, StatusFields));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Solo letras, números, guion y guion bajo, hasta 64 caracteres (evita inyectar líneas o datos en los registros).</summary>
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(sin dato)";
        }

        var clean = new string(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(64).ToArray());
        return clean.Length == 0 ? "(inválido)" : clean;
    }

    private static string? First(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> form, string[] names) =>
        names.Select(n => form.TryGetValue(n, out var v) ? v.FirstOrDefault() : null).FirstOrDefault(v => v is not null);

    private static string? Scalar(JsonElement element, string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null,
                };
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Aviso de Sistecrédito recibido (no se guarda; el POS confirma consultando): transacción {TransactionId}, estado {Status}")]
    private static partial void LogConfirmation(ILogger logger, string transactionId, string status);
}
