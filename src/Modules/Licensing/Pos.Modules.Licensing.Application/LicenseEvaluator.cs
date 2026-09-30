using Pos.Licensing.Contracts;
using Pos.Modules.Licensing.Contracts;

namespace Pos.Modules.Licensing.Application;

/// <summary>
/// Lo guardado en <c>licensing.license_state</c> (D12B-01). Los indicadores los informa el servidor de licencias (revocada, requiere
/// reactivación) o el propio POS (equipo liberado); el estado NO se guarda: lo calcula <see cref="LicenseEvaluator"/>.
/// <c>ClockOffsetSeconds</c> = hora confiable del último check-in menos el reloj local de ese momento (D12B-07);
/// <c>MaxObservedUtc</c> = mayor hora (confiable) vista por este equipo; <c>DemoStartedAt</c> = creación de la instalación.
/// </summary>
public sealed record LicenseRecord(
    Guid NodeId,
    string? Token,
    string? LicenseKeyPrefix,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? LastCheckinAt,
    string? LastCheckinError,
    bool Revoked,
    bool ReactivationRequired,
    bool Deactivated,
    long ClockOffsetSeconds,
    DateTimeOffset MaxObservedUtc,
    DateTimeOffset DemoStartedAt);

/// <summary>
/// Resultado de evaluar la licencia en un instante. <c>EffectiveNow</c> = reloj local corregido con la hora confiable del último
/// check-in; <c>ClockRollback</c> = el reloj retrocedió más de 24 h respecto de la mayor hora observada (RN-LIC-05).
/// </summary>
public sealed record LicenseEvaluation(
    string State,
    string Reason,
    int? DaysLeft,
    LicenseClaims? Claims,
    DateTimeOffset EffectiveNow,
    bool ClockRollback,
    DateTimeOffset DemoEndsAt,
    IReadOnlyList<LicenseNoticeDto> Notices)
{
    public bool Restricted => LicenseStates.IsRestricted(State);
}

/// <summary>
/// Reglas de los estados locales (docs/fases/fase-12b-propuesta.md §2). Función pura: el mismo token, la misma huella y el mismo
/// reloj dan siempre el mismo estado; así se prueba con reloj simulado y editar la BD no sirve de nada (el token va firmado).
/// </summary>
public static class LicenseEvaluator
{
    /// <summary>Días de la demostración (D12B-09). No es configurable por la tienda.</summary>
    public const int DemoDays = 30;

    /// <summary>Retroceso del reloj tolerado antes de restringir (RN-LIC-05).</summary>
    public static readonly TimeSpan ClockRollbackTolerance = TimeSpan.FromHours(24);

    /// <summary>Sin check-in exitoso en este tiempo el estado vigente se muestra como "sin conexión" (24 h + la variación del check-in).</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromHours(26);

