using System.Globalization;
using System.Net;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Licensing.Domain;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Licensing.Application;

/// <summary>Arma y firma el token (L-05) con el estado actual de la suscripción y los mensajes para el POS.</summary>
public sealed class LicenseTokenFactory(ILicenseTokenSigner signer, LicensingOptions options, IClock clock)
{
    public const string ExpiringCode = "SUBSCRIPTION_EXPIRING";
    public const string TrialCode = "TRIAL";
    public const string PastDueCode = "SUBSCRIPTION_PAST_DUE";
    public const string SuspendedCode = "SUBSCRIPTION_SUSPENDED";
    public const string CancelledCode = "SUBSCRIPTION_CANCELLED";
    public const string ExpiredCode = "SUBSCRIPTION_EXPIRED";
    public const string UpdateCode = "UPDATE_AVAILABLE";

    public LicenseTokenResponse Issue(Organization organization, Subscription subscription, License license, Installation installation, Device device, string? appVersion)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(license);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(device);
        var now = clock.UtcNow;
        var messages = Messages(subscription, now, appVersion);
        var claims = new LicenseClaims
        {
            LicenseId = license.Id,
            OrganizationNit = organization.NitNumber.ToString(),
            OrganizationName = organization.LegalName,
            InstallationId = installation.PosInstallationId,
            DeviceFingerprint = device.Fingerprint,
            DeviceRole = device.Role.ToCode(),
            Edition = subscription.Edition.ToCode(),
            SubscriptionStatus = subscription.Status.ToCode(),
            IssuedAt = now,
            ValidUntil = subscription.ValidUntil,
            GraceDays = subscription.GraceDays,
            RefreshAfter = now.AddHours(options.RefreshAfterHours),
            Messages = messages,
        };

        var token = signer.Sign(claims);
        return new LicenseTokenResponse(
            token, signer.PublicKey!.Kid, claims.SubscriptionStatus, claims.ValidUntil, claims.GraceDays, claims.RefreshAfter, messages);
    }

    internal List<LicenseMessage> Messages(Subscription subscription, DateTimeOffset now, string? appVersion)
    {
        var date = (DateTimeOffset instant) => clock.ToBusinessTime(instant).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var messages = new List<LicenseMessage>();
        switch (subscription.Status)
        {
            case SubscriptionStatus.Trial:
                messages.Add(new LicenseMessage(TrialCode, "INFO", $"Periodo de prueba hasta el {date(subscription.ValidUntil)}."));
                break;
            case SubscriptionStatus.PastDue:
                messages.Add(new LicenseMessage(PastDueCode, "WARNING",
                    $"La suscripción venció el {date(subscription.ValidUntil)}. El sistema sigue funcionando con avisos hasta el {date(subscription.GraceUntil)}: renueve para evitar restricciones."));
                break;
            case SubscriptionStatus.Suspended:
                messages.Add(new LicenseMessage(SuspendedCode, "CRITICAL", $"La suscripción está suspendida: {subscription.SuspendedReason}. Comuníquese con soporte."));
                break;
            case SubscriptionStatus.Cancelled:
                messages.Add(new LicenseMessage(CancelledCode, "CRITICAL", "La suscripción fue cancelada. Comuníquese con soporte."));
                break;
            case SubscriptionStatus.Expired:
                messages.Add(new LicenseMessage(ExpiredCode, "CRITICAL", "La suscripción está vencida y terminó el periodo de gracia. Renueve para quitar las restricciones."));
                break;
        }

        if (subscription.Status is SubscriptionStatus.Active or SubscriptionStatus.Trial
            && subscription.ValidUntil - now <= TimeSpan.FromDays(options.ExpiryWarningDays))
        {
            var days = Math.Max(0, (int)Math.Ceiling((subscription.ValidUntil - now).TotalDays));
            messages.Add(new LicenseMessage(ExpiringCode, "WARNING",
                $"La suscripción vence el {date(subscription.ValidUntil)} ({days} días). Renueve para evitar interrupciones."));
        }

        if (options.LatestPosVersion is { } latest && System.Version.TryParse(latest, out var published)
            && System.Version.TryParse(appVersion, out var current) && current < published)
        {
            messages.Add(new LicenseMessage(UpdateCode, "INFO", $"Hay una versión nueva del sistema disponible ({latest})."));
        }

        return messages;
    }
}

