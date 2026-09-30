using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using Pos.Infrastructure.Security;

namespace Pos.Infrastructure.Backup;

/// <summary>
/// Código de recuperación del propietario (D11-04): 24 caracteres en 6 grupos de 4 del alfabeto Crockford base32 (sin I, L, O, U:
/// no se confunden al copiarlo a mano), 120 bits. Se muestra una sola vez; solo el propietario lo guarda.
/// </summary>
public static class RecoveryCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Generate()
    {
        var chars = new char[24];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return string.Join('-', Enumerable.Range(0, 6).Select(g => new string(chars, g * 4, 4)));
    }

    /// <summary>Quita espacios y guiones, pasa a mayúsculas y corrige las letras que se confunden (O→0, I/L→1).</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var normalized = new StringBuilder(24);
        foreach (var c in code.ToUpperInvariant())
        {
            if (c is '-' or ' ')
            {
                continue;
            }

            var mapped = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (!Alphabet.Contains(mapped, StringComparison.Ordinal))
            {
                return null;
            }

            normalized.Append(mapped);
        }

        return normalized.Length == 24 ? normalized.ToString() : null;
    }
}

/// <summary>Clave de datos envuelta con el código de recuperación (Argon2id + AES-256-GCM). Va en el encabezado de cada backup.</summary>
public sealed record WrappedKey(int Version, string Salt, int MemoryKiB, int Iterations, string Nonce, string Ciphertext)
{
    public const int DefaultMemoryKiB = 65536;
    public const int DefaultIterations = 3;

    /// <summary>Envuelve la clave de datos con el código de recuperación.</summary>
    public static WrappedKey Wrap(byte[] dataKey, string recoveryCode, int version, int memoryKiB = DefaultMemoryKiB, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(dataKey);
        var normalized = RecoveryCode.Normalize(recoveryCode) ?? throw new ArgumentException("Código de recuperación inválido.", nameof(recoveryCode));
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Derive(normalized, salt, memoryKiB, iterations);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[dataKey.Length + 16];
        using (var aes = new AesGcm(kek, 16))
        {
            aes.Encrypt(nonce, dataKey, cipher.AsSpan(0, dataKey.Length), cipher.AsSpan(dataKey.Length), Encoding.ASCII.GetBytes("POSBAK-KEY"));
        }

        CryptographicOperations.ZeroMemory(kek);
        return new WrappedKey(version, Convert.ToBase64String(salt), memoryKiB, iterations, Convert.ToBase64String(nonce), Convert.ToBase64String(cipher));
    }

    /// <summary>Recupera la clave de datos con el código. <c>null</c> si el código no corresponde.</summary>
    public byte[]? Unwrap(string recoveryCode)
    {
        var normalized = RecoveryCode.Normalize(recoveryCode);
        if (normalized is null)
        {
            return null;
        }

        var kek = Derive(normalized, Convert.FromBase64String(Salt), MemoryKiB, Iterations);
        var cipher = Convert.FromBase64String(Ciphertext);
        var plain = new byte[cipher.Length - 16];
        try
        {
            using var aes = new AesGcm(kek, 16);
            aes.Decrypt(Convert.FromBase64String(Nonce), cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), plain, Encoding.ASCII.GetBytes("POSBAK-KEY"));
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static byte[] Derive(string code, byte[] salt, int memoryKiB, int iterations)
    {
        var parameters = new Argon2Parameters { DegreeOfParallelism = 1, MemorySize = memoryKiB, NumberOfPasses = iterations };
        return PasswordBasedKeyDerivationAlgorithm.Argon2id(in parameters).DeriveBytes(code, salt, 32);
    }
}

/// <summary>
/// Clave de datos de los backups de la instalación (D11-03): 32 bytes aleatorios guardados en el equipo protegidos con DPAPI de la
/// máquina (<c>{DataRoot}\config\backup.key</c>). Nunca se guarda en la BD ni en claro.
/// </summary>
public sealed class BackupKeyStore(string keyFile)
{
    // Identificador criptográfico interno y estable (no es el nombre comercial): cambiarlo dejaría ilegibles los secretos ya cifrados.
    public const string Purpose = "PosSupermercado.Backup.DataKey.v1";

    public string KeyFile { get; } = keyFile;

    public bool Exists => File.Exists(KeyFile);

    /// <summary>Lee la clave; si no existe la crea (primer backup de la instalación).</summary>
    public byte[] GetOrCreate()
    {
        if (Exists)
        {
            return Read();
        }

        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        File.WriteAllBytes(KeyFile, Protect(key));
        return key;
    }

    public byte[] Read()
    {
        var stored = File.ReadAllBytes(KeyFile);
        return OperatingSystem.IsWindows() ? ProtectedSecret.UnprotectBytes(stored, Purpose) : stored;
    }

    /// <summary>Identificador corto de la clave (no la revela): los primeros 8 bytes del SHA-256.</summary>
    public static string KeyId(byte[] dataKey) => Convert.ToHexStringLower(SHA256.HashData(dataKey).AsSpan(0, 8));

    private static byte[] Protect(byte[] key) => OperatingSystem.IsWindows() ? ProtectedSecret.ProtectBytes(key, Purpose) : key;
}
