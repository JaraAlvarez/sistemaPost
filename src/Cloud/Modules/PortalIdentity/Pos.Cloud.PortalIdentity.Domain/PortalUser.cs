using System.Net;
using System.Text.RegularExpressions;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.PortalIdentity.Domain;

public enum PortalRole
{
    Superadmin,
    Support,
    Reseller,

    /// <summary>Cliente del producto (dueño del supermercado, Fase 16): ve solo los datos de las empresas de su cuenta.</summary>
    Customer,
}

public enum PortalUserKind
{
    Human,
    System,
}

public enum PortalUserStatus
{
    Active,
    Disabled,
}

/// <summary>Política de acceso (⚙️ configurable; valores por defecto de la propuesta §6).</summary>
public sealed record LoginPolicy(int MaxFailedAttempts, TimeSpan LockoutDuration)
{
    public static readonly LoginPolicy Default = new(5, TimeSpan.FromMinutes(15));
}

/// <summary>
/// Usuario del portal (L-08): solo el equipo del propietario. Contraseña Argon2id y TOTP (obligatorio u opcional según
/// <c>Portal:RequireTotp</c>; ADR-0062), o ingreso con Google si el correo coincide; tras
/// <see cref="LoginPolicy.MaxFailedAttempts"/> intentos fallidos (contraseña o código) se bloquea un tiempo.
/// </summary>
[Audited("portal")]
public sealed partial class PortalUser : AggregateRoot<Guid>, IHasAuditLabel
{
    public const int MinPasswordLength = 12;

    private PortalUser(Guid id, string email, string displayName, PortalUserKind kind, PortalRole role)
        : base(id)
    {
        Email = email;
        DisplayName = displayName;
        Kind = kind;
        Role = role;
    }

    public string Email { get; private set; }

    public string DisplayName { get; private set; }

    public PortalUserKind Kind { get; private set; }

    public PortalRole Role { get; private set; }

    /// <summary>
    /// Cuenta a la que está atado el usuario: la del distribuidor (<see cref="PortalRole.Reseller"/>) o la del cliente
    /// (<see cref="PortalRole.Customer"/>, Fase 16). Los demás roles no la llevan.
    /// </summary>
    public Guid? ResellerAccountId { get; private set; }

    public PortalUserStatus Status { get; private set; } = PortalUserStatus.Active;

    [Sensitive]
    public string? PasswordHash { get; private set; }

    public bool MustChangePassword { get; private set; }

    public DateTimeOffset? PasswordChangedAt { get; private set; }

    /// <summary>Secreto TOTP cifrado (protección de datos de ASP.NET Core; las llaves no están en la BD).</summary>
    [Sensitive]
    public string? TotpSecretProtected { get; private set; }

    public bool TotpEnabled { get; private set; }

    [NotAudited]
    public long? TotpLastStep { get; private set; }

    [NotAudited]
    public short FailedLoginCount { get; private set; }

    [NotAudited]
    public DateTimeOffset? LockedUntil { get; private set; }

    [NotAudited]
    public DateTimeOffset? LastLoginAt { get; private set; }

    /// <summary>Sube con cada cambio de seguridad (rol, contraseña, 2FA, estado): invalida las sesiones abiertas.</summary>
    [NotAudited]
    public long SecurityVersion { get; private set; } = 1;

    public string AuditLabel => $"Usuario del portal {Email}";

    public static Result<PortalUser> Create(Guid id, string email, string displayName, PortalRole role, Guid? resellerAccountId, string passwordHash, DateTimeOffset now)
    {
        var normalized = NormalizeEmail(email);
        var name = (displayName ?? string.Empty).Trim();
        if (!IsValidEmail(normalized) || name.Length is 0 or > 120)
        {
            return PortalIdentityErrors.InvalidUser;
        }

        if (NeedsAccount(role) != resellerAccountId.HasValue)
        {
            return PortalIdentityErrors.ResellerAccountRequired;
        }

        var user = new PortalUser(id, normalized, name, PortalUserKind.Human, role) { ResellerAccountId = resellerAccountId };
        user.SetPassword(passwordHash, now, mustChange: true);
        return user;
    }

    /// <summary>Distribuidor y cliente van atados a una cuenta.</summary>
    public static bool NeedsAccount(PortalRole role) => role is PortalRole.Reseller or PortalRole.Customer;

    public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValidEmail(string email) => email.Length is > 3 and <= 120 && EmailPattern().IsMatch(email);

