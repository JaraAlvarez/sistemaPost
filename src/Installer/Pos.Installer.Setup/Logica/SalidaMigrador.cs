using System.Text.RegularExpressions;

namespace Pos.Installer.Setup.Logica;

/// <summary>Lo que el instalador lee de la salida del migrador (<c>Pos.Server.Migrator backup …</c>).</summary>
public static partial class SalidaMigrador
{
    /// <summary>
    /// Ruta del backup que creó el migrador, tomada de su línea <c>Backup KIND verificado: RUTA (… MB, …)</c>
    /// (BackupCommands.BackupAsync). <c>null</c> si no aparece.
    /// </summary>
    public static string? RutaBackup(string? salida)
    {
        if (string.IsNullOrEmpty(salida))
        {
            return null;
        }

        string? ruta = null;
        foreach (var linea in salida.Split('\n'))
        {
            var coincide = LineaBackup().Match(linea.TrimEnd('\r'));
            if (coincide.Success)
            {
                ruta = coincide.Groups["ruta"].Value.Trim();
            }
        }

        return ruta;
    }

    /// <summary>Los <c>.posbak</c> que ya hay en la carpeta (antes de hacer el backup).</summary>
    public static IReadOnlySet<string> Backups(string carpeta) =>
        Directory.Exists(carpeta)
            ? new HashSet<string>(Directory.EnumerateFiles(carpeta, "*.posbak").Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>El único <c>.posbak</c> que apareció en la carpeta y no estaba en <paramref name="antes"/>; <c>null</c> si no hay uno solo.</summary>
    public static string? BackupNuevo(string carpeta, IReadOnlySet<string> antes)
    {
        ArgumentNullException.ThrowIfNull(antes);
        var nuevos = Backups(carpeta).Where(f => !antes.Contains(f)).ToList();
        return nuevos.Count == 1 ? nuevos[0] : null;
    }

    [GeneratedRegex(@"^Backup \S+ verificado: (?<ruta>.+?\.posbak) \(", RegexOptions.CultureInvariant)]
    private static partial Regex LineaBackup();
}
