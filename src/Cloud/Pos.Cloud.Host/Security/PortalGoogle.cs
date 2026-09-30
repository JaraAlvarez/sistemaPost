using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.PortalIdentity.Api;
using Pos.Cloud.PortalIdentity.Application;
using Pos.Cloud.PortalIdentity.Domain;

namespace Pos.Cloud.Host.Security;

/// <summary>
/// Ingreso con Google (⚙️ sección <c>Portal:Google</c>; en el contenedor <c>Portal__Google__ClientId</c> y
/// <c>Portal__Google__ClientSecret</c>). Sin ambos valores el ingreso con Google no existe: ni botón, ni esquema, ni rutas.
/// El secreto nunca se registra en los logs.
/// </summary>
internal sealed class PortalGoogleOptions
{
    public const string SectionName = "Portal:Google";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>
/// Esquemas del ingreso con Google (ADR-0062): el manejador OAuth/OpenID de Google (callback <c>/signin-google</c>) deja la
/// identidad de Google en una cookie TEMPORAL (<see cref="PortalSchemes.External"/>, 5 minutos); <c>/cuenta/google/completar</c>
/// la lee, la borra y crea la sesión NORMAL del portal (cookie <c>__Host-pos-portal</c>) si el correo corresponde a un usuario.
/// </summary>
internal static class PortalGoogle
{
    public const string ChallengePath = "/cuenta/google";
    public const string CompletePath = "/cuenta/google/completar";
    public const string CallbackPath = "/signin-google";

    /// <summary>Reclamo con el <c>email_verified</c> de Google ("true"/"false").</summary>
    public const string EmailVerifiedClaim = "portal:google_email_verified";

    public const string ExternalCookieName = "__Secure-pos-google";

    /// <summary>
    /// Registra el esquema de Google SOLO si la configuración FINAL (leída al resolver las opciones, no al registrar) trae el
    /// ClientId y el secreto: sin ellos, el esquema no existe y <c>/signin-google</c> no responde.
    /// </summary>
    public static AuthenticationBuilder AddPortalGoogle(this AuthenticationBuilder authentication)
    {
        var services = authentication.Services;
        services.AddOptions<PortalGoogleOptions>().BindConfiguration(PortalGoogleOptions.SectionName);

        authentication.AddCookie(PortalSchemes.External, options =>
        {
            options.Cookie.Name = ExternalCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            // Lax: la cookie se escribe al volver de Google (navegación entre sitios) y se lee en la redirección siguiente.
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.IsEssential = true;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            options.SlidingExpiration = false;
        });

        // Lo mismo que AddGoogle, pero con el esquema condicionado a la configuración final.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<GoogleOptions>, OAuthPostConfigureOptions<GoogleOptions, GoogleHandler>>());
        services.TryAddTransient<GoogleHandler>();
        services.AddOptions<AuthenticationOptions>().Configure<IOptions<PortalGoogleOptions>>((auth, google) =>
        {
            if (google.Value.IsConfigured && !auth.SchemeMap.ContainsKey(PortalSchemes.Google))
            {
                auth.AddScheme<GoogleHandler>(PortalSchemes.Google, "Google");
            }
        });
        services.AddOptions<GoogleOptions>(PortalSchemes.Google)
            .Configure<IOptions<PortalGoogleOptions>>((options, google) => Configure(options, google.Value))
            .Validate(options =>
            {
                options.Validate(PortalSchemes.Google);
                return true;
            });
        return authentication;
    }

    private static void Configure(GoogleOptions options, PortalGoogleOptions google)
    {
        options.ClientId = google.ClientId?.Trim() ?? string.Empty;
        options.ClientSecret = google.ClientSecret?.Trim() ?? string.Empty;
        options.SignInScheme = PortalSchemes.External;
        options.CallbackPath = CallbackPath;
        options.SaveTokens = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ClaimActions.MapCustomJson(EmailVerifiedClaim, EmailVerified);
        options.Events.OnRemoteFailure = context =>
        {
            // Cancelado por el usuario, estado vencido o respuesta inválida de Google: de vuelta al ingreso con un aviso.
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PortalGoogle));
            PortalGoogleLog.RemoteFailure(logger, context.Failure?.GetType().Name ?? "desconocido", context.Failure?.Message ?? string.Empty);
            context.Response.Redirect(LoginUrl(context.Request, "error"));
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }

