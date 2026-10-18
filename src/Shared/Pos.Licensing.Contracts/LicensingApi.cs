namespace Pos.Licensing.Contracts;

/// <summary>Rutas de la API de licencias que usa el POS (docs/fases/fase-12a-propuesta.md §7).</summary>
public static class LicensingRoutes
{
    public const string Activations = "/v1/activations";
    public const string Checkins = "/v1/checkins";
    public const string Deactivations = "/v1/deactivations";
    public const string PublicKeys = "/v1/public-keys";

    /// <summary>Demo por equipo: la nube guarda el primer inicio de la demo por huella y lo devuelve firmado (<see cref="DemoToken"/>).</summary>
    public const string DemoDevices = "/v1/demo-devices";

    /// <summary>Instalador premium (Fase 13-B): datos públicos de la empresa de una clave, para no volver a escribirlos.</summary>
    public const string LicensePreviews = "/v1/license-previews";

    /// <summary>Instalador premium (Fase 13-B, D13B-09): valida la cuenta del portal del dueño y entrega el enlace firmado.</summary>
    public const string OwnerLinks = "/v1/owner-links";

    /// <summary>
    /// Instalador premium: "Continuar con Google" (ingreso en el navegador del sistema, tipo "código de dispositivo"). El instalador
    /// crea la solicitud con la clave, abre <c>BrowserUrl</c> y consulta <see cref="OwnerLinkRequestPoll"/> hasta que el dueño la autoriza.
    /// </summary>
    public const string OwnerLinkRequests = "/v1/owner-link-requests";

    /// <summary>Consulta del estado de una solicitud (POST con el <c>PollToken</c> en el cuerpo, nunca en la URL).</summary>
    public static string OwnerLinkRequestPoll(Guid requestId) => $"{OwnerLinkRequests}/{requestId:D}/poll";
}

/// <summary>Estados de una solicitud de "Continuar con Google" vistos por el instalador.</summary>
public static class OwnerLinkRequestStatuses
{
    /// <summary>El dueño todavía no autoriza en el navegador: seguir consultando.</summary>
    public const string Pending = "PENDING";

    /// <summary>Autorizada: la respuesta trae el enlace firmado (una sola vez).</summary>
    public const string Approved = "APPROVED";

    /// <summary>El dueño la canceló en el navegador.</summary>
    public const string Denied = "DENIED";

    /// <summary>Pasaron los minutos de vigencia sin terminar: hay que empezar de nuevo.</summary>
    public const string Expired = "EXPIRED";
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

    /// <summary>Cuenta del portal: correo o contraseña incorrectos (misma respuesta si el correo no existe).</summary>
    public const string OwnerCredentialsInvalid = "LICENSE.OWNER_CREDENTIALS_INVALID";

    /// <summary>Cuenta del portal bloqueada un rato por intentos fallidos.</summary>
    public const string OwnerLocked = "LICENSE.OWNER_LOCKED";

    /// <summary>La cuenta existe y la contraseña es correcta, pero no es un CLIENTE dueño de la empresa de esta clave.</summary>
    public const string OwnerNotLinked = "LICENSE.OWNER_NOT_LINKED";

    /// <summary>La cuenta tiene el doble factor activo: falta (o no es correcto) el código de la aplicación autenticadora.</summary>
    public const string OwnerTotpRequired = "LICENSE.OWNER_TOTP_REQUIRED";

    /// <summary>La contraseña del portal todavía es la temporal: hay que cambiarla primero en el portal.</summary>
    public const string OwnerPasswordChangeRequired = "LICENSE.OWNER_PASSWORD_CHANGE_REQUIRED";

    /// <summary>"Continuar con Google": la solicitud no existe o el token de consulta no es el suyo (misma respuesta).</summary>
    public const string OwnerLinkRequestNotFound = "LICENSE.OWNER_LINK_REQUEST_NOT_FOUND";

    /// <summary>"Continuar con Google": el enlace de esta solicitud ya se entregó (un solo uso).</summary>
    public const string OwnerLinkRequestUsed = "LICENSE.OWNER_LINK_REQUEST_USED";

    /// <summary>"Continuar con Google": la solicitud ya no está pendiente (venció, se autorizó o se canceló).</summary>
    public const string OwnerLinkRequestClosed = "LICENSE.OWNER_LINK_REQUEST_CLOSED";

    /// <summary>Consultas demasiado seguidas (HTTP 429, como <c>slow_down</c> de OAuth): esperar el nuevo intervalo.</summary>
    public const string OwnerLinkSlowDown = "LICENSE.OWNER_LINK_SLOW_DOWN";
}

