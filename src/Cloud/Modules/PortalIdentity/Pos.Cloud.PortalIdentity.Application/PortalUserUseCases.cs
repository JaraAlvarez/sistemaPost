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
        s.Channel == SessionChannel.Api ? "API" : "PORTAL", s.CreatedAt, s.ExpiresAt, s.LastSeenAt, s.RevokedAt, s.RevokedReason, s.IpAddress?.ToString(), s.UserAgent,
        s.AuthMethod == SessionAuthMethod.Google ? "GOOGLE" : "PASSWORD");

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

/// <summary>
/// Crea un usuario del equipo con una contraseña temporal (se muestra una vez); en su primer ingreso enrola el 2FA y la cambia. PN6: crear
/// un SUPERADMINISTRADOR exige siempre el código TOTP del que actúa (<paramref name="Confirmation"/>), como restablecer a otro
/// superadministrador.
/// </summary>
[RequiresPermission(CloudPermissions.PortalUserManage)]
public sealed record CreatePortalUserCommand(string Email, string DisplayName, string Role, Guid? ResellerAccountId = null, string? Confirmation = null)
    : ICommand<Outcome<TemporaryPasswordDto>>;

internal sealed class CreatePortalUserHandler(IPortalIdentityStore store, PortalAuthServices auth, IPortalUserContext current, IAuditWriter audit)
    : ICommandHandler<CreatePortalUserCommand, Outcome<TemporaryPasswordDto>>
{
    public async Task<Result<Outcome<TemporaryPasswordDto>>> Handle(CreatePortalUserCommand request, CancellationToken cancellationToken)
    {
        if (!PortalRoleCodes.TryParse(request.Role, out var role))
        {
            return PortalIdentityErrors.InvalidUser;
        }

        if (await store.FindUserByEmailAsync(PortalUser.NormalizeEmail(request.Email), cancellationToken) is not null)
        {
            return PortalIdentityErrors.EmailDuplicated;
        }

        if (role == PortalRole.Superadmin)
        {
            var confirmed = await AdminReauthentication.CheckAsync(
                current, auth, new AdminTarget(null, PortalUser.NormalizeEmail(request.Email), IsSuperadmin: true), request.Confirmation,
                "crear el superadministrador", cancellationToken);
            if (confirmed.IsFailure)
            {
                return Outcome.Fail<TemporaryPasswordDto>(confirmed.Error);
            }
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
        return Outcome.Ok(new TemporaryPasswordDto(created.Value.Id, created.Value.Email, password));
    }
}

/// <summary>Usuario afectado por una acción de administración que no devuelve datos (actualizar, desbloquear).</summary>
public sealed record PortalUserChangedDto(Guid UserId);

/// <summary>
/// Cambia el nombre, el rol o el estado de otro usuario. PN6/IL6: cambiar el ROL, DESACTIVAR (también a un usuario bloqueado por intentos),
/// REACTIVAR o cambiar la CUENTA asociada exige la misma confirmación que restablecer la contraseña (ingreso reciente o contraseña/TOTP;
/// siempre el TOTP si la cuenta es o pasa a ser de superadministrador). Cambiar solo el nombre no la pide. Sobre sí mismo esos cambios
/// no se permiten (<see cref="PortalIdentityErrors.CannotChangeSelf"/>).
/// </summary>
[RequiresPermission(CloudPermissions.PortalUserManage)]
public sealed record UpdatePortalUserCommand(Guid UserId, string DisplayName, string Role, Guid? ResellerAccountId, bool IsActive, string? Confirmation = null)
    : ICommand<Outcome<PortalUserChangedDto>>;

internal sealed class UpdatePortalUserHandler(IPortalIdentityStore store, IPortalUserContext current, PortalAuthServices auth, IAuditWriter audit)
    : ICommandHandler<UpdatePortalUserCommand, Outcome<PortalUserChangedDto>>
{
    public async Task<Result<Outcome<PortalUserChangedDto>>> Handle(UpdatePortalUserCommand request, CancellationToken cancellationToken)
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

        // IL6: cambios delicados = rol, estado (desactivar, también a un usuario bloqueado por intentos, o reactivar) y cuenta asociada.
        var newStatus = request.IsActive ? PortalUserStatus.Active : PortalUserStatus.Disabled;
        var changesRole = role != user.Role;
        var changesStatus = newStatus != user.Status;
        var changesAccount = request.ResellerAccountId != user.ResellerAccountId;
        if (user.Id == current.UserId && (changesRole || changesStatus || changesAccount))
        {
            return PortalIdentityErrors.CannotChangeSelf;
        }

        var losesSuperadmin = user.Role == PortalRole.Superadmin && user.Status == PortalUserStatus.Active && (role != PortalRole.Superadmin || !request.IsActive);
        if (losesSuperadmin && !await store.AnyActiveSuperadminAsync(user.Id, cancellationToken))
        {
            return PortalIdentityErrors.LastSuperadmin;
        }

        // IL6: los datos inválidos se rechazan ANTES de pedir la confirmación (no se gasta un código TOTP ni un intento en algo que no procede).
        if (PortalUser.NeedsAccount(role) != request.ResellerAccountId.HasValue)
        {
            return PortalIdentityErrors.ResellerAccountRequired;
        }

        if (changesRole || changesStatus || changesAccount)
        {
            var action = changesRole ? "cambiar el rol" : changesStatus ? (request.IsActive ? "reactivar" : "desactivar") : "cambiar la cuenta asociada";
            var confirmed = await AdminReauthentication.CheckAsync(
                current, auth, new AdminTarget(user.Id, user.Email, user.Role == PortalRole.Superadmin || role == PortalRole.Superadmin), request.Confirmation,
                action, cancellationToken);
            if (confirmed.IsFailure)
            {
                return Outcome.Fail<PortalUserChangedDto>(confirmed.Error);
            }
        }

        var version = user.SecurityVersion;
        var previousAccount = user.ResellerAccountId;
        var updated = user.Update(request.DisplayName, role, request.ResellerAccountId, request.IsActive);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        if (user.SecurityVersion != version)
        {
            await PortalUserUseCaseMapping.RevokeAllAsync(store, user.Id, "Cambio de rol o estado del usuario", auth.Clock.UtcNow, cancellationToken);
            await audit.WriteAsync(
                new AuditEntry("portal", "PORTAL_USER_SECURITY_CHANGED", nameof(PortalUser), user.Id, user.AuditLabel,
                    $"{user.Email}: rol {PortalRoleCodes.DisplayName(PortalRoleCodes.ToCode(role))}, {(request.IsActive ? "activo" : "deshabilitado")}, " +
                    $"cuenta {(user.ResellerAccountId?.ToString() ?? "(ninguna)")}" +
                    (previousAccount != user.ResellerAccountId ? $" (antes {previousAccount?.ToString() ?? "(ninguna)"})" : string.Empty) +
                    "; se cerraron sus sesiones.",
                    Severity: AuditSeverity.Critical),
                cancellationToken);
        }

        return Outcome.Ok(new PortalUserChangedDto(user.Id));
    }
}

