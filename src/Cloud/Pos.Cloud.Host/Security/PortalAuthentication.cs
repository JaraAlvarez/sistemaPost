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
/// Cookie de sesión del portal (§6): <c>Secure</c>, <c>HttpOnly</c>, <c>SameSite=Strict</c>, sin dominio. Contiene solo el token
/// opaco; la sesión vive en la BD y se puede revocar.
/// <list type="bullet">
/// <item>En la raíz de un (sub)dominio propio: <c>__Host-pos-portal</c> con ruta <c>/</c> (el prefijo <c>__Host-</c> exige ruta /).</item>
/// <item>Bajo una ruta de un dominio compartido (<c>Cloud:PathBase</c>, p. ej. <c>/businesspost</c>): <c>__Secure-pos-portal</c> con
/// ruta = la base. No se usa <c>__Host-</c> porque obligaría a la ruta /, y el navegador enviaría la sesión del portal a las demás
/// aplicaciones del mismo dominio (p. ej. la tienda en Express/React). <c>__Secure-</c> conserva la garantía de "solo HTTPS".</item>
/// </list>
/// </summary>
internal static class PortalCookie
{
    public const string Name = "__Host-pos-portal";

    /// <summary>Nombre de la cookie bajo una ruta base (ver el resumen de la clase).</summary>
    public const string PathBaseName = "__Secure-pos-portal";

    public static string NameFor(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.PathBase.HasValue ? PathBaseName : Name;
    }

    public static string? Read(HttpRequest request) => request.Cookies[NameFor(request)];

    public static void Write(HttpResponse response, string token, DateTimeOffset expires)
    {
        ArgumentNullException.ThrowIfNull(response);
        var request = response.HttpContext.Request;
        response.Cookies.Append(NameFor(request), token, Options(request, expires));
    }

    public static void Delete(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var request = response.HttpContext.Request;
        response.Cookies.Delete(NameFor(request), Options(request, null));
    }

    private static CookieOptions Options(HttpRequest request, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = request.PathBase.HasValue ? request.PathBase.Value : "/",
        Expires = expires,
        IsEssential = true,
    };

    /// <summary>Solo rutas locales del portal (evita redirecciones abiertas).</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 and < 500 } url && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal) && !url.StartsWith("/cuenta", StringComparison.OrdinalIgnoreCase)
            ? url
            : "/";
}

/// <summary>Enlaces del portal relativos a <c>&lt;base href&gt;</c> (que incluye <c>Cloud:PathBase</c>).</summary>
internal static class PortalLinks
{
    /// <summary><c>/mi-cuenta</c> → <c>mi-cuenta</c>; <c>/</c> → <c>./</c> (la raíz del portal).</summary>
    public static string Local(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var relative = path.TrimStart('/');
        return relative.Length > 0 ? relative : "./";
    }
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
        var token = Options.Channel == SessionChannel.Portal ? PortalCookie.Read(Request) : BearerToken();
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
            Response.Redirect($"{Request.PathBase}{LoginPath}?returnUrl={Uri.EscapeDataString(PortalCookie.SafeReturnUrl(Request.Path + Request.QueryString))}");
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
            Response.Redirect(Request.PathBase + DeniedPath);
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

/// <summary>
/// Mientras la contraseña sea temporal, toda página del portal pedida por el navegador redirige a <c>/mi-cuenta</c> (cambio
/// obligatorio). La API (/admin, /v1), el acceso (/cuenta), la salud y los recursos estáticos no se tocan. La navegación
/// interactiva la cubre <c>Routes.razor</c> (sin permisos, toda página protegida cae en la misma redirección).
/// </summary>
internal sealed class MustChangePasswordMiddleware(RequestDelegate next)
{
    public const string MyAccountPath = "/mi-cuenta";

    private static readonly string[] ExcludedPrefixes =
        [MyAccountPath, "/cuenta", "/sin-acceso", "/admin", "/v1", "/health", "/_framework", "/_content", "/_blazor", "/openapi"];

    public Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) && context.User.MustChangePassword()
            && context.User.HasClaim(PortalClaims.Stage, PortalClaims.ActiveStage) && IsPortalPage(context.Request.Path))
        {
            context.Response.Redirect(context.Request.PathBase + MyAccountPath);
            return Task.CompletedTask;
        }

        return next(context);
    }

    private static bool IsPortalPage(PathString path) =>
        !ExcludedPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)) && !Path.HasExtension(path.Value);
}