/// <summary>Vista previa de la clave en el instalador (Fase 13-B, D13B-08).</summary>
public sealed record LicensePreviewRequest(string LicenseKey);

/// <summary>
/// Solo lo necesario para mostrar la empresa en el instalador: nada secreto (ni ids, ni la clave, ni datos de contacto). Solo se
/// entrega para una clave vigente cuya suscripción está ACTIVE o TRIAL.
/// </summary>
/// <param name="LegalName">Razón social.</param>
/// <param name="Nit">NIT con DV: <c>900123456-7</c>.</param>
/// <param name="Edition"><see cref="LicenseEditions"/>.</param>
/// <param name="SubscriptionStatus"><see cref="SubscriptionStatuses"/> (ACTIVE o TRIAL).</param>
/// <param name="ValidUntil">Fin del periodo pagado o de la prueba.</param>
/// <param name="City">Ciudad registrada en la nube (ayuda a buscar el municipio), si la hay.</param>
/// <param name="BusinessProfile">
/// Tipo de negocio de la empresa en el portal (<see cref="BusinessProfileCodes"/>), si lo tiene: el instalador premium crea la tienda con
/// ese tipo (si no, SUPERMARKET). Opcional: los instaladores anteriores lo ignoran.
/// </param>
public sealed record LicensePreviewResponse(
    string LegalName, string Nit, string Edition, string SubscriptionStatus, DateTimeOffset ValidUntil, string? City, string? BusinessProfile = null);

/// <summary>
/// Correo y contraseña del portal del dueño (y el código TOTP si lo tiene activo), con la clave que identifica la empresa. Sin clave
/// (primer ingreso a una tienda recién instalada, instalador 1.2.1): la empresa es la de la cuenta del usuario del portal, si la tiene.
/// </summary>
public sealed record OwnerLinkRequest(string? LicenseKey, string Email, string Password, string? TotpCode = null);

/// <summary>
/// Enlace firmado (<see cref="OwnerLinkToken"/>) para crear al Propietario del POS con la cuenta del portal. <c>OrganizationName</c>,
/// <c>OrganizationNit</c> y <c>HasLicense</c> (opcionales): la empresa de la cuenta y si tiene una licencia vigente; con ella el POS se
/// activa con el mismo enlace (<see cref="ActivationRequest.OwnerLinkToken"/>). Sin empresa ni licencia, la tienda sigue en la prueba.
/// </summary>
public sealed record OwnerLinkResponse(
    string Token, string Email, string DisplayName, DateTimeOffset ExpiresAt, string? OrganizationName = null, string? OrganizationNit = null,
    bool HasLicense = false);

/// <summary>"Continuar con Google" desde el instalador: la clave identifica la empresa (misma búsqueda y límites que la vista previa).</summary>
public sealed record OwnerLinkRequestStart(string LicenseKey);

/// <summary>Solicitud creada.</summary>
/// <param name="RequestId">Identificador de la solicitud (va en la dirección del navegador y en la ruta de consulta).</param>
/// <param name="PollToken">Secreto de consulta (32 bytes aleatorios en base64url): solo lo tiene el instalador; la nube guarda su hash.</param>
/// <param name="BrowserUrl">Página del portal que el instalador abre en el navegador del sistema (siempre del mismo servidor).</param>
/// <param name="UserCode">Código corto que se muestra en el instalador y en el portal para comparar (<c>XXXX-XXXX</c>).</param>
/// <param name="ExpiresIn">Segundos de vigencia de la solicitud.</param>
/// <param name="Interval">Segundos mínimos entre consultas.</param>
public sealed record OwnerLinkRequestStarted(Guid RequestId, string PollToken, string BrowserUrl, string UserCode, int ExpiresIn, int Interval);

/// <summary>Consulta del estado de la solicitud.</summary>
public sealed record OwnerLinkPoll(string PollToken);

/// <summary>Estado de la solicitud (<see cref="OwnerLinkRequestStatuses"/>); si es APPROVED trae el MISMO enlace firmado de <c>/v1/owner-links</c>.</summary>
public sealed record OwnerLinkPollResponse(string Status, int Interval, OwnerLinkResponse? Link = null);

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
/// <param name="BusinessProfile">
/// Tipo de negocio de la tienda (Fase 7-C parte A): <see cref="BusinessProfileCodes"/>. Opcional: los POS anteriores no lo envían.
/// </param>
/// <param name="OwnerLinkToken">
/// Enlace firmado del propietario (opcional, sin <paramref name="LicenseKey"/>): primer ingreso con la cuenta del portal, cuya empresa
/// tiene una licencia vigente. La nube activa esa licencia sin que el dueño escriba la clave.
/// </param>
public sealed record ActivationRequest(
    string LicenseKey,
    Guid InstallationId,
    string Fingerprint,
    string DeviceRole,
    string AppVersion,
    string OrganizationNit,
    string? BranchName = null,
    string? DeviceName = null,
    string? OperatingSystem = null,
    string? BusinessProfile = null,
    string? OwnerLinkToken = null);