// ─────────────────────────────── Activación ───────────────────────────────

/// <summary>Activación desde el POS (§5.1). Anónima por diseño: la autentica la clave de licencia.</summary>
public sealed record ActivateLicenseCommand(ActivationRequest Request, IPAddress? IpAddress) : ICommand<LicenseTokenResponse>;

internal sealed class ActivateLicenseHandler(
    ILicensingStore store,
    ILicenseTokenSigner signer,
    ILicenseThrottle throttle,
    LicenseTokenFactory tokens,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<ActivateLicenseCommand, LicenseTokenResponse>
{
    public async Task<Result<LicenseTokenResponse>> Handle(ActivateLicenseCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (!signer.IsAvailable)
        {
            return LicenseApiErrors.SigningUnavailable;
        }

        if (!LicenseKey.TryNormalize(request.LicenseKey, out var key))
        {
            return LicenseApiErrors.KeyFormatInvalid;
        }

        if (!DeviceFingerprint.TryParse(request.Fingerprint, out var fingerprint) || !fingerprint.IsUsable)
        {
            return LicenseApiErrors.FingerprintInvalid;
        }

        if (!LicensingCodes.TryParseRole(request.DeviceRole, out var role))
        {
            return LicenseApiErrors.InvalidField(nameof(request.DeviceRole), "Rol del equipo inválido: ALL_IN_ONE o STORE_SERVER.");
        }

        if (request.InstallationId == Guid.Empty)
        {
            return LicenseApiErrors.InvalidField(nameof(request.InstallationId), "Falta el installation_id del POS.");
        }

        // Se buscan las licencias por el prefijo visible y la clave se compara en tiempo constante con TODAS las candidatas.
        // Una clave inexistente y una revocada reciben exactamente la misma respuesta.
        License? license = null;
        foreach (var candidate in await store.FindLicensesByPrefixAsync(LicenseKey.VisiblePrefix(key), cancellationToken))
        {
            if (candidate.Matches(key))
            {
                license ??= candidate;
            }
        }

        if (license is not { IsActive: true })
        {
            return LicenseApiErrors.KeyInvalid;
        }

        if (!throttle.TryAcquire(license.Id))
        {
            return LicenseApiErrors.TooManyRequests;
        }

        await store.LockLicenseAsync(license.Id, cancellationToken);
        var subscription = (await store.GetSubscriptionAsync(license.SubscriptionId, cancellationToken))!;
        var organization = (await store.GetOrganizationAsync(license.OrganizationId, cancellationToken))!;
        var change = new ChangeContext(SystemActor.Id, clock.UtcNow, ids);
        subscription.Refresh(change);
        if (!subscription.AllowsActivation)
        {
            return LicenseApiErrors.SubscriptionInactive;
        }

        if (!NitNumber.TryParse(request.OrganizationNit, out var nit) || !organization.HasNit(nit))
        {
            return LicenseApiErrors.NitMismatch;
        }

        if (!EditionRules.Allows(subscription.Edition, role))
        {
            return LicenseApiErrors.EditionMismatch;
        }

        var installation = await store.FindInstallationAsync(request.InstallationId, cancellationToken);
        if (installation is null)
        {
            if (!license.AdmitsAnotherInstallation(await store.CountActiveInstallationsAsync(license.Id, cancellationToken)))
            {
                return LicenseApiErrors.InstallationsExceeded;
            }

            installation = Installation.Register(ids.NewId(), request.InstallationId, license, change.Now);
            store.Add(installation);
        }
        else
        {
            if (installation.LicenseId != license.Id)
            {
                // Solo se acepta si la instalación era de una licencia REVOCADA de la misma empresa (p. ej. se regeneró la clave).
                var previous = await store.GetLicenseAsync(installation.LicenseId, cancellationToken);
                if (previous is not { IsActive: false } || previous.OrganizationId != license.OrganizationId || installation.MoveToLicense(license).IsFailure)
                {
                    return LicenseApiErrors.InstallationOfOtherLicense;
                }
            }

            if (installation.Status == InstallationStatus.Released
                && !license.AdmitsAnotherInstallation(await store.CountActiveInstallationsAsync(license.Id, cancellationToken)))
            {
                return LicenseApiErrors.InstallationsExceeded;
            }
        }

        var existing = installation.ActiveActivation?.Id;
        var activation = installation.Activate(
            license, new ActivationData(fingerprint, role, request.AppVersion, request.BranchName, request.DeviceName, request.OperatingSystem), change);
        if (activation.IsFailure)
        {
            return activation.Error;
        }

        if (existing != activation.Value.Id)
        {
            await audit.WriteAsync(
                new AuditEntry("licensing", "INSTALLATION_ACTIVATED", nameof(Installation), installation.Id, installation.AuditLabel,
                    $"{organization.LegalName}: instalación {installation.PosInstallationId} activada en el equipo {request.DeviceName ?? "(sin nombre)"} ({role.ToCode()}) con la licencia {license.KeyPrefix}… desde {command.IpAddress?.ToString() ?? "IP desconocida"}."),
                cancellationToken);
        }

        return tokens.Issue(organization, subscription, license, installation, installation.ActiveDevice!, request.AppVersion);
    }
}

// ─────────────────────────────── Check-in ───────────────────────────────

/// <summary>
/// Check-in diario (§5.2): el token (aunque esté vencido) autentica la instalación y la huella actual el equipo. Devuelve un
/// token renovado con el estado actual de la suscripción. Un rechazo también queda registrado (por eso el resultado es un
/// <see cref="Outcome{T}"/> y la transacción se confirma).
/// </summary>
public sealed record CheckinCommand(CheckinRequest Request, IPAddress? IpAddress) : ICommand<Outcome<LicenseTokenResponse>>;

internal sealed class CheckinHandler(
    ILicensingStore store,
    ILicenseTokenSigner signer,
    ITrustedSigningKeys trustedKeys,
    ILicenseThrottle throttle,
    LicenseTokenFactory tokens,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<CheckinCommand, Outcome<LicenseTokenResponse>>
{
    public async Task<Result<Outcome<LicenseTokenResponse>>> Handle(CheckinCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (!signer.IsAvailable)
        {
            return Outcome.Fail<LicenseTokenResponse>(LicenseApiErrors.SigningUnavailable);
        }

        var verification = LicenseToken.Verify(request.Token, await trustedKeys.GetAsync(cancellationToken));
        if (verification is not { IsValid: true, Claims: { } claims })
        {
            return Outcome.Fail<LicenseTokenResponse>(LicenseApiErrors.TokenInvalid);
        }

        var installation = await store.FindInstallationAsync(claims.InstallationId, cancellationToken);
        if (installation is null)
        {
            return Outcome.Fail<LicenseTokenResponse>(LicenseApiErrors.ReactivationRequired);
        }

        var organization = (await store.GetOrganizationAsync(installation.OrganizationId, cancellationToken))!;
        if (!NitNumber.TryParse(claims.OrganizationNit, out var nit) || !organization.HasNit(nit))
        {
            return Outcome.Fail<LicenseTokenResponse>(LicenseApiErrors.TokenInvalid);
        }

        if (!throttle.TryAcquire(installation.LicenseId))
        {
            return Outcome.Fail<LicenseTokenResponse>(LicenseApiErrors.TooManyRequests);
        }

        var now = clock.UtcNow;
        var report = new CheckinReport(request.AppVersion, request.ActiveTerminals, request.ReportedClock, command.IpAddress);
        var license = (await store.GetLicenseAsync(installation.LicenseId, cancellationToken))!;
        Error? rejection = null;
        if (!DeviceFingerprint.TryParse(request.Fingerprint, out var fingerprint))
        {
            rejection = LicenseApiErrors.FingerprintInvalid;
        }
        else if (!license.IsActive)
        {
            rejection = LicenseApiErrors.LicenseRevoked;
        }
        else if (installation.Authenticate(fingerprint, now) is { IsFailure: true } failed)
        {
            rejection = failed.Error;
        }

        if (rejection is not null)
        {
            store.Add(Checkin.Rejected(ids.NewId(), installation, report, rejection.Code, now));
            await audit.WriteAsync(
                new AuditEntry("licensing", "CHECKIN_REJECTED", nameof(Installation), installation.Id, installation.AuditLabel,
                    $"{organization.LegalName}: check-in rechazado ({rejection.Code}) desde {command.IpAddress?.ToString() ?? "IP desconocida"}.",
                    Severity: AuditSeverity.Warning),
                cancellationToken);
            return Outcome.Fail<LicenseTokenResponse>(rejection);
        }

        var subscription = (await store.GetSubscriptionAsync(license.SubscriptionId, cancellationToken))!;
        subscription.Refresh(new ChangeContext(SystemActor.Id, now, ids));
        installation.RecordCheckin(request.AppVersion, request.ActiveTerminals, command.IpAddress, now);
        var response = tokens.Issue(organization, subscription, license, installation, installation.ActiveDevice!, request.AppVersion);
        store.Add(Checkin.Issued(ids.NewId(), installation, installation.ActiveActivation!.Id, report, subscription.Status, subscription.ValidUntil, now));
        return Outcome.Ok(response);
    }
}

// ─────────────────────────────── Liberación desde el POS ───────────────────────────────

/// <summary>El POS libera su propio equipo (p. ej. antes de pasar a otro computador).</summary>
public sealed record DeactivateCommand(DeactivationRequest Request, IPAddress? IpAddress) : ICommand;

internal sealed class DeactivateHandler(
    ILicensingStore store,
    ITrustedSigningKeys trustedKeys,
    ILicenseThrottle throttle,
    IAuditWriter audit,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<DeactivateCommand>
{
    public async Task<Result> Handle(DeactivateCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var verification = LicenseToken.Verify(request.Token, await trustedKeys.GetAsync(cancellationToken));
        if (verification is not { IsValid: true, Claims: { } claims })
        {
            return LicenseApiErrors.TokenInvalid;
        }

        var installation = await store.FindInstallationAsync(claims.InstallationId, cancellationToken);
        if (installation is null || !DeviceFingerprint.TryParse(request.Fingerprint, out var fingerprint))
        {
            return LicenseApiErrors.ReactivationRequired;
        }

        if (!throttle.TryAcquire(installation.LicenseId))
        {
            return LicenseApiErrors.TooManyRequests;
        }

        var now = clock.UtcNow;
        var authenticated = installation.Authenticate(fingerprint, now);
        if (authenticated.IsFailure)
        {
            return authenticated.Error;
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Liberado desde el propio POS" : request.Reason.Trim();
        var released = installation.Release(authenticated.Value.Activation.Id, reason.Length < 5 ? $"Liberado desde el POS: {reason}" : reason,
            new ChangeContext(SystemActor.Id, now, ids));
        if (released.IsFailure)
        {
            return released.Error;
        }

        await audit.WriteAsync(
            new AuditEntry("licensing", "DEVICE_RELEASED", nameof(Installation), installation.Id, installation.AuditLabel,
                $"Equipo liberado desde el POS ({reason}) desde {command.IpAddress?.ToString() ?? "IP desconocida"}.", Severity: AuditSeverity.Warning),
            cancellationToken);
        return Result.Success();
    }
}

// ─────────────────────────────── Claves públicas ───────────────────────────────

/// <summary>Claves públicas de confianza por <c>kid</c> (la activa, las de reserva y las retiradas que aún verifican tokens).</summary>
public sealed record GetPublicKeysQuery : IQuery<PublicKeysResponse>;

internal sealed class GetPublicKeysHandler(ILicensingStore store) : IQueryHandler<GetPublicKeysQuery, PublicKeysResponse>
{
    public async Task<Result<PublicKeysResponse>> Handle(GetPublicKeysQuery request, CancellationToken cancellationToken) =>
        new PublicKeysResponse([.. (await store.GetSigningKeysAsync(cancellationToken))
            .Where(k => k.IsTrusted)
            .OrderBy(k => k.Status)
            .ThenByDescending(k => k.CreatedAt)
            .Select(k => new PublicKeyDto(k.Kid, "OKP", "Ed25519", k.PublicKey, k.Status.ToString().ToUpperInvariant()))]);
}
