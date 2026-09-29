using FluentValidation;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Identity.Domain;
using Pos.Modules.Organization.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Security;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Identity.Application;

/// <summary>
/// Resultado de un intento de acceso. El caso de uso termina "bien" aunque las credenciales fallen: así el intento,
/// el contador y el bloqueo se guardan (la transacción se confirma). La API traduce <see cref="Error"/> a HTTP.
/// </summary>
public sealed record AuthOutcome<T>(T? Value, Error? Error)
    where T : class;

public static class AuthOutcome
{
    public static AuthOutcome<T> Ok<T>(T value)
        where T : class => new(value, null);

    public static AuthOutcome<T> Fail<T>(Error error)
        where T : class => new(null, error);
}

/// <summary>Servicios compartidos por los accesos con contraseña, PIN y autorización de supervisor.</summary>
public sealed class AuthServices(
    IIdentityStore store,
    ISessionStore sessions,
    ILoginAttemptLog attempts,
    ISecretHasher hasher,
    IPermissionEvaluator permissions,
    ISettingsReader settings,
    IInstallationContext installation,
    IClientContext client,
    IAuditWriter audit,
    IActorContext actor,
    IIdGenerator ids,
    IClock clock)
{
    /// <summary>Hash válido de una contraseña que no existe: iguala el tiempo de respuesta cuando el usuario no existe.</summary>
    private static string? _dummyHash;

    public IClock Clock => clock;

    public IIdGenerator Ids => ids;

    public IInstallationContext Installation => installation;

    public IClientContext Client => client;

    public IIdentityStore Store => store;

    public ISecretHasher Hasher => hasher;

    public async Task<int> SettingAsync(SettingDefinition<int> definition, Guid? branchId, CancellationToken cancellationToken) =>
        await settings.GetAsync(definition, new SettingContext(installation.CompanyId!.Value, branchId), cancellationToken);

    public void RecordAttempt(string kind, string identifier, User? user, bool succeeded, string? failure) =>
        attempts.Add(new LoginAttempt(
            ids.NewId(), installation.CompanyId, installation.NodeId, clock.UtcNow, kind,
            identifier.Length > 60 ? identifier[..60] : identifier, user?.Id, client.DeviceId, client.IpAddress, succeeded, failure));

    /// <summary>Verifica la credencial del usuario y aplica contador, bloqueo, auditoría y rehash. Devuelve el error o <c>null</c>.</summary>
    public async Task<Error?> VerifyAsync(User? user, string identifier, string secret, SecretKind kind, string attemptKind, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var withPin = kind == SecretKind.Pin;
        var hash = withPin ? user?.PinHash : user?.PasswordHash;
        if (user is null || hash is null)
        {
            _dummyHash ??= await hasher.HashAsync("contraseña-inexistente", SecretKind.Password, cancellationToken);
            await hasher.VerifyAsync(secret, _dummyHash, SecretKind.Password, cancellationToken);
            RecordAttempt(attemptKind, identifier, user, false, "UNKNOWN_OR_NO_CREDENTIAL");
            return IdentityErrors.InvalidCredentials;
        }

        var allowed = user.CanAuthenticate(now, withPin);
        if (allowed.IsFailure)
        {
            RecordAttempt(attemptKind, identifier, user, false, allowed.Error.Code == IdentityErrors.UserLocked.Code ? "LOCKED" : "DISABLED");
            return allowed.Error;
        }

        var verification = await hasher.VerifyAsync(secret, hash, kind, cancellationToken);
        if (verification == SecretVerification.Failed)
        {
            var max = await SettingAsync(withPin ? SecuritySettings.MaxPinAttempts : SecuritySettings.MaxFailedAttempts, null, cancellationToken);
            var lockMinutes = await SettingAsync(SecuritySettings.LockoutMinutes, null, cancellationToken);
            var locked = user.RecordFailure(now, max, lockMinutes, withPin);
            RecordAttempt(attemptKind, identifier, user, false, "BAD_SECRET");
            if (locked)
            {
                await AuditAsync(user, "USER_LOCKED", $"{user.DisplayName} quedó bloqueado {lockMinutes} min por {max} intentos fallidos ({(withPin ? "PIN" : "contraseña")}).",
                    AuditSeverity.Warning, cancellationToken);
            }

            return locked ? IdentityErrors.UserLocked : IdentityErrors.InvalidCredentials;
        }

        if (verification == SecretVerification.SucceededRehashNeeded)
        {
            var rehashed = await hasher.HashAsync(secret, kind, cancellationToken);
            if (withPin)
            {
                user.RehashPin(rehashed);
            }
            else
            {
                user.RehashPassword(rehashed);
            }
        }

        return null;
    }

    public async Task<LoginResultDto> OpenSessionAsync(User user, bool terminal, Guid branchId, Guid? posTerminalId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        user.RecordSuccess(now);
        var idleMinutes = await SettingAsync(terminal ? SecuritySettings.TerminalIdleMinutes : SecuritySettings.BackofficeIdleMinutes, branchId, cancellationToken);
        var maxHours = await SettingAsync(SecuritySettings.SessionMaxHours, null, cancellationToken);
        var token = SecureTokens.Create();
        var sessionId = ids.NewId();
        sessions.Add(new NewSession(
            sessionId, user.CompanyId, installation.NodeId, user.Id, SecureTokens.Hash(token), terminal, client.DeviceId,
            posTerminalId, branchId, client.IpAddress, client.UserAgent, user.SecurityVersion, idleMinutes * 60, now, now.AddHours(maxHours)));

        actor.Use(user.Id, user.DisplayName, user.CompanyId, branchId);
        RecordAttempt(terminal ? "PIN" : "PASSWORD", terminal ? user.PosCode ?? user.Username : user.Username, user, true, null);
        await audit.WriteAsync(
            new AuditEntry("identity", "LOGIN_SUCCEEDED", nameof(User), user.Id, user.AuditLabel,
                $"{user.DisplayName} inició sesión de {(terminal ? "caja" : "backoffice")}."),
            cancellationToken);

        var me = await BuildMeAsync(user, sessionId, terminal, branchId, posTerminalId, cancellationToken);
        return new LoginResultDto(token, now.AddHours(maxHours), idleMinutes * 60, me);
    }

    public async Task<MeDto> BuildMeAsync(User user, Guid sessionId, bool terminal, Guid branchId, Guid? posTerminalId, CancellationToken cancellationToken)
    {
        var effective = await permissions.GetEffectiveAsync(user.Id, branchId, null, cancellationToken);
        return new MeDto(user.Id, user.Username, user.DisplayName, sessionId, terminal ? "TERMINAL" : "BACKOFFICE", branchId, posTerminalId,
            user.MustChangePassword, [.. effective.Order(StringComparer.Ordinal)]);
    }

    public Task AuditAsync(User user, string action, string summary, AuditSeverity severity, CancellationToken cancellationToken) =>
        audit.WriteAsync(new AuditEntry("identity", action, nameof(User), user.Id, user.AuditLabel, summary, Severity: severity), cancellationToken);
}