/// <summary>Check-in diario: el token vigente (aunque haya vencido) autentica la instalación junto con la huella actual.</summary>
/// <param name="Token">Último token recibido.</param>
/// <param name="Fingerprint">Huella actual del equipo.</param>
/// <param name="AppVersion">Versión del POS.</param>
/// <param name="ActiveTerminals">Cajas activas en la tienda (detección de abuso; sin límite por edición).</param>
/// <param name="ReportedClock">Reloj del equipo (detección de relojes atrasados).</param>
/// <param name="AuditSeal">Último sello de la auditoría del POS (ancla externa, ADR-0048). Opcional: los POS anteriores a 12-B no lo envían.</param>
/// <param name="BusinessProfile">
/// Tipo de negocio de la tienda (Fase 7-C parte A): <see cref="BusinessProfileCodes"/>. Opcional: los POS anteriores no lo envían y la nube
/// conserva el último conocido.
/// </param>
public sealed record CheckinRequest(
    string Token,
    string Fingerprint,
    string AppVersion,
    int ActiveTerminals,
    DateTimeOffset ReportedClock,
    CheckinAuditSeal? AuditSeal = null,
    string? BusinessProfile = null);

/// <summary>
/// Tipos de negocio que la tienda informa a la nube (Fase 7-C parte A; los mismos códigos de <c>org.companies.business_profile</c>). La
/// nube los muestra en el portal ("Tipo de negocio") y filtra por ellos. Un código desconocido se ignora (no rechaza el check-in).
/// </summary>
public static class BusinessProfileCodes
{
    public const string Supermarket = "SUPERMARKET";
    public const string FreshMarket = "FRESH_MARKET";
    public const string Pharmacy = "PHARMACY";
    public const string Apparel = "APPAREL";
    public const string Hardware = "HARDWARE";
    public const string Other = "OTHER";

    public static IReadOnlyList<string> All { get; } = [Supermarket, FreshMarket, Pharmacy, Apparel, Hardware, Other];

    /// <summary>Código normalizado (mayúsculas) si es conocido; si no, <c>null</c>.</summary>
    public static string? Normalize(string? code)
    {
        var upper = code?.Trim().ToUpperInvariant();
        return upper is not null && All.Contains(upper, StringComparer.Ordinal) ? upper : null;
    }

    /// <summary>Nombre para el portal.</summary>
    public static string Name(string? code) => Normalize(code) switch
    {
        Supermarket => "Supermercado / minimercado",
        FreshMarket => "Fruver / carnicería",
        Pharmacy => "Droguería",
        Apparel => "Ropa / calzado / deportes",
        Hardware => "Ferretería / papelería / miscelánea",
        Other => "Otro",
        _ => "Sin informar",
    };
}

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

/// <summary>
/// Token emitido y un resumen legible (el POS confía solo en lo firmado dentro del token). <c>PaymentUrl</c> (Fase 12-C): enlace de pago
/// en línea de la suscripción cuando está por vencer, en gracia o restringida, y la nube tiene el pago en línea configurado; nulo si no.
/// No va firmado: es solo un enlace que el POS muestra. <c>KeyPrefix</c> (opcional): prefijo visible de la clave, para mostrarlo cuando el
/// POS se activó con el enlace del propietario y no con la clave.
/// </summary>
public sealed record LicenseTokenResponse(
    string Token,
    string Kid,
    string SubscriptionStatus,
    DateTimeOffset ValidUntil,
    int GraceDays,
    DateTimeOffset RefreshAfter,
    IReadOnlyList<LicenseMessage> Messages,
    string? PaymentUrl = null,
    string? KeyPrefix = null);

/// <summary>Clave pública publicada, con la forma de un JWK OKP (RFC 8037).</summary>
/// <param name="Kid">Identificador de la clave.</param>
/// <param name="Kty">Siempre <c>OKP</c>.</param>
/// <param name="Crv">Siempre <c>Ed25519</c>.</param>
/// <param name="X">Clave pública cruda en base64url.</param>
/// <param name="Status"><c>ACTIVE</c> (firma hoy), <c>STANDBY</c> (reserva publicada) o <c>RETIRED</c> (verifica tokens antiguos).</param>
public sealed record PublicKeyDto(string Kid, string Kty, string Crv, string X, string Status);

public sealed record PublicKeysResponse(IReadOnlyList<PublicKeyDto> Keys);
