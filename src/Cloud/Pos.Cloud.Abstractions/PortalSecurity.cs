using Pos.SharedKernel.Results;

namespace Pos.Cloud.Abstractions;

/// <summary>
/// Roles del portal (L-08 y resolución de la propuesta): solo el equipo del propietario. El rol de distribuidor queda en el
/// modelo (con su cuenta) pero sin pantallas ni permisos en la Fase 12-A.
/// </summary>
public static class PortalRoles
{
    public const string Superadmin = "SUPERADMIN";
    public const string Support = "SUPPORT";
    public const string Reseller = "RESELLER";

    public static IReadOnlyList<string> All { get; } = [Superadmin, Support, Reseller];
}

/// <summary>
/// Permisos del portal y de la API interna <c>/admin</c> (formato <c>modulo.recurso.accion</c>). La matriz por rol vive aquí para
/// que las pantallas, los endpoints y el pipeline de casos de uso apliquen exactamente la misma regla.
/// </summary>
public static class CloudPermissions
{
    public const string DashboardView = "licensing.dashboard.view";
    public const string LicensingView = "licensing.data.view";
    public const string AccountManage = "licensing.account.manage";

    /// <summary>Crear, renovar, suspender, cancelar y cambiar la edición de una suscripción.</summary>
    public const string SubscriptionManage = "licensing.subscription.manage";

    /// <summary>Reactivar una suscripción suspendida y extender la gracia (Soporte).</summary>
    public const string SubscriptionSupport = "licensing.subscription.support";

    /// <summary>Generar, regenerar y revocar claves; fijar el máximo de instalaciones.</summary>
    public const string LicenseManage = "licensing.license.manage";

    public const string DeviceRelease = "licensing.device.release";
    public const string SigningKeyView = "licensing.signing_key.view";

    /// <summary>Publicar una clave de reserva y revocar una clave comprometida desde el portal (solo superadministrador).</summary>
    public const string SigningKeyManage = "licensing.signing_key.manage";
    public const string PortalUserManage = "portal.user.manage";
    public const string AuditView = "portal.audit.view";

    private static readonly HashSet<string> SupportPermissions =
        [DashboardView, LicensingView, SubscriptionSupport, DeviceRelease, AuditView];

    private static readonly HashSet<string> SuperadminPermissions =
    [
        DashboardView, LicensingView, AccountManage, SubscriptionManage, SubscriptionSupport, LicenseManage, DeviceRelease,
        SigningKeyView, SigningKeyManage, PortalUserManage, AuditView,
    ];

    public static IReadOnlyCollection<string> All => SuperadminPermissions;

    public static IReadOnlySet<string> ForRole(string? role) => role switch
    {
        PortalRoles.Superadmin => SuperadminPermissions,
        PortalRoles.Support => SupportPermissions,
        _ => new HashSet<string>(),
    };

    public static bool RoleHas(string? role, string permission) => ForRole(role).Contains(permission);
}

/// <summary>El caso de uso exige este permiso del portal (lo verifica el pipeline, también cuando lo invoca una pantalla).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RequiresPermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

/// <summary>Usuario del portal en curso (sesión con el segundo factor completo). Fuera del portal no hay usuario.</summary>
public interface IPortalUserContext
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    string? DisplayName { get; }

    string? Role { get; }

    Guid? SessionId { get; }
}

/// <summary>
/// Usuario técnico <c>system</c> del portal (creado por la migración, deshabilitado): autor de los cambios automáticos
/// (vencimientos, check-ins, activaciones desde el POS) y de la línea base.
/// </summary>
public static class SystemActor
{
    public static readonly Guid Id = new("01926a00-0000-7000-8000-000000000001");

    public const string DisplayName = "Sistema";
}

/// <summary>
/// Resultado de un caso de uso que debe confirmar su transacción aunque la operación sea rechazada (el intento fallido, el
/// bloqueo o el check-in rechazado quedan registrados). La API traduce <see cref="Error"/> a HTTP.
/// </summary>
public sealed record Outcome<T>(T? Value, Error? Error)
    where T : class
{
    public bool Succeeded => Error is null;
}

public static class Outcome
{
    public static Outcome<T> Ok<T>(T value)
        where T : class => new(value, null);

    public static Outcome<T> Fail<T>(Error error)
        where T : class => new(null, error);
}
