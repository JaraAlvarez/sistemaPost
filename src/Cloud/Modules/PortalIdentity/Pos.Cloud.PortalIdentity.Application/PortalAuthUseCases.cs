using System.Net;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Cloud.Abstractions;
using Pos.Cloud.PortalIdentity.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Security;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.PortalIdentity.Application;

/// <summary>Servicios compartidos por los pasos del acceso.</summary>
public sealed class PortalAuthServices(
    IPortalIdentityStore store,
    ISecretHasher hasher,
    ITotpSecretProtector protector,
    IAttributedAuditWriter audit,
    PortalIdentityOptions options,
    IIdGenerator ids,
    IClock clock)
{
    /// <summary>Hash válido de una contraseña que no existe: iguala el tiempo de respuesta cuando el correo no existe.</summary>
    private static string? _dummyHash;

    public IPortalIdentityStore Store => store;

    public ITotpSecretProtector Protector => protector;

    public PortalIdentityOptions Options => options;

    public IIdGenerator Ids => ids;

    public IClock Clock => clock;

    public ISecretHasher Hasher => hasher;

    public async Task<bool> VerifyPasswordAsync(PortalUser? user, string password, CancellationToken cancellationToken)
    {
        _dummyHash ??= await hasher.HashAsync("contraseña-inexistente-para-igualar-tiempos", SecretKind.Password, cancellationToken);
        var verification = await hasher.VerifyAsync(password ?? string.Empty, user?.PasswordHash ?? _dummyHash, SecretKind.Password, cancellationToken);
        if (user is null || verification == SecretVerification.Failed)
        {
            return false;
        }

        if (verification == SecretVerification.SucceededRehashNeeded)
        {
            user.RehashPassword(await hasher.HashAsync(password!, SecretKind.Password, cancellationToken));
        }

        return true;
    }

    /// <summary>Resuelve una sesión por su token (en cualquier etapa) junto con su usuario, si sigue utilizable.</summary>
    public async Task<(PortalSession Session, PortalUser User)?> ResolveAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var session = await store.FindSessionByTokenHashAsync(SecureTokens.Hash(token), cancellationToken);
        var user = session is null ? null : await store.GetUserAsync(session.UserId, cancellationToken);
        return session is not null && user is not null && session.IsUsable(user, clock.UtcNow) ? (session, user) : null;
    }

    public Task AuditAsync(PortalUser user, string action, string summary, AuditSeverity severity, CancellationToken cancellationToken) =>
        audit.WriteAsync(new AuditEntry("portal", action, nameof(PortalUser), user.Id, user.AuditLabel, summary, Severity: severity), user.Id, user.DisplayName, cancellationToken);

    /// <summary>Registra el intento fallido (bloqueo tras N) y lo audita. Devuelve el error para el usuario.</summary>
    public async Task<Error> FailAsync(PortalUser user, string action, string detail, CancellationToken cancellationToken)
    {
        var locked = user.RegisterFailedAttempt(options.Login, clock.UtcNow);
        await AuditAsync(user, action, detail, AuditSeverity.Warning, cancellationToken);
        if (!locked)
        {
            return action == "PORTAL_TOTP_FAILED" ? PortalIdentityErrors.InvalidCode : PortalIdentityErrors.InvalidCredentials;
        }

        foreach (var open in await store.GetOpenSessionsAsync(user.Id, cancellationToken))
        {
            open.Revoke("Usuario bloqueado por intentos fallidos", clock.UtcNow);
        }

        await AuditAsync(user, "PORTAL_USER_LOCKED", $"{user.Email} quedó bloqueado {options.LockoutMinutes} minutos por intentos fallidos.", AuditSeverity.Critical, cancellationToken);
        return PortalIdentityErrors.UserLocked;
    }

    public static PortalMeDto Me(PortalUser user) =>
        new(user.Id, user.Email, user.DisplayName, PortalRoleCodes.ToCode(user.Role), user.MustChangePassword, CloudPermissions.ForRole(PortalRoleCodes.ToCode(user.Role)));
}

public static class PortalRoleCodes
{
    public static string ToCode(PortalRole role) => role switch
    {
        PortalRole.Superadmin => PortalRoles.Superadmin,
        PortalRole.Support => PortalRoles.Support,
        _ => PortalRoles.Reseller,
    };

    public static bool TryParse(string? code, out PortalRole role)
    {
        role = code?.Trim().ToUpperInvariant() switch
        {
            PortalRoles.Superadmin => PortalRole.Superadmin,
            PortalRoles.Support => PortalRole.Support,
            PortalRoles.Reseller => PortalRole.Reseller,
            _ => (PortalRole)(-1),
        };
        return Enum.IsDefined(role);
    }

