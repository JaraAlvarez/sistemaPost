using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Pos.Cloud.PortalIdentity.Domain;

/// <summary>
/// Contraseñas de un solo uso por tiempo (TOTP, RFC 6238 sobre HOTP, RFC 4226): HMAC-SHA1, 6 dígitos, pasos de 30 s, como
/// Google Authenticator, Microsoft Authenticator o Aegis. El secreto (160 bits) se muestra en Base32 y en un código QR
/// <c>otpauth://</c>; en la BD se guarda cifrado.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int SecretBytes = 20;

    /// <summary>Pasos aceptados antes y después del actual (tolerancia de reloj del teléfono: ±30 s).</summary>
    public const int AllowedDrift = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepAt(DateTimeOffset instant) => instant.ToUnixTimeSeconds() / StepSeconds;

    public static string ComputeCode(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350 // HMAC-SHA1 es el algoritmo que fija RFC 6238 y que usan las aplicaciones autenticadoras.
        HMACSHA1.HashData(secret, counter, hash);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var code = binary % 1_000_000;
        return code.ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Verifica el código en el paso actual ±<see cref="AllowedDrift"/> y devuelve el paso que coincidió. Un paso igual o anterior
    /// a <paramref name="lastUsedStep"/> se rechaza: el mismo código no sirve dos veces (repetición).
    /// </summary>
    public static long? Verify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        var normalized = (code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (normalized.Length != Digits || !normalized.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = StepAt(now);
        long? matched = null;
        for (var step = current - AllowedDrift; step <= current + AllowedDrift; step++)
        {
            // Se evalúan todos los pasos (sin salir antes) para no filtrar por tiempos de respuesta cuál coincidió.
            var candidate = Encoding.ASCII.GetBytes(ComputeCode(secret, step));
            if (CryptographicOperations.FixedTimeEquals(candidate, Encoding.ASCII.GetBytes(normalized)) && (lastUsedStep is null || step > lastUsedStep))
            {
                matched ??= step;
            }
        }

        return matched;
    }

    public static string ToBase32(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 / 5) + 1);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cleaned = text.Trim().TrimEnd('=').Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var output = new List<byte>(cleaned.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in cleaned)
        {
            var value = Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException("El texto no está en Base32.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }

    /// <summary>Enlace <c>otpauth://totp/…</c> que codifica el QR de enrolamiento.</summary>
    public static string EnrollmentLink(string issuer, string accountName, ReadOnlySpan<byte> secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        var label = Uri.EscapeDataString($"{issuer}:{accountName}");
        return string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{label}?secret={ToBase32(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}");
    }
}
