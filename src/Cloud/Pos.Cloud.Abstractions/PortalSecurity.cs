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

    /// <summary>Cliente (dueño del supermercado, Fase 16): consulta los datos sincronizados de sus empresas y carga paquetes.</summary>
    public const string Customer = "CUSTOMER";

    public static IReadOnlyList<string> All { get; } = [Superadmin, Support, Reseller, Customer];
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

    /// <summary>"Reiniciar la demo de este equipo" (demo por equipo; solo superadministrador, auditado).</summary>
    public const string DemoReset = "licensing.demo.reset";
    public const string SigningKeyView = "licensing.signing_key.view";

    /// <summary>Publicar una clave de reserva y revocar una clave comprometida desde el portal (solo superadministrador).</summary>
    public const string SigningKeyManage = "licensing.signing_key.manage";
    public const string PortalUserManage = "portal.user.manage";
    public const string AuditView = "portal.audit.view";

    /// <summary>Ver las ventas, cierres, existencias y el estado de sincronización (Fase 16). El cliente, solo de su cuenta.</summary>
    public const string SyncDataView = "sync.data.view";

    /// <summary>Cargar un paquete .possync exportado por una tienda sin Internet.</summary>
    public const string SyncPackageUpload = "sync.package.upload";

    /// <summary>
    /// Verificar o descartar paquetes cargados sin verificar la firma y reiniciar la clave de firma de una tienda (revisión S2; solo
    /// superadministrador).
    /// </summary>
    public const string SyncPackageReview = "sync.package.review";

    /// <summary>Ver los pagos en línea, los enlaces de pago y los precios; "Consultar ahora" una orden en la pasarela (Fase 12-C).</summary>
    public const string PaymentView = "licensing.payment.view";

    /// <summary>"Marcar revisado" un pago y regenerar el enlace de pago de una suscripción (solo superadministrador).</summary>
    public const string PaymentManage = "licensing.payment.manage";

    /// <summary>Fijar los precios de las ediciones (solo superadministrador, D12C-02).</summary>
    public const string PriceManage = "licensing.price.manage";

    /// <summary>"Mi suscripción": el cliente ve el vencimiento y los pagos de SUS empresas y paga en línea (D12C-09).</summary>
    public const string SubscriptionSelfView = "licensing.subscription.self";

    /// <summary>"Descargas" (Fase 13-B, D13B-05): instalador de la última versión publicada y manual de instalación.</summary>
    public const string DownloadView = "portal.download.view";

    private static readonly HashSet<string> SupportPermissions =
        [DashboardView, LicensingView, SubscriptionSupport, DeviceRelease, AuditView, SyncDataView, SyncPackageUpload, PaymentView, DownloadView];

    private static readonly HashSet<string> CustomerPermissions = [SyncDataView, SyncPackageUpload, SubscriptionSelfView, DownloadView];

    private static readonly HashSet<string> SuperadminPermissions =
    [
        DashboardView, LicensingView, AccountManage, SubscriptionManage, SubscriptionSupport, LicenseManage, DeviceRelease, DemoReset,
        SigningKeyView, SigningKeyManage, PortalUserManage, AuditView, SyncDataView, SyncPackageUpload, SyncPackageReview, PaymentView, PaymentManage,
        PriceManage, DownloadView,
    ];

    private static readonly HashSet<string> AllPermissions = [.. SuperadminPermissions, .. CustomerPermissions, .. SupportPermissions];

    /// <summary>Todos los permisos que existen (cada uno tiene su política de autorización).</summary>
    public static IReadOnlyCollection<string> All => AllPermissions;

    public static IReadOnlySet<string> ForRole(string? role) => role switch
    {
        PortalRoles.Superadmin => SuperadminPermissions,
        PortalRoles.Support => SupportPermissions,
        PortalRoles.Customer => CustomerPermissions,
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

/// <summary>Resultado de validar la cuenta del portal del dueño de una empresa (D13B-09).</summary>
public enum PortalCustomerCheck
{
    Valid,

    /// <summary>Correo inexistente, usuario no humano, deshabilitado o contraseña incorrecta (la misma respuesta).</summary>
    InvalidCredentials,

    Locked,

    /// <summary>Contraseña correcta, pero no es un usuario CLIENTE de la cuenta dueña de la empresa.</summary>
    NotOwner,

    /// <summary>Tiene el doble factor activo (u obligatorio) y falta el código o no es correcto.</summary>
    TotpRequired,

    /// <summary>La contraseña del portal sigue siendo la temporal.</summary>
    PasswordChangeRequired,

    /// <summary>Autorizar en el navegador exige un ingreso reciente (contraseña o Google): la sesión es antigua.</summary>
    ReauthenticationRequired,

    /// <summary>El TOTP es obligatorio (portal con <c>RequireTotp</c>) pero el usuario aún no activó su autenticador.</summary>
    TotpNotEnrolled,
}

/// <summary>
/// Usuario CLIENTE validado: correo (en minúsculas) y nombre para mostrar. <c>TotpRequired</c> (autorización en el navegador): el
/// usuario debe escribir su código TOTP para autorizar (lo tiene activo o el portal lo exige), sin importar si entró con Google.
/// </summary>
/// <param name="Result">Resultado de la verificación.</param>
/// <param name="Email">Correo del usuario (si es válido).</param>
/// <param name="DisplayName">Nombre del usuario (si es válido).</param>
/// <param name="TotpRequired">Si hace falta el código TOTP.</param>
/// <param name="AccountId">Cuenta del usuario Cliente (la que se verificó, o la suya cuando no se pidió una: primer ingreso sin clave).</param>
public sealed record PortalCustomerVerification(
    PortalCustomerCheck Result, string? Email = null, string? DisplayName = null, bool TotpRequired = false, Guid? AccountId = null);

/// <summary>
/// Lo implementa el módulo de usuarios del portal; lo usa el de licencias para el instalador premium sin depender de él. Cuenta los
/// intentos fallidos y bloquea igual que el ingreso al portal, y deja todo en la auditoría (el llamador confirma la transacción aunque
/// el resultado sea un rechazo).
/// </summary>
public interface IPortalCustomerVerifier
{
    /// <summary>
    /// Correo y contraseña de un usuario Cliente. Con <paramref name="accountId"/>, debe ser de esa cuenta; sin ella (primer ingreso a una
    /// tienda sin clave), basta con que sea Cliente de alguna cuenta, que se devuelve en <see cref="PortalCustomerVerification.AccountId"/>.
    /// </summary>
    Task<PortalCustomerVerification> VerifyAsync(
        string email, string password, string? totpCode, Guid? accountId, string organizationLabel, System.Net.IPAddress? ipAddress, CancellationToken cancellationToken);

    /// <summary>
    /// "Continuar con Google" del instalador: el usuario YA entró al portal (contraseña o Google, con su sesión normal) y autoriza en el
    /// navegador. Las mismas reglas de pertenencia: humano, activo, sin bloqueo, rol CLIENTE y atado a la cuenta dueña de la empresa;
    /// además la sesión <paramref name="sessionId"/> debe ser RECIENTE (si no, <c>ReauthenticationRequired</c>). Indica si el TOTP es
    /// necesario para autorizar (se verifica aparte con <see cref="VerifySignedInTotpAsync"/>). <c>NotOwner</c> solo se audita cuando
    /// <paramref name="deciding"/> (un POST de decisión): abrir la página (GET) no escribe en la auditoría.
    /// </summary>
    Task<PortalCustomerVerification> VerifySignedInAsync(
        Guid userId, Guid? sessionId, Guid accountId, string organizationLabel, bool deciding, System.Net.IPAddress? ipAddress, CancellationToken cancellationToken);

    /// <summary>
    /// Código TOTP para autorizar en el navegador (si el usuario lo tiene activo o el portal lo exige, también tras entrar con Google).
    /// Un código incorrecto cuenta como intento fallido (bloqueo tras N), igual que el ingreso. <c>Valid</c> si no se necesita.
    /// </summary>
    Task<PortalCustomerCheck> VerifySignedInTotpAsync(
        Guid userId, string? totpCode, string organizationLabel, System.Net.IPAddress? ipAddress, CancellationToken cancellationToken);
}

/// <summary>Instalación del POS autenticada con su token de licencia y la huella de su equipo (sincronización, Fase 16, D16-03).</summary>
public sealed record PosInstallationIdentity(Guid InstallationId, Guid OrganizationId, Guid AccountId, string OrganizationName, string? BranchName);

/// <summary>Lo implementa el módulo de licencias; lo usan los módulos que reciben datos de las tiendas sin depender de él.</summary>
public interface IPosInstallationAuthenticator
{
    /// <summary><c>null</c> si el token no es válido, la licencia no está activa o el equipo no es el activado.</summary>
    Task<PosInstallationIdentity?> AuthenticateAsync(string token, string fingerprint, CancellationToken cancellationToken);

    /// <summary>
    /// La instalación (sin autenticar el equipo): para los paquetes cargados en el portal, que llegan cifrados para la nube. <c>null</c> si no
    /// existe, fue liberada o su licencia no está activa.
    /// </summary>
    Task<PosInstallationIdentity?> FindAsync(Guid installationId, CancellationToken cancellationToken);

    /// <summary>Por qué <see cref="FindAsync"/> no devuelve la instalación (<see cref="PosInstallationInactiveReasons"/>); nulo si está activa.</summary>
    Task<string?> InactiveReasonAsync(Guid installationId, CancellationToken cancellationToken);
}

/// <summary>Motivos por los que una instalación no recibe paquetes.</summary>
public static class PosInstallationInactiveReasons
{
    public const string NotFound = "NOT_FOUND";
    public const string Released = "RELEASED";
    public const string LicenseInactive = "LICENSE_INACTIVE";
}