/// <summary>
/// Restablecer la contraseña de OTRO usuario. <paramref name="Confirmation"/>: contraseña o código TOTP del superadministrador que actúa;
/// hace falta si su ingreso no es reciente y SIEMPRE el TOTP si el afectado es otro superadministrador (ver <see cref="AdminResetPolicy"/>).
/// Es un <see cref="Outcome{T}"/>: una confirmación fallida queda registrada (intento fallido y auditoría) aunque se rechace.
/// </summary>
[RequiresPermission(CloudPermissions.PortalUserManage)]
public sealed record ResetPortalUserPasswordCommand(Guid UserId, string? Confirmation = null) : ICommand<Outcome<TemporaryPasswordDto>>;

internal sealed class ResetPortalUserPasswordHandler(IPortalIdentityStore store, PortalAuthServices auth, IPortalUserContext current, IAuditWriter audit)
    : ICommandHandler<ResetPortalUserPasswordCommand, Outcome<TemporaryPasswordDto>>
{
    public async Task<Result<Outcome<TemporaryPasswordDto>>> Handle(ResetPortalUserPasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(request.UserId, cancellationToken);
        if (user is not { Kind: PortalUserKind.Human })
        {
            return PortalIdentityErrors.UserNotFound;
        }

        var confirmed = await AdminReauthentication.CheckAsync(current, auth, AdminTarget.Of(user), request.Confirmation, "restablecer la contraseña", cancellationToken);
        if (confirmed.IsFailure)
        {
            return Outcome.Fail<TemporaryPasswordDto>(confirmed.Error);
        }

        var (password, now) = await AdminReauthentication.NewTemporaryPasswordAsync(auth, user, cancellationToken);
        await PortalUserUseCaseMapping.RevokeAllAsync(store, user.Id, "Contraseña restablecida", now, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("portal", "PORTAL_PASSWORD_RESET", nameof(PortalUser), user.Id, user.AuditLabel,
                $"Contraseña de {user.Email} restablecida (temporal) por {confirmed.Value.Email}; se cerraron sus sesiones.", Severity: AuditSeverity.Critical),
            cancellationToken);
        return Outcome.Ok(new TemporaryPasswordDto(user.Id, user.Email, password));
    }
}

