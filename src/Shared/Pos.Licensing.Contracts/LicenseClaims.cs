using System.Text.Json.Serialization;

namespace Pos.Licensing.Contracts;

/// <summary>Valores del token en el cable (en MAYÚSCULAS, igual que en la BD de la nube).</summary>
public static class LicenseEditions
{
    /// <summary>Caja Única: un equipo hace todo (solo instalaciones <c>ALL_IN_ONE</c>).</summary>
    public const string SingleTerminal = "SINGLE";

    /// <summary>Multicaja: servidor de tienda (<c>STORE_SERVER</c>) o, en una sucursal pequeña, un equipo <c>ALL_IN_ONE</c>.</summary>
    public const string MultiTerminal = "MULTI";
}

public static class DeviceRoles
{
    public const string AllInOne = "ALL_IN_ONE";

    public const string StoreServer = "STORE_SERVER";
}

public static class SubscriptionStatuses
{
    public const string Trial = "TRIAL";
    public const string Active = "ACTIVE";
    public const string PastDue = "PAST_DUE";
    public const string Suspended = "SUSPENDED";
    public const string Cancelled = "CANCELLED";
    public const string Expired = "EXPIRED";
}

/// <summary>Mensaje que el POS muestra al usuario (aviso de vencimiento, actualización disponible…).</summary>
/// <param name="Code">Código estable, p. ej. <c>SUBSCRIPTION_EXPIRING</c>.</param>
/// <param name="Severity"><c>INFO</c>, <c>WARNING</c> o <c>CRITICAL</c>.</param>
/// <param name="Text">Texto en español para el usuario.</param>
public sealed record LicenseMessage(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// Contenido firmado del token de licencia (L-05). Es un contrato: agregar un campo obligatorio o cambiar el significado de uno
/// exige subir <see cref="CurrentVersion"/>; el POS rechaza versiones que no conoce (<see cref="LicenseTokenStatus.UnsupportedVersion"/>).
/// Mientras no pase <see cref="GraceUntil"/>, el POS opera sin Internet (doc 09).
/// </summary>
public sealed record LicenseClaims
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("ver")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Licencia (clave) con la que se activó la instalación.</summary>
    [JsonPropertyName("lic")]
    public required Guid LicenseId { get; init; }

    /// <summary>NIT de la empresa licenciada con su dígito de verificación: <c>900123456-7</c>.</summary>
    [JsonPropertyName("org")]
    public required string OrganizationNit { get; init; }

    [JsonPropertyName("org_name")]
    public required string OrganizationName { get; init; }

    /// <summary><c>installation_id</c> generado por el instalador del POS (una instalación por sucursal).</summary>
    [JsonPropertyName("inst")]
    public required Guid InstallationId { get; init; }

    /// <summary>Huella del equipo activado (<see cref="DeviceFingerprint"/>).</summary>
    [JsonPropertyName("dev")]
    public required string DeviceFingerprint { get; init; }

    /// <summary><see cref="DeviceRoles"/>.</summary>
    [JsonPropertyName("role")]
    public required string DeviceRole { get; init; }

    /// <summary><see cref="LicenseEditions"/>.</summary>
    [JsonPropertyName("edition")]
    public required string Edition { get; init; }

    /// <summary><see cref="SubscriptionStatuses"/>: el POS pasa a restringido si es SUSPENDED, CANCELLED o EXPIRED.</summary>
    [JsonPropertyName("sub_status")]
    public required string SubscriptionStatus { get; init; }

    [JsonPropertyName("iat")]
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>Fin del periodo pagado (o de la prueba).</summary>
    [JsonPropertyName("valid_until")]
    public required DateTimeOffset ValidUntil { get; init; }

    [JsonPropertyName("grace_days")]
    public required int GraceDays { get; init; }

    /// <summary>Desde cuándo el POS debe intentar renovar el token (check-in); si no puede, sigue con el vigente.</summary>
    [JsonPropertyName("refresh_after")]
    public required DateTimeOffset RefreshAfter { get; init; }

    [JsonPropertyName("msgs")]
    public IReadOnlyList<LicenseMessage> Messages { get; init; } = [];

    /// <summary>Último instante en que el POS opera (con avisos) sin renovar: <c>valid_until + grace_days</c>.</summary>
    [JsonIgnore]
    public DateTimeOffset GraceUntil => ValidUntil.AddDays(GraceDays);

    /// <summary>¿Vigente (sin gracia) en ese instante?</summary>
    public bool IsValidAt(DateTimeOffset instant) => instant <= ValidUntil;

    /// <summary>¿Vencido pero todavía dentro de la gracia?</summary>
    public bool IsInGraceAt(DateTimeOffset instant) => instant > ValidUntil && instant <= GraceUntil;
}
