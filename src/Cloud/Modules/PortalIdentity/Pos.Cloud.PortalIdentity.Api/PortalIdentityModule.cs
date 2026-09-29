using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.Cloud.PortalIdentity.Application;
using Pos.Cloud.PortalIdentity.Domain;
using Pos.Cloud.PortalIdentity.Infrastructure;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.PortalIdentity.Api;

/// <summary>
/// Usuarios del portal: acceso de la API interna (<c>/admin/auth</c>, token Bearer con el mismo TOTP obligatorio que el navegador),
/// gestión de usuarios y sesiones (superadministrador) y consulta de la auditoría.
/// </summary>
public sealed class PortalIdentityModule : IModule
{
    /// <summary>Política de límite de intentos de acceso por IP (la registra el host).</summary>
    public const string LoginRateLimit = "portal-login";

    public string Name => "portal";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(PortalIdentityOptions.SectionName).Get<PortalIdentityOptions>() ?? new PortalIdentityOptions();
        PortalIdentityInfrastructureRegistration.Register(services, options);
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/admin/auth").WithTags("Acceso a la API interna");
        auth.MapPost("/login", async (LoginRequest r, HttpContext http, IDispatcher d, CancellationToken ct) =>
                ToHttp(await d.Send(new PortalLoginCommand(r.Email, r.Password, SessionChannel.Api, http.Connection.RemoteIpAddress, UserAgent(http)), ct)))
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimit)
            .WithSummary("Paso 1: correo y contraseña → token pendiente del segundo factor");
        auth.MapPost("/totp/enrollment", async (TokenRequest r, IDispatcher d, CancellationToken ct) =>
                ToHttp(await d.Send(new BeginTotpEnrollmentCommand(r.Token), ct)))
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimit)
            .WithSummary("Primer ingreso: secreto TOTP y enlace otpauth:// para el autenticador");
        auth.MapPost("/totp", async (TotpRequest r, IDispatcher d, CancellationToken ct) =>
                ToHttp(await d.Send(new CompleteSecondFactorCommand(r.Token, r.Code), ct)))
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimit)
            .WithSummary("Paso 2: código TOTP → token de sesión (Bearer)");
        auth.MapPost("/logout", async (IDispatcher d, CancellationToken ct) => (await d.Send(new PortalLogoutCommand(), ct)).ToHttpResult())
            .RequireAuthorization();
        auth.MapGet("/me", async (IDispatcher d, CancellationToken ct) =>
            {
                var result = await d.Send(new GetMyAccountQuery(), ct);
                return result.IsSuccess ? TypedResults.Ok(new { result.Value.Me, result.Value.Sessions }) : result.Error.ToProblem();
            })
            .RequireAuthorization();
        auth.MapPost("/password", async (ChangePasswordRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeOwnPasswordCommand(r.CurrentPassword, r.NewPassword), ct)).ToHttpResult())
            .RequireAuthorization();

        var users = api.MapGroup("/admin/users").WithTags("Usuarios del portal").RequireAuthorization(CloudPermissions.PortalUserManage);
        users.MapGet("/", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListPortalUsersQuery(), ct)).ToHttpResult());
        users.MapPost("/", async (CreatePortalUserCommand command, IDispatcher d, CancellationToken ct) =>
            (await d.Send(command, ct)).ToCreatedResult(u => $"/admin/users/{u.UserId}"));
        users.MapPut("/{id:guid}", async (Guid id, UpdateUserRequest r, IDispatcher d, CancellationToken ct) =>
            (await d.Send(new UpdatePortalUserCommand(id, r.DisplayName, r.Role, r.ResellerAccountId, r.IsActive), ct)).ToHttpResult());
        users.MapPost("/{id:guid}/reset-password", async (Guid id, IDispatcher d, CancellationToken ct) =>
            (await d.Send(new ResetPortalUserPasswordCommand(id), ct)).ToHttpResult());
        users.MapPost("/{id:guid}/reset-totp", async (Guid id, ReasonBody r, IDispatcher d, CancellationToken ct) =>
            (await d.Send(new ResetPortalUserTotpCommand(id, r.Reason), ct)).ToHttpResult());
        users.MapPost("/{id:guid}/unlock", async (Guid id, IDispatcher d, CancellationToken ct) =>
            (await d.Send(new UnlockPortalUserCommand(id), ct)).ToHttpResult());

        var sessions = api.MapGroup("/admin/sessions").WithTags("Usuarios del portal").RequireAuthorization(CloudPermissions.PortalUserManage);
        sessions.MapGet("/", async (Guid? userId, IDispatcher d, CancellationToken ct) => (await d.Send(new ListPortalSessionsQuery(userId), ct)).ToHttpResult());
        sessions.MapPost("/{id:guid}/revoke", async (Guid id, ReasonBody r, IDispatcher d, CancellationToken ct) =>
            (await d.Send(new RevokePortalSessionCommand(id, r.Reason), ct)).ToHttpResult());

        var audit = api.MapGroup("/admin/audit").WithTags("Auditoría").RequireAuthorization(CloudPermissions.AuditView);
        audit.MapGet("/", async (DateTimeOffset? from, DateTimeOffset? to, string? module, string? action, Guid? entityId, string? text, int? limit, ICloudAuditLog log, CancellationToken ct) =>
            TypedResults.Ok(await log.SearchAsync(new CloudAuditFilter(from, to, module, action, entityId, text, limit ?? 200), ct)));
        audit.MapGet("/verify", async (ICloudAuditLog log, CancellationToken ct) => TypedResults.Ok(await log.VerifyAsync(ct)))
            .WithSummary("Verifica filas, sellos y cadena de la auditoría");
    }

    private static string? UserAgent(HttpContext http) => http.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null;

    private static IResult ToHttp<T>(Result<Outcome<T>> result)
        where T : class =>
        result.IsFailure ? result.Error.ToProblem()
        : result.Value.Error is { } error ? error.ToProblem()
        : TypedResults.Ok(result.Value.Value);
}

public sealed record LoginRequest(string Email, string Password);

public sealed record TokenRequest(string Token);

public sealed record TotpRequest(string Token, string Code);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record UpdateUserRequest(string DisplayName, string Role, Guid? ResellerAccountId, bool IsActive);

public sealed record ReasonBody(string Reason);
