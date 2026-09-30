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

    /// <summary>El usuario visto desde una sesión: la obligación de cambiar la contraseña depende de cómo entró (ADR-0062).</summary>
    public static PortalMeDto Me(PortalUser user, PortalSession? session = null) =>
        new(user.Id, user.Email, user.DisplayName, PortalRoleCodes.ToCode(user.Role), session?.RequiresPasswordChange(user) ?? user.MustChangePassword,
            CloudPermissions.ForRole(PortalRoleCodes.ToCode(user.Role)), user.TotpEnabled, user.MustChangePassword,
            session?.AuthMethod == SessionAuthMethod.Google ? "GOOGLE" : "PASSWORD");

    /// <summary>Sesión ACTIVA nueva (contraseña sin TOTP o Google): registra el ingreso y lo audita con su método.</summary>
    public async Task<(PortalSession Session, string Token)> StartActiveSessionAsync(
        PortalUser user, SessionChannel channel, SessionAuthMethod method, IPAddress? ip, string? userAgent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = clock.UtcNow;
        var token = SecureTokens.Create();
        var session = PortalSession.StartActive(ids.NewId(), user, SecureTokens.Hash(token), channel, method, options.Sessions, now, ip, userAgent);
        store.Add(session);
        user.RegisterSuccessfulSignIn(now);
        await AuditAsync(user, "PORTAL_LOGIN_SUCCEEDED", LoginSummary(user, session), AuditSeverity.Info, cancellationToken);
        return (session, token);
    }

    /// <summary>Resumen de la auditoría de un ingreso: canal, método, IP y agente de usuario.</summary>
    public static string LoginSummary(PortalUser user, PortalSession session)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(session);
        var method = session.AuthMethod == SessionAuthMethod.Google ? "GOOGLE" : "CONTRASEÑA";
        var agent = session.UserAgent is { Length: > 0 } ua ? (ua.Length > 120 ? ua[..120] : ua) : "desconocido";
        return $"{user.Email} entró al portal ({(session.Channel == SessionChannel.Api ? "API" : "navegador")}, método {method}) desde "
               + $"{session.IpAddress?.ToString() ?? "IP desconocida"}; agente: {agent}.";
    }
}

public static class PortalRoleCodes
{
    public static string ToCode(PortalRole role) => role switch
    {
        PortalRole.Superadmin => PortalRoles.Superadmin,
        PortalRole.Support => PortalRoles.Support,
        PortalRole.Customer => PortalRoles.Customer,
        _ => PortalRoles.Reseller,
    };

    public static bool TryParse(string? code, out PortalRole role)
    {
        role = code?.Trim().ToUpperInvariant() switch
        {
            PortalRoles.Superadmin => PortalRole.Superadmin,
            PortalRoles.Support => PortalRole.Support,
            PortalRoles.Reseller => PortalRole.Reseller,
            PortalRoles.Customer => PortalRole.Customer,
            _ => (PortalRole)(-1),
        };
        return Enum.IsDefined(role);
    }

    public static string DisplayName(string role) => role switch
    {
        PortalRoles.Superadmin => "Superadministrador",
        PortalRoles.Support => "Soporte",
        PortalRoles.Customer => "Cliente",
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

        // Doble factor opcional (ADR-0062): sin TOTP activo y sin exigirlo la configuración, la contraseña basta.
        if (!user.TotpEnabled && !auth.Options.RequireTotp)
        {
            var (active, activeToken) = await auth.StartActiveSessionAsync(
                user, request.Channel, SessionAuthMethod.Password, request.IpAddress, request.UserAgent, cancellationToken);
            return Outcome.Ok(new LoginChallengeDto(activeToken, "ACTIVE", active.ExpiresAt));
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
        await auth.AuditAsync(user, "PORTAL_LOGIN_SUCCEEDED", PortalAuthServices.LoginSummary(user, session), AuditSeverity.Info, cancellationToken);
        return Outcome.Ok(new PortalSessionDto(token, session.Id, session.ExpiresAt, PortalAuthServices.Me(user, session)));
    }
}

// ─────────────────────────────── Ingreso con un proveedor externo (Google) ───────────────────────────────

/// <summary>
/// Ingreso con Google (ADR-0062): el host ya validó la respuesta de Google (OAuth/OpenID) y entrega el correo y si Google lo
/// verificó. Solo entra un correo VERIFICADO igual (sin distinguir mayúsculas) al de un usuario humano ACTIVO y sin bloqueo;
/// nunca se crean usuarios. Correcto → sesión ACTIVA (sin TOTP: la seguridad de la cuenta de Google hace de segundo factor).
/// Cualquier rechazo responde un error genérico y queda en la auditoría. El caso de uso se confirma aunque falle.
/// </summary>
public sealed record PortalExternalLoginCommand(
    string Provider, string? Email, bool EmailVerified, SessionChannel Channel, IPAddress? IpAddress, string? UserAgent)
    : ICommand<Outcome<PortalSessionDto>>;

internal sealed class PortalExternalLoginHandler(PortalAuthServices auth, IAuditWriter audit)
    : ICommandHandler<PortalExternalLoginCommand, Outcome<PortalSessionDto>>
{
    public async Task<Result<Outcome<PortalSessionDto>>> Handle(PortalExternalLoginCommand request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Provider, PortalExternalProviders.Google, StringComparison.Ordinal))
        {
            return Outcome.Fail<PortalSessionDto>(PortalIdentityErrors.ExternalLoginRejected);
        }

