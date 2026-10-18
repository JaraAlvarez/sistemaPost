using Pos.Licensing.Contracts;
using Pos.Modules.Licensing.Contracts;

namespace Pos.Modules.Licensing.Application;

/// <summary>
/// Lo guardado en <c>licensing.license_state</c> (D12B-01). Los indicadores los informa el servidor de licencias (revocada, requiere
/// reactivación) o el propio POS (equipo liberado); el estado NO se guarda: lo calcula <see cref="LicenseEvaluator"/>.
/// <c>ClockOffsetSeconds</c> = hora confiable del último check-in menos el reloj local de ese momento (D12B-07);
/// <c>MaxObservedUtc</c> = mayor hora (confiable) vista por este equipo; <c>DemoStartedAt</c> = creación de la instalación;
/// <c>PaymentUrl</c> = enlace de pago en línea que envió la nube en la última respuesta (Fase 12-C; no cuenta para el estado).
/// Endurecimiento (docs/fases/fase-licencia-endurecimiento.md): <c>CompanyNit</c> = NIT de la empresa configurada (H7);
/// <c>Tampered</c> = la firma local (MAC) del estado no cuadra o el token es más viejo que el último confirmado (H4);
/// <c>RestrictedSince</c> = hora confiable en que pasó a restringida (H6); <c>OfflineFloorUtc</c> = piso desde el que se cuentan los días
/// sin conexión cuando es posterior a la emisión del token (la actualización que introdujo el límite, N1); <c>VaultUnavailable</c> =
/// mensaje si el archivo protegido de la licencia no se puede leer; <c>HadConfirmedLicense</c> = el archivo ya vio un token confirmado o
/// una revocación (una demo "alterada" en ese equipo no es una instalación nueva).
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
    DateTimeOffset DemoStartedAt,
    string? PaymentUrl = null,
    string? CompanyNit = null,
    bool Tampered = false,
    DateTimeOffset? RestrictedSince = null,
    DateTimeOffset? OfflineFloorUtc = null,
    string? VaultUnavailable = null,
    bool HadConfirmedLicense = false);

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
    IReadOnlyList<LicenseNoticeDto> Notices,
    bool Recoverable = false)
{
    // Recoverable (integración 1.2.0): restringida por el estado local alterado o el archivo ilegible, no por pago ni revocación.
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

    /// <summary>
    /// Máximo de días sin un check-in exitoso (N1), contados desde la emisión (<c>iat</c>) del token vigente: pasado, RESTRINGIDA hasta
    /// conectarse. ⚙️ 30 — PENDIENTE DE APROBACIÓN del usuario. La configuración solo puede acortarlo.
    /// </summary>
    public const int DefaultMaxOfflineDays = 30;

    /// <summary>Días antes del límite sin conexión desde los que se avisa en el Tablero (desde el día 23 de 30).</summary>
    public const int OfflineWarningDays = 7;

    /// <summary>Ventana para seguir vendiendo en la jornada abierta desde que la licencia quedó restringida (H6).</summary>
    public static readonly TimeSpan RestrictedSalesWindow = TimeSpan.FromHours(24);

    /// <summary>Máximo tiempo de funcionamiento que se suma a la marca en un solo recálculo (H3): contar de menos es seguro.</summary>
    public static readonly TimeSpan MaxElapsedPerTick = TimeSpan.FromMinutes(15);

    /// <summary>Hora confiable de la evaluación: el reloj corregido o la mayor hora ya observada, la que sea mayor.</summary>
    public static DateTimeOffset TrustedNow(LicenseRecord record, LicenseEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(evaluation);
        return evaluation.EffectiveNow > record.MaxObservedUtc ? evaluation.EffectiveNow : record.MaxObservedUtc;
    }

    /// <summary>
    /// Nueva marca de tiempo (H3): avanza con el tiempo de funcionamiento medido con el reloj MONÓTONO del proceso
    /// (<paramref name="elapsed"/>, acotado a <see cref="MaxElapsedPerTick"/>) y con el reloj corregido si no hay retroceso. Así atrasar el
    /// reloj de a poco no congela la hora: la marca sigue y la diferencia termina detectándose como retroceso.
    /// </summary>
    public static DateTimeOffset NextMark(LicenseRecord record, LicenseEvaluation evaluation, TimeSpan? elapsed)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(evaluation);
        var step = elapsed is { } e && e > TimeSpan.Zero ? (e > MaxElapsedPerTick ? MaxElapsedPerTick : e) : TimeSpan.Zero;
        var running = record.MaxObservedUtc + step;
        return !evaluation.ClockRollback && evaluation.EffectiveNow > running ? evaluation.EffectiveNow : running;
    }

    /// <summary>¿Es el mismo NIT? Compara el número base y, si los dos lo traen, el dígito de verificación (H7).</summary>
    public static bool SameNit(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        var (na, da) = SplitNit(a);
        var (nb, db) = SplitNit(b);
        return na.Length > 0 && na == nb && (da is null || db is null || da == db);
    }

    public static LicenseEvaluation Evaluate(
        LicenseRecord record, LicenseTokenVerification? verification, DeviceFingerprint? device, string localDeviceRole, DateTimeOffset localNow,
        int maxOfflineDays = DefaultMaxOfflineDays)
    {
        ArgumentNullException.ThrowIfNull(record);
        maxOfflineDays = Math.Clamp(maxOfflineDays, 1, DefaultMaxOfflineDays);
        var effectiveNow = localNow.AddSeconds(record.ClockOffsetSeconds);
        var rollback = effectiveNow < record.MaxObservedUtc - ClockRollbackTolerance;
        var now = effectiveNow > record.MaxObservedUtc ? effectiveNow : record.MaxObservedUtc;
        var demoEnds = record.DemoStartedAt.AddDays(DemoDays);
        var claims = verification is { IsValid: true } ? verification.Claims : null;

        LicenseEvaluation Result(string state, string reason, DateTimeOffset? until = null, bool recoverable = false)
        {
            int? days = until is { } u ? Math.Max(0, (int)Math.Ceiling((u - now).TotalDays)) : null;
            return new LicenseEvaluation(state, reason, days, claims, effectiveNow, rollback, demoEnds, Notices(state, reason, days, claims), recoverable);
        }

        if (rollback)
        {
            return Result(LicenseStates.Restricted,
                $"El reloj del equipo está atrasado (marca {effectiveNow:yyyy-MM-dd HH:mm} UTC y ya se vio {record.MaxObservedUtc:yyyy-MM-dd HH:mm} UTC). Corrija la fecha y la hora de Windows y verifique la licencia.");
        }

        // Sin poder leer el archivo protegido no se puede comprobar nada: restringida (ventana normal de 24 h) con un mensaje propio.
        if (record.VaultUnavailable is { } unavailable)
        {
            return Result(LicenseStates.Restricted, unavailable, recoverable: true);
        }

        // En demostración un estado alterado no restringe: la demo se evalúa con la marca y el inicio que guarda el archivo protegido
        // (lo reconcilia el almacén), así que editar o restaurar la BD no la alarga. Salvo que el equipo ya haya tenido una licencia
        // confirmada (o revocada): entonces borrar el token de la BD no puede devolverlo a la demo.
        if (record.Token is null && record.Tampered && record.HadConfirmedLicense)
        {
            return Result(LicenseStates.Restricted,
                "Los datos de la licencia guardados en este equipo no cuadran con su firma y este equipo ya tuvo una licencia activada. Active la licencia con su clave.",
                recoverable: true);
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

        if (record.Tampered)
        {
            return Result(LicenseStates.Restricted,
                "Los datos de la licencia guardados en este equipo no cuadran con su firma (se modificaron fuera del sistema, se restauró un backup anterior o se perdió el archivo de la licencia). Verifique la licencia con conexión a Internet.",
                recoverable: true);
        }

        if (!string.Equals(claims.DeviceRole, localDeviceRole, StringComparison.Ordinal))
        {
            return Result(LicenseStates.Restricted, "La licencia fue activada para otro tipo de instalación (Caja Única o Multicaja). Actívela de nuevo con la clave.");
        }

        if (record.CompanyNit is not null && !SameNit(claims.OrganizationNit, record.CompanyNit))
        {
            return Result(LicenseStates.Restricted,
                $"La licencia es de otra empresa (NIT {claims.OrganizationNit}) y la configurada en este equipo tiene el NIT {record.CompanyNit}. Comuníquese con su proveedor.");
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

        // N1: el iat va firmado, así que editar la BD no alarga el tiempo sin conexión. Las instalaciones que ya existían cuentan desde
        // la actualización que introdujo el límite (piso firmado y guardado también en el archivo), no desde un check-in viejo.
        var offlineFrom = record.OfflineFloorUtc is { } floor && floor > claims.IssuedAt ? floor : claims.IssuedAt;
        var offlineDeadline = offlineFrom.AddDays(maxOfflineDays);
        if (now > offlineDeadline)
        {
            return Result(LicenseStates.Restricted,
                $"La licencia no se ha podido verificar con el servidor de licencias en más de {maxOfflineDays} días (desde el {offlineFrom:yyyy-MM-dd}). Conecte el equipo a Internet y verifique la licencia.");
        }

        LicenseEvaluation WarnOffline(LicenseEvaluation evaluation)
        {
            if (offlineDeadline - now > TimeSpan.FromDays(OfflineWarningDays))
            {
                return evaluation;
            }

            var left = Math.Max(0, (int)Math.Ceiling((offlineDeadline - now).TotalDays));
            return evaluation with
            {
                Notices = [.. evaluation.Notices, new LicenseNoticeDto("OFFLINE_LIMIT", "WARNING",
                    $"La licencia no se verifica con el servidor desde el {offlineFrom:yyyy-MM-dd}: conecte el equipo a Internet antes del {offlineDeadline:yyyy-MM-dd} (quedan {left} días) o la licencia quedará restringida.")],
            };
        }

        if (claims.IsValidAt(now) && claims.SubscriptionStatus != SubscriptionStatuses.PastDue)
        {
            var online = record.LastCheckinAt is { } last && now - last <= OfflineAfter;
            return WarnOffline(online
                ? Result(LicenseStates.Valid, "Licencia vigente.", claims.ValidUntil)
                : Result(LicenseStates.ValidOffline,
                    $"Licencia vigente; no se ha podido verificar con el servidor de licencias recientemente (sin Internet). Todo funciona con normalidad, pero conéctese antes del {offlineDeadline:yyyy-MM-dd} o la licencia quedará restringida.",
                    claims.ValidUntil));
        }

        if (now <= claims.GraceUntil)
        {
            return WarnOffline(Result(LicenseStates.Grace,
                $"El periodo pagado venció el {claims.ValidUntil:yyyy-MM-dd}. Quedan días de gracia: renueve para evitar la restricción.", claims.GraceUntil));
        }

        return Result(LicenseStates.Restricted, "Venció el periodo pagado y terminaron los días de gracia. Renueve con su proveedor y verifique la licencia.");
    }

    private static (string Number, string? Dv) SplitNit(string value)
    {
        var parts = value.Trim().Split('-', 2);
        var number = new string([.. parts[0].Where(char.IsAsciiDigit)]).TrimStart('0');
        var dv = parts.Length > 1 ? new string([.. parts[1].Where(char.IsAsciiDigit)]) : null;
        return (number, string.IsNullOrEmpty(dv) ? null : dv);
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