    public static string DisplayName(string role) => role switch
    {
        PortalRoles.Superadmin => "Superadministrador",
        PortalRoles.Support => "Soporte",
        _ => "Distribuidor",
    };
}

// ─────────────────────────────── Paso 1: contraseña ───────────────────────────────

/// <summary>
/// Primer paso del acceso: correo y contraseña. Correcta → sesión PENDIENTE (vida corta) que exige el código TOTP o enrolar el
/// autenticador. El caso de uso se confirma aunque falle, para guardar el intento y el bloqueo.
/// </summary>
public sealed record PortalLoginCommand(string Email, string Password, SessionChannel Channel, IPAddress? IpAddress, string? UserAgent)
    : ICommand<Outcome<LoginChallengeDto>>;

internal sealed class PortalLoginHandler(PortalAuthServices auth) : ICommandHandler<PortalLoginCommand, Outcome<LoginChallengeDto>>
{
    public async Task<Result<Outcome<LoginChallengeDto>>> Handle(PortalLoginCommand request, CancellationToken cancellationToken)
    {
        var now = auth.Clock.UtcNow;
        var email = PortalUser.NormalizeEmail(request.Email);
        var user = email.Length is > 0 and <= 120 ? await auth.Store.FindUserByEmailAsync(email, cancellationToken) : null;
        var passwordOk = await auth.VerifyPasswordAsync(user, request.Password, cancellationToken);

        if (user is null || user.Kind != PortalUserKind.Human)
        {
            return Outcome.Fail<LoginChallengeDto>(PortalIdentityErrors.InvalidCredentials);
        }

        if (user.IsLockedAt(now))
        {
            await auth.AuditAsync(user, "PORTAL_LOGIN_FAILED", $"Intento de acceso de {user.Email} mientras está bloqueado.", AuditSeverity.Warning, cancellationToken);
            return Outcome.Fail<LoginChallengeDto>(PortalIdentityErrors.UserLocked);
        }

        if (!passwordOk || !user.CanSignIn(now))
        {
            return Outcome.Fail<LoginChallengeDto>(await auth.FailAsync(user, "PORTAL_LOGIN_FAILED", $"Contraseña incorrecta o usuario deshabilitado ({user.Email}).", cancellationToken));
        }

        var token = SecureTokens.Create();
        var session = PortalSession.StartPending(
            auth.Ids.NewId(), user, SecureTokens.Hash(token), request.Channel, auth.Options.Sessions, now, request.IpAddress, request.UserAgent);
        auth.Store.Add(session);
        await auth.AuditAsync(user, "PORTAL_PASSWORD_ACCEPTED",
            $"{user.Email}: contraseña correcta desde {request.IpAddress?.ToString() ?? "IP desconocida"}; falta el segundo factor.", AuditSeverity.Info, cancellationToken);
        return Outcome.Ok(new LoginChallengeDto(token, session.Stage == SessionStage.PendingTotp ? "TOTP_REQUIRED" : "ENROLLMENT_REQUIRED", session.ExpiresAt));
    }
}

// ─────────────────────────────── Enrolamiento del autenticador ───────────────────────────────

/// <summary>Genera el secreto TOTP (nuevo en cada llamada) para mostrarlo como QR; se confirma con el primer código correcto.</summary>
public sealed record BeginTotpEnrollmentCommand(string Token) : ICommand<Outcome<TotpEnrollmentDto>>;

internal sealed class BeginTotpEnrollmentHandler(PortalAuthServices auth) : ICommandHandler<BeginTotpEnrollmentCommand, Outcome<TotpEnrollmentDto>>
{
    public async Task<Result<Outcome<TotpEnrollmentDto>>> Handle(BeginTotpEnrollmentCommand request, CancellationToken cancellationToken)
    {
        if (await auth.ResolveAsync(request.Token, cancellationToken) is not { Session.Stage: SessionStage.PendingEnrollment } resolved)
        {
            return Outcome.Fail<TotpEnrollmentDto>(PortalIdentityErrors.SessionInvalid);
        }

        var secret = Totp.GenerateSecret();
        var begun = resolved.User.BeginTotpEnrollment(auth.Protector.Protect(secret));
        return begun.IsFailure
            ? Outcome.Fail<TotpEnrollmentDto>(begun.Error)
            : Outcome.Ok(new TotpEnrollmentDto(Totp.ToBase32(secret), Totp.EnrollmentLink(auth.Options.TotpIssuer, resolved.User.Email, secret)));
    }
}

// ─────────────────────────────── Paso 2: código TOTP ───────────────────────────────

