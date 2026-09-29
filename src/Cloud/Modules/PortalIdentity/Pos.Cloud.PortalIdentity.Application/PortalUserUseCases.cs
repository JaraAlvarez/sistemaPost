using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Cloud.Abstractions;
using Pos.Cloud.PortalIdentity.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.PortalIdentity.Application;

internal static class PortalUserUseCaseMapping
{
    public static PortalUserDto ToDto(PortalUser u) => new(
        u.Id, u.Email, u.DisplayName, u.Kind == PortalUserKind.Human ? "HUMAN" : "SYSTEM", PortalRoleCodes.ToCode(u.Role), u.ResellerAccountId,
        u.Status == PortalUserStatus.Active ? "ACTIVE" : "DISABLED", u.TotpEnabled, u.MustChangePassword, u.LockedUntil, u.LastLoginAt);

    public static PortalSessionInfoDto ToDto(PortalSession s) => new(
        s.Id, s.UserId, s.Stage switch { SessionStage.Active => "ACTIVE", SessionStage.PendingTotp => "PENDING_TOTP", _ => "PENDING_ENROLLMENT" },
        s.Channel == SessionChannel.Api ? "API" : "PORTAL", s.CreatedAt, s.ExpiresAt, s.LastSeenAt, s.RevokedAt, s.RevokedReason, s.IpAddress?.ToString(), s.UserAgent);

    public static async Task RevokeAllAsync(IPortalIdentityStore store, Guid userId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var session in await store.GetOpenSessionsAsync(userId, cancellationToken))
        {
            session.Revoke(reason, now);
        }
    }
}

[RequiresPermission(CloudPermissions.PortalUserManage)]
public sealed record ListPortalUsersQuery : IQuery<IReadOnlyList<PortalUserDto>>;

