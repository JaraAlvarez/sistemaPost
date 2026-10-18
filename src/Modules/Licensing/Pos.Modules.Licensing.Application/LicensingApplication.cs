using FluentValidation;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Licensing.Contracts;
using Pos.Modules.Licensing.Contracts;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Licensing.Application;

public sealed class LicensePermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => LicensePermissions.All;
}

public static class LicenseErrors
{
    public static readonly Error SetupRequired = Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
    public static readonly Error NotConfigured = Error.BusinessRule(
        "LICENSE.NOT_CONFIGURED", "Este equipo no tiene configurada la dirección del servidor de licencias (Pos:Licensing:ServerUrl).");
    public static readonly Error NotActivated = Error.BusinessRule("LICENSE.NOT_ACTIVATED", "La licencia no está activada en este equipo.");
    public static readonly Error KeyFormat = Error.Validation(
        LicenseErrorCodes.KeyFormatInvalid, "La clave debe tener la forma POS-XXXXX-XXXXX-XXXXX-XXXXX. Revise que la escribió completa.");
    public static readonly Error FingerprintUnavailable = Error.BusinessRule(
        LicenseErrorCodes.FingerprintInvalid, "No se pudo leer la identificación de este equipo (placa, disco y Windows). Ejecute el servidor como servicio de Windows.");
    public static readonly Error Unreachable = Error.BusinessRule(
        "LICENSE.SERVER_UNREACHABLE", "No se pudo comunicar con el servidor de licencias. Revise la conexión a Internet e intente de nuevo.");
    public static readonly Error NoPaymentLink = Error.NotFound(
        "LICENSE.NO_PAYMENT_LINK", "No hay un pago pendiente de la mensualidad: la licencia está al día o el pago en línea no está disponible.");
    public static readonly Error TokenRejected = Error.BusinessRule(
        "LICENSE.TOKEN_REJECTED", "El servidor respondió con una licencia que no corresponde a este equipo o no está firmada por una clave de confianza.");

    /// <summary>Error devuelto por el servidor de licencias (código estable <c>LICENSE.*</c> y mensaje en español).</summary>
    public static Error FromCloud(string code, string? message) => Error.BusinessRule(code, message ?? Describe(code));

    public static string Describe(string code) => code switch
    {
        LicenseErrorCodes.KeyInvalid => "La clave de licencia no existe o fue revocada.",
        LicenseErrorCodes.EditionMismatch => "La licencia es de otra edición (Caja Única o Multicaja) que la de esta instalación.",
        LicenseErrorCodes.InstallationsExceeded => "La licencia ya tiene todas las sucursales (instalaciones) permitidas.",
        LicenseErrorCodes.SubscriptionInactive => "La suscripción no está activa.",
        LicenseErrorCodes.NitMismatch => "El NIT de la empresa configurada no es el de la licencia.",
        LicenseErrorCodes.InstallationActiveOnOtherDevice => "Esta sucursal ya está activa en otro equipo: pida a soporte que libere el equipo anterior.",
        LicenseErrorCodes.ReactivationRequired => "Hay que activar de nuevo este equipo con la clave.",
        LicenseErrorCodes.LicenseRevoked => "La licencia fue revocada.",
        LicenseErrorCodes.TooManyRequests => "Demasiados intentos; espere unos minutos.",
        _ => "El servidor de licencias rechazó la operación.",
    };
}

/// <summary>
/// Enlace de pago en línea que envía la nube (Fase 12-C, D12C-08). No va firmado, así que el POS solo acepta una dirección absoluta
/// http o https, sin usuario ni contraseña, sin espacios y de hasta <see cref="MaxLength"/> caracteres; cualquier otra cosa se ignora.
/// </summary>
public static class LicensePaymentLink
{
    public const int MaxLength = 500;

    /// <summary>El enlace limpio (sin espacios alrededor) o <c>null</c> si no es una dirección web válida.</summary>
    public static string? Normalize(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return null;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrEmpty(uri.Host)
                ? text
                : null;
    }
}

