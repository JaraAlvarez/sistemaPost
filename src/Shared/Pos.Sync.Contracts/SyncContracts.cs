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

    /// <summary>Clave pública Ed25519 de firma de paquetes de la instalación (base64url): la nube la registra (revisión S2).</summary>
    public const string SigningKeyHeader = "X-Pos-Sync-Key";

    /// <summary>Prueba de posesión de la clave: firma de <see cref="SyncSigning.KeyProofMessage"/> (base64url).</summary>
    public const string SigningKeyProofHeader = "X-Pos-Sync-Key-Proof";
}

/// <summary>Límites del lote y del paquete (revisión S3): los mismos en la tienda y en la nube.</summary>
public static class SyncLimits
{
    /// <summary>Datos por lote en línea.</summary>
    public const int MaxOnlineItems = 5_000;

    /// <summary>Datos por paquete <c>.possync</c>.</summary>
    public const int MaxPackageItems = 200_000;

    /// <summary>
    /// Bytes del lote descomprimido dentro de un paquete (protege contra una "bomba" gzip). La tienda arma paquetes más chicos que esto; si
    /// no caben, exporta menos datos y el resto sale en el paquete siguiente.
    /// </summary>
    public const long MaxPackageBytes = 128L * 1024 * 1024;

    /// <summary>Bytes de UN dato (venta, cierre, saldo o producto): su JSON en UTF-8 (<see cref="DataBytes"/>).</summary>
    public const int MaxItemBytes = 1024 * 1024;

    /// <summary>Tamaño de un dato como lo miden la tienda y la nube: bytes UTF-8 de su JSON.</summary>
    public static int DataBytes(JsonElement data) =>
        data.ValueKind == JsonValueKind.Undefined ? 0 : Encoding.UTF8.GetByteCount(data.GetRawText());
}