internal sealed class ListPortalUsersHandler(IPortalIdentityStore store) : IQueryHandler<ListPortalUsersQuery, IReadOnlyList<PortalUserDto>>
{
    public async Task<Result<IReadOnlyList<PortalUserDto>>> Handle(ListPortalUsersQuery request, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PortalUserDto>>([.. (await store.ListUsersAsync(cancellationToken))
            .Where(u => u.Kind == PortalUserKind.Human)
            .OrderBy(u => u.Email, StringComparer.Ordinal)
            .Select(PortalUserUseCaseMapping.ToDto)]);
}

/// <summary>Crea un usuario del equipo con una contraseña temporal (se muestra una vez); en su primer ingreso enrola el 2FA y la cambia.</summary>
[RequiresPermission(CloudPermissions.PortalUserManage)]
public sealed record CreatePortalUserCommand(string Email, string DisplayName, string Role, Guid? ResellerAccountId = null) : ICommand<TemporaryPasswordDto>;

internal sealed class CreatePortalUserHandler(IPortalIdentityStore store, PortalAuthServices auth, IAuditWriter audit)
    : ICommandHandler<CreatePortalUserCommand, TemporaryPasswordDto>
{
    public async Task<Result<TemporaryPasswordDto>> Handle(CreatePortalUserCommand request, CancellationToken cancellationToken)
    {
        if (!PortalRoleCodes.TryParse(request.Role, out var role))
        {
            return PortalIdentityErrors.InvalidUser;
        }

        if (await store.FindUserByEmailAsync(PortalUser.NormalizeEmail(request.Email), cancellationToken) is not null)
        {
            return PortalIdentityErrors.EmailDuplicated;
        }

        var password = TemporaryPasswords.Create();
        var created = PortalUser.Create(
            auth.Ids.NewId(), request.Email, request.DisplayName, role, request.ResellerAccountId,
            await auth.Hasher.HashAsync(password, SecretKind.Password, cancellationToken), auth.Clock.UtcNow);
        if (created.IsFailure)
        {
            return created.Error;
        }

        store.Add(created.Value);
        await audit.WriteAsync(
            new AuditEntry("portal", "PORTAL_USER_CREATED", nameof(PortalUser), created.Value.Id, created.Value.AuditLabel,
                $"Usuario del portal {created.Value.Email} creado con el rol {PortalRoleCodes.DisplayName(PortalRoleCodes.ToCode(role))}.", Severity: AuditSeverity.Warning),
            cancellationToken);
        return new TemporaryPasswordDto(created.Value.Id, created.Value.Email, password);
    }
}

[RequiresPermission(CloudPermissions.PortalUserManage)]
public sealed record UpdatePortalUserCommand(Guid UserId, string DisplayName, string Role, Guid? ResellerAccountId, bool IsActive) : ICommand;

internal sealed class UpdatePortalUserHandler(IPortalIdentityStore store, IPortalUserContext current, PortalAuthServices auth, IAuditWriter audit)
    : ICommandHandler<UpdatePortalUserCommand>
{
    public async Task<Result> Handle(UpdatePortalUserCommand request, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(request.UserId, cancellationToken);
        if (user is null)
        {
            return PortalIdentityErrors.UserNotFound;
        }

        if (!PortalRoleCodes.TryParse(request.Role, out var role))
        {
            return PortalIdentityErrors.InvalidUser;
        }

        if (user.Id == current.UserId && (role != user.Role || !request.IsActive))
        {
            return PortalIdentityErrors.CannotChangeSelf;
        }

        var losesSuperadmin = user.Role == PortalRole.Superadmin && user.Status == PortalUserStatus.Active && (role != PortalRole.Superadmin || !request.IsActive);
        if (losesSuperadmin && !await store.AnyActiveSuperadminAsync(user.Id, cancellationToken))
        {
            return PortalIdentityErrors.LastSuperadmin;
        }

        var version = user.SecurityVersion;
        var updated = user.Update(request.DisplayName, role, request.ResellerAccountId, request.IsActive);
        if (updated.IsFailure)
        {
            return updated;
        }

        if (user.SecurityVersion != version)
        {
            await PortalUserUseCaseMapping.RevokeAllAsync(store, user.Id, "Cambio de rol o estado del usuario", auth.Clock.UtcNow, cancellationToken);
            await audit.WriteAsync(
                new AuditEntry("portal", "PORTAL_USER_SECURITY_CHANGED", nameof(PortalUser), user.Id, user.AuditLabel,
                    $"{user.Email}: rol {PortalRoleCodes.DisplayName(PortalRoleCodes.ToCode(role))}, {(request.IsActive ? "activo" : "deshabilitado")}; se cerraron sus sesiones.",
                    Severity: AuditSeverity.Critical),
                cancellationToken);
        }

        return Result.Success();
    }
}

[RequiresPermission(CloudPermissions.PortalUserManage)]
public sealed record ResetPortalUserPasswordCommand(Guid UserId) : ICommand<TemporaryPasswordDto>;

internal sealed class ResetPortalUserPasswordHandler(IPortalIdentityStore store, PortalAuthServices auth, IAuditWriter audit)
    : ICommandHandler<ResetPortalUserPasswordCommand, TemporaryPasswordDto>
{
    public async Task<Result<TemporaryPasswordDto>> Handle(ResetPortalUserPasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(request.UserId, cancellationToken);
        if (user is not { Kind: PortalUserKind.Human })
        {
            return PortalIdentityErrors.UserNotFound;
        }

        var password = TemporaryPasswords.Create();
        var now = auth.Clock.UtcNow;
        user.SetPassword(await auth.Hasher.HashAsync(password, SecretKind.Password, cancellationToken), now, mustChange: true);
        await PortalUserUseCaseMapping.RevokeAllAsync(store, user.Id, "Contraseña restablecida", now, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("portal", "PORTAL_PASSWORD_RESET", nameof(PortalUser), user.Id, user.AuditLabel,
                $"Contraseña de {user.Email} restablecida (temporal); se cerraron sus sesiones.", Severity: AuditSeverity.Critical),
            cancellationToken);
        return new TemporaryPasswordDto(user.Id, user.Email, password);
    }
}

/// <summary>Recuperación manual del doble factor (teléfono perdido): el usuario vuelve a escanear un QR en su próximo ingreso.</summary>
[RequiresPermission(CloudPermissions.PortalUserManage)]
public sealed record ResetPortalUserTotpCommand(Guid UserId, string Reason) : ICommand;

internal sealed class ResetPortalUserTotpHandler(IPortalIdentityStore store, PortalAuthServices auth, IAuditWriter audit) : ICommandHandler<ResetPortalUserTotpCommand>
{
    public async Task<Result> Handle(ResetPortalUserTotpCommand request, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(request.UserId, cancellationToken);
        if (user is not { Kind: PortalUserKind.Human })
        {
            return PortalIdentityErrors.UserNotFound;
        }

        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length is < 5 or > 300)
        {
            return Error.Validation("PORTAL.REASON_REQUIRED", "Indique el motivo (de 5 a 300 caracteres).");
        }

