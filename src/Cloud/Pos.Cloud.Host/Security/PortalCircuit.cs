using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Pos.Application.Abstractions.Security;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.PortalIdentity.Application;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Host.Security;

/// <summary>
/// Datos de la petición para la auditoría (correlación e IP). En HTTP los fija el middleware; en las pantallas interactivas,
/// el circuito de Blazor al conectarse (la IP es la de la conexión del navegador).
/// </summary>
internal sealed class CloudRequestContext : IRequestContext
{
    public string? CorrelationId { get; private set; }

    public IPAddress? IpAddress { get; private set; }

    public Guid? DeviceId => null;

    public void Set(HttpContext? http)
    {
        if (http is null)
        {
            return;
        }

        CorrelationId = http.TraceIdentifier;
        IpAddress = http.Connection.RemoteIpAddress;
    }

    public void CopyFrom(CloudRequestContext other, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(other);
        IpAddress = other.IpAddress;
        CorrelationId = correlationId;
    }
}

/// <summary>
/// Ejecuta casos de uso desde las pantallas interactivas. El circuito de Blazor vive mientras la pestaña esté abierta: cada
/// acción usa un ámbito NUEVO (su propio contexto EF, sin entidades viejas en memoria) con el usuario y la IP del circuito.
/// </summary>
internal sealed class PortalOperations(IServiceScopeFactory scopes, PortalUserContext user, CloudRequestContext request)
{
    public async Task<TResponse> SendAsync<TResponse>(Pos.Application.Abstractions.Messaging.IRequest<TResponse> command, CancellationToken cancellationToken = default)
        where TResponse : Pos.SharedKernel.Results.Result
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<PortalUserContext>().CopyFrom(user);
        scope.ServiceProvider.GetRequiredService<CloudRequestContext>().CopyFrom(request, Guid.CreateVersion7().ToString("N"));
        return await scope.ServiceProvider.GetRequiredService<Pos.Application.Abstractions.Messaging.IDispatcher>().Send(command, cancellationToken);
    }

    public async Task<T> QueryAsync<T>(Func<IServiceProvider, Task<T>> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<PortalUserContext>().CopyFrom(user);
        return await query(scope.ServiceProvider);
    }
}

internal sealed class RequestContextMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, CloudRequestContext request)
    {
        request.Set(context);
        return next(context);
    }
}

/// <summary>
/// En un circuito de Blazor no hay petición HTTP por cada acción: este manejador pasa el usuario autenticado del circuito (y sus
/// cambios) al <see cref="PortalUserContext"/> del ámbito del circuito, que usan los casos de uso y la auditoría.
/// </summary>
internal sealed class PortalCircuitHandler(
    AuthenticationStateProvider authentication, PortalUserContext user, CloudRequestContext request, IHttpContextAccessor http)
    : CircuitHandler, IDisposable
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        authentication.AuthenticationStateChanged += OnAuthenticationChanged;
        return base.OnCircuitOpenedAsync(circuit, cancellationToken);
    }

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        request.Set(http.HttpContext);
        user.Set((await authentication.GetAuthenticationStateAsync()).User);
    }

    public void Dispose() => authentication.AuthenticationStateChanged -= OnAuthenticationChanged;

    private void OnAuthenticationChanged(Task<AuthenticationState> state) => _ = UpdateAsync(state);

    private async Task UpdateAsync(Task<AuthenticationState> state)
    {
        try
        {
            user.Set((await state).User);
        }
        catch (InvalidOperationException)
        {
            user.Set(null);
        }
    }
}

/// <summary>
/// Revalida la sesión del circuito cada minuto: si fue revocada, venció o cambió la seguridad del usuario, la pantalla deja de
/// estar autenticada sin esperar a que recargue (sesiones revocables, §6).
/// </summary>
internal sealed class PortalRevalidatingAuthenticationStateProvider(ILoggerFactory loggerFactory, IServiceScopeFactory scopes)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);
        if (!Guid.TryParse(authenticationState.User.FindFirst(PortalClaims.SessionId)?.Value, out var sessionId))
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPortalIdentityStore>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var session = await store.GetSessionAsync(sessionId, cancellationToken);
        var user = session is null ? null : await store.GetUserAsync(session.UserId, cancellationToken);
        return session is not null && user is not null && session.IsUsable(user, clock.UtcNow);
    }
}

internal static class ClaimsPrincipalExtensions
{
    public static bool HasPortalPermission(this ClaimsPrincipal user, string permission) =>
        user.HasClaim(PortalClaims.Permission, permission);

    public static bool MustChangePassword(this ClaimsPrincipal user) =>
        string.Equals(user.FindFirst(PortalClaims.MustChangePassword)?.Value, bool.TrueString, StringComparison.OrdinalIgnoreCase);
}
