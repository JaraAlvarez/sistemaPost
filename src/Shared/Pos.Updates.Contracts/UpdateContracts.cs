using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Licensing.Contracts;

namespace Pos.Updates.Contracts;

/// <summary>Canales de publicación (doc 10 §Q).</summary>
public static class UpdateChannels
{
    public const string Stable = "stable";
    public const string Beta = "beta";
    public const string Internal = "internal";

    public static bool IsKnown(string? channel) => channel is Stable or Beta or Internal;
}

/// <summary>
/// Una versión publicada (docs/fases/fase-13-propuesta.md D13-08). El paquete es un ZIP con las carpetas <c>server/</c>, <c>migrator/</c>,
/// <c>agent/</c> y <c>updater/</c> de esa versión.
/// </summary>
/// <param name="Product">Siempre <c>BusinessPost</c> (u otro nombre comercial): un manifiesto de otro producto se rechaza.</param>
/// <param name="Channel"><see cref="UpdateChannels"/>.</param>
/// <param name="Version">SemVer <c>MAYOR.MENOR.PARCHE</c>.</param>
/// <param name="PackageUrl">Dirección del ZIP (absoluta o relativa al manifiesto).</param>
/// <param name="PackageSha256">Huella SHA-256 del ZIP en hexadecimal.</param>
/// <param name="PackageSize">Tamaño del ZIP en bytes.</param>
/// <param name="MinimumTerminalVersion">Versión mínima del agente de caja que funciona con este servidor.</param>
/// <param name="PublishedAt">Fecha de publicación.</param>
/// <param name="Notes">Novedades en español para el propietario.</param>
public sealed record UpdateManifest(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("packageUrl")] string PackageUrl,
    [property: JsonPropertyName("packageSha256")] string PackageSha256,
    [property: JsonPropertyName("packageSize")] long PackageSize,
    [property: JsonPropertyName("minimumTerminalVersion")] string MinimumTerminalVersion,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt,
    [property: JsonPropertyName("notes")] string Notes);

/// <summary>
/// Manifiesto firmado: <c>payload</c> es el JSON del manifiesto en base64url y <c>signature</c> la firma Ed25519 de esos bytes con la clave
/// <c>kid</c>. Firmar los bytes exactos evita problemas de canonicalización del JSON.
/// </summary>
public sealed record SignedUpdateManifest(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("signature")] string Signature);

public enum ManifestStatus
{
    Valid,
    Malformed,
    UnknownKey,
    BadSignature,
    WrongProduct,
}

/// <summary>Firma y verificación de manifiestos con la clave de actualizaciones (distinta de la de licencias).</summary>
public static class UpdateSigning
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SignedUpdateManifest Sign(UpdateManifest manifest, LicenseSigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        return new SignedUpdateManifest(Base64Url.EncodeToString(payload), key.Kid, Base64Url.EncodeToString(key.Sign(payload)));
    }

    public static ManifestStatus Verify(SignedUpdateManifest? envelope, LicenseKeyRing trustedKeys, string product, [NotNullWhen(true)] out UpdateManifest? manifest)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        manifest = null;
        if (envelope is null || string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Signature))
        {
            return ManifestStatus.Malformed;
        }

        if (!trustedKeys.TryGet(envelope.Kid, out var key))
        {
            return ManifestStatus.UnknownKey;
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Base64Url.DecodeFromChars(envelope.Payload);
            signature = Base64Url.DecodeFromChars(envelope.Signature);
        }
        catch (FormatException)
        {
            return ManifestStatus.Malformed;
        }

        if (!key.Verify(payload, signature))
        {
            return ManifestStatus.BadSignature;
        }

        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(payload, Json);
        }
        catch (JsonException)
        {
            return ManifestStatus.Malformed;
        }

        if (manifest is null || !SemanticVersion.TryParse(manifest.Version, out _))
        {
            manifest = null;
            return ManifestStatus.Malformed;
        }

        if (!string.Equals(manifest.Product, product, StringComparison.Ordinal))
        {
            manifest = null;
            return ManifestStatus.WrongProduct;
        }

        return ManifestStatus.Valid;
    }

    public static string Serialize(SignedUpdateManifest envelope) => JsonSerializer.Serialize(envelope, Json);

    public static SignedUpdateManifest? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SignedUpdateManifest>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Versión <c>MAYOR.MENOR.PARCHE</c>; ignora los metadatos de compilación (<c>+abc</c>) y ordena las preliminares (<c>-beta.1</c>) antes.</summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim().Split('+')[0];
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        var pre = dash >= 0 ? core[(dash + 1)..] : null;
        var parts = (dash >= 0 ? core[..dash] : core).Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, string.IsNullOrEmpty(pre) ? null : pre);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        result = result != 0 ? result : Minor.CompareTo(other.Minor);
        result = result != 0 ? result : Patch.CompareTo(other.Patch);
        if (result != 0 || PreRelease == other.PreRelease)
        {
            return result;
        }

        return PreRelease is null ? 1 : other.PreRelease is null ? -1 : string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