/// <summary>
/// Segundo factor: el código TOTP (o el primero tras escanear el QR, que confirma el enrolamiento). Correcto → la sesión pasa a
/// ACTIVA con un token nuevo. Un código usado no sirve dos veces; los fallos cuentan para el bloqueo.
/// </summary>
public sealed record CompleteSecondFactorCommand(string Token, string Code) : ICommand<Outcome<PortalSessionDto>>;

internal sealed class CompleteSecondFactorHandler(PortalAuthServices auth) : ICommandHandler<CompleteSecondFactorCommand, Outcome<PortalSessionDto>>
{
    public async Task<Result<Outcome<PortalSessionDto>>> Handle(CompleteSecondFactorCommand request, CancellationToken cancellationToken)
    {
        if (await auth.ResolveAsync(request.Token, cancellationToken) is not { } resolved || resolved.Session.Stage == SessionStage.Active)
        {
            return Outcome.Fail<PortalSessionDto>(PortalIdentityErrors.SessionInvalid);
        }

        var (session, user) = resolved;
        var now = auth.Clock.UtcNow;
        if (user.IsLockedAt(now))
        {
            return Outcome.Fail<PortalSessionDto>(PortalIdentityErrors.UserLocked);
        }

        var secret = user.TotpSecretProtected is { } stored ? auth.Protector.Unprotect(stored) : null;
        if (secret is null)
        {
            return Outcome.Fail<PortalSessionDto>(PortalIdentityErrors.TotpEnrollmentRequired);
        }

        var step = Totp.Verify(secret, request.Code, now, user.TotpLastStep);
        if (step is null)
        {
            return Outcome.Fail<PortalSessionDto>(await auth.FailAsync(user, "PORTAL_TOTP_FAILED", $"Código de verificación incorrecto ({user.Email}).", cancellationToken));
        }

        var enrolling = session.Stage == SessionStage.PendingEnrollment;
        if (enrolling)
        {
            var confirmed = user.ConfirmTotp(step.Value);
            if (confirmed.IsFailure)
            {
                return Outcome.Fail<PortalSessionDto>(confirmed.Error);
            }

            await auth.AuditAsync(user, "PORTAL_TOTP_ENROLLED", $"{user.Email} activó el doble factor.", AuditSeverity.Warning, cancellationToken);
        }
        else
        {
            user.UseTotpStep(step.Value);
        }

        var token = SecureTokens.Create();
        session.CompleteSecondFactor(user, SecureTokens.Hash(token), auth.Options.Sessions, now);
        user.RegisterSuccessfulSignIn(now);
        await auth.AuditAsync(user, "PORTAL_LOGIN_SUCCEEDED",
            $"{user.Email} entró al portal ({(session.Channel == SessionChannel.Api ? "API" : "navegador")}) desde {session.IpAddress?.ToString() ?? "IP desconocida"}.",
            AuditSeverity.Info, cancellationToken);
        return Outcome.Ok(new PortalSessionDto(token, session.Id, session.ExpiresAt, PortalAuthServices.Me(user)));
    }
}

// ─────────────────────────────── Salida y cuenta propia ───────────────────────────────

public sealed record PortalLogoutCommand : ICommand;

internal sealed class PortalLogoutHandler(IPortalUserContext current, PortalAuthServices auth) : ICommandHandler<PortalLogoutCommand>
{
    public async Task<Result> Handle(PortalLogoutCommand request, CancellationToken cancellationToken)
    {
        if (!current.IsAuthenticated || current.SessionId is not { } sessionId || await auth.Store.GetSessionAsync(sessionId, cancellationToken) is not { } session)
        {
            return Result.Success();
        }

        if (session.Revoke("Salida del usuario", auth.Clock.UtcNow) && await auth.Store.GetUserAsync(session.UserId, cancellationToken) is { } user)
        {
            await auth.AuditAsync(user, "PORTAL_LOGOUT", $"{user.Email} salió del portal.", AuditSeverity.Info, cancellationToken);
        }

        return Result.Success();
    }
}

public sealed record GetMyAccountQuery : IQuery<(PortalMeDto Me, IReadOnlyList<PortalSessionInfoDto> Sessions)>;

