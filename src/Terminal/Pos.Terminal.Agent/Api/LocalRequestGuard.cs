using System.Net;
using Microsoft.Extensions.Options;

namespace Pos.Terminal.Agent.Api;

/// <summary>
/// Protección del agente, que no autentica a quien lo llama (D7-14): solo peticiones del propio equipo (IP de loopback), con
/// <c>Host</c> local (evita el "DNS rebinding") y, si vienen de un navegador (<c>Origin</c>), solo desde los orígenes permitidos
/// (evita que una página web cualquiera imprima o abra el cajón). Kestrel además escucha solo en 127.0.0.1 y ::1.
/// </summary>
public sealed class LocalRequestGuard(RequestDelegate next, IOptions<AgentOptions> options, ILogger<LocalRequestGuard> logger)
{
    public const string LocalOnly = "AGENT.LOCAL_ONLY";
    public const string InvalidHost = "AGENT.INVALID_HOST";
    public const string OriginNotAllowed = "AGENT.ORIGIN_NOT_ALLOWED";

    private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "[::1]"];

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var remote = context.Connection.RemoteIpAddress;
        var origin = context.Request.Headers.Origin.ToString();
        (string Code, string Detail)? rejection = null;
        if (remote is not null && !IPAddress.IsLoopback(remote))
        {
            rejection = (LocalOnly, "El agente solo atiende al propio equipo.");
        }
        else if (!LocalHosts.Contains(context.Request.Host.Host, StringComparer.OrdinalIgnoreCase))
        {
            rejection = (InvalidHost, "Use http://localhost para llamar al agente.");
        }
        else if (origin.Length > 0 && !IsAllowed(origin))
        {
            rejection = (OriginNotAllowed, $"El origen {origin} no puede usar el agente.");
        }

        if (rejection is { } r)
        {
            AgentLog.RequestRejected(logger, r.Code, remote?.ToString() ?? "-");
            await AgentEndpoints.Problem(HttpStatusCode.Forbidden, r.Code, r.Detail).ExecuteAsync(context);
            return;
        }

        // Private Network Access (Chrome): la interfaz servida por el servidor de la tienda pide permiso para llamar a localhost.
        if (origin.Length > 0 && HttpMethods.IsOptions(context.Request.Method)
            && string.Equals(context.Request.Headers["Access-Control-Request-Private-Network"], "true", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
        }

        await next(context);
    }

    private bool IsAllowed(string origin) => options.Value.AllowedOrigins.Any(a => string.Equals(a.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
}
