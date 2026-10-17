using System.Net;
using System.Runtime.InteropServices;

namespace Pos.Infrastructure.Backup;

/// <summary>Resultado de comprobar una carpeta de destino de backups.</summary>
public sealed record BackupFolderProbeResult(bool Ok, string Message, string? FullPath, string? VolumeLabel, long? FreeBytes);

/// <summary>Tipo de destino que se comprueba.</summary>
public enum BackupFolderKind
{
    /// <summary>Disco externo o interno distinto del de la carpeta LOCAL: letra de unidad, nunca UNC.</summary>
    External,

    /// <summary>Carpeta compartida de OTRO equipo: ruta UNC <c>\\equipo\carpeta</c>.</summary>
    Network,
}

/// <summary>
/// Comprueba, con la cuenta del servicio (LocalSystem: en la red se presenta como la cuenta del equipo), que una carpeta sirve como destino
/// de backups (pantallas faltantes §1, PA3/PA4/PA7):
/// <list type="bullet">
/// <item>Ruta completa. Disco externo: letra de unidad (Removable o Fixed), en un VOLUMEN distinto del de la carpeta LOCAL. Red: UNC hacia
/// otro equipo (se rechazan <c>\\?\</c>, <c>\\.\</c>, localhost, 127.x, ::1 y el nombre de este equipo).</item>
/// <item>Que exista la carpeta o, si no, su carpeta superior (la de destino se crea en el primer backup). NO se crean carpetas.</item>
/// <item>Que se pueda escribir: un archivo temporal que se borra. Ese mismo archivo sirve para descubrir alias (junction, subst, recurso
/// compartido del propio equipo): si aparece dentro de la carpeta LOCAL, la carpeta ES la LOCAL o está dentro de ella.</item>
/// </list>
/// </summary>
public static partial class BackupFolderProbe
{
    public static async Task<BackupFolderProbeResult> CheckAsync(
        string? path, string? volumeLabel, string localDirectory, BackupFolderKind kind, bool allowSameVolume, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Fail("Indique la carpeta (p. ej. E:\\Backups o \\\\NAS\\backups).");
        }

        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal) || trimmed.StartsWith(@"\\.\", StringComparison.Ordinal)
            || trimmed.StartsWith("//?/", StringComparison.Ordinal) || trimmed.StartsWith("//./", StringComparison.Ordinal))
        {
            return Fail("No se aceptan rutas de dispositivo (\\\\?\\ o \\\\.\\): escriba la ruta normal de la carpeta.");
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            return Fail("Escriba la ruta completa de la carpeta (p. ej. E:\\Backups o \\\\NAS\\backups).");
        }

        var unc = IsUnc(trimmed);
        if (kind == BackupFolderKind.External && unc)
        {
            return Fail("Un disco externo se indica con su letra (p. ej. E:\\Backups). Para una carpeta compartida elija «Carpeta de red».");
        }

        if (kind == BackupFolderKind.Network)
        {
            if (!unc)
            {
                return Fail("La carpeta de red se escribe como \\\\equipo\\carpeta: el servicio no ve las unidades mapeadas (Z:).");
            }

            if (OwnMachineHost(trimmed) is { } host)
            {
                return Fail($"«{host}» es este mismo equipo: una copia aquí no protege si el equipo se daña. Use una carpeta de OTRO equipo o un NAS.");
            }
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail($"La ruta no es válida: {ex.Message}");
        }

        var local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localDirectory));
        if (IsSameOrInside(full, local))
        {
            return Fail(LocalMessage);
        }

        var root = Path.GetPathRoot(full);
        string? label = null;
        long? free = null;
        if (unc)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return Fail($"No se encuentra la carpeta compartida {root}. Revise la red y que la cuenta de este equipo tenga permiso de escritura en ella.");
            }
        }
        else
        {
            var drive = string.IsNullOrEmpty(root) ? null : new DriveInfo(root);
            if (drive is not { IsReady: true })
            {
                return Fail($"La unidad {root} no está conectada o no está lista.");
            }

            if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed))
            {
                return Fail($"La unidad {root} es de tipo {drive.DriveType}: use un disco externo o interno con letra, o «Carpeta de red» con ruta UNC.");
            }

            label = drive.VolumeLabel;
            free = drive.AvailableFreeSpace;
            if (!string.IsNullOrWhiteSpace(volumeLabel) && !string.Equals(drive.VolumeLabel, volumeLabel.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"El disco {root} no es '{volumeLabel.Trim()}' (es '{drive.VolumeLabel}').", full, label, free);
            }
        }

        if (File.Exists(full))
        {
            return Fail("La ruta es un archivo, no una carpeta.", full, label, free);
        }

        // La carpeta o, si no existe, su superior: ahí se prueba la escritura. No se crea nada.
        var exists = Directory.Exists(full);
        var target = exists ? full : Path.GetDirectoryName(full);
        if (target is null || !Directory.Exists(target))
        {
            return Fail("No existe la carpeta ni su carpeta superior. Créela primero en el disco o en el equipo de la red.", full, label, free);
        }

        // Mismo volumen que la LOCAL (incluye letras de subst y recursos compartidos del propio equipo): no protege si el disco falla.
        if (!allowSameVolume && VolumeSerial(root!) is { } serial && VolumeSerial(Path.GetPathRoot(local)!) is { } localSerial && serial == localSerial)
        {
            return Fail("Esa carpeta está en el mismo disco que la carpeta de los backups del servidor: si el disco falla se pierden ambas. Use otro disco.",
                full, label, free);
        }

        var probeName = $".prueba-{Guid.NewGuid():N}.tmp";
        var probe = Path.Combine(target, probeName);
        try
        {
            await File.WriteAllTextAsync(probe, "prueba", cancellationToken);
            try
            {
                if (await File.ReadAllTextAsync(probe, cancellationToken) != "prueba")
                {
                    return Fail("Se escribió un archivo de prueba pero no se pudo leer igual.", full, label, free);
                }

                // Alias de la carpeta LOCAL (junction, subst, \\este-equipo\recurso): el archivo de prueba aparece dentro de ella.
                if (Directory.Exists(local) && Directory.EnumerateFiles(local, probeName, SearchOption.AllDirectories).Any())
                {
                    return Fail(LocalMessage, full, label, free);
                }
            }
            finally
            {
                File.Delete(probe);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"No se puede escribir en la carpeta: {ex.Message}", full, label, free);
        }

        var message = exists ? "La carpeta existe y se puede escribir." : "La carpeta no existe todavía: se creará en el primer backup (su carpeta superior se puede escribir).";
        if (free is { } bytes)
        {
            message += $" Espacio libre: {(bytes / 1_073_741_824d).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("es-CO"))} GB.";
        }

        return new BackupFolderProbeResult(true, message, full, label, free);
    }

    public static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    /// <summary>El equipo de una ruta UNC si es este mismo equipo (localhost, 127.x, ::1 o su nombre), o <c>null</c>.</summary>
    public static string? OwnMachineHost(string uncPath)
    {
        ArgumentNullException.ThrowIfNull(uncPath);
        var host = uncPath.TrimStart('\\', '/').Split('\\', '/')[0].Trim();
        if (host.Length == 0)
        {
            return null;
        }

        var bare = host.Trim('[', ']');
        if (string.Equals(bare, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(Literal(bare), out var ip) && IPAddress.IsLoopback(ip)))
        {
            return host;
        }

        var machine = Environment.MachineName;
        return string.Equals(bare, machine, StringComparison.OrdinalIgnoreCase) || bare.StartsWith(machine + ".", StringComparison.OrdinalIgnoreCase)
            ? host
            : null;
    }

    /// <summary>Dirección de un host UNC: <c>0--1.ipv6-literal.net</c> es la forma UNC de <c>::1</c>.</summary>
    private static string Literal(string host)
    {
        const string suffix = ".ipv6-literal.net";
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? host[..^suffix.Length].Replace('-', ':') : host;
    }

    private const string LocalMessage = "Esa es la carpeta LOCAL de los backups (o está dentro de ella): elija otra unidad o carpeta (una copia en el mismo lugar no protege).";

    private static bool IsSameOrInside(string candidate, string local) =>
        string.Equals(candidate, local, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Número de serie del volumen (Windows; resuelve letras de subst y recursos compartidos). <c>null</c> si no se puede saber.</summary>
    private static uint? VolumeSerial(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var normalized = root.EndsWith('\\') ? root : root + "\\";
        return GetVolumeInformation(normalized, IntPtr.Zero, 0, out var serial, out _, out _, IntPtr.Zero, 0) ? serial : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformation(
        string rootPathName, IntPtr volumeNameBuffer, uint volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength,
        out uint fileSystemFlags, IntPtr fileSystemNameBuffer, uint fileSystemNameSize);

    private static BackupFolderProbeResult Fail(string message, string? full = null, string? label = null, long? free = null) => new(false, message, full, label, free);
}
