using System.Buffers;
using Serilog.Context;

namespace Pos.Server.Host.Middleware;

/// <summary>
/// Asigna a cada petición un identificador de correlación: lo toma de la cabecera <c>X-Correlation-Id</c>
/// si es válido o genera uno nuevo. Se devuelve en la respuesta, se agrega a todos los logs de la petición
/// y (desde la Fase 2) a la auditoría. Es el código que el usuario le dicta a soporte ante un error.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public const int MaxLength = 64;

    private static readonly SearchValues<char> AllowedChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.");

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsValid(incoming) ? incoming : Guid.CreateVersion7().ToString("N");

        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && !value.AsSpan().ContainsAnyExcept(AllowedChars);
}