        var now = auth.Clock.UtcNow;
        var email = PortalUser.NormalizeEmail(request.Email);
        var shown = email.Length == 0 ? "(sin correo)" : email.Length > 120 ? email[..120] : email;
        var origin = $"desde {request.IpAddress?.ToString() ?? "IP desconocida"}";
        var user = PortalUser.IsValidEmail(email) ? await auth.Store.FindUserByEmailAsync(email, cancellationToken) : null;
        if (user is null || user.Kind != PortalUserKind.Human)
        {
            // Sin usuario al que atribuirlo: fila de auditoría sin entidad (el correo es el dato del intento).
            await audit.WriteAsync(
                new AuditEntry("portal", "PORTAL_LOGIN_FAILED", nameof(PortalUser), null, null,
                    $"Ingreso con Google rechazado: el correo {shown} no corresponde a un usuario del portal ({origin}).", Severity: AuditSeverity.Warning),
                cancellationToken);
            return Outcome.Fail<PortalSessionDto>(PortalIdentityErrors.ExternalLoginRejected);
        }

        if (!request.EmailVerified)
        {
            await auth.AuditAsync(user, "PORTAL_LOGIN_FAILED",
                $"Ingreso con Google rechazado: Google no verificó el correo {user.Email} ({origin}).", AuditSeverity.Warning, cancellationToken);
            return Outcome.Fail<PortalSessionDto>(PortalIdentityErrors.ExternalLoginRejected);
        }

        if (user.IsLockedAt(now))
        {
            await auth.AuditAsync(user, "PORTAL_LOGIN_FAILED", $"Ingreso con Google de {user.Email} mientras está bloqueado ({origin}).", AuditSeverity.Warning, cancellationToken);
            return Outcome.Fail<PortalSessionDto>(PortalIdentityErrors.UserLocked);
        }

        if (user.Status != PortalUserStatus.Active)
        {
            await auth.AuditAsync(user, "PORTAL_LOGIN_FAILED", $"Ingreso con Google de {user.Email}: usuario deshabilitado ({origin}).", AuditSeverity.Warning, cancellationToken);
            return Outcome.Fail<PortalSessionDto>(PortalIdentityErrors.ExternalLoginRejected);
        }

        var (session, token) = await auth.StartActiveSessionAsync(user, request.Channel, SessionAuthMethod.Google, request.IpAddress, request.UserAgent, cancellationToken);
        return Outcome.Ok(new PortalSessionDto(token, session.Id, session.ExpiresAt, PortalAuthServices.Me(user, session)));
    }
}

/// <summary>Proveedores externos de identidad admitidos.</summary>
public static class PortalExternalProviders
{
    public const string Google = "GOOGLE";
}

// ─────────────────────────────── Doble factor opcional desde "Mi cuenta" ───────────────────────────────

/// <summary>Resuelve el usuario y la sesión ACTIVA en curso (acciones del propio usuario sobre su seguridad).</summary>
internal static class OwnAccount
{
    public static async Task<(PortalUser User, PortalSession Session)?> ResolveAsync(IPortalUserContext current, PortalAuthServices auth, CancellationToken cancellationToken)
    {
        if (!current.IsAuthenticated || current.SessionId is not { } sessionId
            || await auth.Store.GetSessionAsync(sessionId, cancellationToken) is not { Stage: SessionStage.Active } session
            || await auth.Store.GetUserAsync(session.UserId, cancellationToken) is not { } user || !session.IsUsable(user, auth.Clock.UtcNow))
        {
            return null;
        }

        return (user, session);
    }

    /// <summary>Tras el cambio de seguridad: la sesión en curso sigue abierta y las demás se cierran.</summary>
    public static async Task KeepOnlyCurrentAsync(PortalAuthServices auth, PortalUser user, PortalSession current, string reason, CancellationToken cancellationToken)
    {
        current.KeepAfterSecurityChange(user);
        foreach (var session in await auth.Store.GetOpenSessionsAsync(user.Id, cancellationToken))
        {
            if (session.Id != current.Id)
            {
                session.Revoke(reason, auth.Clock.UtcNow);
            }
        }
    }
}

/// <summary>El usuario empieza a activar su TOTP (opcional): secreto nuevo para el QR, sin confirmar hasta el primer código.</summary>
public sealed record BeginOwnTotpEnrollmentCommand : ICommand<TotpEnrollmentDto>;

