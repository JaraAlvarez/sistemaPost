using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Licensing.Contracts;

/// <summary>Permisos de la licencia (docs/fases/fase-12b-propuesta.md §7).</summary>
public static class LicensePermissions
{
    public const string View = "licensing.license.view";
    public const string Check = "licensing.license.check";
    public const string Manage = "licensing.license.manage";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(View, "Ver el estado de la licencia y el historial de verificaciones", isSensitive: false),
        new(Check, "Verificar la licencia ahora con el servidor de licencias", isSensitive: false),
        new(Manage, "Activar la licencia y liberar este equipo (solo el propietario)", isSensitive: true),
    ];
}

/// <summary>Estados locales de la licencia (§2). Se CALCULAN en cada consulta; no se guardan.</summary>
public static class LicenseStates
{
    /// <summary>Nunca activada: 30 días completos con "DEMOSTRACIÓN" en los documentos impresos.</summary>
    public const string Demo = "DEMO";

    /// <summary>Token vigente y verificado con el servidor en las últimas 24 horas.</summary>
    public const string Valid = "VALID";

    /// <summary>Token vigente, sin verificación reciente (sin Internet): funciona completo.</summary>
    public const string ValidOffline = "VALID_OFFLINE";

    /// <summary>Periodo vencido, dentro de los días de gracia: funciona con avisos.</summary>
    public const string Grace = "GRACE";

    /// <summary>Solo se vende y se cierra en las jornadas abiertas; consultas, reportes, exportación y backups siguen disponibles.</summary>
    public const string Restricted = "RESTRICTED";

    /// <summary>El token no es de este equipo (p. ej. backup restaurado en otro PC): igual que RESTRICTED hasta reactivar.</summary>
    public const string ReactivationRequired = "REACTIVATION_REQUIRED";

    public static bool IsRestricted(string state) => state is Restricted or ReactivationRequired;
}

/// <summary>Aviso para el usuario (del servidor de licencias o del propio POS).</summary>
public sealed record LicenseNoticeDto(string Code, string Severity, string Text);

/// <summary>
/// Estado de la licencia de este equipo: <c>State</c> (<see cref="LicenseStates"/>), el motivo en español y <c>DaysLeft</c>, los días hasta
/// el siguiente cambio de estado (fin de la demostración, del periodo o de la gracia; <c>null</c> si no aplica).
/// <c>PaymentUrl</c> (Fase 12-C): enlace para pagar la mensualidad en línea; la nube lo envía cuando faltan 15 días o menos, o la licencia
/// está en gracia o restringida. <c>null</c> si la licencia está al día o el pago en línea no está disponible.
/// </summary>
public sealed record LicenseStatusDto(
    string State,
    string Reason,
    int? DaysLeft,
    string? Edition,
    string? OrganizationName,
    string? OrganizationNit,
    string? LicenseKeyPrefix,
    string? SubscriptionStatus,
    DateTimeOffset? ValidUntil,
    DateTimeOffset? GraceUntil,
    DateTimeOffset? DemoEndsAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? LastCheckinAt,
    string? LastCheckinError,
    IReadOnlyList<LicenseNoticeDto> Notices,
    string? PaymentUrl = null)
{
    public bool IsRestricted => LicenseStates.IsRestricted(State);
}

/// <summary>
/// Resumen para <c>/auth/me</c> y el tablero: lo visible para cualquier usuario (sin datos de la clave). <c>PaymentUrl</c> (Fase 12-C) solo
/// llega a quien puede ver la licencia; la franja de avisos de la administración muestra con él la acción "Pagar".
/// </summary>
public sealed record LicenseSummaryDto(string State, int? DaysLeft, IReadOnlyList<LicenseNoticeDto> Notices, string? PaymentUrl = null);

/// <summary>Un intento de verificación con el servidor de licencias.</summary>
public sealed record LicenseCheckinDto(DateTimeOffset OccurredAt, string Kind, bool Succeeded, string? ErrorCode, string? SubscriptionStatus, int DurationMs);

/// <summary>Estado de la licencia para otros módulos (Identity: <c>/auth/me</c>; Reporting: tablero).</summary>
public interface ILicenseStatus
{
    LicenseSummaryDto Summary { get; }
}

/// <summary>Enlace del propietario ya verificado: correo (en minúsculas), nombre y NIT con DV de la empresa de la licencia.</summary>
/// <param name="Email">Correo validado por la nube.</param>
/// <param name="DisplayName">Nombre del usuario del portal.</param>
/// <param name="OrganizationNit">NIT con DV; vacío si la cuenta del portal aún no tiene empresa (la tienda sigue en prueba).</param>
/// <param name="OrganizationName">Razón social de la empresa del portal (opcional; enlaces anteriores no la traen).</param>
/// <param name="BusinessProfile">Tipo de negocio de la empresa en el portal (opcional).</param>
public sealed record PortalOwnerLink(string Email, string DisplayName, string OrganizationNit, string? OrganizationName = null, string? BusinessProfile = null);

/// <summary>
/// Verifica sin conexión el enlace firmado por la nube con el que el instalador premium crea al Propietario con su cuenta del portal
/// (Fase 13-B, D13B-09): misma clave de confianza que el token de licencia, tipo propio, vigencia corta. <c>null</c> si no es válido,
/// está vencido o fue firmado con una clave que este equipo no conoce.
/// </summary>
public interface IPortalOwnerLinkVerifier
{
    PortalOwnerLink? Verify(string token);
}

/// <summary>Token de licencia y huella del equipo con los que la tienda se identifica ante la nube (sincronización, Fase 16, D16-03).</summary>
public sealed record LicenseCredentials(string Token, string Fingerprint);

/// <summary>Credenciales de la tienda ante la nube; <c>null</c> si la licencia no está activada o no hay huella.</summary>
public interface ILicenseCredentials
{
    Task<LicenseCredentials?> GetAsync(CancellationToken cancellationToken);
}