/// <summary>Resultado de restablecer el doble factor: la contraseña temporal nueva si también se restableció (lo predeterminado).</summary>
public sealed record TotpResetDto(Guid UserId, string Email, string? TemporaryPassword);

/// <summary>
/// Recuperación manual del doble factor (teléfono perdido): el usuario vuelve a escanear un QR en su próximo ingreso. Por defecto
/// TAMBIÉN se restablece la contraseña (temporal, a cambiar en el próximo ingreso): quitar solo el segundo factor dejaría la cuenta
/// protegida únicamente por una contraseña que quizá también se filtró. Mismas confirmaciones que restablecer la contraseña.
/// </summary>
[RequiresPermission(CloudPermissions.PortalUserManage)]
public sealed record ResetPortalUserTotpCommand(Guid UserId, string Reason, bool AlsoResetPassword = true, string? Confirmation = null) : ICommand<Outcome<TotpResetDto>>;

internal sealed class ResetPortalUserTotpHandler(IPortalIdentityStore store, PortalAuthServices auth, IPortalUserContext current, IAuditWriter audit)
    : ICommandHandler<ResetPortalUserTotpCommand, Outcome<TotpResetDto>>
{
    public async Task<Result<Outcome<TotpResetDto>>> Handle(ResetPortalUserTotpCommand request, CancellationToken cancellationToken)
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

        var confirmed = await AdminReauthentication.CheckAsync(current, auth, AdminTarget.Of(user), request.Confirmation, "restablecer el doble factor", cancellationToken);
        if (confirmed.IsFailure)
        {
            return Outcome.Fail<TotpResetDto>(confirmed.Error);
        }

        user.ResetTotp();
        string? password = null;
        var now = auth.Clock.UtcNow;
        if (request.AlsoResetPassword)
        {
            (password, now) = await AdminReauthentication.NewTemporaryPasswordAsync(auth, user, cancellationToken);
        }

        await PortalUserUseCaseMapping.RevokeAllAsync(store, user.Id, "Doble factor restablecido", now, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("portal", "PORTAL_TOTP_RESET", nameof(PortalUser), user.Id, user.AuditLabel,
                $"Doble factor de {user.Email} restablecido por {confirmed.Value.Email}{(password is null ? " (la contraseña NO se restableció)" : " junto con la contraseña (temporal)")}: {reason}.",
                Severity: AuditSeverity.Critical),
            cancellationToken);
        return Outcome.Ok(new TotpResetDto(user.Id, user.Email, password));
    }
}

/// <summary>Cuenta afectada por una acción delicada: <c>Id</c> nulo si todavía no existe (crear un superadministrador).</summary>
internal sealed record AdminTarget(Guid? Id, string Email, bool IsSuperadmin)
{
    public static AdminTarget Of(PortalUser user) => new(user.Id, user.Email, AdminResetPolicy.RequiresTotp(user));
}

/// <summary>Confirmación del superadministrador para las acciones delicadas sobre otro usuario (<see cref="AdminResetPolicy"/>).</summary>
internal static class AdminReauthentication
{
    public static readonly Error ReauthenticationRequired = Error.Forbidden(
        "PORTAL.REAUTH_REQUIRED",
        "Por seguridad, confirme con su contraseña o con el código de su aplicación autenticadora (su ingreso no es reciente).");

    public static readonly Error TotpConfirmationRequired = Error.Forbidden(
        "PORTAL.TOTP_CONFIRMATION_REQUIRED", "Para actuar sobre una cuenta de superadministrador, escriba el código de su aplicación autenticadora.");

    public static readonly Error ActorTotpRequired = Error.BusinessRule(
        "PORTAL.ACTOR_TOTP_REQUIRED", "Para actuar sobre una cuenta de superadministrador, primero active su propio doble factor en \"Mi cuenta\".");