        user.ResetTotp();
        await PortalUserUseCaseMapping.RevokeAllAsync(store, user.Id, "Doble factor restablecido", auth.Clock.UtcNow, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("portal", "PORTAL_TOTP_RESET", nameof(PortalUser), user.Id, user.AuditLabel,
                $"Doble factor de {user.Email} restablecido: {reason}.", Severity: AuditSeverity.Critical),
            cancellationToken);
        return Result.Success();
    }
}

[RequiresPermission(CloudPermissions.PortalUserManage)]
public sealed record UnlockPortalUserCommand(Guid UserId) : ICommand;

internal sealed class UnlockPortalUserHandler(IPortalIdentityStore store, IAuditWriter audit) : ICommandHandler<UnlockPortalUserCommand>
{
    public async Task<Result> Handle(UnlockPortalUserCommand request, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(request.UserId, cancellationToken);
        if (user is null)
        {
            return PortalIdentityErrors.UserNotFound;
        }

        user.Unlock();
        await audit.WriteAsync(
            new AuditEntry("portal", "PORTAL_USER_UNLOCKED", nameof(PortalUser), user.Id, user.AuditLabel, $"{user.Email} desbloqueado.", Severity: AuditSeverity.Warning),
            cancellationToken);
        return Result.Success();
    }
}

[RequiresPermission(CloudPermissions.PortalUserManage)]
public sealed record ListPortalSessionsQuery(Guid? UserId = null) : IQuery<IReadOnlyList<PortalSessionInfoDto>>;

