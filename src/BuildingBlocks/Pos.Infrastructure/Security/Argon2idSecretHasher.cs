using System.Globalization;
using System.Security.Cryptography;
using NSec.Cryptography;
using Pos.Application.Abstractions.Security;

namespace Pos.Infrastructure.Security;

/// <summary>Parámetros de Argon2id (memoria en KiB).</summary>
public sealed record Argon2Settings(int MemoryKiB, int Iterations)
{
    /// <summary>Contraseñas: 64 MiB, 3 pasadas (doc 06).</summary>
    public static readonly Argon2Settings Password = new(65536, 3);

    /// <summary>PIN: 19 MiB, 2 pasadas (mínimo recomendado por OWASP).</summary>
    public static readonly Argon2Settings Pin = new(19456, 2);
}

/// <summary>
/// Argon2id con libsodium (NSec) en formato PHC: <c>$argon2id$v=19$m=65536,t=3,p=1$SAL$HASH</c> (base64 sin relleno).
/// Los parámetros viajan en el hash: si se endurecen, los hashes antiguos se siguen verificando y se recalculan al
/// entrar (D3-02). Un semáforo limita los cálculos simultáneos para no agotar la memoria del servidor.
/// </summary>
public sealed class Argon2idSecretHasher : ISecretHasher, IDisposable
{
    public const int MaxConcurrentHashes = 4;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly SemaphoreSlim _gate = new(MaxConcurrentHashes, MaxConcurrentHashes);
    private readonly Argon2Settings _password;
    private readonly Argon2Settings _pin;

    public Argon2idSecretHasher()
        : this(Argon2Settings.Password, Argon2Settings.Pin)
    {
    }

    public Argon2idSecretHasher(Argon2Settings password, Argon2Settings pin)
    {
        _password = password;
        _pin = pin;
    }

    public async Task<string> HashAsync(string secret, SecretKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var settings = kind == SecretKind.Pin ? _pin : _password;
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = await DeriveAsync(secret, salt, settings, cancellationToken);
        return Format(settings, salt, hash);
    }

    public async Task<SecretVerification> VerifyAsync(string secret, string phcHash, SecretKind kind, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(secret) || !TryParse(phcHash, out var stored, out var salt, out var expected))
        {
            return SecretVerification.Failed;
        }

        var actual = await DeriveAsync(secret, salt, stored, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return SecretVerification.Failed;
        }

        var current = kind == SecretKind.Pin ? _pin : _password;
        return stored.MemoryKiB < current.MemoryKiB || stored.Iterations < current.Iterations
            ? SecretVerification.SucceededRehashNeeded
            : SecretVerification.Succeeded;
    }

    public void Dispose() => _gate.Dispose();

    internal static string Format(Argon2Settings settings, byte[] salt, byte[] hash) =>
        string.Create(CultureInfo.InvariantCulture, $"$argon2id$v=19$m={settings.MemoryKiB},t={settings.Iterations},p=1${B64(salt)}${B64(hash)}");

    internal static bool TryParse(string? phc, out Argon2Settings settings, out byte[] salt, out byte[] hash)
    {
        settings = Argon2Settings.Password;
        salt = [];
        hash = [];
        var parts = phc?.Split('$');
        if (parts is not ["", "argon2id", "v=19", var parameters, var saltText, var hashText])
        {
            return false;
        }

        var values = parameters.Split(',').Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        if (!values.TryGetValue("m", out var m) || !values.TryGetValue("t", out var t) || values.GetValueOrDefault("p") != "1"
            || !int.TryParse(m, CultureInfo.InvariantCulture, out var memory) || !int.TryParse(t, CultureInfo.InvariantCulture, out var iterations))
        {
            return false;
        }

        try
        {
            salt = FromB64(saltText);
            hash = FromB64(hashText);
        }
        catch (FormatException)
        {
            return false;
        }

        settings = new Argon2Settings(memory, iterations);
        return salt.Length >= SaltBytes && hash.Length == HashBytes;
    }

    private async Task<byte[]> DeriveAsync(string secret, byte[] salt, Argon2Settings settings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var parameters = new Argon2Parameters
            {
                DegreeOfParallelism = 1,
                MemorySize = settings.MemoryKiB,
                NumberOfPasses = settings.Iterations,
            };
            var algorithm = PasswordBasedKeyDerivationAlgorithm.Argon2id(in parameters);
            return await Task.Run(() => algorithm.DeriveBytes(secret, salt, HashBytes), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] FromB64(string text) => Convert.FromBase64String(text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '='));
}
