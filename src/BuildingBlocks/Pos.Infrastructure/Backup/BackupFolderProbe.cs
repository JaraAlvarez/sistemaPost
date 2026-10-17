namespace Pos.Infrastructure.Backup;

/// <summary>Resultado de comprobar una carpeta de destino de backups.</summary>
public sealed record BackupFolderProbeResult(bool Ok, string Message, string? FullPath, string? VolumeLabel, long? FreeBytes);

/// <summary>
/// Comprueba, con la cuenta del servicio, que una carpeta sirve como destino de backups (disco externo o carpeta de red): ruta completa,
/// unidad conectada (y con la etiqueta pedida), carpeta compartida accesible, que no sea la carpeta LOCAL y que se pueda escribir, leer y
/// borrar un archivo. Si la carpeta no existe se crea para probar y se borra si quedó vacía (la comprobación no deja rastro).
/// </summary>
public static class BackupFolderProbe
{
    public static async Task<BackupFolderProbeResult> CheckAsync(string? path, string? volumeLabel, string localDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Fail("Indique la carpeta (p. ej. E:\\Backups o \\\\NAS\\backups).");
        }

        var trimmed = path.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            return Fail("Escriba la ruta completa de la carpeta (p. ej. E:\\Backups o \\\\NAS\\backups).");
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
        if (string.Equals(full, local, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Esa es la carpeta LOCAL de los backups: elija otra unidad o carpeta (una copia en el mismo lugar no protege).");
        }

        var root = Path.GetPathRoot(full);
        string? label = null;
        long? free = null;
        if (IsUnc(full))
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return Fail($"No se encuentra la carpeta compartida {root}. Revise la red y que la cuenta del servicio de BusinessPost tenga permiso.");
            }
        }
        else
        {
            var drive = string.IsNullOrEmpty(root) ? null : new DriveInfo(root);
            if (drive is not { IsReady: true })
            {
                return Fail($"La unidad {root} no está conectada o no está lista.");
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

        var created = !Directory.Exists(full);
        try
        {
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, $".prueba-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(probe, "prueba", cancellationToken);
            var ok = await File.ReadAllTextAsync(probe, cancellationToken) == "prueba";
            File.Delete(probe);
            if (!ok)
            {
                return Fail("Se escribió un archivo de prueba pero no se pudo leer igual.", full, label, free);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"No se puede escribir en la carpeta: {ex.Message}", full, label, free);
        }
        finally
        {
            if (created)
            {
                TryRemoveEmpty(full);
            }
        }

        var message = created ? "La carpeta se puede usar (no existía: se creará en el primer backup)." : "La carpeta existe y se puede escribir.";
        if (free is { } bytes)
        {
            message += $" Espacio libre: {(bytes / 1_073_741_824d).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("es-CO"))} GB.";
        }

        return new BackupFolderProbeResult(true, message, full, label, free);
    }

    public static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    private static void TryRemoveEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Una carpeta vacía que no se pudo borrar no afecta el resultado.
        }
    }

    private static BackupFolderProbeResult Fail(string message, string? full = null, string? label = null, long? free = null) => new(false, message, full, label, free);
}
