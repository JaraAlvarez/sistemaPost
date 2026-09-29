using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Abstractions.Security;
using Pos.SharedKernel.Results;

namespace Pos.Api.Abstractions;

/// <summary>Metadato: el endpoint exige este permiso (y opcionalmente admite autorización de supervisor).</summary>
public sealed record PermissionRequirement(string PermissionCode, bool AllowSupervisor = false);

/// <summary>Metadato: el endpoint no exige un permiso concreto, solo un usuario autenticado.</summary>
public sealed record AuthenticatedOnly;

/// <summary>Metadato: el endpoint es anónimo por diseño (p. ej. el asistente inicial o la entrada al sistema).</summary>
public sealed record AnonymousByDesign(string Reason);

/// <summary>Metadato: el endpoint funciona aunque el usuario deba cambiar su contraseña.</summary>
public sealed record AllowedWhilePasswordChangeRequired;

/// <summary>¿Falta crear el Propietario? Lo implementa el módulo Identity; mejora el mensaje de los 401.</summary>
public interface IOwnerSetupStatus
{
    Task<bool> IsOwnerPendingAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Declaración de seguridad obligatoria para todo endpoint de <c>/api/v1</c> (una prueba falla si falta).
/// Desde la Fase 3 la verificación es real: sin sesión → 401; sin permiso → 403 (o autorización de supervisor si el
/// endpoint la admite); con cambio de contraseña pendiente → 403 salvo los endpoints marcados.
/// </summary>
public static class EndpointSecurity
{
    public const string AuthenticationRequiredCode = "AUTH.REQUIRED";
    public const string ForbiddenCode = "AUTH.PERMISSION_DENIED";
    public const string AuthorizationRequiredCode = "AUTH.AUTHORIZATION_REQUIRED";
    public const string PasswordChangeRequiredCode = "AUTH.PASSWORD_CHANGE_REQUIRED";
    public const string OwnerRequiredCode = "SETUP.OWNER_REQUIRED";
    public const string GrantHeader = "X-Authorization-Grant";

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permissionCode, bool allowSupervisor = false)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionCode);
        builder.WithMetadata(new PermissionRequirement(permissionCode, allowSupervisor));
        return builder;
    }

    public static TBuilder RequireAuthenticatedUser<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new AuthenticatedOnly());
        return builder;
    }

    /// <summary>
    /// Aplica la seguridad declarada por el endpoint ANTES de leer el cuerpo de la petición (la invoca el middleware de
    /// autenticación del host). Devuelve la respuesta de rechazo o <c>null</c> si la petición puede continuar.
    /// </summary>
    public static async Task<IResult?> AuthorizeAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var metadata = http.GetEndpoint()?.Metadata;
        var requirement = metadata?.GetMetadata<PermissionRequirement>();
        if (requirement is null && metadata?.GetMetadata<AuthenticatedOnly>() is null)
        {
            return null;
        }

        var denied = await CheckSessionAsync(http);
        if (denied is not null || requirement is null)
        {
            return denied;
        }

        var services = http.RequestServices;
        if (await services.GetRequiredService<IPermissionChecker>().HasPermissionAsync(requirement.PermissionCode, cancellationToken: http.RequestAborted))
        {
            return null;
        }

        // Autorización de supervisor de un solo uso, ligada a este permiso, a esta acción y a este objetivo.
        var current = services.GetRequiredService<ICurrentUser>();
        if (requirement.AllowSupervisor && Guid.TryParse(http.Request.Headers[GrantHeader].ToString(), out var grantId))
        {
            var consumed = await services.GetRequiredService<ISupervisorAuthorization>()
                .TryConsumeAsync(grantId, requirement.PermissionCode, ActionOf(http), TargetOf(http), http.RequestAborted);
            if (consumed is not null)
            {
                services.GetRequiredService<IAuthorizationScope>().Use(consumed);
                return null;
            }
        }

        var supervisorPossible = requirement.AllowSupervisor && current.IsTerminalSession;
        return TypedResults.Problem(
            title: "Acceso denegado",
            detail: supervisorPossible
                ? $"Esta acción requiere la autorización de un supervisor ({requirement.PermissionCode})."
                : $"No tiene el permiso '{requirement.PermissionCode}'.",
            statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?>
            {
                [ResultHttpExtensions.ErrorCodeExtension] = supervisorPossible ? AuthorizationRequiredCode : ForbiddenCode,
                ["permission"] = requirement.PermissionCode,
                ["action"] = supervisorPossible ? ActionOf(http) : null,
                ["targetId"] = supervisorPossible ? TargetOf(http) : null,
            });
    }

    public static TBuilder AllowAnonymousByDesign<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        builder.WithMetadata(new AnonymousByDesign(reason));
        return builder;
    }

    public static TBuilder AllowWhilePasswordChangeRequired<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new AllowedWhilePasswordChangeRequired());
        return builder;
    }

    /// <summary>Acción que identifica una autorización de supervisor: método + plantilla de la ruta.</summary>
    public static string ActionOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var route = (http.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value ?? string.Empty;
        var action = $"{http.Request.Method} {route}";
        return action.Length > 100 ? action[..100] : action;
    }

    /// <summary>Objetivo de la acción: el primer identificador de la ruta, si lo hay.</summary>
    public static Guid? TargetOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Request.RouteValues.Values.Select(v => Guid.TryParse(v?.ToString(), out var id) ? id : (Guid?)null).FirstOrDefault(v => v is not null);
    }

    private static async Task<IResult?> CheckSessionAsync(HttpContext http)
    {
        var current = http.RequestServices.GetRequiredService<ICurrentUser>();
        if (!current.IsAuthenticated)
        {
            var ownerPending = http.RequestServices.GetService<IOwnerSetupStatus>() is { } status
                && await status.IsOwnerPendingAsync(http.RequestAborted);
            return ownerPending
                ? Error.Forbidden(OwnerRequiredCode, "Cree primero el usuario Propietario desde el propio servidor (POST /api/v1/setup/owner).").ToProblem()
                : Error.Unauthorized(AuthenticationRequiredCode, "Inicie sesión para continuar.").ToProblem();
        }

        if (current.MustChangePassword && http.GetEndpoint()?.Metadata.GetMetadata<AllowedWhilePasswordChangeRequired>() is null)
        {
            return Error.Forbidden(PasswordChangeRequiredCode, "Debe cambiar su contraseña antes de continuar.").ToProblem();
        }

        return null;
    }
}