    public bool IsLockedAt(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>¿Puede intentar entrar? Humano, activo, con contraseña y sin bloqueo vigente.</summary>
    public bool CanSignIn(DateTimeOffset now) =>
        Kind == PortalUserKind.Human && Status == PortalUserStatus.Active && PasswordHash is not null && !IsLockedAt(now);

    /// <summary>Registra un intento fallido; devuelve <c>true</c> si este intento bloqueó al usuario.</summary>
    public bool RegisterFailedAttempt(LoginPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        FailedLoginCount++;
        if (FailedLoginCount < policy.MaxFailedAttempts)
        {
            return false;
        }

        FailedLoginCount = 0;
        LockedUntil = now + policy.LockoutDuration;
        return true;
    }

    public void RegisterSuccessfulSignIn(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginAt = now;
    }

    public void Unlock()
    {
        FailedLoginCount = 0;
        LockedUntil = null;
    }

    public void SetPassword(string passwordHash, DateTimeOffset now, bool mustChange)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        PasswordChangedAt = now;
        MustChangePassword = mustChange;
        Unlock();
        SecurityVersion++;
    }

    /// <summary>Recalcula el hash con parámetros actuales (misma contraseña): no invalida sesiones.</summary>
    public void RehashPassword(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    /// <summary>Guarda un secreto nuevo (aún no confirmado) para enrolar el autenticador.</summary>
    public Result BeginTotpEnrollment(string protectedSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedSecret);
        if (TotpEnabled)
        {
            return PortalIdentityErrors.TotpAlreadyEnabled;
        }

        TotpSecretProtected = protectedSecret;
        return Result.Success();
    }

    /// <summary>El primer código correcto confirma el enrolamiento.</summary>
    public Result ConfirmTotp(long step)
    {
        if (TotpEnabled || TotpSecretProtected is null)
        {
            return PortalIdentityErrors.TotpEnrollmentRequired;
        }

        TotpEnabled = true;
        TotpLastStep = step;
        SecurityVersion++;
        return Result.Success();
    }

    public void UseTotpStep(long step) => TotpLastStep = step;

    /// <summary>
    /// Recuperación manual del 2FA (teléfono perdido) o desactivación por el propio usuario cuando el doble factor es opcional:
    /// el próximo ingreso vuelve a enrolar solo si <c>Portal:RequireTotp</c> lo exige.
    /// </summary>
    public void ResetTotp()
    {
        TotpEnabled = false;
        TotpSecretProtected = null;
        TotpLastStep = null;
        SecurityVersion++;
    }

    public Result Update(string displayName, PortalRole role, Guid? resellerAccountId, bool isActive)
    {
        if (Kind == PortalUserKind.System)
        {
            return PortalIdentityErrors.SystemUserImmutable;
        }

        var name = (displayName ?? string.Empty).Trim();
        if (name.Length is 0 or > 120)
        {
            return PortalIdentityErrors.InvalidUser;
        }

        if (NeedsAccount(role) != resellerAccountId.HasValue)
        {
            return PortalIdentityErrors.ResellerAccountRequired;
        }

        var status = isActive ? PortalUserStatus.Active : PortalUserStatus.Disabled;
        if (role != Role || status != Status || resellerAccountId != ResellerAccountId)
        {
            SecurityVersion++;
        }

        DisplayName = name;
        Role = role;
        ResellerAccountId = resellerAccountId;
        Status = status;
        return Result.Success();
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();
}

public enum SessionStage
{
    /// <summary>Contraseña correcta; falta el código TOTP.</summary>
    PendingTotp,

    /// <summary>Contraseña correcta; el usuario debe enrolar su autenticador (primer ingreso o tras un reinicio del 2FA).</summary>
    PendingEnrollment,

    /// <summary>Segundo factor completo.</summary>
    Active,
}

/// <summary>Cómo se autenticó la sesión (ADR-0062).</summary>
public enum SessionAuthMethod
{
    /// <summary>Correo y contraseña (más el TOTP si el usuario lo tiene activo o si la configuración lo exige).</summary>
    Password,

    /// <summary>Cuenta de Google con correo verificado igual al del usuario del portal.</summary>
    Google,
}

public enum SessionChannel
{
    /// <summary>Navegador (cookie <c>HttpOnly</c>, <c>SameSite=Strict</c>).</summary>
    Portal,