// ------------------------------------------------------------------------------------------------ Puertos

/// <summary>Datos de la empresa y la tienda que se declaran al activar y en el check-in.</summary>
/// <summary>Datos de la tienda que viajan a la nube. <c>BusinessProfile</c> (Fase 7-C parte A): tipo de negocio (SUPERMARKET…).</summary>
public sealed record LicenseSiteInfo(string OrganizationNit, string OrganizationName, string? BranchName, int OpenCashSessions, string? BusinessProfile = null);

public interface ILicenseStateStore
{
    /// <summary>La fila del nodo; <c>null</c> si todavía no existe (ver <see cref="EnsureAsync"/>).</summary>
    Task<LicenseRecord?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Crea la fila del nodo si no existe (la mayor hora observada empieza en la creación de la instalación).</summary>
    Task EnsureAsync(CancellationToken cancellationToken);

    Task<string?> GetLastStateAsync(CancellationToken cancellationToken);

    /// <summary>Guarda el último estado calculado y desde cuándo está restringida (<c>null</c> si no lo está; H6).</summary>
    Task SetLastStateAsync(string state, DateTimeOffset? restrictedSince, CancellationToken cancellationToken);

    /// <summary>
    /// Guarda un token verificado (con la hora CONFIABLE de su emisión) y limpia los indicadores (revocada, reactivación, liberado).
    /// <paramref name="paymentUrl"/> reemplaza el enlace de pago guardado (<c>null</c> lo borra: la licencia está al día).
    /// <paramref name="trustedNow"/> = hora confiable de la nube: REEMPLAZA la mayor hora observada aunque sea menor (R2: un reloj
    /// adelantado por error se corrige con el siguiente check-in).
    /// </summary>
    Task SaveTokenAsync(
        string token, string? keyPrefix, DateTimeOffset at, bool activation, long clockOffsetSeconds, string? paymentUrl, DateTimeOffset trustedNow,
        CancellationToken cancellationToken);

    Task SaveCheckinErrorAsync(string error, bool? revoked, bool? reactivationRequired, CancellationToken cancellationToken);

    /// <summary>Equipo liberado: se olvida el token y el enlace de pago.</summary>
    Task ClearTokenAsync(CancellationToken cancellationToken);

    Task RaiseMaxObservedAsync(DateTimeOffset value, CancellationToken cancellationToken);

    Task RecordCheckinAsync(LicenseCheckinDto checkin, CancellationToken cancellationToken);

    Task<IReadOnlyList<LicenseCheckinDto>> ListCheckinsAsync(int limit, CancellationToken cancellationToken);

    Task<LicenseSiteInfo?> SiteAsync(CancellationToken cancellationToken);
}

/// <summary>Respuesta del servidor de licencias: el token (o 204 sin cuerpo), un error con código estable o "no se pudo comunicar".</summary>
public sealed record CloudResult(bool Reached, LicenseTokenResponse? Response, string? ErrorCode, string? ErrorMessage)
{
    public static readonly CloudResult NoConnection = new(false, null, null, null);

    public bool Unreachable => !Reached;

    public bool Succeeded => Reached && ErrorCode is null;

    public static CloudResult Ok(LicenseTokenResponse? response) => new(true, response, null, null);

    public static CloudResult Fail(string code, string? message) => new(true, null, code, message);
}

public interface ILicenseCloud
{
    bool IsConfigured { get; }

    Task<CloudResult> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken);

    Task<CloudResult> CheckinAsync(CheckinRequest request, CancellationToken cancellationToken);

    Task<CloudResult> DeactivateAsync(DeactivationRequest request, CancellationToken cancellationToken);
}

/// <summary>Identidad de este equipo (D12B-05) y versión del POS.</summary>
public interface IDeviceIdentity
{
    DeviceFingerprint? Fingerprint { get; }