internal sealed class BeginOwnTotpEnrollmentHandler(IPortalUserContext current, PortalAuthServices auth)
    : ICommandHandler<BeginOwnTotpEnrollmentCommand, TotpEnrollmentDto>
{
    public async Task<Result<TotpEnrollmentDto>> Handle(BeginOwnTotpEnrollmentCommand request, CancellationToken cancellationToken)
    {
        if (await OwnAccount.ResolveAsync(current, auth, cancellationToken) is not { } own)
        {
            return PortalIdentityErrors.SessionInvalid;
        }

        var secret = Totp.GenerateSecret();
        var begun = own.User.BeginTotpEnrollment(auth.Protector.Protect(secret));
        return begun.IsFailure
            ? begun.Error
            : new TotpEnrollmentDto(Totp.ToBase32(secret), Totp.EnrollmentLink(auth.Options.TotpIssuer, own.User.Email, secret));
    }
}

/// <summary>El primer código correcto activa el TOTP del usuario; desde ahí el ingreso con contraseña lo pide siempre.</summary>
public sealed record ConfirmOwnTotpCommand(string Code) : ICommand;

internal sealed class ConfirmOwnTotpHandler(IPortalUserContext current, PortalAuthServices auth) : ICommandHandler<ConfirmOwnTotpCommand>
{
    public async Task<Result> Handle(ConfirmOwnTotpCommand request, CancellationToken cancellationToken)
    {
        if (await OwnAccount.ResolveAsync(current, auth, cancellationToken) is not { } own)
        {
            return PortalIdentityErrors.SessionInvalid;
        }

        var (user, session) = own;
        if (user.TotpEnabled)
        {
            return PortalIdentityErrors.TotpAlreadyEnabled;
        }

        if ((user.TotpSecretProtected is { } stored ? auth.Protector.Unprotect(stored) : null) is not { } secret)
        {
            return PortalIdentityErrors.TotpEnrollmentRequired;
        }

        if (Totp.Verify(secret, request.Code, auth.Clock.UtcNow, user.TotpLastStep) is not { } step)
        {
            return PortalIdentityErrors.InvalidCode;
        }

        var confirmed = user.ConfirmTotp(step);
        if (confirmed.IsFailure)
        {
            return confirmed.Error;
        }

        await OwnAccount.KeepOnlyCurrentAsync(auth, user, session, "Activación del doble factor", cancellationToken);
        await auth.AuditAsync(user, "PORTAL_TOTP_ENROLLED", $"{user.Email} activó el doble factor desde Mi cuenta; se cerraron sus otras sesiones.",
            AuditSeverity.Warning, cancellationToken);
        return Result.Success();
    }
}

/// <summary>
/// El usuario desactiva su TOTP con un código vigente (prueba de que aún tiene el autenticador). No se permite si la
/// configuración lo exige (<c>Portal:RequireTotp</c>).
/// </summary>
public sealed record DisableOwnTotpCommand(string Code) : ICommand;

internal sealed class DisableOwnTotpHandler(IPortalUserContext current, PortalAuthServices auth) : ICommandHandler<DisableOwnTotpCommand>
{
    public async Task<Result> Handle(DisableOwnTotpCommand request, CancellationToken cancellationToken)
    {
        if (await OwnAccount.ResolveAsync(current, auth, cancellationToken) is not { } own)
        {
            return PortalIdentityErrors.SessionInvalid;
        }

        if (auth.Options.RequireTotp)
        {
            return PortalIdentityErrors.TotpRequiredByPolicy;
        }

        var (user, session) = own;
        if ((user.TotpEnabled && user.TotpSecretProtected is { } stored ? auth.Protector.Unprotect(stored) : null) is not { } secret)
        {
            return PortalIdentityErrors.TotpNotEnabled;
        }

        if (Totp.Verify(secret, request.Code, auth.Clock.UtcNow, user.TotpLastStep) is null)
        {
            return PortalIdentityErrors.InvalidCode;
        }

        user.ResetTotp();
        await OwnAccount.KeepOnlyCurrentAsync(auth, user, session, "Desactivación del doble factor", cancellationToken);
        await auth.AuditAsync(user, "PORTAL_TOTP_DISABLED", $"{user.Email} desactivó su doble factor desde Mi cuenta; se cerraron sus otras sesiones.",
            AuditSeverity.Warning, cancellationToken);
        return Result.Success();
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
        var session = current.SessionId is { } sessionId ? await auth.Store.GetSessionAsync(sessionId, cancellationToken) : null;
        return (PortalAuthServices.Me(user, session), [.. sessions.Select(PortalUserUseCaseMapping.ToDto)]);
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
               && !session.RequiresPasswordChange(user)
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
        var mustChange = session.RequiresPasswordChange(user);
        return new AuthenticatedPortalSession(
            session.Id, user.Id, user.Email, user.DisplayName, role, session.Stage, mustChange,
            session.Stage == SessionStage.Active && !mustChange ? CloudPermissions.ForRole(role) : []);
    }
}
