using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Pos.Server.Updater;

/// <summary>
/// R5: el actualizador corre como SYSTEM y ejecuta lo que hay en la carpeta de la instalación y en la de datos. Si un usuario normal (un
/// cajero) pudiera escribir ahí, o decidir por configuración dónde está la instalación, podría hacer que SYSTEM ejecute su programa. Aquí:
/// <list type="bullet">
/// <item>la carpeta de la instalación sale de la ubicación del propio actualizador (<c>{app}\updater</c>) y de la clave de Inno, nunca de
/// la configuración, y se comprueba que un usuario sin privilegios no pueda escribir en ella;</item>
/// <item>en la caja adicional la carpeta de datos queda solo para SYSTEM y Administradores (como en el equipo principal), con lectura para
/// los usuarios solo de los archivos que necesita la ventana BusinessPost.exe;</item>
/// <item>no se siguen uniones NTFS ni vínculos simbólicos al copiar, borrar o ejecutar.</item>
/// </list>
/// </summary>
public static class UpdaterSecurity
{
    /// <summary>Clave de desinstalación de Inno (AppId de installer/BusinessPost.iss).</summary>
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ED01E874-EA07-4102-813B-7DAD1DE455D9}_is1";

    /// <summary>Archivos de <c>config</c> que la ventana BusinessPost.exe (usuario normal) puede LEER en una caja adicional.</summary>
    public static readonly string[] UserReadableConfig = ["updater.json", "desktop.json"];

    /// <summary>
    /// Carpeta de la instalación: la carpeta padre de <c>updater\</c>, donde está este programa. Si la clave de Inno indica otra carpeta, no
    /// se confía en ninguna (devuelve <c>null</c>): alguien copió el actualizador a otro lugar.
    /// </summary>
    public static string? ResolveInstallRoot(string baseDirectory, string? innoInstallLocation)
    {
        var fromBinary = Path.GetFullPath(Path.Combine(baseDirectory, ".."));
        if (string.IsNullOrWhiteSpace(innoInstallLocation))
        {
            return fromBinary;
        }

        var fromRegistry = Path.GetFullPath(innoInstallLocation.Trim());
        return string.Equals(fromBinary.TrimEnd(Path.DirectorySeparatorChar), fromRegistry.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            ? fromBinary
            : null;
    }

    [SupportedOSPlatform("windows")]
    public static string? InnoInstallLocation()
    {
        using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") as string;
    }

    /// <summary>¿Es una unión NTFS, un vínculo simbólico u otro punto de reanálisis? No se sigue.</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Problema de permisos de una carpeta en la que el actualizador confía (instalación): <c>null</c> si un usuario sin privilegios no puede
    /// escribir, crear, borrar ni cambiar permisos; si no, el texto del problema.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? WritableByUnprivileged(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return $"No existe la carpeta {directory}.";
        }

        if (IsReparsePoint(directory))
        {
            return $"{directory} es un enlace (unión NTFS o vínculo): no se confía en él.";
        }

        return WritableByUnprivileged(new DirectoryInfo(directory).GetAccessControl(), directory);
    }

    [SupportedOSPlatform("windows")]
    internal static string? WritableByUnprivileged(DirectorySecurity security, string what)
    {
        ArgumentNullException.ThrowIfNull(security);
        const FileSystemRights Dangerous = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var unprivileged = new[]
        {
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
        };
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & Dangerous) != 0
                && rule.IdentityReference is SecurityIdentifier sid && unprivileged.Contains(sid))
            {
                return $"Un usuario sin privilegios ({sid.Translate(typeof(NTAccount))}) puede escribir en {what}: no se instalan actualizaciones hasta corregir los permisos.";
            }
        }

        return null;
    }

    /// <summary>
    /// R5 en la caja adicional: la carpeta de datos (configuración, descargas, estado del actualizador) solo para SYSTEM y Administradores,
    /// igual que el migrador la deja en el equipo principal (<c>RestrictDataRootAsync</c>), y lectura para los usuarios de
    /// <see cref="UserReadableConfig"/>. Devuelve los avisos (vacío si todo salió bien).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> RestrictTerminalDataRoot(string dataRoot)
    {
        var avisos = new List<string>();
        Directory.CreateDirectory(dataRoot);
        if (IsReparsePoint(dataRoot))
        {
            avisos.Add($"{dataRoot} es un enlace: no se cambian sus permisos.");
            return avisos;
        }

        Icacls(avisos, dataRoot, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "/Q");
        foreach (var name in UserReadableConfig)
        {
            var file = Path.Combine(dataRoot, "config", name);
            if (File.Exists(file) && !IsReparsePoint(file))
            {
                Icacls(avisos, file, "/grant", "*S-1-5-32-545:R", "/Q");
            }
        }

        return avisos;
    }

    private static void Icacls(List<string> avisos, string path, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "icacls.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(path);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            avisos.Add($"No se pudo ejecutar icacls para {path}.");
            return;
        }

        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            avisos.Add($"icacls {path}: código {process.ExitCode} {error.Trim()}");
        }
    }
}