    /// <summary>API interna <c>/admin</c> (<c>Authorization: Bearer</c>).</summary>
    Api,
}

/// <summary>Duraciones de las sesiones (⚙️).</summary>
public sealed record SessionPolicy(TimeSpan PendingLifetime, TimeSpan AbsoluteLifetime, TimeSpan IdleTimeout)
{
    public static readonly SessionPolicy Default = new(TimeSpan.FromMinutes(5), TimeSpan.FromHours(12), TimeSpan.FromMinutes(30));
}

/// <summary>Sesión del portal con token opaco (solo su SHA-256 en la BD), revocable y con vencimiento absoluto y por inactividad.</summary>
public sealed class PortalSession : Entity<Guid>
{
    private PortalSession(Guid id, Guid userId, string tokenHash, SessionStage stage, SessionChannel channel, DateTimeOffset createdAt)
        : base(id)
    {
        UserId = userId;
        TokenHash = tokenHash;
        Stage = stage;
        Channel = channel;
        CreatedAt = createdAt;
    }

    public Guid UserId { get; private set; }

    public string TokenHash { get; private set; }

    public SessionStage Stage { get; private set; }

    public SessionChannel Channel { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset IdleExpiresAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset? SecondFactorAt { get; private set; }

    public long SecurityVersion { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public IPAddress? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    /// <summary>Método con el que se autenticó (contraseña o Google).</summary>
    public SessionAuthMethod AuthMethod { get; private set; } = SessionAuthMethod.Password;

    /// <summary>
    /// ¿Debe cambiar la contraseña antes de usar el portal? Solo si la contraseña es temporal y la sesión entró CON ella: quien
    /// entró con Google no usó la contraseña temporal (ADR-0062).
    /// </summary>
    public bool RequiresPasswordChange(PortalUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.MustChangePassword && AuthMethod == SessionAuthMethod.Password;
    }

    public static PortalSession StartPending(
        Guid id, PortalUser user, string tokenHash, SessionChannel channel, SessionPolicy policy, DateTimeOffset now, IPAddress? ip, string? userAgent)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(policy);
        var stage = user.TotpEnabled ? SessionStage.PendingTotp : SessionStage.PendingEnrollment;
        return new PortalSession(id, user.Id, tokenHash, stage, channel, now)
        {
            ExpiresAt = now + policy.PendingLifetime,
            IdleExpiresAt = now + policy.PendingLifetime,
            LastSeenAt = now,
            SecurityVersion = user.SecurityVersion,
            IpAddress = ip,
            UserAgent = userAgent is { Length: > 300 } ? userAgent[..300] : userAgent,
        };
    }

    /// <summary>
    /// Sesión ACTIVA desde el primer paso: contraseña de un usuario sin TOTP cuando el doble factor no es obligatorio, o Google.
    /// </summary>
    public static PortalSession StartActive(
        Guid id, PortalUser user, string tokenHash, SessionChannel channel, SessionAuthMethod method, SessionPolicy policy, DateTimeOffset now,
        IPAddress? ip, string? userAgent)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(policy);
        var session = new PortalSession(id, user.Id, tokenHash, SessionStage.Active, channel, now)
        {
            AuthMethod = method,
            IpAddress = ip,
            UserAgent = userAgent is { Length: > 300 } ? userAgent[..300] : userAgent,
        };
        session.CompleteSecondFactor(user, tokenHash, policy, now);
        return session;
    }

    /// <summary>
    /// Tras un cambio de seguridad hecho por el propio usuario en esta sesión (contraseña, activar o desactivar el TOTP), la sesión
    /// en curso sigue abierta con la nueva versión de seguridad (las demás se revocan aparte).
    /// </summary>
    public void KeepAfterSecurityChange(PortalUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        SecurityVersion = user.SecurityVersion;
    }

    public bool IsUsable(PortalUser user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        return RevokedAt is null && now < ExpiresAt && now < IdleExpiresAt && user.Id == UserId
               && user.Status == PortalUserStatus.Active && user.SecurityVersion == SecurityVersion;
    }

    /// <summary>Completa el segundo factor: la sesión pasa a activa con un token NUEVO (evita fijación de sesión).</summary>
    public void CompleteSecondFactor(PortalUser user, string newTokenHash, SessionPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(policy);
        TokenHash = newTokenHash;
        Stage = SessionStage.Active;
        SecondFactorAt = now;
        SecurityVersion = user.SecurityVersion;
        ExpiresAt = now + policy.AbsoluteLifetime;
        IdleExpiresAt = now + policy.IdleTimeout;
        LastSeenAt = now;
    }

    /// <summary>Extiende la inactividad (se llama como máximo una vez por minuto para no escribir en cada petición).</summary>
    public bool Touch(SessionPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (Stage != SessionStage.Active || now - LastSeenAt < TimeSpan.FromMinutes(1))
        {
            return false;
        }

        LastSeenAt = now;
        IdleExpiresAt = now + policy.IdleTimeout < ExpiresAt ? now + policy.IdleTimeout : ExpiresAt;
        return true;
    }