/// <summary>
/// Firma Ed25519 de la instalación (revisión S2): cada tienda tiene su par de claves; la pública la registra la nube en la sincronización en
/// línea (canal autenticado con el token de licencia y la huella) y con ella verifica los paquetes cargados en el portal.
/// </summary>
public static class SyncSigning
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    public static Key GenerateKey() => Key.Create(Algorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    public static byte[] ExportPrivate(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Export(KeyBlobFormat.RawPrivateKey);
    }

    public static Key ImportPrivate(ReadOnlySpan<byte> raw) =>
        Key.Import(Algorithm, raw, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    public static string PublicOf(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Base64Url.EncodeToString(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    public static byte[] Sign(Key key, ReadOnlySpan<byte> data) => Algorithm.Sign(key, data);

    /// <summary>¿<paramref name="signature"/> es la firma de <paramref name="data"/> con la clave pública (base64url)? Falso si algo no es válido.</summary>
    public static bool Verify(string? publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (!IsPublicKey(publicKey) || signature.Length != Algorithm.SignatureSize)
        {
            return false;
        }

        return PublicKey.TryImport(Algorithm, Base64Url.DecodeFromChars(publicKey), KeyBlobFormat.RawPublicKey, out var key)
            && key is not null && Algorithm.Verify(key, data, signature);
    }

    public static bool IsPublicKey(string? text)
    {
        if (text is not { Length: 43 })
        {
            return false;
        }

        try
        {
            return Base64Url.DecodeFromChars(text).Length == Algorithm.PublicKeySize;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Mensaje de la prueba de posesión que acompaña a la clave en cada lote en línea.</summary>
    public static byte[] KeyProofMessage(Guid installationId, Guid batchId) =>
        Encoding.ASCII.GetBytes($"pos-sync-key-v1:{installationId:D}:{batchId:D}");

    public static string KeyProof(Key key, Guid installationId, Guid batchId) =>
        Base64Url.EncodeToString(Sign(key, KeyProofMessage(installationId, batchId)));

    public static bool VerifyKeyProof(string? publicKey, string? proof, Guid installationId, Guid batchId)
    {
        if (string.IsNullOrWhiteSpace(proof) || proof.Length > 100)
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Base64Url.DecodeFromChars(proof.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return Verify(publicKey, KeyProofMessage(installationId, batchId), signature);
    }
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

/// <summary>Paquete abierto: el lote y la clave pública con que la tienda lo firmó (<c>null</c> = paquete v1, sin firma).</summary>
public sealed record OpenedSyncPackage(SyncBatch Batch, string? SignerPublicKey);

/// <summary>
/// Paquete <c>.possync</c> (D16-04): línea de encabezado legible + lote comprimido y cifrado con ChaCha20-Poly1305 bajo una clave acordada por
/// X25519 entre una clave efímera y la clave pública de sincronización de la nube. El encabezado va autenticado con el cifrado: cambiarlo
/// invalida el paquete. Solo la nube, con su clave privada, lo abre.
/// <para>
/// <c>POSSYNC2</c> (revisión S2): el encabezado lleva la clave pública de la instalación (<c>signer</c>) y el paquete termina con 64 bytes de
/// firma Ed25519 de todo lo anterior. <c>POSSYNC1</c> (sin firma) se sigue leyendo. Al abrir se limitan los datos y los bytes
/// descomprimidos (S3).
/// </para>
/// </summary>
public static class SyncPackage
{
    public const string Magic = "POSSYNC1";
    public const string SignedMagic = "POSSYNC2";

    private const int SignatureSize = 64;
    private const byte NewLine = 0x0A;

    private static readonly KeyAgreementAlgorithm Agreement = KeyAgreementAlgorithm.X25519;
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.ChaCha20Poly1305;
    private static readonly byte[] Info = Encoding.ASCII.GetBytes("pos-sync-package-v1");

    /// <summary>Opciones de lectura por máximo de datos (se reutilizan: casi siempre es <see cref="SyncLimits.MaxPackageItems"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, JsonSerializerOptions> ReadOptions = new();

    public sealed record Header(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("installationId")] Guid InstallationId,
        [property: JsonPropertyName("batchId")] Guid BatchId,
        [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("items")] int Items,
        [property: JsonPropertyName("ephemeralKey")] string EphemeralKey,
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("signer")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Signer = null);

    /// <summary>Cifra el lote para la nube y, con <paramref name="signingKey"/>, lo firma con la clave de la instalación (formato v2).</summary>
    public static byte[] Seal(SyncBatch batch, string cloudPublicKey, Key? signingKey = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var recipient = PublicKey.Import(Agreement, Base64Url.DecodeFromChars(cloudPublicKey), KeyBlobFormat.RawPublicKey);
        using var ephemeral = Key.Create(Agreement);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(Aead.NonceSize);
        var header = new Header(signingKey is null ? Magic : SignedMagic, batch.InstallationId, batch.BatchId, batch.CreatedAt, batch.Items.Count,
            Base64Url.EncodeToString(ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey)), Base64Url.EncodeToString(nonce),
            signingKey is null ? null : SyncSigning.PublicOf(signingKey));
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, SyncJson.Options);
        using var key = DeriveKey(ephemeral, recipient, headerBytes);
        var ciphertext = Aead.Encrypt(key, nonce, headerBytes, Compress(JsonSerializer.SerializeToUtf8Bytes(batch, SyncJson.Options)));
        byte[] body = [.. headerBytes, NewLine, .. ciphertext];
        return signingKey is null ? body : [.. body, .. SyncSigning.Sign(signingKey, body)];
    }

    public static Header ReadHeader(ReadOnlySpan<byte> package)
    {
        var newline = package.IndexOf(NewLine);
        var header = newline > 0 ? TryParse(package[..newline]) : null;
        return header is { Format: Magic or SignedMagic } ? header : throw new SyncPackageException("No es un paquete de sincronización (.possync) válido.");
    }

    public static SyncBatch Open(ReadOnlySpan<byte> package, Key cloudPrivateKey) => OpenSigned(package, cloudPrivateKey).Batch;

    /// <summary>
    /// Abre el paquete: verifica la firma de la instalación (v2), descifra y descomprime con límites (<see cref="SyncLimits"/>). Devuelve la clave
    /// que firmó: quien lo recibe debe compararla con la registrada para la instalación.
    /// </summary>
    public static OpenedSyncPackage OpenSigned(
        ReadOnlySpan<byte> package, Key cloudPrivateKey, int maxItems = SyncLimits.MaxPackageItems, long maxBytes = SyncLimits.MaxPackageBytes)
    {
        var header = ReadHeader(package);
        if (header.Items < 0 || header.Items > maxItems)
        {
            throw new SyncPackageException($"El paquete supera el máximo de {maxItems} datos.");
        }

        var signed = header.Format == SignedMagic;
        if (signed)
        {
            if (package.Length <= SignatureSize || !SyncSigning.IsPublicKey(header.Signer)
                || !SyncSigning.Verify(header.Signer, package[..^SignatureSize], package[^SignatureSize..]))
            {
                throw new SyncPackageException("La firma de la tienda no es válida: el paquete fue alterado.");
            }

            package = package[..^SignatureSize];
        }

        var newline = package.IndexOf(NewLine);
        var headerBytes = package[..newline].ToArray();
        var sender = PublicKey.Import(Agreement, Base64Url.DecodeFromChars(header.EphemeralKey), KeyBlobFormat.RawPublicKey);
        using var key = DeriveKey(cloudPrivateKey, sender, headerBytes);
        var plain = Aead.Decrypt(key, Base64Url.DecodeFromChars(header.Nonce), headerBytes, package[(newline + 1)..])
            ?? throw new SyncPackageException("El paquete no se pudo abrir: fue alterado o no es para esta nube.");
        var batch = Decompress(plain, maxBytes, maxItems) ?? throw new SyncPackageException("El paquete está vacío.");
        if (batch.Items is null || batch.Items.Count > maxItems)
        {
            throw new SyncPackageException($"El paquete supera el máximo de {maxItems} datos.");
        }

        return batch.InstallationId == header.InstallationId && batch.BatchId == header.BatchId
            ? new OpenedSyncPackage(batch, signed ? header.Signer : null)
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

    /// <summary>
    /// Descomprime y deserializa en flujo, sin pasar de <paramref name="maxBytes"/> descomprimidos; los datos se cuentan y se mide cada uno
    /// MIENTRAS se leen (no se arma la lista completa para contarla después).
    /// </summary>
    private static SyncBatch? Decompress(byte[] data, long maxBytes, int maxItems)
    {
        using var input = new LimitedReadStream(new GZipStream(new MemoryStream(data), CompressionMode.Decompress), maxBytes);
        var options = ReadOptions.GetOrAdd(maxItems, static max =>
        {
            var created = new JsonSerializerOptions(SyncJson.Options);
            created.Converters.Add(new LimitedItemsConverter(max, SyncLimits.MaxItemBytes));
            return created;
        });
        try
        {
            return JsonSerializer.Deserialize<SyncBatch>(input, options);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new SyncPackageException("El contenido del paquete no es un lote válido.");
        }
    }
}

/// <summary>
/// Lee la lista de datos de un paquete (revisión S3). System.Text.Json entrega a un convertidor propio el valor COMPLETO ya leído del flujo:
/// la lista entera queda en el búfer (acotado por <see cref="SyncLimits.MaxPackageBytes"/> con <see cref="LimitedReadStream"/>), pero los
/// datos se convierten uno por uno y la lectura se corta en cuanto pasa del máximo de datos o un dato pasa del máximo de bytes, sin crear
/// todos los objetos para contarlos después.
/// </summary>
internal sealed class LimitedItemsConverter(int maxItems, int maxItemBytes) : JsonConverter<IReadOnlyList<SyncItem>>
{
    public override IReadOnlyList<SyncItem> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Se esperaba la lista de datos.");
        }

        var items = new List<SyncItem>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (items.Count >= maxItems)
            {
                throw new SyncPackageException($"El paquete supera el máximo de {maxItems} datos.");
            }

            using var item = JsonDocument.ParseValue(ref reader);
            if (item.RootElement.ValueKind != JsonValueKind.Object
                || (item.RootElement.TryGetProperty("data", out var data) && SyncLimits.DataBytes(data) > maxItemBytes))
            {
                throw new SyncPackageException($"Un dato del paquete supera {maxItemBytes / 1024} KiB.");
            }

            items.Add(item.RootElement.Deserialize<SyncItem>(SyncJson.Options) ?? throw new JsonException("Dato vacío."));
        }

        return items;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<SyncItem> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, SyncJson.Options);
}

/// <summary>Flujo de solo lectura que falla al pasar de un máximo de bytes (contenido descomprimido de un paquete, revisión S3).</summary>
public sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
{
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Count(int read)
    {
        _read += read;
        return _read > maxBytes ? throw new SyncPackageException($"El paquete descomprimido supera {maxBytes / (1024 * 1024)} MiB.") : read;
    }
}