// ───────────────────────────── Backoffice ─────────────────────────────

public sealed record LoginCommand(string Username, string Password) : ICommand<AuthOutcome<LoginResultDto>>;

internal sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

internal sealed class LoginHandler(AuthServices auth, TerminalResolver terminals) : ICommandHandler<LoginCommand, AuthOutcome<LoginResultDto>>
{
    public async Task<Result<AuthOutcome<LoginResultDto>>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        if (auth.Installation.CompanyId is null || auth.Installation.BranchId is not { } homeBranch)
        {
            return Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial.");
        }

        var username = IdentityRules.NormalizeUsername(request.Username);
        var user = await auth.Store.FindByUsernameAsync(username, cancellationToken);
        var error = await auth.VerifyAsync(user, username, request.Password, SecretKind.Password, "PASSWORD", cancellationToken);
        if (error is not null)
        {
            return AuthOutcome.Fail<LoginResultDto>(error);
        }

        // Backoffice: la sucursal es la de la caja si entra desde una caja; si no, la de la instalación.
        var terminal = auth.Client.DeviceKind == DeviceKind.Terminal ? await terminals.ResolveAsync(cancellationToken) : null;
        var branch = terminal?.BranchId ?? homeBranch;
        return AuthOutcome.Ok(await auth.OpenSessionAsync(user!, terminal: false, branch, null, cancellationToken));
    }
}

// ───────────────────────────── Caja ─────────────────────────────

/// <summary>Entrada en caja con código de cajero + PIN (D3-08). Solo desde una caja emparejada o, en Caja Única, el propio equipo.</summary>
public sealed record PosLoginCommand(string PosCode, string Pin) : ICommand<AuthOutcome<LoginResultDto>>;

internal sealed class PosLoginValidator : AbstractValidator<PosLoginCommand>
{
    public PosLoginValidator()
    {
        RuleFor(x => x.PosCode).NotEmpty().MaximumLength(6);
        RuleFor(x => x.Pin).NotEmpty().MaximumLength(6);
    }
}

