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
/// <param name="ExpiresAt">
/// Caducidad del manifiesto: después de esa fecha el actualizador del servidor no lo acepta (así un manifiesto viejo, aunque esté bien
/// firmado, no se puede volver a servir para frenar las actualizaciones). Obligatoria.
/// </param>
/// <param name="Sequence">
/// Número de secuencia creciente de cada publicación (Pos.Release usa la hora Unix de la firma). El actualizador guarda el último que vio y
/// rechaza uno menor (ataque de repetición o vuelta atrás). Obligatorio (mayor que cero).
/// </param>
public sealed record UpdateManifest(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("packageUrl")] string PackageUrl,
    [property: JsonPropertyName("packageSha256")] string PackageSha256,
    [property: JsonPropertyName("packageSize")] long PackageSize,
    [property: JsonPropertyName("minimumTerminalVersion")] string MinimumTerminalVersion,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt = null,
    [property: JsonPropertyName("sequence")] long Sequence = 0);

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

    /// <summary>Le falta la caducidad o el número de secuencia (manifiesto de un generador viejo).</summary>
    MissingFreshness,

    /// <summary>Venció (<see cref="UpdateManifest.ExpiresAt"/>).</summary>
    Expired,

    /// <summary>Su número de secuencia es menor que el último visto en este equipo.</summary>
    Replayed,
}

/// <summary>
/// Frescura del manifiesto (además de la firma): caducidad y número de secuencia. En las cajas no se exige la caducidad: el servidor de la
/// tienda les sirve el manifiesto de la versión que instaló, que puede ser de hace meses; sí la secuencia.
/// </summary>
public static class ManifestFreshness
{
    /// <summary>Vigencia que pone Pos.Release por defecto (M5: 90 días; hay que volver a firmar antes, ver docs/guia-instalacion.md).</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);

    /// <summary>Con menos de esto por vencer, Pos.Release verify avisa que hay que volver a firmar.</summary>
    public static readonly TimeSpan RenewalWarning = TimeSpan.FromDays(15);

    public static ManifestStatus Check(UpdateManifest manifest, DateTimeOffset now, long lastSequence, bool requireNotExpired)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.ExpiresAt is not { } expiresAt || manifest.Sequence <= 0)
        {
            return ManifestStatus.MissingFreshness;
        }

        if (requireNotExpired && now >= expiresAt)
        {
            return ManifestStatus.Expired;
        }

        return manifest.Sequence < lastSequence ? ManifestStatus.Replayed : ManifestStatus.Valid;
    }
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
    /// <summary>
    /// SemVer ESTRICTA: <c>MAYOR.MENOR.PARCHE</c> con dígitos, preliminar y metadatos opcionales con identificadores no vacíos de
    /// <c>[0-9A-Za-z-]</c>, sin espacios ni otros caracteres y de hasta 64 caracteres. La versión se usa como nombre de carpeta
    /// (<c>app\{versión}</c>): nada de <c>..</c>, barras, dos puntos ni espacios.
    /// </summary>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text) || text.Length > 64 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+'))
        {
            return false;
        }

        var plus = text.Split('+');
        if (plus.Length > 2 || (plus.Length == 2 && !Identifiers(plus[1])))
        {
            return false;
        }

        var core = plus[0];
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        var pre = dash >= 0 ? core[(dash + 1)..] : null;
        if (pre is not null && !Identifiers(pre))
        {
            return false;
        }

        var parts = (dash >= 0 ? core[..dash] : core).Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0)
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, string.IsNullOrEmpty(pre) ? null : pre);
        return true;
    }

    /// <summary>Identificadores separados por puntos, cada uno no vacío y solo con <c>[0-9A-Za-z-]</c>.</summary>
    private static bool Identifiers(string text) =>
        text.Length > 0 && text.Split('.').All(i => i.Length > 0 && i.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    /// <summary>¿Sirve como nombre de la carpeta de una versión (<c>app\{versión}</c>)? SemVer estricta.</summary>
    public static bool IsSafeFolderName(string? text) => TryParse(text, out _);

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        result = result != 0 ? result : Minor.CompareTo(other.Minor);
        result = result != 0 ? result : Patch.CompareTo(other.Patch);
        if (result != 0 || PreRelease == other.PreRelease)
        {
            return result;
        }

        return PreRelease is null ? 1 : other.PreRelease is null ? -1 : ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>
    /// Precedencia de las versiones preliminares según SemVer 2.0 (§11): identificadores separados por puntos; los numéricos se comparan
    /// como números y van antes que los alfanuméricos; con todo igual, la que tiene menos identificadores va antes
    /// (<c>beta.2 &lt; beta.10</c>, que como texto quedaba al revés).
    /// </summary>
    private static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var leftNumeric = long.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var x);
            var rightNumeric = long.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var y);
            var result = (leftNumeric, rightNumeric) switch
            {
                (true, true) => x.CompareTo(y),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (result != 0)
            {
                return result;
            }
        }

        return a.Length.CompareTo(b.Length);
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
    [property: JsonPropertyName("nextWindow")] DateTimeOffset? NextWindow,
    [property: JsonPropertyName("needsSupport")] bool NeedsSupport = false);

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

    /// <summary>U4: actualización en curso (se escribe al empezar y se borra al terminar; si queda, al arrancar se vuelve atrás).</summary>
    public const string InProgress = "update-in-progress.json";

    /// <summary>Último número de secuencia de manifiesto aceptado en este equipo, por canal (M4).</summary>
    public const string LastSequence = "manifest-sequence.json";

    /// <summary>M2: versión que tuvo que volver atrás en este equipo (no se reintenta hasta que haya una más nueva).</summary>
    public const string FailedVersion = "failed-version.json";

    /// <summary>ALTA 4: el propietario pidió "instalar ahora": el servidor lo propaga a las cajas (24 h) en el manifiesto que les sirve.</summary>
    public const string TerminalsInstallNow = "terminals-install-now.request";

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