internal sealed class ListPortalSessionsHandler(IPortalIdentityStore store) : IQueryHandler<ListPortalSessionsQuery, IReadOnlyList<PortalSessionInfoDto>>
{
    public async Task<Result<IReadOnlyList<PortalSessionInfoDto>>> Handle(ListPortalSessionsQuery request, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PortalSessionInfoDto>>([.. (await store.ListSessionsAsync(request.UserId, 200, cancellationToken)).Select(PortalUserUseCaseMapping.ToDto)]);
}

[RequiresPermission(CloudPermissions.PortalUserManage)]
public sealed record RevokePortalSessionCommand(Guid SessionId, string Reason) : ICommand;

internal sealed class RevokePortalSessionHandler(IPortalIdentityStore store, PortalAuthServices auth, IAuditWriter audit) : ICommandHandler<RevokePortalSessionCommand>
{
    public async Task<Result> Handle(RevokePortalSessionCommand request, CancellationToken cancellationToken)
    {
        var session = await store.GetSessionAsync(request.SessionId, cancellationToken);
        if (session is null)
        {
            return Error.NotFound("PORTAL.SESSION_NOT_FOUND", "La sesión no existe.");
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Revocada por un superadministrador" : request.Reason.Trim();
        if (session.Revoke(reason, auth.Clock.UtcNow) && await store.GetUserAsync(session.UserId, cancellationToken) is { } user)
        {
            await audit.WriteAsync(
                new AuditEntry("portal", "PORTAL_SESSION_REVOKED", nameof(PortalUser), user.Id, user.AuditLabel,
                    $"Sesión de {user.Email} revocada: {reason}.", Severity: AuditSeverity.Warning),
                cancellationToken);
        }

        return Result.Success();
    }
}

/// <summary>
/// Primer superadministrador (arranque del servidor): solo funciona mientras NO exista ningún superadministrador humano. Nunca hay
/// una contraseña por defecto: se genera una temporal que se muestra una vez en la consola del servidor.
/// </summary>
public sealed record BootstrapSuperadminCommand(string Email, string DisplayName) : ICommand<TemporaryPasswordDto>;

internal sealed class BootstrapSuperadminHandler(IPortalIdentityStore store, PortalAuthServices auth, IAuditWriter audit)
    : ICommandHandler<BootstrapSuperadminCommand, TemporaryPasswordDto>
{
    public async Task<Result<TemporaryPasswordDto>> Handle(BootstrapSuperadminCommand request, CancellationToken cancellationToken)
    {
        if ((await store.ListUsersAsync(cancellationToken)).Any(u => u.Kind == PortalUserKind.Human && u.Role == PortalRole.Superadmin))
        {
            return PortalIdentityErrors.BootstrapDone;
        }

        var password = TemporaryPasswords.Create();
        var created = PortalUser.Create(
            auth.Ids.NewId(), request.Email, request.DisplayName, PortalRole.Superadmin, null,
            await auth.Hasher.HashAsync(password, SecretKind.Password, cancellationToken), auth.Clock.UtcNow);
        if (created.IsFailure)
        {
            return created.Error;
        }

        store.Add(created.Value);
        await audit.WriteAsync(
            new AuditEntry("portal", "PORTAL_BOOTSTRAP", nameof(PortalUser), created.Value.Id, created.Value.AuditLabel,
                $"Primer superadministrador {created.Value.Email} creado desde la consola del servidor.", Severity: AuditSeverity.Critical),
            cancellationToken);
        return new TemporaryPasswordDto(created.Value.Id, created.Value.Email, password);
    }
}

/// <summary>
/// Recuperación de emergencia por consola del servidor (p. ej. el único superadministrador perdió el teléfono o la contraseña):
/// contraseña temporal nueva, doble factor restablecido, desbloqueo y cierre de sesiones. Solo la consola lo usa (no hay endpoint).
/// </summary>
public sealed record EmergencyRecoverUserCommand(string Email, string Reason) : ICommand<TemporaryPasswordDto>;

internal sealed class EmergencyRecoverUserHandler(IPortalIdentityStore store, PortalAuthServices auth, IAuditWriter audit)
    : ICommandHandler<EmergencyRecoverUserCommand, TemporaryPasswordDto>
{
    public async Task<Result<TemporaryPasswordDto>> Handle(EmergencyRecoverUserCommand request, CancellationToken cancellationToken)
    {
        var user = await store.FindUserByEmailAsync(PortalUser.NormalizeEmail(request.Email), cancellationToken);
        if (user is not { Kind: PortalUserKind.Human })
        {
            return PortalIdentityErrors.UserNotFound;
        }

        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length is < 5 or > 300)
        {
            return Error.Validation("PORTAL.REASON_REQUIRED", "Indique el motivo (de 5 a 300 caracteres).");
        }

        var password = TemporaryPasswords.Create();
        var now = auth.Clock.UtcNow;
        user.SetPassword(await auth.Hasher.HashAsync(password, SecretKind.Password, cancellationToken), now, mustChange: true);
        user.ResetTotp();
        await PortalUserUseCaseMapping.RevokeAllAsync(store, user.Id, "Recuperación de emergencia", now, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("portal", "PORTAL_EMERGENCY_RECOVERY", nameof(PortalUser), user.Id, user.AuditLabel,
                $"Recuperación de emergencia de {user.Email} desde la consola del servidor: {reason}.", Severity: AuditSeverity.Critical),
            cancellationToken);
        return new TemporaryPasswordDto(user.Id, user.Email, password);
    }
}
