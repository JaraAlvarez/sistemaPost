using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Identity.Domain;

public enum UserKind
{
    Human,
    System,
}

public enum UserStatus
{
    Active,
    Locked,
    Disabled,
}

public enum LockReason
{
    FailedAttempts,
    PinAttempts,
    Admin,
}

public enum OverrideEffect
{
    Grant,
    Deny,
}

/// <summary>Rol asignado a un usuario. <c>BranchId</c> nulo = todas las sucursales.</summary>
public sealed class UserRole
{
    public UserRole(Guid id, Guid roleId, Guid? branchId, DateTimeOffset grantedAt, Guid grantedBy)
    {
        Id = id;
        RoleId = roleId;
        BranchId = branchId;
        GrantedAt = grantedAt;
        GrantedBy = grantedBy;
    }

    public Guid Id { get; private set; }

    public Guid RoleId { get; private set; }

    public Guid? BranchId { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    public Guid GrantedBy { get; private set; }
}

/// <summary>Excepción puntual de un permiso para un usuario (DENY siempre gana).</summary>
public sealed class UserPermissionOverride
{
    public UserPermissionOverride(Guid id, string permissionCode, OverrideEffect effect, Guid? branchId, string reason)
    {
        Id = id;
        PermissionCode = permissionCode;
        Effect = effect;
        BranchId = branchId;
        Reason = reason;
    }

    public Guid Id { get; private set; }

    public string PermissionCode { get; private set; }

    public OverrideEffect Effect { get; private set; }

    public Guid? BranchId { get; private set; }

    public string Reason { get; private set; }
}

/// <summary>
/// Usuario de la empresa. Las credenciales (hash de contraseña y de PIN) se sincronizan con los demás nodos; los
/// intentos fallidos, el bloqueo por intentos y el último acceso son estado LOCAL del nodo (D3-09).
/// </summary>
[Audited("identity")]
public sealed class User : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    public const string SystemUsername = "system";
    public const string SystemDisplayName = "Sistema";

    private readonly List<UserRole> _roles = [];
    private readonly List<UserPermissionOverride> _overrides = [];

    private User(Guid id, Guid companyId, string username, string displayName, UserKind kind, UserStatus status)
        : base(id)
    {
        CompanyId = companyId;
        Username = username;
        DisplayName = displayName;
        Kind = kind;
        Status = status;
    }

    public Guid CompanyId { get; private set; }

    public string Username { get; private set; }

    public string DisplayName { get; private set; }

    [PersonalData(PersonalDataKind.Email)]
    public string? Email { get; private set; }

    public UserKind Kind { get; private set; }

    public UserStatus Status { get; private set; }

    public bool MustChangePassword { get; private set; }

    [Sensitive]
    public string? PasswordHash { get; private set; }

    [Sensitive]
    public string? PinHash { get; private set; }

    /// <summary>Código de cajero (3–6 dígitos), único en la empresa.</summary>
    public string? PosCode { get; private set; }

    public DateTimeOffset? PasswordChangedAt { get; private set; }

    public DateTimeOffset? PinChangedAt { get; private set; }

    [LocalOnly]
    public short FailedLoginCount { get; private set; }

    [LocalOnly]
    public DateTimeOffset? LockedUntil { get; private set; }

    public LockReason? LockedReason { get; private set; }

    [LocalOnly]
    public DateTimeOffset? LastLoginAt { get; private set; }

    /// <summary>Sube con cada cambio que afecta la seguridad: invalida los permisos cacheados de sus sesiones.</summary>
    [NotAudited]
    public long SecurityVersion { get; private set; } = 1;

    public Guid? EmployeeId { get; private set; }

    public IReadOnlyCollection<UserRole> Roles => _roles.AsReadOnly();

    public IReadOnlyCollection<UserPermissionOverride> Overrides => _overrides.AsReadOnly();

    public string AuditLabel => $"Usuario {Username} · {DisplayName}";

    public bool IsHuman => Kind == UserKind.Human;

    /// <summary>
    /// Usuario técnico: autor de las semillas, del asistente inicial y de los procesos automáticos.
    /// Deshabilitado y sin credenciales: nunca puede iniciar sesión (lo garantiza también un CHECK en la BD).
    /// </summary>
    public static User CreateSystem(Guid id, Guid companyId) =>
        new(id, companyId, SystemUsername, SystemDisplayName, UserKind.System, UserStatus.Disabled);

    public static Result<User> CreateHuman(
        Guid id, Guid companyId, string username, string displayName, string? email, string passwordHash, DateTimeOffset now, bool mustChangePassword)
    {
        var normalized = IdentityRules.NormalizeUsername(username);
        if (!IdentityRules.IsValidUsername(normalized) || normalized == SystemUsername)
        {
            return IdentityErrors.InvalidUsername;
        }

        var user = new User(id, companyId, normalized, displayName.Trim(), UserKind.Human, UserStatus.Active) { Email = email?.Trim() };
        user.SetPassword(passwordHash, now, mustChangePassword);
        return user;
    }

