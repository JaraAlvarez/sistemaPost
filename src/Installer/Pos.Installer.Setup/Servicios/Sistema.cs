using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Pos.Installer.Setup.Servicios;

/// <summary>Lo que el instalador necesita de Windows: permisos, registro de "Aplicaciones instaladas", abrir programas y rutas.</summary>
internal static partial class Sistema
{
    /// <summary>AppId de installer/BusinessPost.iss (no cambia): clave de desinstalación que crea Inno Setup.</summary>
    public const string ClaveDesinstalacion = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ED01E874-EA07-4102-813B-7DAD1DE455D9}_is1";

    /// <summary>Valor propio con el desinstalador de Inno (el premium ocupa <c>UninstallString</c>).</summary>
    public const string ValorDesinstaladorInno = "BusinessPostInnoUninstallString";

    public static string Version =>
        typeof(Sistema).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static Uri? ServidorLicencias =>
        typeof(Sistema).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "PosLicenseServer")?.Value is { Length: > 0 } valor
        && Uri.TryCreate(valor.TrimEnd('/') + "/", UriKind.Absolute, out var url)
            ? url
            : null;

    public static string Datos => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BusinessPost");

    public static bool EsAdministrador()
    {
        using var identidad = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identidad).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Se vuelve a abrir como administrador (UN aviso de Windows). <c>false</c> si el usuario dijo que no.</summary>
    public static bool ReabrirComoAdministrador(IEnumerable<string> argumentos)
    {
        try
        {
            var inicio = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            foreach (var argumento in argumentos)
            {
                inicio.ArgumentList.Add(argumento);
            }

            Process.Start(inicio);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Abre un programa o una página con el usuario normal (no como administrador): Explorer lo abre con la sesión de quien inició el
    /// instalador. Así BusinessPost y el navegador no quedan con permisos de administrador.
    /// </summary>
    public static void AbrirComoUsuario(string rutaOUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                ArgumentList = { rutaOUrl },
                UseShellExecute = false,
            });
        }
        catch (Win32Exception)
        {
            Process.Start(new ProcessStartInfo(rutaOUrl) { UseShellExecute = true });
        }
    }

    /// <summary>Carpeta de la instalación (<c>InstallLocation</c> de Inno) o <c>null</c> si BusinessPost no está instalado.</summary>
    public static string? CarpetaInstalada()
    {
        using var clave = Registry.LocalMachine.OpenSubKey(ClaveDesinstalacion);
        return clave?.GetValue("InstallLocation") is string ruta && Directory.Exists(ruta) ? ruta.TrimEnd('\\') : null;
    }

    public static string? VersionInstalada()
    {
        using var clave = Registry.LocalMachine.OpenSubKey(ClaveDesinstalacion);
        return clave?.GetValue("DisplayVersion") as string;
    }

    public static bool SigueInstalado()
    {
        using var clave = Registry.LocalMachine.OpenSubKey(ClaveDesinstalacion);
        return clave is not null;
    }

    /// <summary>Ruta del desinstalador de Inno (unins000.exe) de la instalación actual.</summary>
    public static string? DesinstaladorInno()
    {
        using var clave = Registry.LocalMachine.OpenSubKey(ClaveDesinstalacion);
        foreach (var nombre in new[] { ValorDesinstaladorInno, "UninstallString", "QuietUninstallString" })
        {
            if (clave?.GetValue(nombre) is string valor && valor.Contains("unins", StringComparison.OrdinalIgnoreCase))
            {
                var ruta = valor.Trim().StartsWith('"') ? valor.Trim().Split('"', StringSplitOptions.RemoveEmptyEntries)[0] : valor.Split(' ')[0];
                if (File.Exists(ruta))
                {
                    return ruta;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Después de instalar: deja una copia de este instalador en <c>{app}\instalador</c> y la registra como el desinstalador de
    /// "Aplicaciones instaladas" (desinstalación premium, §8). El desinstalador de Inno queda guardado aparte y se usa en silencio.
    /// </summary>
    public static void RegistrarDesinstaladorPremium(string carpeta)
    {
        var destino = Path.Combine(carpeta, "instalador", "BusinessPost-Setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(destino), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(Environment.ProcessPath!, destino, overwrite: true);
        }

        using var clave = Registry.LocalMachine.OpenSubKey(ClaveDesinstalacion, writable: true);
        if (clave is null)
        {
            return;
        }

        if (clave.GetValue("UninstallString") is string inno && inno.Contains("unins", StringComparison.OrdinalIgnoreCase))
        {
            clave.SetValue(ValorDesinstaladorInno, inno);
        }

        clave.SetValue("UninstallString", $"\"{destino}\" --desinstalar");
    }

    /// <summary>
    /// Carpeta propuesta para el backup final: "Documentos" del usuario que INICIÓ SESIÓN en Windows (sobrevive aunque se borren los
    /// datos de BusinessPost). El desinstalador corre como administrador (runas), así que <c>MyDocuments</c> sería la carpeta del
    /// administrador si es otro usuario; por eso se pregunta a Windows por los Documentos del dueño de <c>explorer.exe</c> de esta sesión.
    /// </summary>
    public static string CarpetaBackupFinal() => Path.Combine(DocumentosDelUsuario(), "BusinessPost - backup final");

    /// <summary>Documentos del usuario de la sesión (el dueño de explorer.exe); los del proceso si no se puede saber.</summary>
    public static string DocumentosDelUsuario()
    {
        try
        {
            var sesion = Process.GetCurrentProcess().SessionId;
            foreach (var explorer in Process.GetProcessesByName("explorer"))
            {
                using (explorer)
                {
                    if (explorer.SessionId != sesion || !OpenProcessToken(explorer.Handle, TokenQuery | TokenImpersonate | TokenDuplicate, out var token))
                    {
                        continue;
                    }

                    try
                    {
                        if (SHGetKnownFolderPath(DocumentosId, 0, token, out var ruta) == 0)
                        {
                            try
                            {
                                var texto = Marshal.PtrToStringUni(ruta);
                                if (!string.IsNullOrWhiteSpace(texto) && Directory.Exists(texto))
                                {
                                    return texto;
                                }
                            }
                            finally
                            {
                                Marshal.FreeCoTaskMem(ruta);
                            }
                        }
                    }
                    finally
                    {
                        CloseHandle(token);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            // Sin acceso al explorador (p. ej. otra sesión): se usan los Documentos de este proceso y el dueño elige la carpeta.
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>
    /// Borra este ejecutable (la copia temporal del desinstalador) cuando el proceso termine: un <c>cmd</c> oculto espera unos segundos y
    /// lo borra; además se marca para borrarse al reiniciar Windows por si el primero no alcanza (antivirus, archivo aún abierto).
    /// </summary>
    public static void ProgramarBorradoDeEsteArchivo()
    {
        var ruta = Environment.ProcessPath;
        if (string.IsNullOrEmpty(ruta) || !Path.GetFileName(ruta).StartsWith(Logica.Rutas.PrefijoCopiaDesinstalar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = MoveFileEx(ruta, null, MoveFileDelayUntilReboot);
        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Process.Start(new ProcessStartInfo(cmd, $"/d /c ping 127.0.0.1 -n 4 >nul & del /f /q \"{ruta}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        catch (Win32Exception)
        {
            // Queda programado para el reinicio.
        }
    }

    /// <summary>Copias temporales de desinstalaciones anteriores que no se pudieron borrar (mejor esfuerzo).</summary>
    public static void BorrarCopiasViejas()
    {
        try
        {
            var actual = Environment.ProcessPath is { } p ? Path.GetFullPath(p) : string.Empty;
            foreach (var copia in Directory.EnumerateFiles(CarpetaTemporal(), $"{Logica.Rutas.PrefijoCopiaDesinstalar}*.exe"))
            {
                if (!string.Equals(Path.GetFullPath(copia), actual, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Delete(copia);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Otra desinstalación abierta: se deja.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin carpeta temporal accesible.
        }
    }

    private const uint MoveFileDelayUntilReboot = 0x4;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existingFileName, string? newFileName, uint flags);

    private const uint TokenQuery = 0x0008;
    private const uint TokenImpersonate = 0x0004;
    private const uint TokenDuplicate = 0x0002;

    /// <summary>FOLDERID_Documents.</summary>
    private static readonly Guid DocumentosId = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);

    public static string CarpetaTemporal()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), "BusinessPost-Setup");
        Directory.CreateDirectory(carpeta);
        return carpeta;
    }
}
