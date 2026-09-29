using Pos.Licensing.Contracts;

namespace Pos.Cloud.Licensing.Domain;

public enum AccountKind
{
    Direct,
    Reseller,
}

public enum RecordStatus
{
    Active,
    Inactive,
}

/// <summary>Edición (ADR-0015): la única diferencia comercial. En la BD y en el token: <c>SINGLE</c> / <c>MULTI</c>.</summary>
public enum LicenseEdition
{
    SingleTerminal,
    MultiTerminal,
}

public enum BillingPeriod
{
    Monthly,
    Annual,
}

public enum SubscriptionStatus
{
    Trial,
    Active,
    PastDue,
    Suspended,
    Cancelled,
    Expired,
}

public enum SubscriptionEventType
{
    Created,
    Renewed,
    EditionChanged,
    Suspended,
    Reactivated,
    Cancelled,
    GraceExtended,

    /// <summary>Cambio automático por el paso del tiempo (vence, entra en gracia, expira) o por un cambio de la gracia.</summary>
    StatusChanged,
}

public enum LicenseStatus
{
    Active,
    Revoked,
}

public enum DeviceRole
{
    AllInOne,
    StoreServer,
}

public enum InstallationStatus
{
    Active,
    Released,
}

public enum ActivationStatus
{
    Active,
    Released,
}

public enum CheckinResult
{
    TokenIssued,
    Rejected,
}

public enum SigningKeyStatus
{
    Standby,
    Active,
    Retired,
    Revoked,
}

/// <summary>Correspondencia entre las enumeraciones del dominio y los valores del cable (token, API).</summary>
public static class LicensingCodes
{
    public static string ToCode(this LicenseEdition edition) =>
        edition == LicenseEdition.SingleTerminal ? LicenseEditions.SingleTerminal : LicenseEditions.MultiTerminal;

    public static bool TryParseEdition(string? code, out LicenseEdition edition)
    {
        edition = code switch
        {
            LicenseEditions.SingleTerminal => LicenseEdition.SingleTerminal,
            LicenseEditions.MultiTerminal => LicenseEdition.MultiTerminal,
            _ => (LicenseEdition)(-1),
        };
        return Enum.IsDefined(edition);
    }

    public static string ToCode(this DeviceRole role) =>
        role == DeviceRole.AllInOne ? DeviceRoles.AllInOne : DeviceRoles.StoreServer;

    public static bool TryParseRole(string? code, out DeviceRole role)
    {
        role = code switch
        {
            DeviceRoles.AllInOne => DeviceRole.AllInOne,
            DeviceRoles.StoreServer => DeviceRole.StoreServer,
            _ => (DeviceRole)(-1),
        };
        return Enum.IsDefined(role);
    }

    public static string ToCode(this SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.Trial => SubscriptionStatuses.Trial,
        SubscriptionStatus.Active => SubscriptionStatuses.Active,
        SubscriptionStatus.PastDue => SubscriptionStatuses.PastDue,
        SubscriptionStatus.Suspended => SubscriptionStatuses.Suspended,
        SubscriptionStatus.Cancelled => SubscriptionStatuses.Cancelled,
        _ => SubscriptionStatuses.Expired,
    };

    /// <summary>Nombre para las pantallas del portal.</summary>
    public static string DisplayName(this SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.Trial => "En prueba",
        SubscriptionStatus.Active => "Activa",
        SubscriptionStatus.PastDue => "En gracia",
        SubscriptionStatus.Suspended => "Suspendida",
        SubscriptionStatus.Cancelled => "Cancelada",
        _ => "Vencida",
    };

    public static string DisplayName(this LicenseEdition edition) =>
        edition == LicenseEdition.SingleTerminal ? "Caja Única" : "Multicaja";
}

/// <summary>
/// Qué rol de equipo admite cada edición (resolución 1 de la propuesta): Caja Única solo <c>ALL_IN_ONE</c>; Multicaja admite
/// <c>STORE_SERVER</c> o, en una sucursal pequeña, un equipo <c>ALL_IN_ONE</c>.
/// </summary>
public static class EditionRules
{
    public static bool Allows(LicenseEdition edition, DeviceRole role) =>
        edition == LicenseEdition.MultiTerminal || role == DeviceRole.AllInOne;
}
