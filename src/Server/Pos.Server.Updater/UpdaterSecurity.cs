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
        // B1: también los derechos genéricos (GENERIC_WRITE 0x40000000, GENERIC_ALL 0x10000000), que aparecen en ACE heredables.
        const FileSystemRights Dangerous = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | (FileSystemRights)0x40000000 | (FileSystemRights)0x10000000;
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
    /// ALTA 1: ¿el dueño del archivo es SYSTEM o el grupo Administradores? Un archivo de control (registro en curso, secuencia, configuración)
    /// creado por un usuario normal antes de restringir la carpeta no se usa.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool IsOwnedByTrusted(string path)
    {
        try
        {
            if (IsReparsePoint(path))
            {
                return false;
            }

            var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// ALTA 1: borra los archivos de control (registro en curso, secuencias, versión fallida y, en una caja adicional, un
    /// <c>config\server.json</c>) cuyo dueño no es SYSTEM ni Administradores. Devuelve los avisos.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> PurgeUntrustedControlFiles(string dataRoot)
    {
        var avisos = new List<string>();
        var updates = Path.Combine(dataRoot, "updates");
        var candidates = new List<string>
        {
            Path.Combine(updates, "update-in-progress.json"), Path.Combine(updates, "manifest-sequence.json"), Path.Combine(updates, "failed-version.json"),
            // Ronda 3 (3): también los pedidos "instalar ahora" (un usuario no puede forzar la instalación).
            Path.Combine(updates, "install-now.request"), Path.Combine(updates, "terminals-install-now.request"),
        };
        var updaterJson = Path.Combine(dataRoot, "config", "updater.json");
        if (File.Exists(updaterJson) && IsOwnedByTrusted(updaterJson) && File.ReadAllText(updaterJson).Contains("\"Terminal\"", StringComparison.OrdinalIgnoreCase))
        {
            // En la caja adicional server.json no lo escribe nadie de confianza (es del equipo principal): si apareció, lo dejó otro.
            candidates.Add(Path.Combine(dataRoot, "config", "server.json"));
            // desktop.json decide a qué dirección abre la ventana: solo si lo dejó alguien de confianza.
            candidates.Add(Path.Combine(dataRoot, "config", "desktop.json"));
        }

        foreach (var file in candidates.Where(File.Exists))
        {
            if (!IsOwnedByTrusted(file))
            {
                try
                {
                    File.Delete(file);
                    avisos.Add($"Se borró {file}: no era de SYSTEM ni de los Administradores.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    avisos.Add($"No se pudo borrar {file} ({ex.Message}).");
                }
            }
        }

        return avisos;
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

        // ALTA 1: primero se quita la propiedad a los usuarios (el dueño de un archivo puede volver a darse permisos) y se borran los
        // permisos propios de cada archivo; recién entonces la carpeta deja de heredar y queda solo para SYSTEM y Administradores.
        Icacls(avisos, dataRoot, "/setowner", "*S-1-5-32-544", "/T", "/C", "/Q");
        Icacls(avisos, dataRoot, "/reset", "/T", "/C", "/Q");
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
