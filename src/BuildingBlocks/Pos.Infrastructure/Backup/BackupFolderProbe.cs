using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pos.Infrastructure.Backup;

/// <summary>Resultado de comprobar una carpeta de destino de backups.</summary>
public sealed record BackupFolderProbeResult(bool Ok, string Message, string? FullPath, string? VolumeLabel, long? FreeBytes);

/// <summary>Tipo de destino que se comprueba.</summary>
public enum BackupFolderKind
{
    /// <summary>Disco externo o interno distinto del de la carpeta LOCAL: letra de unidad, nunca UNC.</summary>
    External,

    /// <summary>Carpeta compartida de OTRO equipo de la red local: ruta UNC <c>\\equipo\carpeta</c>.</summary>
    Network,
}

/// <summary>
/// Comprueba, con la cuenta del servicio (LocalSystem: en la red se presenta como la cuenta del equipo), que una carpeta sirve como destino
/// de backups (pantallas faltantes §1):
/// <list type="bullet">
/// <item>Ruta completa, sin rutas de dispositivo (<c>\\?\</c>, <c>\\.\</c>).</item>
/// <item>Disco externo: letra de unidad (Removable o Fixed) en un VOLUMEN distinto del de la carpeta LOCAL. Si no se puede identificar el
/// volumen, se rechaza.</item>
/// <item>Red: UNC hacia OTRO equipo de la red local: el equipo debe ser una IP privada (RFC 1918 o IPv6 ULA) o un nombre que resuelva solo
/// a IP privadas; se rechazan loopback, 0.0.0.0, las IP de este equipo, su nombre y su FQDN.</item>
/// <item>Ruta REAL: se resuelve con <c>GetFinalPathNameByHandle</c> sobre la carpeta (o su superior) y sobre la LOCAL; así las junctions,
/// los puntos de montaje y las letras de <c>subst</c> no esconden la LOCAL ni su disco.</item>
/// <item>Debe existir la carpeta o su superior; se escribe, lee y borra un archivo temporal. No se crean carpetas.</item>
/// </list>
/// </summary>
public static class BackupFolderProbe
{
    private const string LocalMessage =
        "Esa es la carpeta LOCAL de los backups (o está dentro de ella): elija otra unidad o carpeta (una copia en el mismo lugar no protege).";

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

