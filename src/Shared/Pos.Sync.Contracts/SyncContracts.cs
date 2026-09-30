using System.Buffers.Text;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;

namespace Pos.Sync.Contracts;

/// <summary>Rutas y cabeceras de la sincronización (docs/fases/fase-16-propuesta.md).</summary>
public static class SyncRoutes
{
    public const string Batches = "/v1/sync/batches";

    /// <summary>Autenticación de la tienda: <c>License &lt;token de licencia&gt;</c> (D16-03).</summary>
    public const string AuthorizationScheme = "License";

    /// <summary>Huella del equipo que envía (debe coincidir con la del token, 2 de 3).</summary>
    public const string FingerprintHeader = "X-Pos-Fingerprint";
}

/// <summary>Tipos de dato que suben (D16-01).</summary>
public static class SyncKinds
{
    public const string Sale = "SALE";
    public const string CashSession = "CASH_SESSION";
    public const string Stock = "STOCK";
    public const string Product = "PRODUCT";

    public static IReadOnlyList<string> All { get; } = [Sale, CashSession, Stock, Product];
}

/// <summary>
/// Un dato de la tienda. <c>Id</c> identifica el documento dentro de la instalación; <c>Version</c> es la marca de tiempo de su último cambio
/// (la nube conserva la más reciente); <c>Data</c> es el JSON del documento.
/// </summary>
public sealed record SyncItem(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] DateTimeOffset Version,
    [property: JsonPropertyName("businessDate")] DateOnly? BusinessDate,
    [property: JsonPropertyName("data")] JsonElement Data);

/// <summary>Lote de la tienda: el mismo en línea y en el paquete <c>.possync</c>.</summary>
public sealed record SyncBatch(
    [property: JsonPropertyName("batchId")] Guid BatchId,
    [property: JsonPropertyName("installationId")] Guid InstallationId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("items")] IReadOnlyList<SyncItem> Items);

/// <summary>Acuse de la nube: cuántos datos se aplicaron y cuántos se ignoraron por ser iguales o más viejos (idempotencia).</summary>
public sealed record SyncAck(
    [property: JsonPropertyName("batchId")] Guid BatchId,
    [property: JsonPropertyName("received")] int Received,
    [property: JsonPropertyName("applied")] int Applied,
    [property: JsonPropertyName("ignored")] int Ignored,
    [property: JsonPropertyName("duplicate")] bool Duplicate);

public static class SyncJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>El paquete no se pudo abrir: clave equivocada, archivo alterado o formato desconocido.</summary>
public sealed class SyncPackageException(string message) : Exception(message);

/// <summary>
/// Paquete <c>.possync</c> (D16-04): línea de encabezado legible + lote comprimido y cifrado con ChaCha20-Poly1305 bajo una clave acordada por
/// X25519 entre una clave efímera y la clave pública de sincronización de la nube. El encabezado va autenticado con el cifrado: cambiarlo
/// invalida el paquete. Solo la nube, con su clave privada, lo abre.
/// </summary>
public static class SyncPackage
{
    public const string Magic = "POSSYNC1";

    private static readonly KeyAgreementAlgorithm Agreement = KeyAgreementAlgorithm.X25519;
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.ChaCha20Poly1305;
    private static readonly byte[] Info = Encoding.ASCII.GetBytes("pos-sync-package-v1");

    public sealed record Header(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("installationId")] Guid InstallationId,
        [property: JsonPropertyName("batchId")] Guid BatchId,
        [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("items")] int Items,
        [property: JsonPropertyName("ephemeralKey")] string EphemeralKey,
        [property: JsonPropertyName("nonce")] string Nonce);

    public static byte[] Seal(SyncBatch batch, string cloudPublicKey)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var recipient = PublicKey.Import(Agreement, Base64Url.DecodeFromChars(cloudPublicKey), KeyBlobFormat.RawPublicKey);
        using var ephemeral = Key.Create(Agreement);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(Aead.NonceSize);
        var header = new Header(Magic, batch.InstallationId, batch.BatchId, batch.CreatedAt, batch.Items.Count,
            Base64Url.EncodeToString(ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey)), Base64Url.EncodeToString(nonce));
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, SyncJson.Options);
        using var key = DeriveKey(ephemeral, recipient, headerBytes);
        var ciphertext = Aead.Encrypt(key, nonce, headerBytes, Compress(JsonSerializer.SerializeToUtf8Bytes(batch, SyncJson.Options)));
        return [.. headerBytes, (byte)'\n', .. ciphertext];
    }

    public static Header ReadHeader(ReadOnlySpan<byte> package)
    {
        var newline = package.IndexOf((byte)'\n');
        var header = newline > 0 ? TryParse(package[..newline]) : null;
        return header is { Format: Magic } ? header : throw new SyncPackageException("No es un paquete de sincronización (.possync) válido.");
    }

    public static SyncBatch Open(ReadOnlySpan<byte> package, Key cloudPrivateKey)
    {
        var header = ReadHeader(package);
        var newline = package.IndexOf((byte)'\n');
        var headerBytes = package[..newline].ToArray();
        var sender = PublicKey.Import(Agreement, Base64Url.DecodeFromChars(header.EphemeralKey), KeyBlobFormat.RawPublicKey);
        using var key = DeriveKey(cloudPrivateKey, sender, headerBytes);
        var plain = Aead.Decrypt(key, Base64Url.DecodeFromChars(header.Nonce), headerBytes, package[(newline + 1)..])
            ?? throw new SyncPackageException("El paquete no se pudo abrir: fue alterado o no es para esta nube.");
        var batch = JsonSerializer.Deserialize<SyncBatch>(Decompress(plain), SyncJson.Options)
            ?? throw new SyncPackageException("El paquete está vacío.");
        return batch.InstallationId == header.InstallationId && batch.BatchId == header.BatchId
            ? batch
            : throw new SyncPackageException("El encabezado del paquete no corresponde a su contenido.");
    }

    /// <summary>Par de claves de sincronización de la nube: la privada en un archivo protegido; la pública se embebe en el POS.</summary>
    public static (Key Private, string PublicKey) GenerateKeyPair()
    {
        var key = Key.Create(Agreement, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (key, Base64Url.EncodeToString(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }

    public static string ExportPrivate(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Base64Url.EncodeToString(key.Export(KeyBlobFormat.RawPrivateKey));
    }

    public static Key ImportPrivate(string text) =>
        Key.Import(Agreement, Base64Url.DecodeFromChars(text.Trim()), KeyBlobFormat.RawPrivateKey);

    public static string PublicOf(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Base64Url.EncodeToString(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    private static Key DeriveKey(Key own, PublicKey other, byte[] salt)
    {
        using var shared = Agreement.Agree(own, other) ?? throw new SyncPackageException("No se pudo acordar la clave del paquete.");
        return KeyDerivationAlgorithm.HkdfSha256.DeriveKey(shared, salt, Info, Aead);
    }

    private static Header? TryParse(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize<Header>(json, SyncJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
