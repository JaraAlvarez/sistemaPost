using Pos.SharedKernel.Domain;

namespace Pos.Modules.Organization.Domain;

public enum DeviceType
{
    StoreServer,
    Terminal,
    AdminWorkstation,
    AllInOne,
}

public enum DeviceStatus
{
    Active,
    Revoked,
}

/// <summary>
/// Equipo físico emparejado con el servidor (D3-04). Se autentica con un secreto de 256 bits que solo conoce el equipo;
/// aquí se guarda su SHA-256. Revocarlo cierra de inmediato sus sesiones.
/// </summary>
[Audited("organization")]
public sealed class Device : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private Device(Guid id, Guid companyId, Guid nodeId, DeviceType kind, string hostname)
        : base(id)
    {
        CompanyId = companyId;
        NodeId = nodeId;
        Kind = kind;
        Hostname = hostname;
    }

    public Guid CompanyId { get; private set; }

    public Guid NodeId { get; private set; }

    public DeviceType Kind { get; private set; }

    public string Hostname { get; private set; }

    public string MachineFingerprintHash { get; private set; } = string.Empty;

    public string? OsVersion { get; private set; }

    public string? AppVersion { get; private set; }

    public DateTimeOffset PairedAt { get; private set; }

    [LocalOnly]
    public DateTimeOffset? LastSeenAt { get; private set; }

    public DeviceStatus Status { get; private set; } = DeviceStatus.Active;

    [Sensitive]
    public string? CredentialHash { get; private set; }

    public string? CertificatePin { get; private set; }

    public Guid PairedBy { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? RevokedBy { get; private set; }

    public string AuditLabel => $"Equipo {Hostname} ({Kind})";

    public static Device Pair(
        Guid id, Guid companyId, Guid nodeId, DeviceType kind, string hostname, string fingerprintHash, string? osVersion, string? appVersion,
        string credentialHash, string? certificatePin, Guid pairedBy, DateTimeOffset now) =>
        new(id, companyId, nodeId, kind, Trim(hostname, 100))
        {
            MachineFingerprintHash = fingerprintHash,
            OsVersion = osVersion is null ? null : Trim(osVersion, 60),
            AppVersion = appVersion is null ? null : Trim(appVersion, 60),
            CredentialHash = credentialHash,
            CertificatePin = certificatePin,
            PairedBy = pairedBy,
            PairedAt = now,
            LastSeenAt = now,
        };

    public void Revoke(Guid? by, DateTimeOffset now)
    {
        if (Status == DeviceStatus.Revoked)
        {
            return;
        }

        Status = DeviceStatus.Revoked;
        RevokedAt = now;
        RevokedBy = by;
    }

    private static string Trim(string value, int max) => value.Length > max ? value[..max] : value;
}
