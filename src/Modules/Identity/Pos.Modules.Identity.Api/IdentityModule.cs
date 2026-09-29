using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Identity.Application;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Identity.Infrastructure;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Identity.Api;

/// <summary>Módulo Identity: autenticación, usuarios, roles, excepciones, sesiones, empleados y autorización de supervisor.</summary>
public sealed class IdentityModule : IModule
{
    public string Name => "identity";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(IIdentityStore).Assembly);
        IdentityInfrastructureRegistration.Register(services);
        services.AddSingleton<IPermissionCatalogProvider, IdentityPermissionCatalog>();
        services.AddSingleton<ISettingDefinitionProvider, SecuritySettingsProvider>();
        services.AddScoped<IIdentityProvisioning, IdentityProvisioning>();
        services.AddScoped<AuthServices>();
        services.AddScoped<TerminalResolver>();
        services.AddScoped<CredentialPolicy>();
        services.AddScoped<PrivilegeGuard>();
        services.AddScoped<UserMutations>();
        services.AddScoped<RoleRules>();
        services.AddScoped<SystemRoleSynchronizer>();
        services.AddSingleton<IDatabaseReadyHook, SystemRolesHook>();
        services.AddSingleton<IOwnerSetupStatus, OwnerSetupStatus>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        MapAuth(api.MapGroup("/auth").WithTags("Autenticación"));
        MapIdentity(api.MapGroup("/identity").WithTags("Identidad"));

        api.MapPost("/setup/owner", async (OwnerInput owner, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateOwnerCommand(owner), ct)).ToHttpResult())
            .WithTags("Configuración inicial")
            .AllowAnonymousByDesign("Solo desde el propio servidor y solo mientras no exista un Propietario activo.")
            .WithSummary("Crea el Propietario en una instalación configurada sin él");
    }

    private static void MapAuth(RouteGroupBuilder group)
    {
        group.MapPost("/login", async (LoginCommand command, IDispatcher d, CancellationToken ct) => (await d.Send(command, ct)).ToAuthResult())
            .AllowAnonymousByDesign("Entrada al backoffice: desde el servidor o un equipo emparejado.")
            .RequireRateLimiting(AuthRateLimits.Login)
            .WithSummary("Usuario + contraseña → token de sesión opaco");

        group.MapPost("/pos-login", async (PosLoginCommand command, IDispatcher d, CancellationToken ct) => (await d.Send(command, ct)).ToAuthResult())
            .AllowAnonymousByDesign("Entrada en caja: solo desde una caja emparejada o el equipo Caja Única.")
            .RequireRateLimiting(AuthRateLimits.Login)
            .WithSummary("Código de cajero + PIN → sesión de caja");

        group.MapPost("/logout", async (IDispatcher d, CancellationToken ct) => (await d.Send(new LogoutCommand(), ct)).ToHttpResult())
            .RequireAuthenticatedUser()
            .AllowWhilePasswordChangeRequired();

        group.MapGet("/me", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetMeQuery(), ct)).ToHttpResult())
            .RequireAuthenticatedUser()
            .AllowWhilePasswordChangeRequired()
            .WithSummary("Perfil y permisos efectivos del usuario de la sesión");

        group.MapPost("/change-password", async (ChangePasswordCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToHttpResult())
            .RequireAuthenticatedUser()
            .AllowWhilePasswordChangeRequired();

        group.MapPost("/change-pin", async (ChangePinCommand command, IDispatcher d, CancellationToken ct) => (await d.Send(command, ct)).ToHttpResult())
            .RequireAuthenticatedUser();

        group.MapPost("/authorizations", async (CreateAuthorizationCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToAuthResult(StatusCodes.Status201Created))
            .RequireAuthenticatedUser()
            .RequireRateLimiting(AuthRateLimits.Login)
            .WithSummary("Autorización de supervisor de un solo uso (código + PIN del supervisor)");
    }

    private static void MapIdentity(RouteGroupBuilder group)
    {
        group.MapGet("/permissions", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListPermissionsQuery(), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.PermissionView)
            .WithSummary("Catálogo de permisos");

        group.MapGet("/roles", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListRolesQuery(), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.PermissionView)
            .WithSummary("Roles de la empresa con sus permisos");

        group.MapPost("/roles", async (CreateRoleCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(r => $"/api/v1/identity/roles/{r.Id}"))
            .RequirePermission(IdentityPermissions.RoleManage);

        group.MapPost("/roles/{roleId:guid}/clone", async (Guid roleId, CloneRoleRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CloneRoleCommand(roleId, r.Code, r.Name), ct)).ToCreatedResult(x => $"/api/v1/identity/roles/{x.Id}"))
            .RequirePermission(IdentityPermissions.RoleManage);

        group.MapPut("/roles/{roleId:guid}", async (Guid roleId, UpdateRoleRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateRoleCommand(roleId, r.Name, r.Description, r.Permissions), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.RoleManage);

        group.MapDelete("/roles/{roleId:guid}", async (Guid roleId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeleteRoleCommand(roleId), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.RoleManage);

        group.MapGet("/users", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListUsersQuery(), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserView);

        group.MapGet("/users/{userId:guid}", async (Guid userId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetUserQuery(userId), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserView);

        group.MapPost("/users", async (CreateUserCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(u => $"/api/v1/identity/users/{u.Id}"))
            .RequirePermission(IdentityPermissions.UserManage);

        group.MapPut("/users/{userId:guid}", async (Guid userId, UpdateUserRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateUserCommand(userId, r.DisplayName, r.Email, r.EmployeeId), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage);

        group.MapPost("/users/{userId:guid}/activate", async (Guid userId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetUserActiveCommand(userId, true), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage);

        group.MapPost("/users/{userId:guid}/deactivate", async (Guid userId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetUserActiveCommand(userId, false), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage);

        group.MapPost("/users/{userId:guid}/unlock", async (Guid userId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UnlockUserCommand(userId), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage);

        group.MapPost("/users/{userId:guid}/reset-password", async (Guid userId, ResetPasswordRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ResetPasswordCommand(userId, r.TemporaryPassword), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage);

        group.MapPost("/users/{userId:guid}/reset-pin", async (Guid userId, ResetPinRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ResetPinCommand(userId, r.PosCode, r.Pin), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage);

        group.MapPut("/users/{userId:guid}/roles", async (Guid userId, IReadOnlyList<RoleAssignmentInput> roles, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetUserRolesCommand(userId, roles), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.RoleManage);

        group.MapPut("/users/{userId:guid}/overrides", async (Guid userId, IReadOnlyList<OverrideInput> overrides, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetUserOverridesCommand(userId, overrides), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.RoleManage);

        group.MapGet("/sessions", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListSessionsQuery(), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.SessionRevoke);

        group.MapPost("/sessions/{sessionId:guid}/revoke", async (Guid sessionId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RevokeSessionCommand(sessionId), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.SessionRevoke, allowSupervisor: true);

        group.MapGet("/employees", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListEmployeesQuery(), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserView);

        group.MapPost("/employees", async (EmployeeInput employee, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateEmployeeCommand(employee), ct)).ToCreatedResult(e => $"/api/v1/identity/employees/{e.Id}"))
            .RequirePermission(IdentityPermissions.UserManage);

        group.MapPut("/employees/{employeeId:guid}", async (Guid employeeId, EmployeeInput employee, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateEmployeeCommand(employeeId, employee), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage);
    }
}

public sealed record CloneRoleRequest(string Code, string Name);

public sealed record UpdateRoleRequest(string Name, string? Description, IReadOnlyList<string> Permissions);

public sealed record UpdateUserRequest(string DisplayName, string? Email, Guid? EmployeeId);

public sealed record ResetPasswordRequest(string TemporaryPassword);

public sealed record ResetPinRequest(string PosCode, string Pin);

/// <summary>Nombres de las políticas de límite de peticiones (las define el host).</summary>
public static class AuthRateLimits
{
    public const string Login = "auth-login";
    public const string Pairing = "device-pairing";
}

internal static class AuthOutcomeHttp
{
    /// <summary>Un intento fallido se confirma en la BD (contador, bloqueo) y responde con su error.</summary>
    public static IResult ToAuthResult<T>(this Result<AuthOutcome<T>> result, int successStatus = StatusCodes.Status200OK)
        where T : class
    {
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        var outcome = result.Value;
        return outcome.Error is { } error
            ? error.ToProblem()
            : successStatus == StatusCodes.Status201Created
                ? TypedResults.Created((string?)null, outcome.Value)
                : TypedResults.Ok(outcome.Value);
    }
}

/// <summary>Al arrancar con la BD lista: los roles de sistema reciben los permisos nuevos de esta versión.</summary>
internal sealed class SystemRolesHook : IDatabaseReadyHook
{
    public async Task RunAsync(IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        var context = scopedServices.GetRequiredService<PosDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await scopedServices.GetRequiredService<SystemRoleSynchronizer>().SynchronizeAsync(cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

internal sealed class OwnerSetupStatus(IIdentityState state) : IOwnerSetupStatus
{
    public Task<bool> IsOwnerPendingAsync(CancellationToken cancellationToken) => state.IsOwnerPendingAsync(cancellationToken);
}
