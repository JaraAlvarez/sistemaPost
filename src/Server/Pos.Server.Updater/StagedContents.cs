using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Server.Updater;

/// <summary>
/// U1: contenido de una versión preparada (<c>app\{versión}</c>). Al extraer el paquete verificado se guarda, junto a los archivos, la huella
/// SHA-256 de cada uno (<see cref="ManifestFile"/>). "Preparada" significa que el manifiesto existe, que coincide con la huella del paquete y
/// que todos los archivos están y coinciden, sin enlaces (uniones NTFS o vínculos) en ninguna parte del árbol.
/// </summary>
public static class StagedContents
{
    public const string ManifestFile = ".paquete-verificado.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>Extrae <paramref name="zip"/> en <paramref name="directory"/> (nueva) y deja el manifiesto de huellas.</summary>
    public static async Task ExtractAsync(ZipArchive zip, string directory, string packageSha256, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zip);
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"El paquete tiene una ruta fuera de su carpeta ({entry.FullName}).");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            var relative = Normalize(Path.GetRelativePath(root, destination));
            if (string.Equals(relative, ManifestFile, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("El paquete no puede traer su propio manifiesto de huellas.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var source = await entry.OpenAsync(cancellationToken))
            await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                files[relative] = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (entry.LastWriteTime.Year > 1980)
            {
                File.SetLastWriteTimeUtc(destination, entry.LastWriteTime.UtcDateTime);
            }
        }

        await File.WriteAllTextAsync(Path.Combine(root, ManifestFile), JsonSerializer.Serialize(new Contents(packageSha256, files), Json), cancellationToken);
    }

    /// <summary>
    /// <c>null</c> si la carpeta está completa y sin cambios y (M3) salió del paquete con esa huella (<paramref name="packageSha256"/>, si se
    /// indica); si no, el problema.
    /// </summary>
    public static string? Verify(string directory, string? packageSha256 = null)
    {
        if (Read(directory, out var contents) is { } problem)
        {
            return problem;
        }

        if (packageSha256 is not null && !string.Equals(contents!.PackageSha256, packageSha256, StringComparison.OrdinalIgnoreCase))
        {
            return $"{directory} es de otro paquete (otra huella).";
        }

        if (FindLink(directory) is { } link)
        {
            return $"{link} es un enlace.";
        }

        foreach (var (relative, sha) in contents!.Files)
        {
            if (Hash(directory, relative) is not { } actual || !string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
            {
                return $"{relative} falta o cambió.";
            }
        }

        return null;
    }

    /// <summary>Un archivo de la versión preparada: que esté en el manifiesto, sin enlaces en el camino y con su huella.</summary>
    public static string? VerifyFile(string directory, string relativePath)
    {
        if (Read(directory, out var contents) is { } problem)
        {
            return problem;
        }

        var relative = Normalize(relativePath);
        if (!contents!.Files.TryGetValue(relative, out var sha))
        {
            return $"{relative} no salió del paquete verificado.";
        }

        var current = directory;
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if (UpdaterSecurity.IsReparsePoint(current))
            {
                return $"{current} es un enlace.";
            }
        }

        return string.Equals(Hash(directory, relative), sha, StringComparison.OrdinalIgnoreCase) ? null : $"{relative} cambió.";
    }

    /// <summary>Borra una carpeta sin seguir enlaces: un enlace se quita como enlace (no se borra su destino). Devuelve un problema o <c>null</c>.</summary>
    public static string? DeleteWithoutFollowingLinks(string directory)
    {
        if (!Directory.Exists(directory))
        {
            if (File.Exists(directory))
            {
                File.Delete(directory);
            }

            return null;
        }

        if (UpdaterSecurity.IsReparsePoint(directory))
        {
            Directory.Delete(directory);
            return null;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        {
            if (UpdaterSecurity.IsReparsePoint(child))
            {
                if (Directory.Exists(child))
                {
                    Directory.Delete(child);
                }
                else
                {
                    File.Delete(child);
                }
            }
            else if (Directory.Exists(child))
            {
                if (DeleteWithoutFollowingLinks(child) is { } problem)
                {
                    return problem;
                }
            }
            else
            {
                File.SetAttributes(child, FileAttributes.Normal);
                File.Delete(child);
            }
        }

        Directory.Delete(directory);
        return null;
    }

    private static string? Read(string directory, out Contents? contents)
    {
        contents = null;
        var path = Path.Combine(directory, ManifestFile);
        if (!Directory.Exists(directory) || UpdaterSecurity.IsReparsePoint(directory))
        {
            return $"{directory} no existe o es un enlace.";
        }

        if (!File.Exists(path) || UpdaterSecurity.IsReparsePoint(path))
        {
            return $"{directory} no tiene el manifiesto de huellas del paquete verificado.";
        }

        try
        {
            contents = JsonSerializer.Deserialize<Contents>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return $"El manifiesto de huellas de {directory} está dañado ({ex.Message}).";
        }

        return contents is { Files.Count: > 0 } ? null : $"El manifiesto de huellas de {directory} está vacío.";
    }

    private static string? FindLink(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }))
        {
            if (UpdaterSecurity.IsReparsePoint(entry))
            {
                return entry;
            }

            if (Directory.Exists(entry) && FindLink(entry) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static string? Hash(string directory, string relative)
    {
        var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Normalize(string relative) => relative.Replace('\\', '/').TrimStart('/');

    private sealed record Contents(
        [property: JsonPropertyName("packageSha256")] string PackageSha256,
        [property: JsonPropertyName("files")] SortedDictionary<string, string> Files);
}