    string DeviceName { get; }

    string OperatingSystem { get; }

    string AppVersion { get; }
}

/// <summary>Claves públicas de confianza (D12B-04): las embebidas en el binario.</summary>
public interface ILicenseKeys
{
    LicenseKeyRing Ring { get; }
}

/// <summary>Parámetros de la evaluación que fija la instalación (N1). Solo pueden ACORTAR el máximo del producto.</summary>
public sealed record LicensePolicy(int MaxOfflineDays = LicenseEvaluator.DefaultMaxOfflineDays)
{
    public static readonly LicensePolicy Default = new();
}

/// <summary>
/// Último estado calculado, en memoria: lo leen el filtro del pipeline, <c>/auth/me</c> y los documentos impresos. Si no hay un cálculo
/// exitoso en <see cref="StaleAfter"/> (H5: la evaluación falla una y otra vez), el filtro pasa a RESTRINGIDA con un mensaje claro;
/// la edad se mide con el reloj MONÓTONO (cambiar la hora de Windows no la altera).
/// </summary>
public sealed class LicenseStateCache(TimeProvider? time = null) : ILicenseGate, ILicenseStatus
{
    /// <summary>Tolerancia a fallos transitorios del cálculo (BD caída, error inesperado) antes de restringir.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    public const string StaleReason =
        "No se pudo calcular el estado de la licencia en la última hora (error interno o base de datos no disponible). Revise que el servicio y la base de datos estén funcionando y verifique la licencia.";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly long _startedAt = (time ?? TimeProvider.System).GetTimestamp();
    private volatile Snapshot? _snapshot;

    private sealed record Snapshot(LicenseEvaluation Evaluation, LicenseGateState Gate, string? PaymentUrl, long At);

    public LicenseEvaluation? Current => _snapshot?.Evaluation;

    // Sin cálculo reciente se restringe, pero se CONSERVA la última decisión de venta (si ya no se podía vender, sigue sin poderse) y
    // el resto de la compuerta (con "with": ningún campo se toma por posición).
    LicenseGateState ILicenseGate.Current => _snapshot is { } s
        ? (_time.GetElapsedTime(s.At) > StaleAfter ? s.Gate with { Restricted = true, Reason = StaleReason } : s.Gate)
        : (_time.GetElapsedTime(_startedAt) > StaleAfter
            ? new LicenseGateState(Restricted: true, Demo: false, Reason: StaleReason)
            : LicenseGateState.Unrestricted);

    public LicenseSummaryDto Summary => _snapshot is { } s
        ? new LicenseSummaryDto(s.Evaluation.State, s.Evaluation.DaysLeft, s.Evaluation.Notices, s.PaymentUrl)
        : new LicenseSummaryDto(LicenseStates.Demo, null, []);

    /// <param name="evaluation">Estado calculado.</param>
    /// <param name="paymentUrl">Enlace de pago vigente.</param>
    /// <param name="salesWindowClosed">Restringida desde hace más de <see cref="LicenseEvaluator.RestrictedSalesWindow"/> (H6).</param>
    /// <param name="recoverableRestriction">
    /// Restringida por el estado alterado o el archivo ilegible (no por pago): en la ventana de 24 h también se abren jornadas.
    /// </param>
    public void Update(LicenseEvaluation evaluation, string? paymentUrl = null, bool salesWindowClosed = false, bool recoverableRestriction = false)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _snapshot = new Snapshot(
            evaluation,
            new LicenseGateState(
                Restricted: evaluation.Restricted,
                Demo: evaluation.State == LicenseStates.Demo,
                Reason: evaluation.Restricted ? evaluation.Reason : null,
                SalesWindowClosed: evaluation.Restricted && salesWindowClosed,
                AllowsOpeningSessions: evaluation.State == LicenseStates.Restricted && recoverableRestriction),
            paymentUrl,
            _time.GetTimestamp());
    }
}

// ------------------------------------------------------------------------------------------------ Servicio