    public static LicenseEvaluation Evaluate(
        LicenseRecord record, LicenseTokenVerification? verification, DeviceFingerprint? device, string localDeviceRole, DateTimeOffset localNow)
    {
        ArgumentNullException.ThrowIfNull(record);
        var effectiveNow = localNow.AddSeconds(record.ClockOffsetSeconds);
        var rollback = effectiveNow < record.MaxObservedUtc - ClockRollbackTolerance;
        var now = effectiveNow > record.MaxObservedUtc ? effectiveNow : record.MaxObservedUtc;
        var demoEnds = record.DemoStartedAt.AddDays(DemoDays);
        var claims = verification is { IsValid: true } ? verification.Claims : null;

        LicenseEvaluation Result(string state, string reason, DateTimeOffset? until = null)
        {
            int? days = until is { } u ? Math.Max(0, (int)Math.Ceiling((u - now).TotalDays)) : null;
            return new LicenseEvaluation(state, reason, days, claims, effectiveNow, rollback, demoEnds, Notices(state, reason, days, claims));
        }

        if (rollback)
        {
            return Result(LicenseStates.Restricted,
                $"El reloj del equipo está atrasado (marca {effectiveNow:yyyy-MM-dd HH:mm} UTC y ya se vio {record.MaxObservedUtc:yyyy-MM-dd HH:mm} UTC). Corrija la fecha y la hora de Windows y verifique la licencia.");
        }

        if (record.Token is null)
        {
            if (record.Deactivated)
            {
                return Result(LicenseStates.Restricted, "Este equipo fue liberado de la licencia. Active la licencia con la clave para seguir administrando.");
            }

            return now <= demoEnds
                ? Result(LicenseStates.Demo, "Modo demostración: el sistema funciona completo y los documentos impresos dicen \"DEMOSTRACIÓN\". Active la licencia con su clave.", demoEnds)
                : Result(LicenseStates.Restricted, "Terminó la demostración de 30 días. Active la licencia con su clave.");
        }

        if (claims is null)
        {
            return Result(LicenseStates.Restricted,
                $"La licencia guardada no es válida ({verification?.Status.ToString() ?? "sin verificar"}). Verifique la licencia o actívela de nuevo con la clave.");
        }

        if (record.ReactivationRequired || claims.InstallationId != record.NodeId || device is null
            || !DeviceFingerprint.TryParse(claims.DeviceFingerprint, out var tokenDevice) || !device.Matches(tokenDevice))
        {
            return Result(LicenseStates.ReactivationRequired,
                "La licencia no corresponde a este equipo (cambio de computador o backup restaurado en otro equipo). Actívela de nuevo con la clave; si el equipo anterior sigue activo, soporte lo libera.");
        }

        if (!string.Equals(claims.DeviceRole, localDeviceRole, StringComparison.Ordinal))
        {
            return Result(LicenseStates.Restricted, "La licencia fue activada para otro tipo de instalación (Caja Única o Multicaja). Actívela de nuevo con la clave.");
        }

        if (record.Revoked)
        {
            return Result(LicenseStates.Restricted, "La licencia fue revocada. Comuníquese con su proveedor.");
        }

        switch (claims.SubscriptionStatus)
        {
            case SubscriptionStatuses.Suspended:
                return Result(LicenseStates.Restricted, "La suscripción está suspendida. Comuníquese con su proveedor.");
            case SubscriptionStatuses.Cancelled:
                return Result(LicenseStates.Restricted, "La suscripción fue cancelada. Comuníquese con su proveedor.");
            case SubscriptionStatuses.Expired:
                return Result(LicenseStates.Restricted, "La suscripción venció y terminaron los días de gracia. Renueve con su proveedor.");
        }

        if (claims.IsValidAt(now) && claims.SubscriptionStatus != SubscriptionStatuses.PastDue)
        {
            var online = record.LastCheckinAt is { } last && now - last <= OfflineAfter;
            return online
                ? Result(LicenseStates.Valid, "Licencia vigente.", claims.ValidUntil)
                : Result(LicenseStates.ValidOffline, "Licencia vigente; no se ha podido verificar con el servidor de licencias recientemente (sin Internet). Todo funciona con normalidad.", claims.ValidUntil);
        }

        if (now <= claims.GraceUntil)
        {
            return Result(LicenseStates.Grace,
                $"El periodo pagado venció el {claims.ValidUntil:yyyy-MM-dd}. Quedan días de gracia: renueve para evitar la restricción.", claims.GraceUntil);
        }

        return Result(LicenseStates.Restricted, "Venció el periodo pagado y terminaron los días de gracia. Renueve con su proveedor y verifique la licencia.");
    }

    private static List<LicenseNoticeDto> Notices(string state, string reason, int? daysLeft, LicenseClaims? claims)
    {
        var notices = new List<LicenseNoticeDto>();
        switch (state)
        {
            case LicenseStates.Demo:
                notices.Add(new("DEMO", "WARNING", $"Modo demostración: quedan {daysLeft} días. Active la licencia con su clave."));
                break;
            case LicenseStates.Grace:
                notices.Add(new("GRACE", "WARNING", $"{reason} Quedan {daysLeft} días."));
                break;
            case LicenseStates.Restricted:
            case LicenseStates.ReactivationRequired:
                notices.Add(new(state, "CRITICAL", reason));
                break;
            case LicenseStates.ValidOffline:
                notices.Add(new("OFFLINE", "INFO", reason));
                break;
        }

        foreach (var message in claims?.Messages ?? [])
        {
            if (notices.TrueForAll(n => n.Code != message.Code))
            {
                notices.Add(new(message.Code, message.Severity, message.Text));
            }
        }

        return notices;
    }
}
