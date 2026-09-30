using System.Net;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.Licensing.Domain;

/// <summary>Equipo de una instalación: el servidor de tienda (Multicaja) o el equipo que hace todo (Caja Única).</summary>
public sealed class Device : Entity<Guid>
{
    internal Device(Guid id, Guid installationId, DeviceFingerprint fingerprint, DeviceRole role, string? deviceName, string? operatingSystem, DateTimeOffset now)
        : base(id)
    {
        InstallationId = installationId;
        Fingerprint = fingerprint.ToString();
        Role = role;
        DeviceName = Account.Clean(deviceName);
        OperatingSystem = Account.Clean(operatingSystem);
        FirstSeenAt = now;
        LastSeenAt = now;
    }

    private Device(Guid id, Guid installationId, string fingerprint)
        : base(id)
    {
        InstallationId = installationId;
        Fingerprint = fingerprint;
    }

    public Guid InstallationId { get; private set; }

    /// <summary>Huella en su forma textual (<see cref="DeviceFingerprint"/>): solo hashes.</summary>
    public string Fingerprint { get; private set; }

    public DeviceRole Role { get; private set; }

    public string? DeviceName { get; private set; }

    public string? OperatingSystem { get; private set; }

    public DateTimeOffset FirstSeenAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>¿Es este equipo? Tolerancia de 2 de 3 componentes (L-07).</summary>
    public bool Matches(DeviceFingerprint fingerprint) =>
        DeviceFingerprint.TryParse(Fingerprint, out var stored) && stored.Matches(fingerprint);

    /// <summary>Registra que el equipo se presentó; si cambió un componente (p. ej. el disco), guarda la huella nueva.</summary>
    internal void Seen(DeviceFingerprint fingerprint, DateTimeOffset now, DeviceRole? role = null, string? deviceName = null, string? operatingSystem = null)
    {
        Fingerprint = fingerprint.ToString();
        LastSeenAt = now;
        Role = role ?? Role;
        DeviceName = Account.Clean(deviceName) ?? DeviceName;
        OperatingSystem = Account.Clean(operatingSystem) ?? OperatingSystem;
    }
}

/// <summary>Activación de una licencia en un equipo. Liberarla (cambio de PC) no la borra: queda con quién, cuándo y por qué.</summary>
[Audited("licensing")]
public sealed class Activation : Entity<Guid>, IHasAuditLabel
{
    internal Activation(Guid id, Guid installationId, Guid licenseId, Guid deviceId, DateTimeOffset activatedAt)
        : base(id)
    {
        InstallationId = installationId;
        LicenseId = licenseId;
        DeviceId = deviceId;
        ActivatedAt = activatedAt;
    }

    public Guid InstallationId { get; private set; }

    public Guid LicenseId { get; private set; }

    public Guid DeviceId { get; private set; }

    public ActivationStatus Status { get; private set; } = ActivationStatus.Active;

    public DateTimeOffset ActivatedAt { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    public Guid? ReleasedBy { get; private set; }

    public string? ReleaseReason { get; private set; }

    public string AuditLabel => $"Activación del equipo {DeviceId}";

    internal void Release(string reason, ChangeContext change)
    {
        Status = ActivationStatus.Released;
        ReleasedAt = change.Now;
        ReleasedBy = change.ActorId;
        ReleaseReason = reason;
    }

    internal void MoveTo(Guid licenseId) => LicenseId = licenseId;
}

/// <summary>Lo que el POS declara al activar.</summary>
public sealed record ActivationData(DeviceFingerprint Fingerprint, DeviceRole Role, string AppVersion, string? BranchName, string? DeviceName, string? OperatingSystem);

/// <summary>
/// Instalación del POS en una sucursal (L-03/L-07): identificada por el <c>installation_id</c> que genera el instalador; se
/// activa en UN equipo a la vez (el servidor de la tienda o el equipo Caja Única). Las cajas adicionales no se activan en la
/// nube: se emparejan con el servidor de la tienda.
/// </summary>
[Audited("licensing")]
public sealed class Installation : AggregateRoot<Guid>, IHasAuditLabel
{
    private readonly List<Device> _devices = [];
    private readonly List<Activation> _activations = [];

    private Installation(Guid id, Guid posInstallationId, Guid licenseId, Guid organizationId, string appVersion)
        : base(id)
    {
        PosInstallationId = posInstallationId;
        LicenseId = licenseId;
        OrganizationId = organizationId;
        AppVersion = appVersion;
    }