    public void Update(string displayName, string? email, Guid? employeeId)
    {
        DisplayName = displayName.Trim();
        Email = email?.Trim();
        EmployeeId = employeeId;
    }

    public void SetPassword(string passwordHash, DateTimeOffset now, bool mustChangePassword)
    {
        PasswordHash = Guard(passwordHash);
        PasswordChangedAt = now;
        MustChangePassword = mustChangePassword;
        ClearLock();
        SecurityVersion++;
    }

    /// <summary>Recalcula el hash con parámetros actuales sin cambiar la contraseña (no invalida sesiones).</summary>
    public void RehashPassword(string passwordHash) => PasswordHash = Guard(passwordHash);

    public void RehashPin(string pinHash) => PinHash = Guard(pinHash);

    public Result SetPin(string posCode, string pinHash, DateTimeOffset now)
    {
        if (!IdentityRules.IsValidPosCode(posCode))
        {
            return IdentityErrors.InvalidPosCode;
        }

        PosCode = posCode;
        PinHash = Guard(pinHash);
        PinChangedAt = now;
        SecurityVersion++;
        return Result.Success();
    }

    /// <summary>¿Puede intentar entrar? Un bloqueo por intentos vence solo.</summary>
    public Result CanAuthenticate(DateTimeOffset now, bool withPin)
    {
        if (!IsHuman || Status == UserStatus.Disabled)
        {
            return IdentityErrors.InvalidCredentials;
        }

        if (Status == UserStatus.Locked)
        {
            if (LockedUntil is { } until && until <= now)
            {
                ClearLock();
            }
            else if (!(LockedReason == LockReason.PinAttempts && !withPin))
            {
                return IdentityErrors.UserLocked;
            }
        }

        return Result.Success();
    }

    /// <summary>Registra un intento fallido; al llegar al máximo bloquea por <paramref name="lockMinutes"/> (RN-SEC-02).</summary>
    public bool RecordFailure(DateTimeOffset now, int maxAttempts, int lockMinutes, bool withPin)
    {
        FailedLoginCount++;
        if (FailedLoginCount < maxAttempts)
        {
            return false;
        }

        Status = UserStatus.Locked;
        LockedReason = withPin ? LockReason.PinAttempts : LockReason.FailedAttempts;
        LockedUntil = now.AddMinutes(lockMinutes);
        return true;
    }

    public void RecordSuccess(DateTimeOffset now)
    {
        ClearLock();
        LastLoginAt = now;
    }

    public void Unlock() => ClearLock();

    public void Activate()
    {
        Status = UserStatus.Active;
        ClearLock();
        SecurityVersion++;
    }

    public void Deactivate()
    {
        Status = UserStatus.Disabled;
        LockedUntil = null;
        LockedReason = null;
        SecurityVersion++;
    }

    public void ReplaceRoles(IEnumerable<(Guid RoleId, Guid? BranchId)> roles, Func<Guid> newId, Guid grantedBy, DateTimeOffset now)
    {
        var wanted = roles.Distinct().ToList();
        _roles.RemoveAll(r => !wanted.Contains((r.RoleId, r.BranchId)));
        foreach (var (roleId, branchId) in wanted.Where(w => !_roles.Any(r => r.RoleId == w.RoleId && r.BranchId == w.BranchId)))
        {
            _roles.Add(new UserRole(newId(), roleId, branchId, now, grantedBy));
        }

        SecurityVersion++;
    }

    public void ReplaceOverrides(IEnumerable<(string Code, OverrideEffect Effect, Guid? BranchId, string Reason)> overrides, Func<Guid> newId)
    {
        _overrides.Clear();
        foreach (var o in overrides.DistinctBy(o => (o.Code, o.BranchId)))
        {
            _overrides.Add(new UserPermissionOverride(newId(), o.Code, o.Effect, o.BranchId, o.Reason.Trim()));
        }

        SecurityVersion++;
    }

    /// <summary>Invalida los permisos cacheados (p. ej. cuando cambian los permisos de uno de sus roles).</summary>
    public void BumpSecurityVersion() => SecurityVersion++;

    private void ClearLock()
    {
        if (Status == UserStatus.Locked)
        {
            Status = UserStatus.Active;
        }

        FailedLoginCount = 0;
        LockedUntil = null;
        LockedReason = null;
    }

    private static string Guard(string hash) =>
        string.IsNullOrWhiteSpace(hash) ? throw new DomainException("El hash de la credencial no puede estar vacío.") : hash;
}
