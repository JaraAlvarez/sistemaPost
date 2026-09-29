using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Pos.Api.Abstractions;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.PortalIdentity.Application;
using Pos.Cloud.PortalIdentity.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.Host.Security;

/// <summary>Esquemas de autenticación del portal: cookie (navegador) y Bearer (API interna /admin), elegidos por la ruta.</summary>
internal static class PortalSchemes
{
    public const string Selector = "Portal";
    public const string Cookie = "PortalCookie";
    public const string Bearer = "PortalBearer";
}

/// <summary>
/// Cookie de sesión del portal: prefijo <c>__Host-</c> (solo HTTPS, sin dominio, ruta /), <c>HttpOnly</c> y <c>SameSite=Strict</c>
/// (§6). Contiene solo el token opaco; la sesión vive en la BD y se puede revocar.
/// </summary>
internal static class PortalCookie
{
    public const string Name = "__Host-pos-portal";

    public static void Write(HttpResponse response, string token, DateTimeOffset expires)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expires,
            IsEssential = true,
        });
    }

    public static void Delete(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Delete(Name, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
    }

    /// <summary>Solo rutas locales del portal (evita redirecciones abiertas).</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 and < 500 } url && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal) && !url.StartsWith("/cuenta", StringComparison.OrdinalIgnoreCase)
            ? url
            : "/";
}

internal sealed class PortalAuthenticationOptions : AuthenticationSchemeOptions
{
    public SessionChannel Channel { get; set; }
}

/// <summary>Resuelve la sesión del token opaco (cookie o Bearer) contra la BD en cada petición: revocar es inmediato.</summary>
internal sealed class PortalAuthenticationHandler(IOptionsMonitor<PortalAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<PortalAuthenticationOptions>(options, logger, encoder)
{
    public const string LoginPath = "/cuenta/ingresar";
    public const string DeniedPath = "/sin-acceso";

    public static ClaimsPrincipal ToPrincipal(AuthenticatedPortalSession session, string scheme)
    {
        ArgumentNullException.ThrowIfNull(session);
        List<Claim> claims =
        [
            new(PortalClaims.UserId, session.UserId.ToString()),
            new(PortalClaims.SessionId, session.SessionId.ToString()),
            new(ClaimTypes.Name, session.DisplayName),
            new(ClaimTypes.Email, session.Email),
            new(PortalClaims.Role, session.Role),
            new(PortalClaims.Stage, session.Stage == SessionStage.Active ? PortalClaims.ActiveStage : session.Stage.ToString()),
            new(PortalClaims.MustChangePassword, session.MustChangePassword.ToString(CultureInfo.InvariantCulture)),
            .. session.Permissions.Select(p => new Claim(PortalClaims.Permission, p)),
        ];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, ClaimTypes.Name, PortalClaims.Role));
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Options.Channel == SessionChannel.Portal ? Request.Cookies[PortalCookie.Name] : BearerToken();
        if (string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var session = await Context.RequestServices.GetRequiredService<PortalSessionAuthenticator>()
            .AuthenticateAsync(token, Options.Channel, Context.RequestAborted);
        return session is null
            ? AuthenticateResult.Fail("La sesión no existe, venció o fue revocada.")
            : AuthenticateResult.Success(new AuthenticationTicket(ToPrincipal(session, Scheme.Name), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Options.Channel == SessionChannel.Portal)
        {
            Response.Redirect($"{LoginPath}?returnUrl={Uri.EscapeDataString(PortalCookie.SafeReturnUrl(Request.Path + Request.QueryString))}");
            return;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Error.Unauthorized("AUTH.REQUIRED", "Inicie sesión (POST /admin/auth/login y /admin/auth/totp) y envíe el token como Bearer.")
            .ToProblem().ExecuteAsync(Context);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        if (Options.Channel == SessionChannel.Portal)
        {
            Response.Redirect(DeniedPath);
            return;
        }

        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Error.Forbidden("AUTH.PERMISSION_DENIED", "Su rol no tiene permiso para esta operación (o debe cambiar su contraseña).")
            .ToProblem().ExecuteAsync(Context);
    }

    private string? BearerToken()
    {
        var header = Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }
}

internal static class PortalAuthorization
{
    /// <summary>Autenticado = sesión con el segundo factor completo.</summary>
    public static AuthorizationPolicy ActiveSession { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(PortalClaims.Stage, PortalClaims.ActiveStage)
        .Build();

    public static IServiceCollection AddPortalAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(PortalSchemes.Selector)
            .AddPolicyScheme(PortalSchemes.Selector, "Portal", o => o.ForwardDefaultSelector = context =>
                context.Request.Path.StartsWithSegments("/admin") ? PortalSchemes.Bearer : PortalSchemes.Cookie)
            .AddScheme<PortalAuthenticationOptions, PortalAuthenticationHandler>(PortalSchemes.Cookie, o => o.Channel = SessionChannel.Portal)
            .AddScheme<PortalAuthenticationOptions, PortalAuthenticationHandler>(PortalSchemes.Bearer, o => o.Channel = SessionChannel.Api);

        var authorization = services.AddAuthorizationBuilder().SetDefaultPolicy(ActiveSession);
        foreach (var permission in CloudPermissions.All)
        {
            authorization.AddPolicy(permission, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(PortalClaims.Stage, PortalClaims.ActiveStage)
                .RequireClaim(PortalClaims.Permission, permission));
        }

        return services;
    }
}

/// <summary>Pasa la identidad autenticada de la petición al <see cref="PortalUserContext"/> que usan los casos de uso y la auditoría.</summary>
internal sealed class PortalUserContextMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, PortalUserContext user)
    {
        user.Set(context.User);
        return next(context);
    }
}
