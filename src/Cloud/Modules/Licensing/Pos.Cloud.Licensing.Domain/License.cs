using Pos.Licensing.Contracts;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Security;

namespace Pos.Cloud.Licensing.Domain;

/// <summary>
/// Licencia de una empresa (L-03, L-06): una sola vigente por empresa. De la clave solo se guarda su SHA-256 y el prefijo
/// visible; la clave completa se muestra una única vez al generarla. Regenerar revoca la anterior (queda <see cref="ReplacedBy"/>).
/// </summary>
[Audited("licensing")]
public sealed class License : AggregateRoot<Guid>, IHasAuditLabel
{
    private License(Guid id, Guid organizationId, Guid subscriptionId, string keyHash, string keyPrefix)
        : base(id)
    {
        OrganizationId = organizationId;
        SubscriptionId = subscriptionId;
        KeyHash = keyHash;
        KeyPrefix = keyPrefix;
    }

    public Guid OrganizationId { get; private set; }

    public Guid SubscriptionId { get; private set; }

    [NotAudited]
    public string KeyHash { get; private set; }

    public string KeyPrefix { get; private set; }

    public LicenseStatus Status { get; private set; } = LicenseStatus.Active;

    /// <summary>Máximo de instalaciones (sucursales) activas; <c>null</c> = sin límite (valor por defecto).</summary>
    public int? MaxInstallations { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public Guid? ReplacedBy { get; private set; }

    public string AuditLabel => $"Licencia {KeyPrefix}…";

    /// <summary>Emite la licencia para una clave ya generada (en forma canónica).</summary>
    public static Result<License> Issue(Guid id, Subscription subscription, string canonicalKey, int? maxInstallations, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (!LicenseKey.TryNormalize(canonicalKey, out var key) || !string.Equals(key, canonicalKey, StringComparison.Ordinal))
        {
            return LicensingErrors.InvalidLicenseKey;
        }

        if (subscription.Status == SubscriptionStatus.Cancelled)
        {
            return LicensingErrors.SubscriptionRequired;
        }

        if (maxInstallations is < 1)
        {
            return LicensingErrors.InvalidMaxInstallations;
        }

        return new License(id, subscription.OrganizationId, subscription.Id, SecureTokens.Hash(key), LicenseKey.VisiblePrefix(key))
        {
            MaxInstallations = maxInstallations,
            IssuedAt = now,
        };
    }

    public bool IsActive => Status == LicenseStatus.Active;

    /// <summary>¿La clave canónica es la de esta licencia? Comparación del hash en tiempo constante (§6).</summary>
    public bool Matches(string canonicalKey) => SecureTokens.Matches(canonicalKey, KeyHash);

    /// <summary>¿Cabe una instalación activa más?</summary>
    public bool AdmitsAnotherInstallation(int activeInstallations) => MaxInstallations is null || activeInstallations < MaxInstallations;

    public Result SetMaxInstallations(int? maxInstallations)
    {
        if (!IsActive)
        {
            return LicensingErrors.LicenseRevoked;
        }

        if (maxInstallations is < 1)
        {
            return LicensingErrors.InvalidMaxInstallations;
        }

        MaxInstallations = maxInstallations;
        return Result.Success();
    }

    public Result Revoke(string reason, DateTimeOffset now, Guid? replacedBy = null)
    {
        if (!IsActive)
        {
            return LicensingErrors.LicenseRevoked;
        }

        if (!Subscription.TryReason(reason, out var cleaned))
        {
            return LicensingErrors.ReasonRequired;
        }

        Status = LicenseStatus.Revoked;
        RevokedAt = now;
        RevokedReason = cleaned;
        ReplacedBy = replacedBy;
        return Result.Success();
    }
}