/// <summary>
/// Activación, check-in, liberación y recálculo del estado (§6). Corre dentro de un comando (transacción del pipeline): el estado,
/// el historial de verificaciones y la auditoría se guardan juntos.
/// </summary>
public sealed class LicenseService(
    ILicenseStateStore store,
    ILicenseCloud cloud,
    IDeviceIdentity device,
    ILicenseKeys keys,
    LicenseStateCache cache,
    IInstallationContext installation,
    IAuditWriter audit,
    IAuditAnchor anchor,
    IClock clock,
    LicensePolicy? policy = null)
{
    private readonly LicensePolicy _policy = policy ?? LicensePolicy.Default;

    public string LocalDeviceRole => installation.NodeRole == NodeRole.StoreServer ? DeviceRoles.StoreServer : DeviceRoles.AllInOne;

    /// <summary>
    /// Recalcula el estado. Con <paramref name="persist"/> (dentro de un comando) además audita si cambió (<c>LICENSE_STATE_CHANGED</c>)
    /// y sube la mayor hora observada (con el reloj y con el tiempo de funcionamiento <paramref name="elapsed"/>, H3); las consultas solo leen.
    /// </summary>
    public async Task<(LicenseEvaluation Evaluation, LicenseRecord Record)?> RefreshAsync(
        bool persist, CancellationToken cancellationToken, TimeSpan? elapsed = null)
    {
        var record = await store.GetAsync(cancellationToken);
        if (record is null)
        {
            await store.EnsureAsync(cancellationToken);
            record = await store.GetAsync(cancellationToken);
            if (record is null)
            {
                return null;
            }
        }

        var evaluation = Evaluate(record);
        var trustedNow = LicenseEvaluator.TrustedNow(record, evaluation);
        var restrictedSince = evaluation.Restricted ? record.RestrictedSince ?? (persist ? trustedNow : null) : null;
        // Integración 1.2.0 (ALTA 2): después de una vuelta atrás o de restaurar un backup el estado queda "alterado" sin jornadas abiertas;
        // con ese motivo (o el archivo de la licencia ilegible) también se pueden abrir jornadas dentro de la ventana de 24 h.
        cache.Update(evaluation, PaymentUrlOf(record), SalesWindowClosed(evaluation.Restricted ? restrictedSince : null, trustedNow),
            recoverableRestriction: evaluation.Recoverable);
        if (!persist)
        {
            return (evaluation, record);
        }

        var mark = LicenseEvaluator.NextMark(record, evaluation, elapsed);
        if (mark > record.MaxObservedUtc)
        {
            await store.RaiseMaxObservedAsync(mark, cancellationToken);
        }

        var previous = await store.GetLastStateAsync(cancellationToken);
        if (previous != evaluation.State || restrictedSince != record.RestrictedSince)
        {
            await store.SetLastStateAsync(evaluation.State, restrictedSince, cancellationToken);
        }

        if (previous != evaluation.State)
        {
            if (installation.IsSetupCompleted && previous is not null)
            {
                if (evaluation.ClockRollback)
                {
                    await audit.WriteAsync(
                        new AuditEntry("licensing", "LICENSE_CLOCK_ROLLBACK", "License", installation.NodeId, "Licencia", evaluation.Reason,
                            Severity: AuditSeverity.Critical),
                        cancellationToken);
                }

                await audit.WriteAsync(
                    new AuditEntry("licensing", "LICENSE_STATE_CHANGED", "License", installation.NodeId, "Licencia",
                        $"Licencia: {previous} → {evaluation.State}. {evaluation.Reason}",
                        new Dictionary<string, object?> { ["state"] = previous },
                        new Dictionary<string, object?> { ["state"] = evaluation.State },
                        Severity: evaluation.Restricted ? AuditSeverity.Critical : AuditSeverity.Warning),
                    cancellationToken);
            }
        }

        return (evaluation, record);
    }

    public async Task<Result<LicenseStatusDto>> StatusAsync(CancellationToken cancellationToken, bool persist = true, TimeSpan? elapsed = null) =>
        await RefreshAsync(persist, cancellationToken, elapsed) is { } current
            ? ToDto(current.Evaluation, current.Record)
            : Result.Failure<LicenseStatusDto>(LicenseErrors.SetupRequired);

    public async Task<Result<LicenseStatusDto>> ActivateAsync(string licenseKey, CancellationToken cancellationToken)
    {
        if (!LicenseKey.TryNormalize(licenseKey, out var key))
        {
            return Result.Failure<LicenseStatusDto>(LicenseErrors.KeyFormat);
        }

        if (!installation.IsSetupCompleted || await store.SiteAsync(cancellationToken) is not { } site)
        {
            return Result.Failure<LicenseStatusDto>(LicenseErrors.SetupRequired);
        }

        if (!cloud.IsConfigured)
        {
            return Result.Failure<LicenseStatusDto>(LicenseErrors.NotConfigured);
        }

        if (device.Fingerprint is not { IsUsable: true } fingerprint)
        {
            return Result.Failure<LicenseStatusDto>(LicenseErrors.FingerprintUnavailable);
        }

        var started = clock.UtcNow;
        var result = await cloud.ActivateAsync(
            new ActivationRequest(key, installation.NodeId, fingerprint.ToString(), LocalDeviceRole, device.AppVersion, site.OrganizationNit,
                site.BranchName, device.DeviceName, device.OperatingSystem, site.BusinessProfile),
            cancellationToken);
        await RecordAsync("ACTIVATION", started, result, cancellationToken);
        if (result.Response is not { } response)
        {
            return Result.Failure<LicenseStatusDto>(result.Unreachable ? LicenseErrors.Unreachable : LicenseErrors.FromCloud(result.ErrorCode!, result.ErrorMessage));
        }

        if (VerifyForThisDevice(response.Token, site.OrganizationNit, await store.GetAsync(cancellationToken)) is not { } claims)
        {
            return Result.Failure<LicenseStatusDto>(LicenseErrors.TokenRejected);
        }

        await store.SaveTokenAsync(
            response.Token, LicenseKey.VisiblePrefix(key), claims.IssuedAt, activation: true, Offset(claims), LicensePaymentLink.Normalize(response.PaymentUrl),
            TrustedNow(claims), cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("licensing", "LICENSE_ACTIVATED", "License", installation.NodeId, "Licencia",
                $"Licencia {LicenseKey.VisiblePrefix(key)}… activada: {claims.OrganizationName} ({claims.OrganizationNit}), edición {claims.Edition}, vigente hasta {claims.ValidUntil:yyyy-MM-dd}."),
            cancellationToken);
        return await StatusAsync(cancellationToken);
    }

    /// <summary>Check-in con el servidor. Sin conexión o con rechazo el resultado es EXITOSO (se guarda el intento) y el error va en el estado.</summary>
    public async Task<Result<LicenseStatusDto>> CheckinAsync(string kind, CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(cancellationToken);
        if (record?.Token is not { } token)
        {
            return Result.Failure<LicenseStatusDto>(LicenseErrors.NotActivated);
        }

        if (!cloud.IsConfigured)
        {
            return Result.Failure<LicenseStatusDto>(LicenseErrors.NotConfigured);
        }

        var site = await store.SiteAsync(cancellationToken);
        var seal = await anchor.SealNowAsync(cancellationToken);
        var started = clock.UtcNow;
        var result = await cloud.CheckinAsync(
            new CheckinRequest(token, device.Fingerprint?.ToString() ?? string.Empty, device.AppVersion, site?.OpenCashSessions ?? 0, started,
                seal is null ? null : new CheckinAuditSeal(installation.NodeId, seal.SealNo, seal.ShortCode, seal.SealedAt), site?.BusinessProfile),
            cancellationToken);
        await RecordAsync(kind, started, result, cancellationToken);
        if (result.Response is { } response && VerifyForThisDevice(response.Token, site?.OrganizationNit, record) is { } claims)
        {
            // El enlace de pago se reemplaza con lo que diga la nube: si ya no lo envía (pago aplicado), se borra.
            await store.SaveTokenAsync(
                response.Token, record.LicenseKeyPrefix, claims.IssuedAt, activation: false, Offset(claims), LicensePaymentLink.Normalize(response.PaymentUrl),
                TrustedNow(claims), cancellationToken);
        }
        else
        {
            var code = result.Response is not null ? LicenseErrors.TokenRejected.Code : result.ErrorCode;
            var message = code is null ? "Sin conexión con el servidor de licencias." : $"{code}: {result.ErrorMessage ?? LicenseErrors.Describe(code)}";
            await store.SaveCheckinErrorAsync(
                message,
                code == LicenseErrorCodes.LicenseRevoked ? true : null,
                code is LicenseErrorCodes.ReactivationRequired or LicenseErrorCodes.InstallationActiveOnOtherDevice or LicenseErrorCodes.InstallationOfOtherLicense ? true : null,
                cancellationToken);
            if (record.LastCheckinError is null && installation.IsSetupCompleted)
            {
                await audit.WriteAsync(
                    new AuditEntry("licensing", "LICENSE_CHECKIN_FAILED", "License", installation.NodeId, "Licencia",
                        $"La verificación de la licencia falló: {message} (se registra solo la primera de una racha).", Severity: AuditSeverity.Warning),
                    cancellationToken);
            }
        }

        return await StatusAsync(cancellationToken);
    }

    public async Task<Result<LicenseStatusDto>> DeactivateAsync(string? reason, CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(cancellationToken);
        if (record?.Token is not { } token)
        {
            return Result.Failure<LicenseStatusDto>(LicenseErrors.NotActivated);
        }

        if (!cloud.IsConfigured)
        {
            return Result.Failure<LicenseStatusDto>(LicenseErrors.NotConfigured);
        }

        var started = clock.UtcNow;
        var result = await cloud.DeactivateAsync(new DeactivationRequest(token, device.Fingerprint?.ToString() ?? string.Empty, reason), cancellationToken);
        await RecordAsync("DEACTIVATION", started, result, cancellationToken);

        // Si la nube ya no reconoce este equipo (ya fue liberado desde el portal), liberar localmente también es correcto.
        if (!result.Succeeded && result.ErrorCode != LicenseErrorCodes.ReactivationRequired)
        {
            return Result.Failure<LicenseStatusDto>(result.Unreachable ? LicenseErrors.Unreachable : LicenseErrors.FromCloud(result.ErrorCode!, result.ErrorMessage));
        }

        await store.ClearTokenAsync(cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("licensing", "LICENSE_DEACTIVATED", "License", installation.NodeId, "Licencia",
                $"Este equipo fue liberado de la licencia {record.LicenseKeyPrefix}…{(string.IsNullOrWhiteSpace(reason) ? string.Empty : $" Motivo: {reason}")}",
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return await StatusAsync(cancellationToken);
    }

    public Task<IReadOnlyList<LicenseCheckinDto>> ListCheckinsAsync(int limit, CancellationToken cancellationToken) =>
        store.ListCheckinsAsync(Math.Clamp(limit, 1, 500), cancellationToken);

    internal LicenseEvaluation Evaluate(LicenseRecord record) =>
        LicenseEvaluator.Evaluate(
            record, record.Token is null ? null : LicenseToken.Verify(record.Token, keys.Ring), device.Fingerprint, LocalDeviceRole, clock.UtcNow,
            _policy.MaxOfflineDays);

    /// <summary>
    /// Acepta el token solo si verifica con las claves de confianza, es de ESTA instalación y ESTE equipo, es de la empresa configurada
    /// (H7) y no es más viejo que el vigente (H4: no se puede volver a un token anterior para retroceder la hora confiable).
    /// </summary>
    private LicenseClaims? VerifyForThisDevice(string token, string? companyNit, LicenseRecord? current)
    {
        var verification = LicenseToken.Verify(token, keys.Ring);
        if (verification is not { IsValid: true, Claims: { } claims } || claims.InstallationId != installation.NodeId
            || device.Fingerprint is not { } fingerprint || !DeviceFingerprint.TryParse(claims.DeviceFingerprint, out var tokenDevice)
            || !fingerprint.Matches(tokenDevice)
            || (companyNit is not null && !LicenseEvaluator.SameNit(claims.OrganizationNit, companyNit)))
        {
            return null;
        }

        if (current?.Token is { } existing && LicenseToken.Verify(existing, keys.Ring) is { IsValid: true, Claims: { } vigente }
            && claims.IssuedAt < vigente.IssuedAt)
        {
            return null;
        }

        return claims;
    }

    /// <summary>Hora confiable al recibir un token (R2): la emisión firmada o el reloj corregido, la que sea mayor.</summary>
    private DateTimeOffset TrustedNow(LicenseClaims claims)
    {
        var corrected = clock.UtcNow.AddSeconds(Offset(claims));
        return corrected > claims.IssuedAt ? corrected : claims.IssuedAt;
    }

    /// <summary>
    /// ¿Pasó la ventana de 24 h para vender desde que quedó restringida (H6)? También con el estado alterado: una vuelta atrás del
    /// actualizador o un backup restaurado no dejan la tienda sin vender al instante (la fecha la reconcilia el archivo protegido).
    /// </summary>
    private static bool SalesWindowClosed(DateTimeOffset? restrictedSince, DateTimeOffset trustedNow) =>
        restrictedSince is { } since && trustedNow - since > LicenseEvaluator.RestrictedSalesWindow;

    /// <summary>Hora confiable (emisión del token) menos el reloj local; diferencias menores a 5 minutos se ignoran (latencia).</summary>
    private long Offset(LicenseClaims claims)
    {
        var offset = (claims.IssuedAt - clock.UtcNow).TotalSeconds;
        return Math.Abs(offset) < 300 ? 0 : (long)offset;
    }

    private Task RecordAsync(string kind, DateTimeOffset started, CloudResult result, CancellationToken cancellationToken) =>
        store.RecordCheckinAsync(
            new LicenseCheckinDto(
                started, kind, result.Succeeded, result.Unreachable ? LicenseErrors.Unreachable.Code : result.ErrorCode,
                result.Response?.SubscriptionStatus, (int)Math.Min(int.MaxValue, (clock.UtcNow - started).TotalMilliseconds)),
            cancellationToken);

    private static LicenseStatusDto ToDto(LicenseEvaluation evaluation, LicenseRecord record)
    {
        var claims = evaluation.Claims;
        return new LicenseStatusDto(
            evaluation.State, evaluation.Reason, evaluation.DaysLeft, claims?.Edition, claims?.OrganizationName, claims?.OrganizationNit,
            record.LicenseKeyPrefix, claims?.SubscriptionStatus, claims?.ValidUntil, claims?.GraceUntil,
            record.Token is null ? evaluation.DemoEndsAt : null, record.ActivatedAt, record.LastCheckinAt, record.LastCheckinError, evaluation.Notices,
            PaymentUrlOf(record));
    }

    /// <summary>Sin licencia activada no hay nada que pagar; lo guardado se vuelve a validar al leerlo (la BD no es de fiar).</summary>
    private static string? PaymentUrlOf(LicenseRecord record) => record.Token is null ? null : LicensePaymentLink.Normalize(record.PaymentUrl);
}

// ------------------------------------------------------------------------------------------------ Casos de uso

public sealed record GetLicenseStatusQuery : IQuery<LicenseStatusDto>;

internal sealed class GetLicenseStatusHandler(LicenseService service) : IQueryHandler<GetLicenseStatusQuery, LicenseStatusDto>
{
    public Task<Result<LicenseStatusDto>> Handle(GetLicenseStatusQuery query, CancellationToken cancellationToken) =>
        service.StatusAsync(cancellationToken, persist: false);
}

public sealed record ListLicenseCheckinsQuery(int Limit) : IQuery<IReadOnlyList<LicenseCheckinDto>>;

internal sealed class ListLicenseCheckinsHandler(LicenseService service) : IQueryHandler<ListLicenseCheckinsQuery, IReadOnlyList<LicenseCheckinDto>>
{
    public async Task<Result<IReadOnlyList<LicenseCheckinDto>>> Handle(ListLicenseCheckinsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await service.ListCheckinsAsync(query.Limit, cancellationToken));
}

/// <summary>Activa la licencia con la clave (solo el propietario, D12B-01).</summary>
public sealed record ActivateLicenseCommand(string LicenseKey) : ICommand<LicenseStatusDto>, IAllowedWhenRestricted;

internal sealed class ActivateLicenseValidator : AbstractValidator<ActivateLicenseCommand>
{
    public ActivateLicenseValidator() => RuleFor(x => x.LicenseKey).NotEmpty().MaximumLength(60);
}

internal sealed class ActivateLicenseHandler(LicenseService service) : ICommandHandler<ActivateLicenseCommand, LicenseStatusDto>
{
    public Task<Result<LicenseStatusDto>> Handle(ActivateLicenseCommand command, CancellationToken cancellationToken) =>
        service.ActivateAsync(command.LicenseKey, cancellationToken);
}

/// <summary>Verifica ahora con el servidor (<c>MANUAL</c>) o el check-in programado (<c>SCHEDULED</c>).</summary>
public sealed record CheckLicenseNowCommand(string Kind = "MANUAL") : ICommand<LicenseStatusDto>, IAllowedWhenRestricted;

internal sealed class CheckLicenseNowHandler(LicenseService service) : ICommandHandler<CheckLicenseNowCommand, LicenseStatusDto>
{
    public Task<Result<LicenseStatusDto>> Handle(CheckLicenseNowCommand command, CancellationToken cancellationToken) =>
        service.CheckinAsync(command.Kind, cancellationToken);
}

/// <summary>Libera este equipo (cambio de computador).</summary>
public sealed record DeactivateLicenseCommand(string? Reason) : ICommand<LicenseStatusDto>, IAllowedWhenRestricted;

internal sealed class DeactivateLicenseValidator : AbstractValidator<DeactivateLicenseCommand>
{
    public DeactivateLicenseValidator() => RuleFor(x => x.Reason).MaximumLength(200);
}

internal sealed class DeactivateLicenseHandler(LicenseService service) : ICommandHandler<DeactivateLicenseCommand, LicenseStatusDto>
{
    public Task<Result<LicenseStatusDto>> Handle(DeactivateLicenseCommand command, CancellationToken cancellationToken) =>
        service.DeactivateAsync(command.Reason, cancellationToken);
}

/// <summary>
/// Recalcula el estado (cada minuto y al arrancar): audita los cambios y sube la mayor hora observada. <c>Elapsed</c> = tiempo de
/// funcionamiento desde el recálculo anterior, medido por el proceso de fondo con el reloj monótono (H3).
/// </summary>
public sealed record RefreshLicenseCommand(TimeSpan? Elapsed = null) : ICommand<LicenseStatusDto>, IAllowedWhenRestricted;

internal sealed class RefreshLicenseHandler(LicenseService service) : ICommandHandler<RefreshLicenseCommand, LicenseStatusDto>
{
    public Task<Result<LicenseStatusDto>> Handle(RefreshLicenseCommand command, CancellationToken cancellationToken) =>
        service.StatusAsync(cancellationToken, elapsed: command.Elapsed);
}
