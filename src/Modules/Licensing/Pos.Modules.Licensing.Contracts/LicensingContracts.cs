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
    IReadOnlyList<LicenseNoticeDto> Notices)
{
    public bool IsRestricted => LicenseStates.IsRestricted(State);
}

/// <summary>Resumen para <c>/auth/me</c> y el tablero: lo visible para cualquier usuario (sin datos de la clave).</summary>
public sealed record LicenseSummaryDto(string State, int? DaysLeft, IReadOnlyList<LicenseNoticeDto> Notices);

/// <summary>Un intento de verificación con el servidor de licencias.</summary>
public sealed record LicenseCheckinDto(DateTimeOffset OccurredAt, string Kind, bool Succeeded, string? ErrorCode, string? SubscriptionStatus, int DurationMs);

/// <summary>Estado de la licencia para otros módulos (Identity: <c>/auth/me</c>; Reporting: tablero).</summary>
public interface ILicenseStatus
{
    LicenseSummaryDto Summary { get; }
}

/// <summary>Token de licencia y huella del equipo con los que la tienda se identifica ante la nube (sincronización, Fase 16, D16-03).</summary>
public sealed record LicenseCredentials(string Token, string Fingerprint);

/// <summary>Credenciales de la tienda ante la nube; <c>null</c> si la licencia no está activada o no hay huella.</summary>
public interface ILicenseCredentials
{
    Task<LicenseCredentials?> GetAsync(CancellationToken cancellationToken);
}
