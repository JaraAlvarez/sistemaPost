using System.Net;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Identity.Domain;

namespace Pos.Modules.Identity.Application;

/// <summary>Puerto de persistencia de usuarios, roles y empleados.</summary>
public interface IIdentityStore
{
    void Add(User user);

    void Add(Role role);

    void Add(Employee employee);

    void Remove(Role role);

    /// <summary>
    /// Guarda el estado de autenticación (intentos, bloqueo, último acceso, rehash) con una actualización directa, sin
    /// concurrencia optimista: dos ingresos simultáneos del mismo usuario no deben chocar. El resto de cambios del
    /// usuario sigue el camino normal.
    /// </summary>
    Task SaveAuthenticationStateAsync(User user, CancellationToken cancellationToken);

    Task<User?> GetUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken);

    Task<User?> FindByPosCodeAsync(string posCode, CancellationToken cancellationToken);

    Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken);

    Task<bool> PosCodeExistsAsync(string posCode, Guid? exceptUserId, CancellationToken cancellationToken);

    Task<Role?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken);

    Task<Role?> GetRoleByCodeAsync(string code, CancellationToken cancellationToken);

    Task<IReadOnlyList<Role>> GetRolesAsync(IEnumerable<Guid> roleIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<Role>> GetSystemRolesAsync(CancellationToken cancellationToken);

    Task<bool> RoleCodeExistsAsync(string code, CancellationToken cancellationToken);

    Task<Employee?> GetEmployeeAsync(Guid employeeId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(CancellationToken cancellationToken);

    /// <summary>Códigos vigentes (no obsoletos) del catálogo de la BD.</summary>
    Task<IReadOnlyList<string>> ListActivePermissionCodesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken cancellationToken);

    Task<UserDto?> GetUserDtoAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EmployeeDto>> ListEmployeesAsync(CancellationToken cancellationToken);

    /// <summary>Usuarios humanos activos con rol OWNER o ADMIN para todas las sucursales, sin contar <paramref name="excludingUserId"/>.</summary>
    Task<int> CountActiveAdministratorsAsync(Guid? excludingUserId, CancellationToken cancellationToken);

    Task<bool> HasActiveOwnerAsync(CancellationToken cancellationToken);

    /// <summary>Invalida los permisos cacheados de todos los usuarios que tienen el rol.</summary>
    Task BumpSecurityVersionForRoleAsync(Guid roleId, CancellationToken cancellationToken);

    void AddPasswordHistory(Guid historyId, Guid userId, string passwordHash, DateTimeOffset now);

    Task<IReadOnlyList<string>> RecentPasswordHashesAsync(Guid userId, int count, CancellationToken cancellationToken);
}

