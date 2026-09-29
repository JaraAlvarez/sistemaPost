using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Licensing.Domain;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Licensing.Application;

// ─────────────────────────────── Licencias (claves) ───────────────────────────────

/// <summary>Genera la clave de la empresa (se muestra UNA vez). <c>MaxInstallations</c> nulo = sin límite de sucursales.</summary>
[RequiresPermission(CloudPermissions.LicenseManage)]
public sealed record GenerateLicenseCommand(Guid OrganizationId, int? MaxInstallations = null) : ICommand<GeneratedLicenseDto>;

internal sealed class GenerateLicenseHandler(ILicensingStore store, PortalChange portal, IAuditWriter audit) : ICommandHandler<GenerateLicenseCommand, GeneratedLicenseDto>
{
    public async Task<Result<GeneratedLicenseDto>> Handle(GenerateLicenseCommand request, CancellationToken cancellationToken)
    {
        var organization = await store.GetOrganizationAsync(request.OrganizationId, cancellationToken);
        if (organization is null)
        {
            return LicensingErrors.OrganizationNotFound;
        }

        if (await store.GetCurrentSubscriptionAsync(organization.Id, cancellationToken) is not { } subscription)
        {
            return LicensingErrors.SubscriptionRequired;
        }

        if (await store.GetActiveLicenseAsync(organization.Id, cancellationToken) is not null)
        {
            return LicensingErrors.LicenseExists;
        }

        var change = portal.Create();
        var key = LicenseKey.Generate();
        var license = License.Issue(change.Ids.NewId(), subscription, key, request.MaxInstallations, change.Now);
        if (license.IsFailure)
        {
            return license.Error;
        }

        store.Add(license.Value);
        await audit.WriteAsync(
            new AuditEntry("licensing", "LICENSE_ISSUED", nameof(License), license.Value.Id, organization.AuditLabel,
                $"{portal.ActorName} generó la clave {license.Value.KeyPrefix}… de {organization.LegalName} ({Limit(request.MaxInstallations)}).",
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return new GeneratedLicenseDto(license.Value.Id, organization.Id, key, license.Value.KeyPrefix);
    }

    internal static string Limit(int? max) => max is null ? "instalaciones sin límite" : $"máximo {max} instalaciones";
}

/// <summary>
/// Regenera la clave (se perdió o se filtró): revoca la anterior y emite otra. Las instalaciones activas pasan a la licencia nueva
/// sin reactivarse (reciben el nuevo <c>lic</c> en su siguiente check-in); la clave anterior ya no activa equipos nuevos.
/// </summary>
[RequiresPermission(CloudPermissions.LicenseManage)]
public sealed record RegenerateLicenseCommand(Guid LicenseId, string Reason) : ICommand<GeneratedLicenseDto>;

internal sealed class RegenerateLicenseHandler(ILicensingStore store, PortalChange portal, IAuditWriter audit) : ICommandHandler<RegenerateLicenseCommand, GeneratedLicenseDto>
{
    public async Task<Result<GeneratedLicenseDto>> Handle(RegenerateLicenseCommand request, CancellationToken cancellationToken)
    {
        var old = await store.GetLicenseAsync(request.LicenseId, cancellationToken);
        if (old is null)
        {
            return LicensingErrors.LicenseNotFound;
        }

        if ((request.Reason ?? string.Empty).Trim().Length is < 5 or > 250)
        {
            return LicensingErrors.ReasonRequired;
        }

        var subscription = (await store.GetSubscriptionAsync(old.SubscriptionId, cancellationToken))!;
        var change = portal.Create();
        var key = LicenseKey.Generate();
        var created = License.Issue(change.Ids.NewId(), subscription, key, old.MaxInstallations, change.Now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var revoked = old.Revoke($"Clave regenerada: {request.Reason?.Trim()}", change.Now, created.Value.Id);
        if (revoked.IsFailure)
        {
            return revoked.Error;
        }

        // Solo puede haber una licencia vigente por empresa: la anterior se revoca antes de insertar la nueva.
        await store.FlushAsync(cancellationToken);
        store.Add(created.Value);
        foreach (var installation in await store.GetInstallationsOfLicenseAsync(old.Id, cancellationToken))
        {
            installation.MoveToLicense(created.Value);
        }

        var organization = (await store.GetOrganizationAsync(old.OrganizationId, cancellationToken))!;
        await audit.WriteAsync(
            new AuditEntry("licensing", "LICENSE_REGENERATED", nameof(License), created.Value.Id, organization.AuditLabel,
                $"{portal.ActorName} regeneró la clave de {organization.LegalName}: {old.KeyPrefix}… revocada, nueva {created.Value.KeyPrefix}…. Motivo: {request.Reason?.Trim()}.",
                Severity: AuditSeverity.Critical),
            cancellationToken);
        return new GeneratedLicenseDto(created.Value.Id, organization.Id, key, created.Value.KeyPrefix);
    }
}

/// <summary>Revoca la clave sin reemplazo: los POS de la empresa reciben <c>LICENSE.REVOKED</c> en su siguiente check-in.</summary>
[RequiresPermission(CloudPermissions.LicenseManage)]
public sealed record RevokeLicenseCommand(Guid LicenseId, string Reason) : ICommand;

internal sealed class RevokeLicenseHandler(ILicensingStore store, PortalChange portal, IAuditWriter audit) : ICommandHandler<RevokeLicenseCommand>
{
    public async Task<Result> Handle(RevokeLicenseCommand request, CancellationToken cancellationToken)
    {
        var license = await store.GetLicenseAsync(request.LicenseId, cancellationToken);
        if (license is null)
        {
            return LicensingErrors.LicenseNotFound;
        }

        var revoked = license.Revoke(request.Reason, portal.Create().Now);
        if (revoked.IsFailure)
        {
            return revoked;
        }

        var organization = (await store.GetOrganizationAsync(license.OrganizationId, cancellationToken))!;
        await audit.WriteAsync(
            new AuditEntry("licensing", "LICENSE_REVOKED", nameof(License), license.Id, organization.AuditLabel,
                $"{portal.ActorName} revocó la clave {license.KeyPrefix}… de {organization.LegalName}: {request.Reason.Trim()}.", Severity: AuditSeverity.Critical),
            cancellationToken);
        return Result.Success();
    }
}

[RequiresPermission(CloudPermissions.LicenseManage)]
public sealed record SetMaxInstallationsCommand(Guid LicenseId, int? MaxInstallations) : ICommand;

internal sealed class SetMaxInstallationsHandler(ILicensingStore store, PortalChange portal, IAuditWriter audit) : ICommandHandler<SetMaxInstallationsCommand>
{
    public async Task<Result> Handle(SetMaxInstallationsCommand request, CancellationToken cancellationToken)
    {
        var license = await store.GetLicenseAsync(request.LicenseId, cancellationToken);
        if (license is null)
        {
            return LicensingErrors.LicenseNotFound;
        }

        var previous = license.MaxInstallations;
        var updated = license.SetMaxInstallations(request.MaxInstallations);
        if (updated.IsFailure)
        {
            return updated;
        }

        var organization = (await store.GetOrganizationAsync(license.OrganizationId, cancellationToken))!;
        await audit.WriteAsync(
            new AuditEntry("licensing", "LICENSE_LIMIT_CHANGED", nameof(License), license.Id, organization.AuditLabel,
                $"{portal.ActorName} cambió el límite de la licencia de {organization.LegalName}: de {GenerateLicenseHandler.Limit(previous)} a {GenerateLicenseHandler.Limit(request.MaxInstallations)}."),
            cancellationToken);
        return Result.Success();
    }
}

// ─────────────────────────────── Instalaciones y equipos ───────────────────────────────

[RequiresPermission(CloudPermissions.LicensingView)]
public sealed record ListInstallationsQuery(Guid? OrganizationId = null, string? Search = null) : IQuery<IReadOnlyList<InstallationDto>>;

internal sealed class ListInstallationsHandler(ILicensingReadModel read) : IQueryHandler<ListInstallationsQuery, IReadOnlyList<InstallationDto>>
{
    public async Task<Result<IReadOnlyList<InstallationDto>>> Handle(ListInstallationsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await read.ListInstallationsAsync(request.OrganizationId, request.Search, cancellationToken));
}

/// <summary>Libera el equipo de una instalación (cambio de PC o equipo dañado): la sucursal activa el equipo nuevo con su clave.</summary>
[RequiresPermission(CloudPermissions.DeviceRelease)]
public sealed record ReleaseActivationCommand(Guid ActivationId, string Reason) : ICommand;

internal sealed class ReleaseActivationHandler(ILicensingStore store, PortalChange portal, IAuditWriter audit) : ICommandHandler<ReleaseActivationCommand>
{
    public async Task<Result> Handle(ReleaseActivationCommand request, CancellationToken cancellationToken)
    {
        var installation = await store.FindInstallationByActivationAsync(request.ActivationId, cancellationToken);
        if (installation is null)
        {
            return LicensingErrors.ActivationNotFound;
        }

        var device = installation.ActiveDevice;
        var released = installation.Release(request.ActivationId, request.Reason, portal.Create());
        if (released.IsFailure)
        {
            return released;
        }

        var organization = (await store.GetOrganizationAsync(installation.OrganizationId, cancellationToken))!;
        await audit.WriteAsync(
            new AuditEntry("licensing", "DEVICE_RELEASED", nameof(Installation), installation.Id, installation.AuditLabel,
                $"{portal.ActorName} liberó el equipo {device?.DeviceName ?? device?.Id.ToString()} de {organization.LegalName}: {request.Reason.Trim()}.",
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return Result.Success();
    }
}

// ─────────────────────────────── Tablero ───────────────────────────────

[RequiresPermission(CloudPermissions.DashboardView)]
public sealed record GetDashboardQuery : IQuery<DashboardDto>;

internal sealed class GetDashboardHandler(ILicensingReadModel read, LicensingOptions options, IClock clock) : IQueryHandler<GetDashboardQuery, DashboardDto>
{
    public async Task<Result<DashboardDto>> Handle(GetDashboardQuery request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var subscriptions = await read.ListSubscriptionsAsync(now, cancellationToken);
        var installations = await read.ListInstallationsAsync(null, null, cancellationToken);
        var (accounts, organizations) = await read.CountAsync(cancellationToken);
        var current = subscriptions.Where(s => s.Status is SubscriptionStatuses.Active or SubscriptionStatuses.Trial).ToList();
        var expiring = current.Where(s => s.DaysLeft is >= 0 and <= 30).OrderBy(s => s.ValidUntil).ToList();
        var threshold = now.AddDays(-options.NoCheckinAlertDays);
        var silent = installations
            .Where(i => i.Status == "ACTIVE" && (i.LastCheckinAt ?? i.FirstActivatedAt) < threshold)
            .OrderBy(i => i.LastCheckinAt ?? i.FirstActivatedAt)
            .ToList();

        return new DashboardDto(
            accounts,
            organizations,
            installations.Count(i => i.Status == "ACTIVE"),
            expiring.Count(s => s.DaysLeft <= 7),
            expiring.Count(s => s.DaysLeft <= 15),
            expiring.Count,
            expiring,
            [.. subscriptions.Where(s => s.Status == SubscriptionStatuses.PastDue).OrderBy(s => s.GraceUntil)],
            [.. subscriptions.Where(s => s.Status == SubscriptionStatuses.Suspended)],
            [.. subscriptions.Where(s => s.Status == SubscriptionStatuses.Expired).OrderByDescending(s => s.ValidUntil)],
            silent,
            options.NoCheckinAlertDays,
            subscriptions.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));
    }
}

// ─────────────────────────────── Claves de firma ───────────────────────────────

[RequiresPermission(CloudPermissions.SigningKeyView)]
public sealed record ListSigningKeysQuery : IQuery<IReadOnlyList<SigningKeyDto>>;

internal sealed class ListSigningKeysHandler(ILicensingStore store, ILicenseTokenSigner signer) : IQueryHandler<ListSigningKeysQuery, IReadOnlyList<SigningKeyDto>>
{
    public async Task<Result<IReadOnlyList<SigningKeyDto>>> Handle(ListSigningKeysQuery request, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<SigningKeyDto>>([.. (await store.GetSigningKeysAsync(cancellationToken))
            .OrderBy(k => k.Status)
            .ThenByDescending(k => k.CreatedAt)
            .Select(k => new SigningKeyDto(
                k.Kid, k.PublicKey, k.Status.ToString().ToUpperInvariant(), k.CreatedAt, k.ActivatedAt, k.RetiredAt, k.RevokedAt,
                string.Equals(k.Kid, signer.PublicKey?.Kid, StringComparison.Ordinal)))]);
}

/// <summary>
/// Al arrancar: la clave del archivo configurado es LA clave activa. Si es nueva se registra; si estaba de reserva se activa y
/// la anterior pasa a retirada (sigue verificando tokens emitidos). Si fue retirada o revocada, el servidor NO firma.
/// Devuelve <c>true</c> si el servidor puede firmar. Solo lo usan el arranque y la consola (sin permiso del portal).
/// </summary>
public sealed record EnsureActiveSigningKeyCommand : ICommand<bool>;

internal sealed class EnsureActiveSigningKeyHandler(
    ILicensingStore store, ILicenseTokenSigner signer, ITrustedSigningKeys trusted, IAuditWriter audit, IClock clock)
    : ICommandHandler<EnsureActiveSigningKeyCommand, bool>
{
    public async Task<Result<bool>> Handle(EnsureActiveSigningKeyCommand request, CancellationToken cancellationToken)
    {
        if (signer.PublicKey is not { } publicKey)
        {
            signer.SetEnabled(false);
            return false;
        }

        var now = clock.UtcNow;
        var keys = await store.GetSigningKeysAsync(cancellationToken);
        var key = keys.FirstOrDefault(k => k.Kid == publicKey.Kid);
        if (key is { Status: SigningKeyStatus.Active })
        {
            signer.SetEnabled(true);
            return true;
        }

        if (key is { Status: SigningKeyStatus.Retired or SigningKeyStatus.Revoked })
        {
            signer.SetEnabled(false);
            await audit.WriteAsync(
                new AuditEntry("licensing", "SIGNING_KEY_REJECTED", nameof(SigningKey), null, key.Kid,
                    $"La clave de firma configurada ({key.Kid}) está {key.Status.ToString().ToUpperInvariant()}: el servidor no emite tokens hasta configurar otra.",
                    Severity: AuditSeverity.Critical),
                cancellationToken);
            return false;
        }

        if (key is null)
        {
            key = SigningKey.Register(publicKey.X, now).Value;
            store.Add(key);
        }

        if (keys.FirstOrDefault(k => k.Status == SigningKeyStatus.Active) is { } previous)
        {
            previous.Retire(now);
            await store.FlushAsync(cancellationToken);
        }

        key.Activate(now);
        await audit.WriteAsync(
            new AuditEntry("licensing", "SIGNING_KEY_ACTIVATED", nameof(SigningKey), null, key.Kid,
                $"Clave de firma {key.Kid} activada.", Severity: AuditSeverity.Critical),
            cancellationToken);
        trusted.Invalidate();
        signer.SetEnabled(true);
        return true;
    }
}

/// <summary>Publica una clave de reserva (su privada queda guardada fuera del servidor) para poder rotar sin reinstalar el POS.</summary>
public sealed record RegisterStandbySigningKeyCommand(string PublicKey) : ICommand<string>;

internal sealed class RegisterStandbySigningKeyHandler(ILicensingStore store, ITrustedSigningKeys trusted, IAuditWriter audit, IClock clock)
    : ICommandHandler<RegisterStandbySigningKeyCommand, string>
{
    public async Task<Result<string>> Handle(RegisterStandbySigningKeyCommand request, CancellationToken cancellationToken)
    {
        var key = SigningKey.Register(request.PublicKey, clock.UtcNow);
        if (key.IsFailure)
        {
            return key.Error;
        }

        if ((await store.GetSigningKeysAsync(cancellationToken)).Any(k => k.Kid == key.Value.Kid))
        {
            return Error.Conflict("LICENSING.SIGNING_KEY_EXISTS", "Esa clave ya está registrada.");
        }

        store.Add(key.Value);
        await audit.WriteAsync(
            new AuditEntry("licensing", "SIGNING_KEY_REGISTERED", nameof(SigningKey), null, key.Value.Kid,
                $"Clave de firma de reserva {key.Value.Kid} publicada.", Severity: AuditSeverity.Critical),
            cancellationToken);
        trusted.Invalidate();
        return key.Value.Kid;
    }
}

/// <summary>Revoca una clave comprometida: sus tokens dejan de aceptarse en el check-in y deja de publicarse.</summary>
public sealed record RevokeSigningKeyCommand(string Kid) : ICommand;

internal sealed class RevokeSigningKeyHandler(ILicensingStore store, ITrustedSigningKeys trusted, IAuditWriter audit, IClock clock)
    : ICommandHandler<RevokeSigningKeyCommand>
{
    public async Task<Result> Handle(RevokeSigningKeyCommand request, CancellationToken cancellationToken)
    {
        var key = (await store.GetSigningKeysAsync(cancellationToken)).FirstOrDefault(k => k.Kid == request.Kid);
        if (key is null)
        {
            return LicensingErrors.SigningKeyNotFound;
        }

        var revoked = key.Revoke(clock.UtcNow);
        if (revoked.IsFailure)
        {
            return revoked;
        }

        await audit.WriteAsync(
            new AuditEntry("licensing", "SIGNING_KEY_REVOKED", nameof(SigningKey), null, key.Kid, $"Clave de firma {key.Kid} revocada.", Severity: AuditSeverity.Critical),
            cancellationToken);
        trusted.Invalidate();
        return Result.Success();
    }
}

/// <summary>Publica una clave de reserva desde el portal (misma regla que la consola, con permiso del superadministrador).</summary>
[RequiresPermission(CloudPermissions.SigningKeyManage)]
public sealed record PortalRegisterStandbySigningKeyCommand(string PublicKey) : ICommand<string>;

internal sealed class PortalRegisterStandbySigningKeyHandler(ILicensingStore store, ITrustedSigningKeys trusted, IAuditWriter audit, IClock clock)
    : ICommandHandler<PortalRegisterStandbySigningKeyCommand, string>
{
    public Task<Result<string>> Handle(PortalRegisterStandbySigningKeyCommand request, CancellationToken cancellationToken) =>
        new RegisterStandbySigningKeyHandler(store, trusted, audit, clock).Handle(new RegisterStandbySigningKeyCommand(request.PublicKey?.Trim() ?? string.Empty), cancellationToken);
}

/// <summary>Revoca desde el portal una clave comprometida (de reserva o retirada; la activa se reemplaza primero en el servidor).</summary>
[RequiresPermission(CloudPermissions.SigningKeyManage)]
public sealed record PortalRevokeSigningKeyCommand(string Kid) : ICommand;

internal sealed class PortalRevokeSigningKeyHandler(ILicensingStore store, ITrustedSigningKeys trusted, IAuditWriter audit, IClock clock)
    : ICommandHandler<PortalRevokeSigningKeyCommand>
{
    public Task<Result> Handle(PortalRevokeSigningKeyCommand request, CancellationToken cancellationToken) =>
        new RevokeSigningKeyHandler(store, trusted, audit, clock).Handle(new RevokeSigningKeyCommand(request.Kid), cancellationToken);
}
