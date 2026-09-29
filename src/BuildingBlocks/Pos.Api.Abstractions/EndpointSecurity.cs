using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Abstractions.Security;
using Pos.SharedKernel.Results;

namespace Pos.Api.Abstractions;

/// <summary>Metadato: el endpoint exige este permiso.</summary>
public sealed record PermissionRequirement(string PermissionCode);

/// <summary>Metadato: el endpoint no exige un permiso concreto, solo un usuario autenticado (desde la Fase 3).</summary>
public sealed record AuthenticatedOnly;

/// <summary>Metadato: el endpoint es anónimo por diseño (p. ej. el asistente inicial, que se bloquea al completarse).</summary>
public sealed record AnonymousByDesign(string Reason);

/// <summary>
/// Declaración de seguridad obligatoria para todo endpoint de negocio: una prueba de arquitectura falla si un endpoint
/// de <c>/api/v1</c> no declara ninguna de estas opciones. En la Fase 2 la verificación es permisiva (todavía no hay
/// login); en la Fase 3 se activa sin tocar los endpoints.
/// </summary>
public static class EndpointSecurity
{
    public const string ForbiddenCode = "SECURITY.PERMISSION_DENIED";

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permissionCode)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionCode);
        builder.WithMetadata(new PermissionRequirement(permissionCode));
        builder.AddEndpointFilter(async (context, next) =>
        {
            var checker = context.HttpContext.RequestServices.GetRequiredService<IPermissionChecker>();
            if (!await checker.HasPermissionAsync(permissionCode, cancellationToken: context.HttpContext.RequestAborted))
            {
                return Error.Forbidden(ForbiddenCode, $"No tiene el permiso '{permissionCode}'.").ToProblem();
            }

            return await next(context);
        });
        return builder;
    }

    public static TBuilder RequireAuthenticatedUser<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new AuthenticatedOnly());
        return builder;
    }

    public static TBuilder AllowAnonymousByDesign<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        builder.WithMetadata(new AnonymousByDesign(reason));
        return builder;
    }
}
