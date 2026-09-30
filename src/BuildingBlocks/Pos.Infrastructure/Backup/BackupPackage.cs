using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pos.Infrastructure.Backup;

/// <summary>
/// Encabezado legible del paquete <c>.posbak</c> (D11-02): identifica el backup sin descifrarlo. El SHA-256 del encabezado es parte de
/// los datos autenticados de cada bloque cifrado: cambiar el encabezado invalida el paquete.
/// </summary>
public sealed record BackupHeader(
    int Format,
    string AppVersion,
    string SchemaVersion,
    string CompanyIdentification,
    string CompanyName,
    string BranchCode,
    Guid NodeId,
    int NodeEpoch,
    DateTimeOffset CreatedAt,
    string Kind,
    long? AuditSealNo,
    string? AuditSealCode,
    string PayloadSha256,
    long PayloadSize,
    string KeyId,
    WrappedKey? RecoveryKey,
    IReadOnlyDictionary<string, long> Counts);

/// <summary>El paquete no es válido: formato desconocido, alterado, truncado o clave incorrecta.</summary>
public sealed class BackupPackageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Formato del paquete <c>.posbak</c> v1:
/// <c>"POSBAK1\n"</c> · longitud del encabezado (int32 LE) · encabezado JSON UTF-8 · bloques cifrados con AES-256-GCM de hasta 1 MiB:
/// longitud (int32 LE) · nonce (12) · texto cifrado · etiqueta (16). Datos autenticados de cada bloque = SHA-256(encabezado) · número
/// de bloque (int64 LE) · marca de último bloque: no se pueden alterar, reordenar ni truncar sin que se detecte.
/// </summary>
public static class BackupPackage
{
    public const int CurrentFormat = 1;
    public const string Extension = ".posbak";
    private const int ChunkSize = 1024 * 1024;
    private static readonly byte[] Magic = "POSBAK1\n"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static async Task WriteAsync(Stream output, BackupHeader header, Stream payload, byte[] dataKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(payload);
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, Json);
        var headerHash = SHA256.HashData(headerBytes);
        await output.WriteAsync(Magic, cancellationToken);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, headerBytes.Length);
        await output.WriteAsync(length, cancellationToken);
        await output.WriteAsync(headerBytes, cancellationToken);

        using var aes = new AesGcm(dataKey, 16);
        var buffer = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var read = await FillAsync(payload, buffer, cancellationToken);
        long index = 0;
        while (true)
        {
            var nextRead = read == ChunkSize ? await FillAsync(payload, next, cancellationToken) : 0;
            var final = nextRead == 0;
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[read];
            var tag = new byte[16];
            aes.Encrypt(nonce, buffer.AsSpan(0, read), cipher, tag, Aad(headerHash, index, final));
            BinaryPrimitives.WriteInt32LittleEndian(length, read);
            await output.WriteAsync(length, cancellationToken);
            await output.WriteAsync(nonce, cancellationToken);
            await output.WriteAsync(cipher, cancellationToken);
            await output.WriteAsync(tag, cancellationToken);
            if (final)
            {
                break;
            }

            (buffer, next) = (next, buffer);
            read = nextRead;
            index++;
        }
    }

    /// <summary>Lee solo el encabezado (sin clave).</summary>
    public static async Task<BackupHeader> ReadHeaderAsync(Stream input, CancellationToken cancellationToken) =>
        (await ReadHeaderCoreAsync(input, cancellationToken)).Header;

    /// <summary>Descifra el contenido en <paramref name="payload"/> y verifica cada bloque y el SHA-256 del contenido.</summary>
    public static async Task<BackupHeader> DecryptAsync(Stream input, byte[] dataKey, Stream payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanSeek)
        {
            throw new ArgumentException("El backup se descifra desde un archivo (flujo con posición).", nameof(input));
        }

        var (header, headerHash) = await ReadHeaderCoreAsync(input, cancellationToken);
        if (BackupKeyStore.KeyId(dataKey) != header.KeyId)
        {
            throw new BackupPackageException("La clave no corresponde a este backup (fue hecho con otra clave: use el código de recuperación).");
        }

        using var aes = new AesGcm(dataKey, 16);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var lengthBuffer = new byte[4];
        long index = 0;
        long total = 0;
        while (true)
        {
            if (await FillAsync(input, lengthBuffer, cancellationToken) != 4)
            {
                throw new BackupPackageException("El backup está incompleto (truncado).");
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (length is < 0 or > ChunkSize)
            {
                throw new BackupPackageException("El backup está dañado (bloque inválido).");
            }

            var nonce = new byte[12];
            var cipher = new byte[length];
            var tag = new byte[16];
            if (await FillAsync(input, nonce, cancellationToken) != 12 || await FillAsync(input, cipher, cancellationToken) != length
                || await FillAsync(input, tag, cancellationToken) != 16)
            {
                throw new BackupPackageException("El backup está incompleto (truncado).");
            }

            var plain = new byte[length];

            // El último bloque se cifró con la marca de final: si alguien trunca el archivo, el bloque que queda al final no la tiene.
            var final = input.Position == input.Length;
            try
            {
                aes.Decrypt(nonce, cipher, tag, plain, Aad(headerHash, index, final));
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new BackupPackageException("El backup fue alterado, está truncado o la clave no es correcta.", ex);
            }

            sha.AppendData(plain);
            total += length;
            await payload.WriteAsync(plain, cancellationToken);
            if (final)
            {
                break;
            }

            index++;
        }

        if (Convert.ToHexStringLower(sha.GetHashAndReset()) != header.PayloadSha256 || total != header.PayloadSize)
        {
            throw new BackupPackageException("El contenido del backup no coincide con su huella (SHA-256).");
        }

        return header;
    }

    private static async Task<(BackupHeader Header, byte[] HeaderHash)> ReadHeaderCoreAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var magic = new byte[Magic.Length];
        if (await FillAsync(input, magic, cancellationToken) != Magic.Length || !magic.AsSpan().SequenceEqual(Magic))
        {
            throw new BackupPackageException("El archivo no es un backup del POS (.posbak).");
        }

        var length = new byte[4];
        await FillAsync(input, length, cancellationToken);
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (headerLength is <= 0 or > 1024 * 1024)
        {
            throw new BackupPackageException("El encabezado del backup está dañado.");
        }

        var headerBytes = new byte[headerLength];
        if (await FillAsync(input, headerBytes, cancellationToken) != headerLength)
        {
            throw new BackupPackageException("El encabezado del backup está incompleto.");
        }

        BackupHeader? header;
        try
        {
            header = JsonSerializer.Deserialize<BackupHeader>(headerBytes, Json);
        }
        catch (JsonException ex)
        {
            throw new BackupPackageException("El encabezado del backup está dañado.", ex);
        }

        if (header is null || header.Format != CurrentFormat)
        {
            throw new BackupPackageException($"Formato de backup no soportado ({header?.Format}).");
        }

        return (header, SHA256.HashData(headerBytes));
    }

    private static byte[] Aad(byte[] headerHash, long index, bool final)
    {
        var aad = new byte[headerHash.Length + 9];
        headerHash.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(headerHash.Length), index);
        aad[^1] = final ? (byte)1 : (byte)0;
        return aad;
    }

    private static async Task<int> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>Nombre del archivo: <c>tienda-NIT-SUCURSAL-AAAAMMDD-HHMM-TIPO.posbak</c> (hora local).</summary>
    public static string FileName(BackupHeader header, DateTimeOffset localTime)
    {
        ArgumentNullException.ThrowIfNull(header);
        var nit = new string([.. header.CompanyIdentification.Where(char.IsAsciiLetterOrDigit)]);
        var branch = new string([.. header.BranchCode.Where(char.IsAsciiLetterOrDigit)]);
        return $"tienda-{nit}-{branch}-{localTime:yyyyMMdd-HHmmss}-{header.Kind}{Extension}";
    }

    internal static string Sha256Hex(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));
}