internal sealed class PosLoginHandler(AuthServices auth, TerminalResolver terminals) : ICommandHandler<PosLoginCommand, AuthOutcome<LoginResultDto>>
{
    public async Task<Result<AuthOutcome<LoginResultDto>>> Handle(PosLoginCommand request, CancellationToken cancellationToken)
    {
        var terminal = await terminals.ResolveAsync(cancellationToken);
        if (terminal is null)
        {
            return AuthOutcome.Fail<LoginResultDto>(IdentityErrors.PinRequiresTerminal);
        }

        var user = await auth.Store.FindByPosCodeAsync(request.PosCode, cancellationToken);
        var error = await auth.VerifyAsync(user, request.PosCode, request.Pin, SecretKind.Pin, "PIN", cancellationToken);
        if (error is not null)
        {
            return AuthOutcome.Fail<LoginResultDto>(error);
        }

        return AuthOutcome.Ok(await auth.OpenSessionAsync(user!, terminal: true, terminal.BranchId, terminal.Id, cancellationToken));
    }
}

/// <summary>Caja desde la que se opera: la del equipo emparejado o, en Caja Única y desde el propio equipo, la única caja.</summary>
public sealed class TerminalResolver(IClientContext client, IInstallationContext installation, ITerminalDirectory directory)
{
    public async Task<TerminalInfo?> ResolveAsync(CancellationToken cancellationToken)
    {
        TerminalInfo? terminal = null;
        if (client.DeviceKind == DeviceKind.Terminal && client.PosTerminalId is { } id)
        {
            terminal = await directory.GetAsync(id, cancellationToken);
        }
        else if (client.IsLocal && client.DeviceId is null && installation.NodeRole == NodeRole.AllInOne)
        {
            terminal = await directory.GetSingleActiveAsync(cancellationToken);
        }

        return terminal is { IsActive: true } ? terminal : null;
    }
}

// ───────────────────────────── Sesión actual ─────────────────────────────

public sealed record LogoutCommand : ICommand;

internal sealed class LogoutHandler(ICurrentUser current, ISessionStore sessions, IAuditWriter audit) : ICommandHandler<LogoutCommand>
{
    public async Task<Result> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        if (current.SessionId is { } session)
        {
            await sessions.RevokeAsync(session, current.UserId, "LOGOUT", cancellationToken);
            await audit.WriteAsync(new AuditEntry("identity", "LOGOUT", "Session", session, Summary: $"{current.DisplayName} cerró sesión."), cancellationToken);
        }

        return Result.Success();
    }
}

public sealed record GetMeQuery : IQuery<MeDto>;

internal sealed class GetMeHandler(ICurrentUser current, IIdentityStore store, AuthServices auth) : IQueryHandler<GetMeQuery, MeDto>
{
    public async Task<Result<MeDto>> Handle(GetMeQuery request, CancellationToken cancellationToken)
    {
        var user = current.UserId is { } id ? await store.GetUserAsync(id, cancellationToken) : null;
        return user is null
            ? IdentityErrors.UserNotFound
            : await auth.BuildMeAsync(user, current.SessionId!.Value, current.IsTerminalSession, current.BranchId!.Value, current.PosTerminalId, cancellationToken);
    }
}

public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword) : ICommand;

internal sealed class ChangePasswordHandler(ICurrentUser current, AuthServices auth, CredentialPolicy policy, ISessionStore sessions)
    : ICommandHandler<ChangePasswordCommand>
{
    public async Task<Result> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await auth.Store.GetUserAsync(current.UserId!.Value, cancellationToken);
        if (user?.PasswordHash is null
            || await auth.Hasher.VerifyAsync(request.CurrentPassword, user.PasswordHash, SecretKind.Password, cancellationToken) == SecretVerification.Failed)
        {
            return IdentityErrors.CurrentPasswordInvalid;
        }

        var changed = await policy.ChangePasswordAsync(user, request.NewPassword, mustChange: false, cancellationToken);
        if (changed.IsFailure)
        {
            return changed;
        }

        // Las demás sesiones del usuario se cierran: la contraseña anterior podría estar comprometida.
        await sessions.RevokeAllForUserAsync(user.Id, user.Id, "PASSWORD_CHANGED", current.SessionId, cancellationToken);
        await auth.AuditAsync(user, "PASSWORD_CHANGED", $"{user.DisplayName} cambió su contraseña.", AuditSeverity.Info, cancellationToken);
        return Result.Success();
    }
}

public sealed record ChangePinCommand(string? CurrentPin, string NewPin) : ICommand;

