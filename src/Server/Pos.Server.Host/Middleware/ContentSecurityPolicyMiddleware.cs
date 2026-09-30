namespace Pos.Server.Host.Middleware;

/// <summary>
/// Cabeceras de seguridad de la interfaz (Fase 15, revisión de seguridad de la Fase 14): Blazor WebAssembly necesita
/// <c>wasm-unsafe-eval</c>; MudBlazor usa estilos en línea; la caja habla con su agente de impresión local (<c>localhost:5490</c>).
/// El asistente inicial (<c>/instalacion</c>) es una página única con su script en línea.
/// </summary>
internal sealed class ContentSecurityPolicyMiddleware(RequestDelegate next)
{
    private const string Interface =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; " +
        "connect-src 'self' http://localhost:5490; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    private const string Installation =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = context.Request.Path.StartsWithSegments("/instalacion") ? Installation : Interface;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XFrameOptions = "DENY";
        }

        return next(context);
    }
}