internal sealed class GetMyAccountHandler(IPortalUserContext current, PortalAuthServices auth)
    : IQueryHandler<GetMyAccountQuery, (PortalMeDto Me, IReadOnlyList<PortalSessionInfoDto> Sessions)>
{
    public async Task<Result<(PortalMeDto Me, IReadOnlyList<PortalSessionInfoDto> Sessions)>> Handle(GetMyAccountQuery request, CancellationToken cancellationToken)
    {
        if (!current.IsAuthenticated || current.UserId is not { } userId || await auth.Store.GetUserAsync(userId, cancellationToken) is not { } user)
        {
            return PortalIdentityErrors.SessionInvalid;
        }

        var sessions = await auth.Store.ListSessionsAsync(userId, 20, cancellationToken);
        return (PortalAuthServices.Me(user), [.. sessions.Select(PortalUserUseCaseMapping.ToDto)]);
    }
}

/// <summary>El usuario cambia su contraseña (obligatorio tras una contraseña temporal). Cierra sus otras sesiones.</summary>
public sealed record ChangeOwnPasswordCommand(string CurrentPassword, string NewPassword) : ICommand;

internal sealed class ChangeOwnPasswordHandler(IPortalUserContext current, PortalAuthServices auth) : ICommandHandler<ChangeOwnPasswordCommand>
{
    public async Task<Result> Handle(ChangeOwnPasswordCommand request, CancellationToken cancellationToken)
    {
        if (!current.IsAuthenticated || current.UserId is not { } userId || await auth.Store.GetUserAsync(userId, cancellationToken) is not { } user)
        {
            return PortalIdentityErrors.SessionInvalid;
        }

        if (!await auth.VerifyPasswordAsync(user, request.CurrentPassword, cancellationToken))
        {
            return PortalIdentityErrors.InvalidCredentials;
        }

        if (!PortalIdentityErrors.IsAcceptablePassword(request.NewPassword, user.Email) || request.NewPassword == request.CurrentPassword)
        {
            return PortalIdentityErrors.WeakPassword;
        }

        var now = auth.Clock.UtcNow;
        user.SetPassword(await auth.Hasher.HashAsync(request.NewPassword, SecretKind.Password, cancellationToken), now, mustChange: false);
        foreach (var session in await auth.Store.GetOpenSessionsAsync(user.Id, cancellationToken))
        {
            if (session.Id == current.SessionId)
            {
                // La sesión en curso sigue abierta con la nueva versión de seguridad.
                session.CompleteSecondFactor(user, session.TokenHash, auth.Options.Sessions, now);
            }
            else
            {
                session.Revoke("Cambio de contraseña", now);
            }
        }

        await auth.AuditAsync(user, "PORTAL_PASSWORD_CHANGED", $"{user.Email} cambió su contraseña; se cerraron sus otras sesiones.", AuditSeverity.Warning, cancellationToken);
        return Result.Success();
    }
}

/// <summary>
/// Verificación de permisos del portal (usada por el pipeline): la sesión en curso debe seguir activa (no revocada, sin vencer,
/// misma versión de seguridad) y el rol ACTUAL del usuario en la BD debe tener el permiso.
/// </summary>
public sealed class PortalPermissionChecker(IPortalUserContext current, IPortalIdentityStore store, IClock clock) : IPermissionChecker
{
    public async Task<bool> HasPermissionAsync(string permissionCode, Guid? branchId = null, CancellationToken cancellationToken = default)
    {
        if (!current.IsAuthenticated || current.SessionId is not { } sessionId)
        {
            return false;
        }

        var session = await store.GetSessionAsync(sessionId, cancellationToken);
        var user = session is null ? null : await store.GetUserAsync(session.UserId, cancellationToken);
        return session is { Stage: SessionStage.Active } && user is not null && session.IsUsable(user, clock.UtcNow)
               && !user.MustChangePassword
               && CloudPermissions.RoleHas(PortalRoleCodes.ToCode(user.Role), permissionCode);
    }
}

/// <summary>Resuelve el token de una cookie o de un Bearer (lo usa el host en cada petición) y extiende la inactividad.</summary>
public sealed class PortalSessionAuthenticator(PortalAuthServices auth)
{
    public async Task<AuthenticatedPortalSession?> AuthenticateAsync(string? token, SessionChannel channel, CancellationToken cancellationToken)
    {
        if (await auth.ResolveAsync(token, cancellationToken) is not { } resolved || resolved.Session.Channel != channel)
        {
            return null;
        }

        var (session, user) = resolved;
        if (session.Touch(auth.Options.Sessions, auth.Clock.UtcNow))
        {
            await auth.Store.SaveAsync(cancellationToken);
        }

        var role = PortalRoleCodes.ToCode(user.Role);
        return new AuthenticatedPortalSession(
            session.Id, user.Id, user.Email, user.DisplayName, role, session.Stage, user.MustChangePassword,
            session.Stage == SessionStage.Active && !user.MustChangePassword ? CloudPermissions.ForRole(role) : []);
    }
}
