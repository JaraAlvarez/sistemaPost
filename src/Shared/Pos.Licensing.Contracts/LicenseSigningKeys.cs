using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace Pos.Licensing.Contracts;

/// <summary>
/// Clave privada Ed25519 con la que la nube firma los tokens (L-04). Vive en un archivo protegido fuera de la BD y del
/// repositorio (PEM PKCS#8); el <see cref="Kid"/> se deriva de la clave pública, así el archivo se identifica solo.
/// </summary>
public sealed class LicenseSigningKey : IDisposable
{
    private readonly Key _key;

    private LicenseSigningKey(Key key)
    {
        _key = key;
        PublicKey = LicensePublicKey.FromRaw(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    public LicensePublicKey PublicKey { get; }

    public string Kid => PublicKey.Kid;

    /// <summary>Genera un par de claves nuevo (exportable, para guardarlo en su archivo y en el respaldo cifrado).</summary>
    public static LicenseSigningKey Generate() =>
        new(Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport }));

    /// <summary>Lee la clave privada en formato PEM (<c>-----BEGIN PRIVATE KEY-----</c>).</summary>
    public static LicenseSigningKey ImportPem(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);
        var creation = new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
        if (!Key.TryImport(SignatureAlgorithm.Ed25519, Encoding.ASCII.GetBytes(pem.Trim()), KeyBlobFormat.PkixPrivateKeyText, out var key, creation))
        {
            throw new FormatException("El archivo no contiene una clave privada Ed25519 en formato PEM (PKCS#8).");
        }

        return new LicenseSigningKey(key!);
    }

    /// <summary>Exporta la clave privada en PEM. Solo para guardarla en su archivo protegido o en el respaldo cifrado.</summary>
    public string ExportPem() => Encoding.ASCII.GetString(_key.Export(KeyBlobFormat.PkixPrivateKeyText));

    public byte[] Sign(ReadOnlySpan<byte> data) => SignatureAlgorithm.Ed25519.Sign(_key, data);

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Clave pública Ed25519 identificada por su <see cref="Kid"/>. El POS trae varias embebidas (la activa y una de reserva,
/// L-04) y la nube publica todas las vigentes en <c>GET /v1/public-keys</c>.
/// </summary>
public sealed class LicensePublicKey
{
    private readonly PublicKey _key;

    private LicensePublicKey(PublicKey key, string x)
    {
        _key = key;
        X = x;
        Kid = ComputeKid(Base64Url.DecodeFromChars(x));
    }

    /// <summary>Identificador: <c>ed25519-</c> + 12 hex del SHA-256 de la clave pública.</summary>
    public string Kid { get; }

    /// <summary>Clave pública cruda (32 bytes) en base64url, como el campo <c>x</c> de un JWK OKP.</summary>
    public string X { get; }

    internal PublicKey Key => _key;

    public static LicensePublicKey FromRaw(ReadOnlySpan<byte> raw)
    {
        if (!PublicKey.TryImport(SignatureAlgorithm.Ed25519, raw, KeyBlobFormat.RawPublicKey, out var key))
        {
            throw new FormatException("La clave pública no es una clave Ed25519 válida (32 bytes).");
        }

        return new LicensePublicKey(key!, Base64Url.EncodeToString(raw));
    }

    public static bool TryParse(string? x, [NotNullWhen(true)] out LicensePublicKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(x) || x.Length != 43)
        {
            return false;
        }

        try
        {
            key = FromRaw(Base64Url.DecodeFromChars(x));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string ComputeKid(ReadOnlySpan<byte> rawPublicKey) =>
        "ed25519-" + Convert.ToHexStringLower(SHA256.HashData(rawPublicKey))[..12];

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        SignatureAlgorithm.Ed25519.Verify(_key, data, signature);
}

/// <summary>Conjunto de claves públicas de confianza, por <c>kid</c> (rotación sin reinstalar el POS).</summary>
public sealed class LicenseKeyRing
{
    private readonly Dictionary<string, LicensePublicKey> _keys;

    public LicenseKeyRing(IEnumerable<LicensePublicKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys.GroupBy(k => k.Kid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Kids => _keys.Keys;

    public bool TryGet(string kid, [NotNullWhen(true)] out LicensePublicKey? key) => _keys.TryGetValue(kid, out key);
}