    /// <summary>El <c>installation_id</c> generado por el instalador del POS (único en todo el servidor).</summary>
    public Guid PosInstallationId { get; private set; }

    public Guid LicenseId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string? BranchName { get; private set; }

    public string AppVersion { get; private set; }

    public InstallationStatus Status { get; private set; } = InstallationStatus.Released;

    public DateTimeOffset FirstActivatedAt { get; private set; }

    [NotAudited]
    public DateTimeOffset? LastCheckinAt { get; private set; }

    [NotAudited]
    public IPAddress? LastIp { get; private set; }

    [NotAudited]
    public int? ActiveTerminals { get; private set; }

    public IReadOnlyCollection<Device> Devices => _devices.AsReadOnly();

    public IReadOnlyCollection<Activation> Activations => _activations.AsReadOnly();

    public Activation? ActiveActivation => _activations.SingleOrDefault(a => a.Status == ActivationStatus.Active);

    public Device? ActiveDevice => ActiveActivation is { } active ? _devices.SingleOrDefault(d => d.Id == active.DeviceId) : null;

    public string AuditLabel => $"Instalación {PosInstallationId}{(BranchName is null ? string.Empty : $" · {BranchName}")}";

    public static Installation Register(Guid id, Guid posInstallationId, License license, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(license);
        return new Installation(id, posInstallationId, license.Id, license.OrganizationId, "0") { FirstActivatedAt = now };
    }

    /// <summary>
    /// Activa la instalación en el equipo. Si ya está activa en el MISMO equipo (2 de 3), devuelve esa activación (reintento
    /// idempotente); si está activa en OTRO equipo, exige liberar el anterior desde el portal.
    /// </summary>
    public Result<Activation> Activate(License license, ActivationData data, ChangeContext change)
    {
        ArgumentNullException.ThrowIfNull(license);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(change);
        if (license.Id != LicenseId)
        {
            return LicenseApiErrors.InstallationOfOtherLicense;
        }

        AppVersion = Version(data.AppVersion);
        BranchName = Account.Clean(data.BranchName) ?? BranchName;
        if (ActiveActivation is { } current)
        {
            var device = ActiveDevice!;
            if (!device.Matches(data.Fingerprint))
            {
                return LicenseApiErrors.InstallationActiveOnOtherDevice;
            }

            device.Seen(data.Fingerprint, change.Now, data.Role, data.DeviceName, data.OperatingSystem);
            return current;
        }

        var known = _devices.FirstOrDefault(d => d.Matches(data.Fingerprint));
        if (known is null)
        {
            known = new Device(change.Ids.NewId(), Id, data.Fingerprint, data.Role, data.DeviceName, data.OperatingSystem, change.Now);
            _devices.Add(known);
        }
        else
        {
            known.Seen(data.Fingerprint, change.Now, data.Role, data.DeviceName, data.OperatingSystem);
        }

        var activation = new Activation(change.Ids.NewId(), Id, license.Id, known.Id, change.Now);
        _activations.Add(activation);
        Status = InstallationStatus.Active;
        return activation;
    }

    /// <summary>
    /// Verifica que quien hace el check-in es el equipo activado (2 de 3). Un cambio parcial de hardware actualiza la huella
    /// guardada; un equipo distinto (o una instalación liberada) debe reactivarse con la clave.
    /// </summary>
    public Result<(Activation Activation, Device Device)> Authenticate(DeviceFingerprint fingerprint, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (ActiveActivation is not { } active || ActiveDevice is not { } device || !device.Matches(fingerprint))
        {
            return LicenseApiErrors.ReactivationRequired;
        }

        device.Seen(fingerprint, now);
        return (active, device);
    }

    public void RecordCheckin(string appVersion, int activeTerminals, IPAddress? ip, DateTimeOffset now)
    {
        AppVersion = Version(appVersion);
        ActiveTerminals = Math.Max(0, activeTerminals);
        LastIp = ip;
        LastCheckinAt = now;
    }

    /// <summary>Libera el equipo activo (cambio de PC, equipo dañado o desde el propio POS). La instalación queda sin equipo.</summary>
    public Result Release(Guid activationId, string reason, ChangeContext change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (ActiveActivation is not { } active || active.Id != activationId)
        {
            return LicensingErrors.ActivationNotFound;
        }

        if (!Subscription.TryReason(reason, out var cleaned))
        {
            return LicensingErrors.ReasonRequired;
        }

        active.Release(cleaned, change);
        Status = InstallationStatus.Released;
        return Result.Success();
    }