internal sealed class ChangePinHandler(ICurrentUser current, AuthServices auth, CredentialPolicy policy) : ICommandHandler<ChangePinCommand>
{
    public async Task<Result> Handle(ChangePinCommand request, CancellationToken cancellationToken)
    {
        var user = await auth.Store.GetUserAsync(current.UserId!.Value, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        if (user.PosCode is null)
        {
            return IdentityErrors.InvalidPosCode;
        }

        if (user.PinHash is not null
            && await auth.Hasher.VerifyAsync(request.CurrentPin ?? string.Empty, user.PinHash, SecretKind.Pin, cancellationToken) == SecretVerification.Failed)
        {
            return Error.Validation("IDENTITY.CURRENT_PIN_INVALID", "El PIN actual no es correcto.");
        }

        var changed = await policy.SetPinAsync(user, user.PosCode, request.NewPin, cancellationToken);
        if (changed.IsSuccess)
        {
            await auth.AuditAsync(user, "PIN_CHANGED", $"{user.DisplayName} cambió su PIN.", AuditSeverity.Info, cancellationToken);
        }

        return changed;
    }
}

// ───────────────────────────── Autorización de supervisor ─────────────────────────────

/// <summary>
/// Un supervisor autoriza en la caja una acción que el cajero no tiene permitida (D3-07). La autorización sirve una
/// sola vez, para ese permiso, acción y objetivo, y vence a los pocos minutos.
/// </summary>
public sealed record CreateAuthorizationCommand(
    string SupervisorCode, string SupervisorPin, string PermissionCode, string Action, Guid? TargetId, string? TargetType, string? Reason)
    : ICommand<AuthOutcome<AuthorizationGrantDto>>;

internal sealed class CreateAuthorizationValidator : AbstractValidator<CreateAuthorizationCommand>
{
    public CreateAuthorizationValidator()
    {
        RuleFor(x => x.SupervisorCode).NotEmpty().MaximumLength(6);
        RuleFor(x => x.SupervisorPin).NotEmpty().MaximumLength(6);
        RuleFor(x => x.PermissionCode).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Action).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Reason).MaximumLength(250);
    }
}

internal sealed class CreateAuthorizationHandler(
    ICurrentUser current, AuthServices auth, IPermissionEvaluator permissions, IAuthorizationGrantStore grants, IAuditWriter audit)
    : ICommandHandler<CreateAuthorizationCommand, AuthOutcome<AuthorizationGrantDto>>
{
    public async Task<Result<AuthOutcome<AuthorizationGrantDto>>> Handle(CreateAuthorizationCommand request, CancellationToken cancellationToken)
    {
        if (!current.IsTerminalSession || current.PosTerminalId is null)
        {
            return AuthOutcome.Fail<AuthorizationGrantDto>(IdentityErrors.PinRequiresTerminal);
        }

        var supervisor = await auth.Store.FindByPosCodeAsync(request.SupervisorCode, cancellationToken);
        if (supervisor?.Id == current.UserId)
        {
            return AuthOutcome.Fail<AuthorizationGrantDto>(IdentityErrors.SelfAuthorization);
        }

        var error = await auth.VerifyAsync(supervisor, request.SupervisorCode, request.SupervisorPin, SecretKind.Pin, "SUPERVISOR", cancellationToken);
        if (error is not null)
        {
            return AuthOutcome.Fail<AuthorizationGrantDto>(error);
        }

        var effective = await permissions.GetEffectiveAsync(supervisor!.Id, current.BranchId, null, cancellationToken);
        if (!effective.Contains(request.PermissionCode))
        {
            return AuthOutcome.Fail<AuthorizationGrantDto>(IdentityErrors.SupervisorLacksPermission);
        }

        var now = auth.Clock.UtcNow;
        var seconds = await auth.SettingAsync(SecuritySettings.SupervisorGrantSeconds, null, cancellationToken);
        var grantId = auth.Ids.NewId();
        grants.Add(new NewAuthorizationGrant(
            grantId, supervisor.CompanyId, auth.Installation.NodeId, request.PermissionCode, current.UserId!.Value, supervisor.Id,
            current.PosTerminalId, request.Action, request.TargetType, request.TargetId, request.Reason, now, now.AddSeconds(seconds)));
        await audit.WriteAsync(
            new AuditEntry("identity", "SUPERVISOR_AUTHORIZATION_GRANTED", "AuthorizationGrant", grantId, request.Action,
                $"{supervisor.DisplayName} autorizó a {current.DisplayName}: {request.PermissionCode} ({request.Action}).",
                NewValues: new Dictionary<string, object?> { ["permission"] = request.PermissionCode, ["target"] = request.TargetId, ["reason"] = request.Reason },
                AuthorizedBy: supervisor.Id, Severity: AuditSeverity.Warning),
            cancellationToken);

        return AuthOutcome.Ok(
            new AuthorizationGrantDto(grantId, request.PermissionCode, request.Action, request.TargetId, now.AddSeconds(seconds), supervisor.DisplayName));
    }
}
