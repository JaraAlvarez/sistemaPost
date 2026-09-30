namespace Pos.Cloud.Licensing.Application;

public sealed record AccountInput(
    string Name, string Kind, string? Nit, string? NitCheckDigit, string? ContactName, string? ContactEmail, string? ContactPhone, Guid? ParentAccountId,
    string? Notes);

public sealed record AccountDto(
    Guid Id,
    string Name,
    string Kind,
    string? Nit,
    string? NitCheckDigit,
    string? ContactName,
    string? ContactEmail,
    string? ContactPhone,
    Guid? ParentAccountId,
    string Status,
    string? Notes,
    int Organizations);

/// <summary>Resumen de la suscripción. <c>EffectiveStatus</c> es el estado por fechas en este momento.</summary>
public sealed record SubscriptionSummaryDto(
    Guid Id,
    string Edition,
    string BillingPeriod,
    string Status,
    string EffectiveStatus,
    DateTimeOffset? TrialEndsAt,
    DateTimeOffset? CurrentPeriodStart,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset ValidUntil,
    int GraceDays,
    DateTimeOffset GraceUntil,
    string? SuspendedReason);

public sealed record OrganizationRowDto(
    Guid Id,
    Guid AccountId,
    string AccountName,
    string LegalName,
    string Nit,
    string? City,
    string Status,
    SubscriptionSummaryDto? Subscription,
    string? LicensePrefix,
    int ActiveInstallations);

public sealed record SubscriptionEventDto(
    Guid Id,
    string Type,
    DateTimeOffset OccurredAt,
    string ActorName,
    string? PaymentReference,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd,
    string? OldValue,
    string? NewValue,
    string? Reason);

public sealed record LicenseDto(
    Guid Id,
    Guid SubscriptionId,
    string KeyPrefix,
    string Status,
    int? MaxInstallations,
    DateTimeOffset IssuedAt,
    DateTimeOffset? RevokedAt,
    string? RevokedReason,
    Guid? ReplacedBy,
    int ActiveInstallations);

public sealed record DeviceDto(
    Guid Id, string Fingerprint, string Role, string? DeviceName, string? OperatingSystem, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);

public sealed record ActivationDto(
    Guid Id,
    Guid DeviceId,
    Guid LicenseId,
    string Status,
    DateTimeOffset ActivatedAt,
    DateTimeOffset? ReleasedAt,
    string? ReleasedByName,
    string? ReleaseReason);

public sealed record InstallationDto(
    Guid Id,
    Guid InstallationId,
    Guid OrganizationId,
    string OrganizationName,
    string? BranchName,
    string AppVersion,
    string Status,
    DateTimeOffset FirstActivatedAt,
    DateTimeOffset? LastCheckinAt,
    string? LastIp,
    int? ActiveTerminals,
    IReadOnlyList<DeviceDto> Devices,
    IReadOnlyList<ActivationDto> Activations);

public sealed record CheckinDto(
    Guid Id,
    Guid InstallationId,
    DateTimeOffset OccurredAt,
    string AppVersion,
    int ActiveTerminals,
    DateTimeOffset ReportedClock,
    string? IpAddress,
    string Result,
    string? RejectionCode,
    string? SubscriptionStatus,
    DateTimeOffset? TokenValidUntil,
    long? AuditSealNo = null,
    string? AuditSealCode = null,
    DateTimeOffset? AuditSealedAt = null);

public sealed record OrganizationDetailDto(
    OrganizationRowDto Organization,
    AccountDto Account,
    IReadOnlyList<SubscriptionEventDto> Events,
    IReadOnlyList<LicenseDto> Licenses,
    IReadOnlyList<InstallationDto> Installations,
    IReadOnlyList<CheckinDto> RecentCheckins);

/// <summary>Fila de suscripción para listas y tablero. <c>DaysLeft</c>: días hasta <c>ValidUntil</c> (negativo si ya pasó).</summary>
public sealed record SubscriptionRowDto(
    Guid Id,
    Guid OrganizationId,
    string OrganizationName,
    string Nit,
    string Edition,
    string BillingPeriod,
    string Status,
    DateTimeOffset ValidUntil,
    DateTimeOffset GraceUntil,
    int DaysLeft,
    string? SuspendedReason);

public sealed record DashboardDto(
    int Accounts,
    int Organizations,
    int ActiveInstallations,
    int ExpiringIn7Days,
    int ExpiringIn15Days,
    int ExpiringIn30Days,
    IReadOnlyList<SubscriptionRowDto> Expiring,
    IReadOnlyList<SubscriptionRowDto> InGrace,
    IReadOnlyList<SubscriptionRowDto> Suspended,
    IReadOnlyList<SubscriptionRowDto> Expired,
    IReadOnlyList<InstallationDto> WithoutCheckin,
    int NoCheckinDays,
    IReadOnlyDictionary<string, int> SubscriptionsByStatus);

public sealed record SigningKeyDto(
    string Kid, string PublicKey, string Status, DateTimeOffset CreatedAt, DateTimeOffset? ActivatedAt, DateTimeOffset? RetiredAt, DateTimeOffset? RevokedAt,
    bool LoadedInServer);

/// <summary>Clave recién generada: se muestra UNA sola vez (en la BD queda solo su hash).</summary>
public sealed record GeneratedLicenseDto(Guid LicenseId, Guid OrganizationId, string Key, string KeyPrefix);

/// <summary>Lecturas del portal (sin seguimiento de cambios).</summary>
public interface ILicensingReadModel
{
    Task<IReadOnlyList<AccountDto>> ListAccountsAsync(string? search, CancellationToken cancellationToken);

    Task<AccountDto?> GetAccountAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<OrganizationRowDto>> ListOrganizationsAsync(Guid? accountId, string? search, DateTimeOffset now, CancellationToken cancellationToken);

    Task<OrganizationDetailDto?> GetOrganizationAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<SubscriptionRowDto>> ListSubscriptionsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<InstallationDto>> ListInstallationsAsync(Guid? organizationId, string? search, CancellationToken cancellationToken);

    Task<(int Accounts, int Organizations)> CountAsync(CancellationToken cancellationToken);
}