            if (NetworkHostProblem(HostOf(trimmed), Resolve) is { } problem)
            {
                return Fail(problem);
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

        // La carpeta o, si no existe, su superior: ahí se resuelve la ruta real y se prueba la escritura. No se crea nada.
        var exists = Directory.Exists(full);
        var target = exists ? full : Path.GetDirectoryName(full);
        if (target is null || !Directory.Exists(target))
        {
            return Fail("No existe la carpeta ni su carpeta superior. Créela primero en el disco o en el equipo de la red.", full, label, free);
        }

        // Ruta REAL (junctions, puntos de montaje, subst, recursos del propio equipo) comparada con la de la LOCAL.
        if (OperatingSystem.IsWindows() && Directory.Exists(local))
        {
            var realLocal = FinalPath(local, guidVolume: false);
            var realTarget = FinalPath(target, guidVolume: false);
            if (realLocal is not null && realTarget is not null)
            {
                var realCandidate = exists ? realTarget : Path.Combine(realTarget, Path.GetFileName(full));
                if (IsSameOrInside(Path.TrimEndingDirectorySeparator(realCandidate), Path.TrimEndingDirectorySeparator(realLocal)))
                {
                    return Fail(LocalMessage, full, label, free);
                }
            }

            if (kind == BackupFolderKind.External && !allowSameVolume)
            {
                var targetVolume = VolumeOf(FinalPath(target, guidVolume: true));
                var localVolume = VolumeOf(FinalPath(local, guidVolume: true));
                if (targetVolume is null || localVolume is null)
                {
                    return Fail("No se pudo identificar el disco de la carpeta (o el de la carpeta de los backups del servidor): use otro disco o carpeta.",
                        full, label, free);
                }

                if (string.Equals(targetVolume, localVolume, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail("Esa carpeta está en el mismo disco que la carpeta de los backups del servidor: si el disco falla se pierden ambas. "
                        + "Use otro disco.", full, label, free);
                }
            }
        }

        var probe = Path.Combine(target, $".prueba-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(probe, "prueba", cancellationToken);
            try
            {
                if (await File.ReadAllTextAsync(probe, cancellationToken) != "prueba")
                {
                    return Fail("Se escribió un archivo de prueba pero no se pudo leer igual.", full, label, free);
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

    /// <summary>Equipo de una ruta UNC (<c>\\equipo\carpeta</c> → <c>equipo</c>).</summary>
    public static string HostOf(string uncPath)
    {
        ArgumentNullException.ThrowIfNull(uncPath);
        return uncPath.TrimStart('\\', '/').Split('\\', '/')[0].Trim();
    }

    /// <summary>
    /// Por qué el equipo de una ruta UNC no sirve como destino, o <c>null</c> si sirve: debe ser OTRO equipo de la red local (IP privada
    /// RFC 1918 o IPv6 ULA, o un nombre que resuelva solo a IP privadas). Se rechazan loopback, 0.0.0.0, las IP de este equipo, su nombre
    /// y su FQDN, y las IP públicas.
    /// </summary>
    public static string? NetworkHostProblem(string host, Func<string, IReadOnlyList<IPAddress>> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        var bare = (host ?? string.Empty).Trim().Trim('[', ']');
        if (bare.Length == 0)
        {
            return "Falta el equipo en la ruta de red (\\\\equipo\\carpeta).";
        }

        var own = $"«{host}» es este mismo equipo: una copia aquí no protege si el equipo se daña. Use una carpeta de OTRO equipo o un NAS.";
        if (string.Equals(bare, "localhost", StringComparison.OrdinalIgnoreCase) || IsOwnName(bare))
        {
            return own;
        }

        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(Literal(bare), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = resolve(bare);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                return $"No se encuentra el equipo «{host}» en la red: revise el nombre o use su dirección IP.";
            }

            if (addresses.Count == 0)
            {
                return $"No se encuentra el equipo «{host}» en la red: revise el nombre o use su dirección IP.";
            }
        }

        var ownAddresses = OwnAddresses();
        foreach (var address in addresses)
        {
            var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ownAddresses.Contains(ip))
            {
                return own;
            }

            if (!IsPrivate(ip))
            {
                return $"«{host}» no es un equipo de la red local ({ip}): los backups solo se copian a equipos con IP privada (10.x, 172.16–31.x, 192.168.x).";
            }
        }

        return null;
    }

    /// <summary>IP privada: RFC 1918 (IPv4) o IPv6 ULA (fc00::/7).</summary>
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }

    private static IReadOnlyList<IPAddress> Resolve(string host) => Dns.GetHostAddresses(host);

    private static bool IsOwnName(string host)
    {
        var machine = Environment.MachineName;
        if (string.Equals(host, machine, StringComparison.OrdinalIgnoreCase) || host.StartsWith(machine + ".", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var fqdn = string.IsNullOrEmpty(properties.DomainName) ? properties.HostName : $"{properties.HostName}.{properties.DomainName}";
            return string.Equals(host, fqdn, StringComparison.OrdinalIgnoreCase) || string.Equals(host, properties.HostName, StringComparison.OrdinalIgnoreCase);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static HashSet<IPAddress> OwnAddresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address.IsIPv4MappedToIPv6 ? a.Address.MapToIPv4() : a.Address)];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>Dirección de un host UNC: <c>0--1.ipv6-literal.net</c> es la forma UNC de <c>::1</c>.</summary>
    private static string Literal(string host)
    {
        const string suffix = ".ipv6-literal.net";
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? host[..^suffix.Length].Replace('-', ':') : host;
    }

    private static bool IsSameOrInside(string candidate, string local) =>
        string.Equals(candidate, local, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Volumen de una ruta final con GUID (<c>\\?\Volume{…}\carpeta</c> → <c>\\?\Volume{…}</c>).</summary>
    private static string? VolumeOf(string? finalPath)
    {
        const string prefix = @"\\?\Volume{";
        if (finalPath is null || !finalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var end = finalPath.IndexOf('}', prefix.Length);
        return end < 0 ? null : finalPath[..(end + 1)];
    }

    /// <summary>
    /// Ruta real de una carpeta existente (<c>GetFinalPathNameByHandle</c>): resuelve junctions, puntos de montaje y subst. Con
    /// <paramref name="guidVolume"/> el volumen va como <c>\\?\Volume{GUID}</c>; si no, como letra o <c>\\?\UNC\</c>. Sin el prefijo
    /// <c>\\?\</c> cuando es una ruta normal. <c>null</c> si no se puede.
    /// </summary>
    private static string? FinalPath(string directory, bool guidVolume)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var handle = NativeMethods.CreateFile(
            directory, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, NativeMethods.FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[1024];
        var length = NativeMethods.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, guidVolume ? NativeMethods.VolumeNameGuid : 0u);
        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        var result = new string(buffer, 0, (int)length);
        if (guidVolume)
        {
            return result;
        }

        return result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + result[8..]
            : result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..]
            : result;
    }

    private static BackupFolderProbeResult Fail(string message, string? full = null, string? label = null, long? free = null) => new(false, message, full, label, free);

    /// <summary>P/Invoke clásico (sin código inseguro): abrir la carpeta y leer su ruta final.</summary>
    private static class NativeMethods
    {
        public const uint FileFlagBackupSemantics = 0x02000000;
        public const uint VolumeNameGuid = 0x1;

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] filePath, uint filePathLength, uint flags);
    }
}
