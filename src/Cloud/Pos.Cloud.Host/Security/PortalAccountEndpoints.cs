using Microsoft.AspNetCore.Mvc;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.PortalIdentity.Application;

namespace Pos.Cloud.Host.Security;

internal static class PortalAccountEndpoints
{
    /// <summary>
    /// Salida del portal: formulario POST con token antifalsificación (se valida automáticamente al leer el formulario). Revoca la
    /// sesión en la BD y borra la cookie.
    /// </summary>
    public static IEndpointRouteBuilder MapPortalAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/cuenta/salir", async ([FromForm] string? returnUrl, HttpContext http, IDispatcher dispatcher, CancellationToken ct) =>
            {
                await dispatcher.Send(new PortalLogoutCommand(), ct);
                PortalCookie.Delete(http.Response);
                return TypedResults.LocalRedirect(http.Request.PathBase + PortalAuthenticationHandler.LoginPath);
            })
            .AllowAnonymous();
        return endpoints;
    }
}
