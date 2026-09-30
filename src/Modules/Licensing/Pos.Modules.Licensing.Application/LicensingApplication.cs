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

// ------------------------------------------------------------------------------------------------ Puertos

/// <summary>Datos de la empresa y la tienda que se declaran al activar y en el check-in.</summary>
public sealed record LicenseSiteInfo(string OrganizationNit, string OrganizationName, string? BranchName, int OpenCashSessions);

public interface ILicenseStateStore
{
    /// <summary>La fila del nodo; <c>null</c> si todavía no existe (ver <see cref="EnsureAsync"/>).</summary>
    Task<LicenseRecord?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Crea la fila del nodo si no existe (la mayor hora observada empieza en la creación de la instalación).</summary>
    Task EnsureAsync(CancellationToken cancellationToken);

    Task<string?> GetLastStateAsync(CancellationToken cancellationToken);

    Task SetLastStateAsync(string state, CancellationToken cancellationToken);

    /// <summary>Guarda un token verificado (con la hora CONFIABLE de su emisión) y limpia los indicadores (revocada, reactivación, liberado).</summary>
    Task SaveTokenAsync(string token, string? keyPrefix, DateTimeOffset at, bool activation, long clockOffsetSeconds, CancellationToken cancellationToken);

    Task SaveCheckinErrorAsync(string error, bool? revoked, bool? reactivationRequired, CancellationToken cancellationToken);

    /// <summary>Equipo liberado: se olvida el token.</summary>
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

/// <summary>Último estado calculado, en memoria: lo leen el filtro del pipeline, <c>/auth/me</c> y los documentos impresos.</summary>
public sealed class LicenseStateCache : ILicenseGate, ILicenseStatus
{
    private volatile Snapshot? _snapshot;

    private sealed record Snapshot(LicenseEvaluation Evaluation, LicenseGateState Gate);

    public LicenseEvaluation? Current => _snapshot?.Evaluation;

    LicenseGateState ILicenseGate.Current => _snapshot?.Gate ?? LicenseGateState.Unrestricted;

    public LicenseSummaryDto Summary => _snapshot is { } s
        ? new LicenseSummaryDto(s.Evaluation.State, s.Evaluation.DaysLeft, s.Evaluation.Notices)
        : new LicenseSummaryDto(LicenseStates.Demo, null, []);

    public void Update(LicenseEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _snapshot = new Snapshot(
            evaluation,
            new LicenseGateState(evaluation.Restricted, evaluation.State == LicenseStates.Demo, evaluation.Restricted ? evaluation.Reason : null));
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
    IClock clock)
{
    public string LocalDeviceRole => installation.NodeRole == NodeRole.StoreServer ? DeviceRoles.StoreServer : DeviceRoles.AllInOne;

    /// <summary>
    /// Recalcula el estado. Con <paramref name="persist"/> (dentro de un comando) además audita si cambió (<c>LICENSE_STATE_CHANGED</c>)
    /// y sube la mayor hora observada; las consultas solo leen.
    /// </summary>
    public async Task<(LicenseEvaluation Evaluation, LicenseRecord Record)?> RefreshAsync(bool persist, CancellationToken cancellationToken)
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
        cache.Update(evaluation);
        if (!persist)
        {
            return (evaluation, record);
        }

        if (!evaluation.ClockRollback && evaluation.EffectiveNow > record.MaxObservedUtc)
        {
            await store.RaiseMaxObservedAsync(evaluation.EffectiveNow, cancellationToken);
        }

        var previous = await store.GetLastStateAsync(cancellationToken);
        if (previous != evaluation.State)
        {
            await store.SetLastStateAsync(evaluation.State, cancellationToken);
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

    public async Task<Result<LicenseStatusDto>> StatusAsync(CancellationToken cancellationToken, bool persist = true) =>
        await RefreshAsync(persist, cancellationToken) is { } current
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
                site.BranchName, device.DeviceName, device.OperatingSystem),
            cancellationToken);
        await RecordAsync("ACTIVATION", started, result, cancellationToken);
        if (result.Response is not { } response)
        {
            return Result.Failure<LicenseStatusDto>(result.Unreachable ? LicenseErrors.Unreachable : LicenseErrors.FromCloud(result.ErrorCode!, result.ErrorMessage));
        }

        if (VerifyForThisDevice(response.Token) is not { } claims)
        {
            return Result.Failure<LicenseStatusDto>(LicenseErrors.TokenRejected);
        }

        await store.SaveTokenAsync(response.Token, LicenseKey.VisiblePrefix(key), claims.IssuedAt, activation: true, Offset(claims), cancellationToken);
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
                seal is null ? null : new CheckinAuditSeal(installation.NodeId, seal.SealNo, seal.ShortCode, seal.SealedAt)),
            cancellationToken);
        await RecordAsync(kind, started, result, cancellationToken);
        if (result.Response is { } response && VerifyForThisDevice(response.Token) is { } claims)
        {
            await store.SaveTokenAsync(response.Token, record.LicenseKeyPrefix, claims.IssuedAt, activation: false, Offset(claims), cancellationToken);
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
            record, record.Token is null ? null : LicenseToken.Verify(record.Token, keys.Ring), device.Fingerprint, LocalDeviceRole, clock.UtcNow);

    /// <summary>Acepta el token solo si verifica con las claves de confianza y es de ESTA instalación y ESTE equipo.</summary>
    private LicenseClaims? VerifyForThisDevice(string token)
    {
        var verification = LicenseToken.Verify(token, keys.Ring);
        if (verification is not { IsValid: true, Claims: { } claims } || claims.InstallationId != installation.NodeId
            || device.Fingerprint is not { } fingerprint || !DeviceFingerprint.TryParse(claims.DeviceFingerprint, out var tokenDevice)
            || !fingerprint.Matches(tokenDevice))
        {
            return null;
        }

        return claims;
    }

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
            record.Token is null ? evaluation.DemoEndsAt : null, record.ActivatedAt, record.LastCheckinAt, record.LastCheckinError, evaluation.Notices);
    }
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

/// <summary>Recalcula el estado (cada minuto y al arrancar): audita los cambios y sube la mayor hora observada.</summary>
public sealed record RefreshLicenseCommand : ICommand<LicenseStatusDto>, IAllowedWhenRestricted;

internal sealed class RefreshLicenseHandler(LicenseService service) : ICommandHandler<RefreshLicenseCommand, LicenseStatusDto>
{
    public Task<Result<LicenseStatusDto>> Handle(RefreshLicenseCommand command, CancellationToken cancellationToken) => service.StatusAsync(cancellationToken);
}
