using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Pos.Infrastructure.Security;

/// <summary>
/// Secretos de la instalación protegidos con DPAPI a nivel de máquina (<c>dpapi:BASE64</c>). Solo el propio equipo
/// puede descifrarlos. Los valores sin prefijo se usan tal cual (desarrollo).
/// </summary>
public static class ProtectedSecret
{
    public const string Prefix = "dpapi:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PosSupermercado.Database.v1");

    /// <summary>Protege bytes con DPAPI de la máquina y una entropía propia del uso (p. ej. la clave de los backups).</summary>
    [SupportedOSPlatform("windows")]
    public static byte[] ProtectBytes(byte[] plain, string purpose) =>
        ProtectedData.Protect(plain, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.LocalMachine);

    [SupportedOSPlatform("windows")]
    public static byte[] UnprotectBytes(byte[] protectedBytes, string purpose) =>
        ProtectedData.Unprotect(protectedBytes, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.LocalMachine);

    public static string? Reveal(string? value)
    {
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return value;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Los secretos protegidos con DPAPI solo se pueden leer en Windows.");
        }

        return Unprotect(value[Prefix.Length..]);
    }

    [SupportedOSPlatform("windows")]
    public static string Protect(string plain) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine));

    [SupportedOSPlatform("windows")]
    private static string Unprotect(string base64) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(base64), Entropy, DataProtectionScope.LocalMachine));
}