    public bool Revoke(string reason, DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return false;
        }

        RevokedAt = now;
        RevokedReason = reason.Length > 200 ? reason[..200] : reason;
        return true;
    }
}

/// <summary>Errores del acceso y de la gestión de usuarios del portal.</summary>
public static class PortalIdentityErrors
{
    /// <summary>Misma respuesta si el usuario no existe, está deshabilitado o la contraseña no coincide.</summary>
    public static readonly Error InvalidCredentials = Error.Unauthorized("PORTAL.INVALID_CREDENTIALS", "Correo o contraseña incorrectos.");

    public static readonly Error UserLocked = Error.Unauthorized(
        "PORTAL.USER_LOCKED", "El usuario está bloqueado temporalmente por intentos fallidos. Intente más tarde.");

    public static readonly Error InvalidCode = Error.Unauthorized("PORTAL.INVALID_TOTP", "El código de verificación no es correcto.");

    public static readonly Error SessionInvalid = Error.Unauthorized("PORTAL.SESSION_INVALID", "La sesión venció o fue cerrada. Ingrese de nuevo.");

    public static readonly Error InvalidUser = Error.Validation(
        "PORTAL.INVALID_USER", "Datos del usuario inválidos: correo válido y nombre de 1 a 120 caracteres.");

    public static readonly Error ResellerAccountRequired = Error.Validation(
        "PORTAL.RESELLER_ACCOUNT_REQUIRED", "Los roles de distribuidor y cliente exigen su cuenta (y solo esos roles la llevan).");

    public static readonly Error EmailDuplicated = Error.Conflict("PORTAL.EMAIL_DUPLICATED", "Ya existe un usuario con ese correo.");

    public static readonly Error UserNotFound = Error.NotFound("PORTAL.USER_NOT_FOUND", "El usuario no existe.");

    public static readonly Error SystemUserImmutable = Error.BusinessRule("PORTAL.SYSTEM_USER", "El usuario técnico del sistema no se modifica.");

    public static readonly Error WeakPassword = Error.Validation(
        "PORTAL.WEAK_PASSWORD", "La contraseña debe tener al menos 12 caracteres, con letras y números, y no puede ser el correo.");

    public static readonly Error TotpAlreadyEnabled = Error.BusinessRule("PORTAL.TOTP_ALREADY_ENABLED", "El doble factor ya está activo.");

    public static readonly Error TotpEnrollmentRequired = Error.BusinessRule("PORTAL.TOTP_ENROLLMENT_REQUIRED", "Primero escanee el código QR del doble factor.");

    /// <summary>Ingreso con Google rechazado (correo desconocido, sin verificar o usuario deshabilitado): mensaje genérico.</summary>
    public static readonly Error ExternalLoginRejected = Error.Unauthorized(
        "PORTAL.EXTERNAL_LOGIN_REJECTED",
        "No fue posible ingresar con esa cuenta de Google. Use el correo registrado en el portal o ingrese con su contraseña.");

    public static readonly Error TotpRequiredByPolicy = Error.BusinessRule(
        "PORTAL.TOTP_REQUIRED_BY_POLICY", "En este portal el doble factor es obligatorio: no se puede desactivar.");

    public static readonly Error TotpNotEnabled = Error.BusinessRule("PORTAL.TOTP_NOT_ENABLED", "El doble factor no está activo.");

    public static readonly Error LastSuperadmin = Error.BusinessRule(
        "PORTAL.LAST_SUPERADMIN", "Debe quedar al menos un superadministrador activo.");

    public static readonly Error BootstrapDone = Error.Conflict(
        "PORTAL.BOOTSTRAP_DONE", "Ya existe un superadministrador: los demás usuarios se crean desde el portal.");

    public static readonly Error CannotChangeSelf = Error.BusinessRule(
        "PORTAL.CANNOT_CHANGE_SELF", "No puede cambiar su propio rol ni deshabilitarse.");

    /// <summary>Contraseña: mínimo 12 caracteres, al menos una letra y un dígito, distinta del correo.</summary>
    public static bool IsAcceptablePassword(string? password, string email) =>
        password is { Length: >= PortalUser.MinPasswordLength and <= 128 }
        && password.Any(char.IsLetter) && password.Any(char.IsDigit)
        && !string.Equals(password.Trim(), email, StringComparison.OrdinalIgnoreCase);
}
