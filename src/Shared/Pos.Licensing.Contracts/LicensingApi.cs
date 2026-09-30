namespace Pos.Licensing.Contracts;

/// <summary>Rutas de la API de licencias que usa el POS (docs/fases/fase-12a-propuesta.md §7).</summary>
public static class LicensingRoutes
{
    public const string Activations = "/v1/activations";
    public const string Checkins = "/v1/checkins";
    public const string Deactivations = "/v1/deactivations";
    public const string PublicKeys = "/v1/public-keys";
}

/// <summary>
/// Códigos de error estables de la API de licencias. Son un contrato con el POS (12-B): el mensaje puede cambiar, el código no.
/// </summary>
public static class LicenseErrorCodes
{
    /// <summary>La clave no existe o fue revocada (la respuesta es idéntica en ambos casos: no se enumeran claves).</summary>
    public const string KeyInvalid = "LICENSE.KEY_INVALID";

    /// <summary>La clave no tiene el formato <c>POS-XXXXX-XXXXX-XXXXX-XXXXX</c> o su dígito de control no coincide.</summary>
    public const string KeyFormatInvalid = "LICENSE.KEY_FORMAT_INVALID";

    /// <summary>El rol del equipo no corresponde a la edición (Caja Única solo admite <c>ALL_IN_ONE</c>).</summary>
    public const string EditionMismatch = "LICENSE.EDITION_MISMATCH";

    /// <summary>La licencia llegó al máximo de instalaciones (sucursales) configurado.</summary>
    public const string InstallationsExceeded = "LICENSE.INSTALLATIONS_EXCEEDED";

    /// <summary>La suscripción está suspendida, cancelada o vencida sin gracia: no se activan instalaciones nuevas.</summary>
    public const string SubscriptionInactive = "LICENSE.SUBSCRIPTION_INACTIVE";

    /// <summary>El NIT declarado por el POS no es el de la empresa licenciada.</summary>
    public const string NitMismatch = "LICENSE.NIT_MISMATCH";

    /// <summary>La huella no coincide (menos de 2 de 3) o el equipo fue liberado: hay que activar de nuevo con la clave.</summary>
    public const string ReactivationRequired = "LICENSE.REACTIVATION_REQUIRED";

    /// <summary>La instalación ya está activa en otro equipo: libere el anterior desde el portal (cambio de PC).</summary>
    public const string InstallationActiveOnOtherDevice = "LICENSE.INSTALLATION_ACTIVE_ON_OTHER_DEVICE";

    /// <summary>La instalación pertenece a la licencia de otra empresa.</summary>
    public const string InstallationOfOtherLicense = "LICENSE.INSTALLATION_OF_OTHER_LICENSE";

    /// <summary>El token no es válido (firma, formato o clave de firma desconocida).</summary>
    public const string TokenInvalid = "LICENSE.TOKEN_INVALID";

    /// <summary>La huella enviada no tiene el formato esperado o tiene menos de 2 componentes.</summary>
    public const string FingerprintInvalid = "LICENSE.FINGERPRINT_INVALID";

    /// <summary>La licencia de la instalación fue revocada (sin reemplazo): el POS pasa a restringido.</summary>
    public const string LicenseRevoked = "LICENSE.REVOKED";

    /// <summary>Demasiadas peticiones para esta licencia; reintentar más tarde.</summary>
    public const string TooManyRequests = "LICENSE.TOO_MANY_REQUESTS";

    /// <summary>El servidor no tiene cargada su clave de firma (configuración); reintentar más tarde.</summary>
    public const string SigningUnavailable = "LICENSE.SIGNING_UNAVAILABLE";
}

/// <summary>Activación de una instalación con la clave de licencia (asistente inicial del POS o simulador).</summary>
/// <param name="LicenseKey">Clave tal como la escribió el usuario (se normaliza).</param>
/// <param name="InstallationId">Id de la instalación (sucursal), generado por el instalador del POS.</param>
/// <param name="Fingerprint">Huella del equipo (<see cref="DeviceFingerprint"/>).</param>
/// <param name="DeviceRole"><see cref="DeviceRoles"/>.</param>
/// <param name="AppVersion">Versión del POS.</param>
/// <param name="OrganizationNit">NIT de la empresa configurada en el POS, con DV: <c>900123456-7</c>.</param>
/// <param name="BranchName">Nombre de la sucursal declarado en el POS.</param>
/// <param name="DeviceName">Nombre del equipo.</param>
/// <param name="OperatingSystem">Sistema operativo del equipo.</param>
public sealed record ActivationRequest(
    string LicenseKey,
    Guid InstallationId,
    string Fingerprint,
    string DeviceRole,
    string AppVersion,
    string OrganizationNit,
    string? BranchName = null,
    string? DeviceName = null,
    string? OperatingSystem = null);

/// <summary>Check-in diario: el token vigente (aunque haya vencido) autentica la instalación junto con la huella actual.</summary>
/// <param name="Token">Último token recibido.</param>
/// <param name="Fingerprint">Huella actual del equipo.</param>
/// <param name="AppVersion">Versión del POS.</param>
/// <param name="ActiveTerminals">Cajas activas en la tienda (detección de abuso; sin límite por edición).</param>
/// <param name="ReportedClock">Reloj del equipo (detección de relojes atrasados).</param>
/// <param name="AuditSeal">Último sello de la auditoría del POS (ancla externa, ADR-0048). Opcional: los POS anteriores a 12-B no lo envían.</param>
public sealed record CheckinRequest(
    string Token, string Fingerprint, string AppVersion, int ActiveTerminals, DateTimeOffset ReportedClock, CheckinAuditSeal? AuditSeal = null);

/// <summary>
/// Sello de la bitácora de auditoría del POS (docs/fases/fase-12b-propuesta.md §8): si alguien reescribe la auditoría de la tienda, el
/// último sello guardado en la nube deja de coincidir.
/// </summary>
/// <param name="NodeId">Nodo (cadena de auditoría) que selló.</param>
/// <param name="SealNo">Número del sello.</param>
/// <param name="SealCode">Código corto del sello (<c>7F3A-91C2-0B44-E1D8</c>).</param>
/// <param name="SealedAt">Momento del sello.</param>
public sealed record CheckinAuditSeal(Guid NodeId, long SealNo, string SealCode, DateTimeOffset SealedAt);

/// <summary>Liberación del equipo desde el propio POS (p. ej. antes de cambiar de computador).</summary>
public sealed record DeactivationRequest(string Token, string Fingerprint, string? Reason = null);

/// <summary>Token emitido y un resumen legible (el POS confía solo en lo firmado dentro del token).</summary>
public sealed record LicenseTokenResponse(
    string Token,
    string Kid,
    string SubscriptionStatus,
    DateTimeOffset ValidUntil,
    int GraceDays,
    DateTimeOffset RefreshAfter,
    IReadOnlyList<LicenseMessage> Messages);

/// <summary>Clave pública publicada, con la forma de un JWK OKP (RFC 8037).</summary>
/// <param name="Kid">Identificador de la clave.</param>
/// <param name="Kty">Siempre <c>OKP</c>.</param>
/// <param name="Crv">Siempre <c>Ed25519</c>.</param>
/// <param name="X">Clave pública cruda en base64url.</param>
/// <param name="Status"><c>ACTIVE</c> (firma hoy), <c>STANDBY</c> (reserva publicada) o <c>RETIRED</c> (verifica tokens antiguos).</param>
public sealed record PublicKeyDto(string Kid, string Kty, string Crv, string X, string Status);

public sealed record PublicKeysResponse(IReadOnlyList<PublicKeyDto> Keys);
