using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Pos.Server.Host.Database;

/// <summary>Sección <c>Pos:Database</c>.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Pos:Database";

    /// <summary>Cadena del rol <c>pos_app</c>. Puede venir protegida con DPAPI (<c>dpapi:BASE64</c>, la escribe el instalador).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Cadena del rol <c>pos_migrator</c>; solo se usa si <see cref="MigrateOnStartup"/> está activo.</summary>
    public string? MigratorConnectionString { get; set; }

    /// <summary>
    /// Migrar al arrancar. SOLO en desarrollo: en producción migra el actualizador, después de un backup obligatorio
    /// (docs/fases/fase-02-propuesta.md §7, regla 7).
    /// </summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>Edición instalada: <c>SINGLE</c> (Caja Única) o <c>MULTI</c> (Multicaja). La escribe el instalador.</summary>
    public string Edition { get; set; } = "SINGLE";
}

/// <summary>
/// Secretos de la instalación protegidos con DPAPI a nivel de máquina (<c>dpapi:BASE64</c>). Solo el propio equipo
/// puede descifrarlos. Los valores sin prefijo se usan tal cual (desarrollo).
/// </summary>
public static class ProtectedSecret
{
    public const string Prefix = "dpapi:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PosSupermercado.Database.v1");

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