    /// <summary><c>email_verified</c> (userinfo v3 / OpenID) o <c>verified_email</c> (v2), booleano o texto.</summary>
    private static string? EmailVerified(JsonElement user)
    {
        foreach (var name in (ReadOnlySpan<string>)["email_verified", "verified_email"])
        {
            if (user.TryGetProperty(name, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.True => "true",
                    JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed ? "true" : "false",
                    _ => "false",
                };
            }
        }

        return "false";
    }

    public static string LoginUrl(HttpRequest request, string googleResult) =>
        $"{request.PathBase}{PortalAuthenticationHandler.LoginPath}?google={Uri.EscapeDataString(googleResult)}";

    public static IEndpointRouteBuilder MapPortalGoogleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Botón "Continuar con Google": formulario POST con token antifalsificación → desafío de Google.
        endpoints.MapPost(ChallengePath, ([FromForm] string? returnUrl, HttpContext http, IOptions<PortalGoogleOptions> google) =>
            {
                if (!google.Value.IsConfigured)
                {
                    return Results.NotFound();
                }

                var target = PortalCookie.SafeReturnUrl(returnUrl);
                var properties = new AuthenticationProperties
                {
                    RedirectUri = $"{http.Request.PathBase}{CompletePath}?returnUrl={Uri.EscapeDataString(target)}",
                };
                return Results.Challenge(properties, [PortalSchemes.Google]);
            })
            .AllowAnonymous()
            .RequireRateLimiting(PortalIdentityModule.LoginRateLimit);

        endpoints.MapGet(CompletePath, CompleteAsync)
            .AllowAnonymous()
            .RequireRateLimiting(PortalIdentityModule.LoginRateLimit);
        return endpoints;
    }

    private static async Task<IResult> CompleteAsync(string? returnUrl, HttpContext http, IDispatcher dispatcher, IOptions<PortalGoogleOptions> google)
    {
        if (!google.Value.IsConfigured)
        {
            return Results.NotFound();
        }

        var external = await http.AuthenticateAsync(PortalSchemes.External);
        await http.SignOutAsync(PortalSchemes.External);
        if (!external.Succeeded || external.Principal is not { } principal)
        {
            return Results.Redirect(LoginUrl(http.Request, "error"));
        }

        var userAgent = http.Request.Headers.UserAgent.ToString();
        var result = await dispatcher.Send(new PortalExternalLoginCommand(
            PortalExternalProviders.Google,
            principal.FindFirstValue(ClaimTypes.Email),
            string.Equals(principal.FindFirstValue(EmailVerifiedClaim), "true", StringComparison.Ordinal),
            SessionChannel.Portal,
            http.Connection.RemoteIpAddress,
            userAgent.Length == 0 ? null : userAgent));
        var error = result.IsFailure ? result.Error : result.Value.Error;
        if (error is not null)
        {
            return Results.Redirect(LoginUrl(http.Request, error.Code == PortalIdentityErrors.UserLocked.Code ? "bloqueado" : "rechazado"));
        }

        var session = result.Value.Value!;
        PortalCookie.Write(http.Response, session.Token, session.ExpiresAt);
        var target = $"{http.Request.PathBase}{(session.User.MustChangePassword ? MustChangePasswordMiddleware.MyAccountPath : PortalCookie.SafeReturnUrl(returnUrl))}";
        return SameSiteContinue(target);
    }

    /// <summary>
    /// Página intermedia que sigue a <paramref name="target"/> con una navegación iniciada por el propio portal. Una redirección
    /// HTTP directa seguiría siendo parte de la cadena que empezó en Google (entre sitios) y el navegador no enviaría la cookie
    /// <c>SameSite=Strict</c> de la sesión recién creada: el usuario volvería a ver la pantalla de ingreso.
    /// </summary>
    private static IResult SameSiteContinue(string target)
    {
        var url = WebUtility.HtmlEncode(target);
        var html = string.Create(CultureInfo.InvariantCulture, $"""
            <!DOCTYPE html>
            <html lang="es"><head><meta charset="utf-8" /><meta http-equiv="refresh" content="0;url={url}" /><title>Ingresando · BusinessPost</title></head>
            <body><p>Ingresando al portal… Si la página no cambia, <a href="{url}">continúe aquí</a>.</p></body></html>
            """);
        return Results.Content(html, "text/html; charset=utf-8");
    }
}

internal static partial class PortalGoogleLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingreso con Google no completado ({FailureType}): {FailureMessage}")]
    public static partial void RemoteFailure(ILogger logger, string failureType, string failureMessage);
}