    public static readonly Error CannotResetSelf = Error.BusinessRule(
        "PORTAL.CANNOT_RESET_SELF", "No puede restablecer su propia contraseña o doble factor desde aquí: hágalo en \"Mi cuenta\".");

    public static readonly Error InvalidConfirmation = Error.Unauthorized("PORTAL.INVALID_CONFIRMATION", "La contraseña o el código no son correctos.");

    /// <summary>Devuelve al superadministrador que actúa si puede hacerlo; cuenta los intentos fallidos (bloqueo) y los audita.</summary>
    public static async Task<Result<PortalUser>> CheckAsync(
        IPortalUserContext current, PortalAuthServices auth, AdminTarget target, string? confirmation, string action, CancellationToken cancellationToken)
    {
        if (await OwnAccount.ResolveAsync(current, auth, cancellationToken) is not { } own)
        {
            return PortalIdentityErrors.SessionInvalid;
        }

        var (actor, session) = own;
        if (actor.Id == target.Id)
        {
            return CannotResetSelf;
        }

        var now = auth.Clock.UtcNow;
        var secret = actor.TotpEnabled && actor.TotpSecretProtected is { } stored ? auth.Protector.Unprotect(stored) : null;
        var code = confirmation?.Trim();
        if (target.IsSuperadmin)
        {
            if (secret is null)
            {
                return ActorTotpRequired;
            }

            if (string.IsNullOrEmpty(code))
            {
                return TotpConfirmationRequired;
            }
        }
        else if (string.IsNullOrEmpty(code))
        {
            return AdminResetPolicy.AllowsWithoutConfirmation(session, actor, target.IsSuperadmin, now) ? actor : ReauthenticationRequired;
        }

        // Código TOTP (6 dígitos) o, si el afectado no es superadministrador, la contraseña.
        if (secret is not null && code!.Length == 6 && code.All(char.IsAsciiDigit) && Totp.Verify(secret, code, now, actor.TotpLastStep) is { } step)
        {
            actor.UseTotpStep(step);
            return actor;
        }

        if (!target.IsSuperadmin && await auth.VerifyPasswordAsync(actor, code!, cancellationToken))
        {
            return actor;
        }

        var failed = await auth.FailAsync(actor, "PORTAL_REAUTH_FAILED",
            $"{actor.Email} no confirmó su identidad para {action} de {target.Email}.", cancellationToken);
        return failed.Code == PortalIdentityErrors.UserLocked.Code ? failed : InvalidConfirmation;
    }

    public static async Task<(string Password, DateTimeOffset Now)> NewTemporaryPasswordAsync(PortalAuthServices auth, PortalUser user, CancellationToken cancellationToken)
    {
        var password = TemporaryPasswords.Create();
        var now = auth.Clock.UtcNow;
        user.SetPassword(await auth.Hasher.HashAsync(password, SecretKind.Password, cancellationToken), now, mustChange: true);
        return (password, now);
    }
}

/// <summary>Desbloquea a otro usuario (intentos fallidos). PN6: misma confirmación que restablecer su contraseña.</summary>
[RequiresPermission(CloudPermissions.PortalUserManage)]
public sealed record UnlockPortalUserCommand(Guid UserId, string? Confirmation = null) : ICommand<Outcome<PortalUserChangedDto>>;

internal sealed class UnlockPortalUserHandler(IPortalIdentityStore store, PortalAuthServices auth, IPortalUserContext current, IAuditWriter audit)
    : ICommandHandler<UnlockPortalUserCommand, Outcome<PortalUserChangedDto>>
{
    public async Task<Result<Outcome<PortalUserChangedDto>>> Handle(UnlockPortalUserCommand request, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(request.UserId, cancellationToken);
        if (user is null)
        {
            return PortalIdentityErrors.UserNotFound;
        }

        var confirmed = await AdminReauthentication.CheckAsync(current, auth, AdminTarget.Of(user), request.Confirmation, "desbloquear", cancellationToken);
        if (confirmed.IsFailure)
        {
            return Outcome.Fail<PortalUserChangedDto>(confirmed.Error);
        }

        user.Unlock();
        await audit.WriteAsync(
            new AuditEntry("portal", "PORTAL_USER_UNLOCKED", nameof(PortalUser), user.Id, user.AuditLabel,
                $"{user.Email} desbloqueado por {confirmed.Value.Email}.", Severity: AuditSeverity.Warning),
            cancellationToken);
        return Outcome.Ok(new PortalUserChangedDto(user.Id));
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
