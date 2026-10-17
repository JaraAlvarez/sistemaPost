using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;

namespace Pos.Installer.Setup.Servicios;

/// <summary>Lo que el instalador necesita de Windows: permisos, registro de "Aplicaciones instaladas", abrir programas y rutas.</summary>
internal static class Sistema
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

    /// <summary>Carpeta para el backup final (Documentos del usuario): sobrevive aunque se borren los datos de BusinessPost.</summary>
    public static string CarpetaBackupFinal() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BusinessPost - backup final");

    public static string CarpetaTemporal()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), "BusinessPost-Setup");
        Directory.CreateDirectory(carpeta);
        return carpeta;
    }
}