/// <summary>Resultados que el actualizador registra para que el servidor los audite al arrancar.</summary>
public static class UpdateOutcomes
{
    public const string Downloaded = "UPDATE_DOWNLOADED";
    public const string Applied = "UPDATE_APPLIED";
    public const string Failed = "UPDATE_FAILED";
    public const string RolledBack = "UPDATE_ROLLED_BACK";
}

/// <summary>Una línea del historial (<c>{DataRoot}\updates\history.jsonl</c>, una línea JSON por evento).</summary>
public sealed record UpdateHistoryEntry(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("fromVersion")] string FromVersion,
    [property: JsonPropertyName("toVersion")] string ToVersion,
    [property: JsonPropertyName("detail")] string Detail);

/// <summary>Estado que el actualizador publica para el servidor (<c>{DataRoot}\updates\state.json</c>).</summary>
public sealed record UpdaterState(
    [property: JsonPropertyName("currentVersion")] string CurrentVersion,
    [property: JsonPropertyName("availableVersion")] string? AvailableVersion,
    [property: JsonPropertyName("availableNotes")] string? AvailableNotes,
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("lastCheckAt")] DateTimeOffset? LastCheckAt,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("nextWindow")] DateTimeOffset? NextWindow);

/// <summary>Archivos compartidos entre el actualizador y el servidor bajo <c>{DataRoot}\updates</c>.</summary>
public static class UpdateFiles
{
    public const string Folder = "updates";
    public const string History = "history.jsonl";
    public const string State = "state.json";

    /// <summary>El propietario pidió instalar ahora (sin esperar la ventana); lo borra el actualizador al empezar.</summary>
    public const string InstallNowRequest = "install-now.request";

    /// <summary>Manifiesto firmado de la versión instalada: el servidor lo ofrece a sus cajas junto con el paquete.</summary>
    public const string InstalledManifest = "installed-manifest.json";

    public const string InstalledPackage = "installed-package.zip";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Directory(string dataRoot) => Path.Combine(dataRoot, Folder);

    public static void AppendHistory(string dataRoot, UpdateHistoryEntry entry)
    {
        System.IO.Directory.CreateDirectory(Directory(dataRoot));
        File.AppendAllText(Path.Combine(Directory(dataRoot), History), JsonSerializer.Serialize(entry, Json) + "\n", Encoding.UTF8);
    }

    public static IReadOnlyList<UpdateHistoryEntry> ReadHistory(string dataRoot)
    {
        var path = Path.Combine(Directory(dataRoot), History);
        if (!File.Exists(path))
        {
            return [];
        }

        var entries = new List<UpdateHistoryEntry>();
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<UpdateHistoryEntry>(line, Json) is { } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // Línea truncada por un corte de luz: se ignora.
            }
        }

        return entries;
    }

    public static void WriteState(string dataRoot, UpdaterState state)
    {
        System.IO.Directory.CreateDirectory(Directory(dataRoot));
        var path = Path.Combine(Directory(dataRoot), State);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, Json), Encoding.UTF8);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static UpdaterState? ReadState(string dataRoot)
    {
        var path = Path.Combine(Directory(dataRoot), State);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdaterState>(File.ReadAllText(path, Encoding.UTF8), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