    /// <summary>
    /// Pasa la instalación a la licencia nueva de la misma empresa (al regenerar la clave): la activación vigente sigue en el
    /// mismo equipo, así las sucursales no tienen que volver a escribir la clave; el POS recibe el nuevo <c>lic</c> en su check-in.
    /// </summary>
    public Result MoveToLicense(License license)
    {
        ArgumentNullException.ThrowIfNull(license);
        if (license.OrganizationId != OrganizationId || !license.IsActive)
        {
            return LicenseApiErrors.InstallationOfOtherLicense;
        }

        LicenseId = license.Id;
        ActiveActivation?.MoveTo(license.Id);
        return Result.Success();
    }

    private static string Version(string? version)
    {
        var cleaned = (version ?? string.Empty).Trim();
        return cleaned.Length == 0 ? "0" : cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }
}

/// <summary>Check-in o rechazo de check-in (solo inserción): fecha, versión, cajas, reloj reportado, IP y resultado.</summary>
public sealed class Checkin : Entity<Guid>
{
    private Checkin(Guid id, Guid installationId, DateTimeOffset occurredAt, string appVersion, int activeTerminals, DateTimeOffset reportedClock)
        : base(id)
    {
        InstallationId = installationId;
        OccurredAt = occurredAt;
        AppVersion = appVersion;
        ActiveTerminals = activeTerminals;
        ReportedClock = reportedClock.ToUniversalTime();
    }

    public Guid InstallationId { get; private set; }

    public Guid? ActivationId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string AppVersion { get; private set; }

    public int ActiveTerminals { get; private set; }

    public DateTimeOffset ReportedClock { get; private set; }

    public IPAddress? IpAddress { get; private set; }

    public CheckinResult Result { get; private set; }

    public string? RejectionCode { get; private set; }

    public SubscriptionStatus? SubscriptionStatus { get; private set; }

    public DateTimeOffset? TokenValidUntil { get; private set; }

    /// <summary>Último sello de la auditoría del POS (ancla externa, ADR-0048; Fase 12-B). Los POS anteriores no lo envían.</summary>
    public long? AuditSealNo { get; private set; }

    public string? AuditSealCode { get; private set; }

    public DateTimeOffset? AuditSealedAt { get; private set; }

    public static Checkin Issued(
        Guid id, Installation installation, Guid activationId, CheckinReport report, SubscriptionStatus status, DateTimeOffset validUntil, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(report);
        return new Checkin(id, installation.Id, now, Trim(report.AppVersion), Math.Max(0, report.ActiveTerminals), report.ReportedClock)
        {
            ActivationId = activationId,
            IpAddress = report.IpAddress,
            Result = CheckinResult.TokenIssued,
            SubscriptionStatus = status,
            TokenValidUntil = validUntil,
        }.WithSeal(report);
    }

    public static Checkin Rejected(Guid id, Installation installation, CheckinReport report, string rejectionCode, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectionCode);
        return new Checkin(id, installation.Id, now, Trim(report.AppVersion), Math.Max(0, report.ActiveTerminals), report.ReportedClock)
        {
            ActivationId = installation.ActiveActivation?.Id,
            IpAddress = report.IpAddress,
            Result = CheckinResult.Rejected,
            RejectionCode = rejectionCode,
        }.WithSeal(report);
    }

    private Checkin WithSeal(CheckinReport report)
    {
        if (report.AuditSealNo is { } no && !string.IsNullOrWhiteSpace(report.AuditSealCode) && report.AuditSealCode.Length <= 40)
        {
            AuditSealNo = no;
            AuditSealCode = report.AuditSealCode;
            AuditSealedAt = report.AuditSealedAt?.ToUniversalTime();
        }

        return this;
    }

    private static string Trim(string? version)
    {
        var cleaned = (version ?? string.Empty).Trim();
        return cleaned.Length == 0 ? "0" : cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }
}

/// <summary>Lo que el POS informa en el check-in.</summary>
public sealed record CheckinReport(
    string AppVersion,
    int ActiveTerminals,
    DateTimeOffset ReportedClock,
    IPAddress? IpAddress,
    long? AuditSealNo = null,
    string? AuditSealCode = null,
    DateTimeOffset? AuditSealedAt = null);
