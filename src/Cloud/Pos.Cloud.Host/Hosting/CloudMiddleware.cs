using System.Buffers;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Pos.Api.Abstractions;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;
using Serilog.Context;

namespace Pos.Cloud.Host.Hosting;

/// <summary>Código de correlación por petición (cabecera <c>X-Correlation-Id</c>): va en los logs, en la auditoría y en los errores.</summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    private static readonly SearchValues<char> AllowedChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.");

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = incoming.Length is > 0 and <= 64 && !incoming.AsSpan().ContainsAnyExcept(AllowedChars)
            ? incoming
            : Guid.CreateVersion7().ToString("N");
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
}

/// <summary>
/// Cabeceras de seguridad (§6): CSP estricta (solo recursos propios; estilos en línea por los componentes), sin marcos, sin
/// referer y sin detección de tipo. HSTS lo agrega UseHsts cuando la petición llega por HTTPS (a través del proxy).
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; " +
        "connect-src 'self' wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    /// <summary>
    /// Origen al que redirige el botón "Continuar con Google" (POST /cuenta/google → 302 a Google). Los navegadores aplican
    /// <c>form-action</c> también a las redirecciones de un envío de formulario: sin este origen, Chrome bloquea el clic y el
    /// formulario de Google nunca aparece.
    /// </summary>
    public const string GoogleSignInOrigin = "https://accounts.google.com";

    /// <summary>
    /// Páginas públicas de pago (/pagar): el formulario "Pagar" redirige al checkout alojado por la pasarela, así que
    /// <c>form-action</c> admite además ese origen (solo en estas páginas).
    /// </summary>
    public static string PaymentPagesPolicy(string? checkoutOrigin) => WithFormAction(checkoutOrigin);

    /// <summary>Pantallas de acceso (/cuenta): <c>form-action</c> admite además Google (solo en estas páginas).</summary>
    public static string AccountPagesPolicy => WithFormAction(GoogleSignInOrigin);

    private static string WithFormAction(string? origin) =>
        origin is { Length: > 0 }
            ? ContentSecurityPolicy.Replace("form-action 'self'", $"form-action 'self' {origin}", StringComparison.Ordinal)
            : ContentSecurityPolicy;

    public Task InvokeAsync(HttpContext context, Pos.Cloud.Licensing.Application.PaymentsOptions payments)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = context.Request.Path.StartsWithSegments("/pagar")
                ? PaymentPagesPolicy(Uri.TryCreate(payments.Wompi.CheckoutUrl, UriKind.Absolute, out var checkout) && checkout.Scheme == Uri.UriSchemeHttps
                    ? checkout.GetLeftPart(UriPartial.Authority)
                    : null)
                : context.Request.Path.StartsWithSegments("/cuenta")
                    ? AccountPagesPolicy
                    : context.Request.Path.StartsWithSegments("/sistecredito")
                        ? SistecreditoEndpoints.ContentSecurityPolicy
                        : ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            if (context.Request.Path.StartsWithSegments("/v1") || context.Request.Path.StartsWithSegments("/admin")
                || context.Request.Path.StartsWithSegments("/cuenta") || context.Request.Path.StartsWithSegments("/pagar")
                || context.Request.Path.StartsWithSegments("/sistecredito"))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });
        return next(context);
    }
}

/// <summary>Mientras la BD no esté lista (arranque, falta migrar), la API responde 503 con un código estable.</summary>
internal sealed class DatabaseGateMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, DatabaseReadiness readiness)
    {
        var path = context.Request.Path;
        // Sistecrédito no usa la BD: su aviso y su página responden aunque la BD esté iniciando.
        if (!readiness.IsReady && !path.StartsWithSegments("/health") && !path.StartsWithSegments("/_framework") && !path.StartsWithSegments("/_content")
            && !path.StartsWithSegments("/sistecredito"))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Error.Unexpected("SYSTEM.DATABASE_NOT_READY", $"El servidor está iniciando o la base de datos no está lista: {readiness.Detail}")
                .ToProblem().ExecuteAsync(context);
        }

        return next(context);
    }
}

/// <summary>Excepción no controlada → ProblemDetails con código estable (sin detalles internos fuera de desarrollo).</summary>
internal sealed partial class GlobalExceptionHandler(IProblemDetailsService problems, IHostEnvironment environment, ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case DomainException domain:
                LogDomain(logger, domain.Error.Code, domain);
                problem = Create(ResultHttpExtensions.StatusCodeFor(domain.Error.Type), ResultHttpExtensions.TitleFor(domain.Error.Type), domain.Error.Message, domain.Error.Code);
                break;
            case BadHttpRequestException bad:
                problem = Create(bad.StatusCode, "Solicitud inválida", environment.IsDevelopment() ? bad.Message : "La solicitud no es válida.", "REQUEST.INVALID");
                break;
            default:
                LogUnexpected(logger, exception);
                problem = Create(
                    StatusCodes.Status500InternalServerError,
                    "Error inesperado",
                    environment.IsDevelopment() ? exception.Message : "Ocurrió un error inesperado. Informe a soporte el código de correlación.",
                    "SYSTEM.UNEXPECTED");
                break;
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem, Exception = exception });
    }

    private static ProblemDetails Create(int status, string title, string detail, string code)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions[ResultHttpExtensions.ErrorCodeExtension] = code;
        return problem;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invariante de dominio violada: {ErrorCode}")]
    private static partial void LogDomain(ILogger logger, string errorCode, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error no controlado")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);
}