/// <summary>Datos de una sesión nueva.</summary>
public sealed record NewSession(
    Guid Id,
    Guid CompanyId,
    Guid NodeId,
    Guid UserId,
    string TokenHash,
    bool IsTerminal,
    Guid? DeviceId,
    Guid? PosTerminalId,
    Guid BranchId,
    IPAddress? IpAddress,
    string? UserAgent,
    long SecurityVersion,
    int IdleTimeoutSeconds,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Sesiones (locales del nodo).</summary>
public interface ISessionStore
{
    void Add(NewSession session);

    Task<bool> RevokeAsync(Guid sessionId, Guid? revokedBy, string reason, CancellationToken cancellationToken);

    /// <summary>Revoca las sesiones activas del usuario, salvo <paramref name="exceptSessionId"/>.</summary>
    Task<int> RevokeAllForUserAsync(Guid userId, Guid? revokedBy, string reason, Guid? exceptSessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionDto>> ListActiveAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Intento de acceso (se guarda aunque falle: el caso de uso devuelve el error dentro de un resultado exitoso).</summary>
public sealed record LoginAttempt(
    Guid Id, Guid? CompanyId, Guid NodeId, DateTimeOffset OccurredAt, string Kind, string Identifier, Guid? UserId,
    Guid? DeviceId, IPAddress? IpAddress, bool Succeeded, string? FailureReason);

public interface ILoginAttemptLog
{
    void Add(LoginAttempt attempt);
}

/// <summary>Autorización de supervisor nueva.</summary>
public sealed record NewAuthorizationGrant(
    Guid Id, Guid CompanyId, Guid NodeId, string PermissionCode, Guid RequestedBy, Guid AuthorizedBy, Guid? PosTerminalId,
    string Action, string? TargetType, Guid? TargetId, string? Reason, DateTimeOffset GrantedAt, DateTimeOffset ExpiresAt);

public interface IAuthorizationGrantStore
{
    void Add(NewAuthorizationGrant grant);
}

/// <summary>Permisos efectivos (D3-06), con caché por versión de seguridad.</summary>
public interface IPermissionEvaluator
{
    Task<IReadOnlySet<string>> GetEffectiveAsync(Guid userId, Guid? branchId, long? securityVersion, CancellationToken cancellationToken);
}

/// <summary>Parámetros de seguridad (docs/fases/fase-03-propuesta.md §5). Solo por empresa, salvo la inactividad de caja.</summary>
public static class SecuritySettings
{
    public static readonly SettingDefinition<int> BackofficeIdleMinutes = new(
        "security.backoffice_idle_minutes", 30, SettingScope.Company, "Minutos de inactividad que cierran una sesión de backoffice.", Between(5, 480));

    public static readonly SettingDefinition<int> TerminalIdleMinutes = new(
        "security.terminal_idle_minutes", 15, SettingScope.Company | SettingScope.Branch,
        "Minutos de inactividad que cierran una sesión de caja (la venta en curso se conserva).", Between(1, 240));

    public static readonly SettingDefinition<int> SessionMaxHours = new(
        "security.session_max_hours", 12, SettingScope.Company, "Duración máxima de una sesión.", Between(1, 24));

    public static readonly SettingDefinition<int> MaxFailedAttempts = new(
        "security.max_failed_attempts", 5, SettingScope.Company, "Intentos fallidos de contraseña antes de bloquear.", Between(3, 20));

    public static readonly SettingDefinition<int> MaxPinAttempts = new(
        "security.max_pin_attempts", 3, SettingScope.Company, "Intentos fallidos de PIN antes de bloquear el PIN.", Between(3, 10));

    public static readonly SettingDefinition<int> LockoutMinutes = new(
        "security.lockout_minutes", 15, SettingScope.Company, "Minutos de bloqueo por intentos fallidos.", Between(1, 1440));

    public static readonly SettingDefinition<int> PasswordMinLength = new(
        "security.password_min_length", 8, SettingScope.Company, "Longitud mínima de las contraseñas.", Between(8, 64));

    public static readonly SettingDefinition<int> PasswordHistory = new(
        "security.password_history", 5, SettingScope.Company, "Contraseñas anteriores que no se pueden reutilizar.", Between(0, 24));

    public static readonly SettingDefinition<int> PinLength = new(
        "security.pin_length", 4, SettingScope.Company, "Dígitos del PIN de caja.", Between(4, 6));

    public static readonly SettingDefinition<int> SupervisorGrantSeconds = new(
        "security.supervisor_grant_seconds", 120, SettingScope.Company, "Vigencia de una autorización de supervisor.", Between(30, 600));

    public static IEnumerable<SettingDefinition> All =>
    [
        BackofficeIdleMinutes, TerminalIdleMinutes, SessionMaxHours, MaxFailedAttempts, MaxPinAttempts, LockoutMinutes,
        PasswordMinLength, PasswordHistory, PinLength, SupervisorGrantSeconds,
    ];

    private static Func<int, string?> Between(int min, int max) => v => v < min || v > max ? $"Debe estar entre {min} y {max}." : null;
}

public sealed class SecuritySettingsProvider : ISettingDefinitionProvider
{
    public IEnumerable<SettingDefinition> GetDefinitions() => SecuritySettings.All;
}
