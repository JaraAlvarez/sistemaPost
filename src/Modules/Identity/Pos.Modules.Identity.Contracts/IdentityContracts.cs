using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Identity.Contracts;

/// <summary>Permisos del módulo Identity.</summary>
public static class IdentityPermissions
{
    public const string PermissionView = "identity.permission.view";
    public const string UserView = "identity.user.view";
    public const string UserManage = "identity.user.manage";
    public const string RoleManage = "identity.role.manage";
    public const string SessionRevoke = "identity.session.revoke";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(PermissionView, "Consultar el catálogo de permisos y los roles", isSensitive: false),
        new(UserView, "Consultar usuarios", isSensitive: false),
        new(UserManage, "Crear, editar, activar y desactivar usuarios; restablecer contraseña o PIN", isSensitive: true),
        new(RoleManage, "Crear, clonar y editar roles; asignar roles y excepciones", isSensitive: true),
        new(SessionRevoke, "Cerrar sesiones de otros usuarios", isSensitive: true),
    ];
}

/// <summary>Datos del Propietario que crea el asistente inicial.</summary>
public sealed record OwnerInput(string Username, string DisplayName, string Password, string? Email = null, string? PosCode = null, string? Pin = null);

/// <summary>
/// Alta de la estructura de identidad de una empresa nueva (lo usa el asistente inicial del módulo Organization):
/// usuario técnico <c>system</c>, los 7 roles de sistema y el usuario Propietario.
/// </summary>
public interface IIdentityProvisioning
{
    Task ProvisionCompanyAsync(Guid companyId, Guid systemUserId, CancellationToken cancellationToken = default);

    /// <summary>Valida la contraseña y el PIN y crea el Propietario (rol OWNER en todas las sucursales).</summary>
    Task<SharedKernel.Results.Result<Guid>> CreateOwnerAsync(Guid companyId, OwnerInput owner, CancellationToken cancellationToken = default);
}

/// <summary>Sesión válida resuelta a partir del token (la usa el middleware de autenticación del host).</summary>
public sealed record AuthenticatedSession(
    Guid SessionId,
    Guid UserId,
    string DisplayName,
    Guid CompanyId,
    Guid BranchId,
    Guid? PosTerminalId,
    bool IsTerminal,
    bool MustChangePassword,
    long SecurityVersion);

/// <summary>Versión de seguridad del usuario de la sesión actual (clave de la caché de permisos).</summary>
public interface ICurrentSecurityVersion
{
    long? SecurityVersion { get; }
}

/// <summary>Valida tokens de sesión opacos (D3-01).</summary>
public interface ISessionAuthenticator
{
    /// <summary>Sesión vigente para el token y el equipo desde el que llega la petición, o <c>null</c>.</summary>
    Task<AuthenticatedSession?> AuthenticateAsync(string token, IClientContext client, CancellationToken cancellationToken = default);
}

/// <summary>Revocación de sesiones desde otros módulos (p. ej. al revocar un equipo).</summary>
public interface ISessionRevoker
{
    Task<int> RevokeByDeviceAsync(Guid deviceId, string reason, CancellationToken cancellationToken = default);
}

/// <summary>Estado de la identidad de la instalación: ¿ya existe un Propietario activo? (en caché)</summary>
public interface IIdentityState
{
    Task<bool> IsOwnerPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Descarta la caché (p. ej. tras crear el Propietario).</summary>
    void Invalidate();
}

public sealed record RoleDto(Guid Id, string Code, string Name, string? Description, bool IsSystem, IReadOnlyList<string> Permissions);

public sealed record PermissionDto(string Code, string Module, string Description, bool IsSensitive, bool IsDeprecated);

public sealed record UserRoleDto(Guid RoleId, string RoleCode, Guid? BranchId);

public sealed record UserOverrideDto(string PermissionCode, string Effect, Guid? BranchId, string Reason);

public sealed record UserDto(
    Guid Id,
    string Username,
    string DisplayName,
    string? Email,
    string Status,
    string? PosCode,
    bool HasPin,
    bool MustChangePassword,
    Guid? EmployeeId,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<UserRoleDto> Roles,
    IReadOnlyList<UserOverrideDto> Overrides);

/// <summary>Perfil del usuario autenticado con sus permisos efectivos en la sucursal de la sesión.</summary>
public sealed record MeDto(
    Guid UserId,
    string Username,
    string DisplayName,
    Guid SessionId,
    string SessionKind,
    Guid BranchId,
    Guid? PosTerminalId,
    bool MustChangePassword,
    IReadOnlyList<string> Permissions,
    int OpenIntegrityIncidents = 0,
    int BackupAlerts = 0);

public sealed record LoginResultDto(string Token, DateTimeOffset ExpiresAt, int IdleTimeoutSeconds, MeDto User);

public sealed record SessionDto(
    Guid Id, Guid UserId, string Username, string Kind, Guid? DeviceId, Guid? PosTerminalId, string? IpAddress,
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, DateTimeOffset ExpiresAt);

public sealed record EmployeeDto(
    Guid Id, Guid? BranchId, string IdentificationType, string IdentificationNumber, string FirstName, string LastName,
    string? Phone, string? Email, string Status);

public sealed record AuthorizationGrantDto(Guid GrantId, string PermissionCode, string Action, Guid? TargetId, DateTimeOffset ExpiresAt, string AuthorizedBy);
