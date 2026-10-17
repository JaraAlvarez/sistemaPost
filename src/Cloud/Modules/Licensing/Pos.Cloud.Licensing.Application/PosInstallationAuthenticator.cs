using Pos.Cloud.Abstractions;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Licensing.Application;

/// <summary>
/// Identifica la tienda que envía datos (Fase 16, D16-03): el token de licencia debe verificar con las claves de confianza, su licencia debe
/// estar vigente y la huella debe ser la del equipo activado (2 de 3). Mismo criterio que el check-in.
/// </summary>
public sealed class PosInstallationAuthenticator(ILicensingStore store, ITrustedSigningKeys trustedKeys, IClock clock) : IPosInstallationAuthenticator
{
    public async Task<PosInstallationIdentity?> AuthenticateAsync(string token, string fingerprint, CancellationToken cancellationToken)
    {
        var verification = LicenseToken.Verify(token, await trustedKeys.GetAsync(cancellationToken));
        if (verification is not { IsValid: true, Claims: { } claims } || !DeviceFingerprint.TryParse(fingerprint, out var device))
        {
            return null;
        }

        var installation = await store.FindInstallationAsync(claims.InstallationId, cancellationToken);
        if (installation is null || (await store.GetLicenseAsync(installation.LicenseId, cancellationToken)) is not { IsActive: true }
            || installation.Authenticate(device, clock.UtcNow).IsFailure)
        {
            return null;
        }

        return await IdentityAsync(installation, cancellationToken);
    }

    /// <summary>
    /// Instalación de un paquete cargado en el portal: debe seguir activada (no liberada) y su licencia vigente; si no, el paquete no se aplica
    /// (revisión S2).
    /// </summary>
    public async Task<PosInstallationIdentity?> FindAsync(Guid installationId, CancellationToken cancellationToken)
    {
        if (await store.FindInstallationAsync(installationId, cancellationToken) is not { Status: Domain.InstallationStatus.Active } installation
            || (await store.GetLicenseAsync(installation.LicenseId, cancellationToken)) is not { IsActive: true })
        {
            return null;
        }

        return await IdentityAsync(installation, cancellationToken);
    }

    public async Task<string?> InactiveReasonAsync(Guid installationId, CancellationToken cancellationToken)
    {
        if (await store.FindInstallationAsync(installationId, cancellationToken) is not { } installation)
        {
            return PosInstallationInactiveReasons.NotFound;
        }

        if (installation.Status != Domain.InstallationStatus.Active)
        {
            return PosInstallationInactiveReasons.Released;
        }

        return (await store.GetLicenseAsync(installation.LicenseId, cancellationToken)) is { IsActive: true } ? null : PosInstallationInactiveReasons.LicenseInactive;
    }

    private async Task<PosInstallationIdentity?> IdentityAsync(Domain.Installation installation, CancellationToken cancellationToken) =>
        await store.GetOrganizationAsync(installation.OrganizationId, cancellationToken) is { } organization
            ? new PosInstallationIdentity(installation.PosInstallationId, organization.Id, organization.AccountId, organization.LegalName, installation.BranchName)
            : null;
}
