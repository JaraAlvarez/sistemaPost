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

    public static string Datos => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BusinessPost");

    /// <summary>Modo con el que ya está instalado este equipo (para proponerlo y pedir confirmación si se cambia); <c>null</c> si es nuevo.</summary>
    public static Logica.ModoEquipo? ModoInstalado()
    {
        var config = Path.Combine(Datos, "config");
        string? Leer(string archivo)
        {
            try
            {
                var ruta = Path.Combine(config, archivo);
                return File.Exists(ruta) ? File.ReadAllText(ruta) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        if (CarpetaInstalada() is null)
        {
            return null;
        }

        // IN10: igual que el paquete (DetectPreviousMode): primero el modo que guardó Inno; después, la configuración.
        string? guardado;
        using (var clave = Registry.LocalMachine.OpenSubKey(ClaveDesinstalacion))
        {
            guardado = clave?.GetValue(Logica.ModoInstalado.ValorRegistro) as string;
        }

        return Logica.ModoInstalado.Detectar(guardado, Leer("updater.json"), Leer("server.json"));
    }

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
        int sesion;
        Process[] exploradores;
        try
        {
            sesion = Process.GetCurrentProcess().SessionId;
            exploradores = Process.GetProcessesByName("explorer");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        foreach (var explorer in exploradores)
        {
            using (explorer)
            {
                // PN4: un explorador al que no se tiene acceso (otra sesión, cerrándose) no corta la búsqueda: se sigue con el siguiente.
                try
                {
                    if (explorer.SessionId == sesion && DocumentosDe(explorer.Id) is { } documentos)
                    {
                        return documentos;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                {
                    // Sin acceso a este explorador: se prueba el siguiente.
                }
            }
        }

        // Sin acceso a ningún explorador: se usan los Documentos de este proceso y el dueño elige la carpeta.
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>
    /// Documentos del dueño del proceso <paramref name="id"/>. PN4: el proceso se abre solo con <c>PROCESS_QUERY_LIMITED_INFORMATION</c>
    /// (lo mínimo para leer su token), no con <c>Process.Handle</c>, que pide acceso total.
    /// </summary>
    private static string? DocumentosDe(int id)
    {
        var proceso = OpenProcess(ProcessQueryLimitedInformation, false, (uint)id);
        if (proceso == 0)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(proceso, TokenQuery | TokenImpersonate | TokenDuplicate, out var token))
            {
                return null;
            }

            try
            {
                if (SHGetKnownFolderPath(DocumentosId, 0, token, out var ruta) != 0)
                {
                    return null;
                }

                try
                {
                    var texto = Marshal.PtrToStringUni(ruta);
                    return !string.IsNullOrWhiteSpace(texto) && Directory.Exists(texto) ? texto : null;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ruta);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(proceso);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

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

    /// <summary>
    /// PN1: archivos con el código de recuperación que pudieron quedar en la carpeta temporal (el paquete los borra al leerlos; si se
    /// canceló antes, quedaban). Se llama al salir, cuando ya no hay instalación en curso.
    /// </summary>
    /// <param name="antiguedadMinima">Solo los archivos más viejos que esto (al empezar: los que dejó una ejecución anterior).</param>
    public static void BorrarArchivosDeCodigo(TimeSpan? antiguedadMinima = null)
    {
        try
        {
            foreach (var archivo in Directory.EnumerateFiles(CarpetaTemporal(), $"{Logica.Rutas.PrefijoArchivoCodigo}*.txt"))
            {
                if (antiguedadMinima is { } minima && DateTime.UtcNow - File.GetLastWriteTimeUtc(archivo) < minima)
                {
                    continue;
                }

                try
                {
                    File.Delete(archivo);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Todavía en uso: se intenta la próxima vez.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin carpeta temporal accesible.
        }
    }

    /// <summary>
    /// IR5: se cerró la ventana ANTES de la base de datos y hubo que cortar el paquete: app\current sigue en la versión anterior, así que se
    /// recupera el actualizador anterior (si el paquete ya lo había reemplazado) y vuelven a arrancar los servicios que el paquete había
    /// detenido (los "Deteniendo …" de su registro), en orden. Así el equipo sigue funcionando con la versión de antes.
    /// </summary>
    public static void ReanudarServiciosDetenidos(string? registro, bool restaurarActualizador)
    {
        if (registro is null || !File.Exists(registro))
        {
            return;
        }

        string texto;
        try
        {
            using var lector = new StreamReader(new FileStream(registro, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            texto = lector.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var lineas = new List<string> { "Instalador premium: se cortó el paquete antes de la base de datos; vuelve la versión anterior." };
        if (restaurarActualizador && CarpetaInstalada() is { } carpeta)
        {
            var actualizador = Path.Combine(carpeta, "updater", "Pos.Server.Updater.exe");
            if (File.Exists(actualizador + ".anterior"))
            {
                try
                {
                    File.Copy(actualizador + ".anterior", actualizador, overwrite: true);
                    lineas.Add("Se recuperó el actualizador anterior.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lineas.Add($"Aviso: no se pudo recuperar el actualizador anterior ({ex.Message}).");
                }
            }
        }

        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        foreach (var servicio in Logica.RegistroInno.ServiciosDetenidos(texto.Split('\n')))
        {
            try
            {
                using var proceso = Process.Start(new ProcessStartInfo(sc) { ArgumentList = { "start", servicio }, UseShellExecute = false, CreateNoWindow = true });
                proceso?.WaitForExit(TimeSpan.FromSeconds(15));
                lineas.Add($"sc start {servicio}: {(proceso is { HasExited: true } ? proceso.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "sin respuesta")}");
            }
            catch (Win32Exception ex)
            {
                lineas.Add($"Aviso: no se pudo iniciar {servicio} ({ex.Message}).");
            }
        }

        try
        {
            File.AppendAllLines(registro, lineas);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin registro: los servicios ya se iniciaron.
        }
    }

    public static string CarpetaTemporal()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), "BusinessPost-Setup");
        Directory.CreateDirectory(carpeta);
        return carpeta;
    }
}
