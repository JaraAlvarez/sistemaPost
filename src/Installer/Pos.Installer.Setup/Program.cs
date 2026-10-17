using System.Diagnostics;
using Pos.Installer.Setup.Logica;
using Pos.Installer.Setup.Servicios;

namespace Pos.Installer.Setup;

/// <summary>
/// BusinessPost-Setup.exe (Fase 13-B, D13B-08 y §8).
///   (sin argumentos)       instalar: usa el paquete clásico BusinessPost-Paquete.exe que está junto al programa (o ‑‑paquete ruta)
///   ‑‑desinstalar          desinstalación premium (lo registra la instalación en "Aplicaciones instaladas")
///   ‑‑maqueta              recorre las pantallas sin instalar ni desinstalar nada (revisión del diseño; no pide permisos)
///   ‑‑maqueta ‑‑pantalla X ‑‑capturar f.png [‑‑esperar ms]   guarda la pantalla X como imagen y se cierra (capturas del manual)
/// Códigos de salida: 0 = normal · 3 = este equipo no tiene WebView2 (el paquete descargable abre entonces el asistente clásico) ·
///                    4 = el usuario no dio permisos de administrador.
/// </summary>
internal static class Program
{
    public const int SalidaSinWebView2 = 3;
    public const int SalidaSinPermisos = 4;

    /// <summary>Lo marca la ventana si WebView2 no está disponible.</summary>
    public static bool SinWebView2 { get; set; }

    [STAThread]
    private static int Main(string[] args)
    {
        var desinstalar = args.Contains("--desinstalar", StringComparer.OrdinalIgnoreCase);
        var maqueta = args.Contains("--maqueta", StringComparer.OrdinalIgnoreCase);
        var indice = Array.FindIndex(args, a => string.Equals(a, "--paquete", StringComparison.OrdinalIgnoreCase));
        var paquete = indice >= 0 && indice + 1 < args.Length
            ? args[indice + 1]
            : Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "BusinessPost-Paquete.exe");

        if (!maqueta && !Sistema.EsAdministrador())
        {
            // Un solo aviso de Windows; abierto desde el paquete descargable ya viene con permisos y no llega aquí.
            return Sistema.ReabrirComoAdministrador(args) ? 0 : SalidaSinPermisos;
        }

        // Desinstalar desde {app}\instalador: el desinstalador de Inno borra esa carpeta, así que se trabaja desde una copia temporal.
        var copiaTemporal = args.Contains("--copia", StringComparer.OrdinalIgnoreCase);
        if (desinstalar && !maqueta && !copiaTemporal
            && Sistema.CarpetaInstalada() is { } carpeta
            && Rutas.EstaDentro(Environment.ProcessPath, carpeta))
        {
            var copia = Path.Combine(Sistema.CarpetaTemporal(), $"{Rutas.PrefijoCopiaDesinstalar}{Guid.NewGuid():N}.exe");
            File.Copy(Environment.ProcessPath!, copia, overwrite: true);
            Process.Start(new ProcessStartInfo(copia) { UseShellExecute = false, ArgumentList = { "--desinstalar", "--copia" } });
            return 0;
        }

        Sistema.BorrarCopiasViejas();
        ApplicationConfiguration.Initialize();
        // ‑‑maqueta ‑‑pantalla <nombre>: abre directo en una pantalla (revisión del diseño).
        var pantalla = Array.FindIndex(args, a => string.Equals(a, "--pantalla", StringComparison.OrdinalIgnoreCase)) is var p and >= 0 && p + 1 < args.Length ? args[p + 1] : null;
        Application.Run(new VentanaInstalador(new Opciones(desinstalar, maqueta, paquete, pantalla, ArgumentosCaptura.Leer(args))));

        // PN1: al cerrar la ventana durante la instalación, la tarea en segundo plano detiene el paquete (y lo que lanzó) y borra el
        // archivo del código de recuperación. Antes el proceso salía sin esperarla. Se espera con límite (Detener espera hasta 30 s).
        EsperarTrabajoPendiente(TimeSpan.FromSeconds(45));
        Sistema.BorrarArchivosDeCodigo();
        if (copiaTemporal)
        {
            // La copia temporal no puede borrarse mientras corre: se borra al salir (y, si algo lo impide, al reiniciar Windows).
            Sistema.ProgramarBorradoDeEsteArchivo();
        }

        return SinWebView2 ? SalidaSinWebView2 : 0;
    }

    private static readonly List<Task> TrabajosPendientes = [];

    /// <summary>Instalación o desinstalación en segundo plano: <see cref="Main"/> la espera (con límite) antes de salir.</summary>
    public static void RegistrarTrabajo(Task tarea)
    {
        lock (TrabajosPendientes)
        {
            TrabajosPendientes.RemoveAll(t => t.IsCompleted);
            TrabajosPendientes.Add(tarea);
        }
    }

    internal static bool EsperarTrabajoPendiente(TimeSpan limite)
    {
        Task[] pendientes;
        lock (TrabajosPendientes)
        {
            pendientes = [.. TrabajosPendientes.Where(t => !t.IsCompleted)];
        }

        try
        {
            return pendientes.Length == 0 || Task.WaitAll(pendientes, limite);
        }
        catch (AggregateException)
        {
            // Las tareas ya informan sus fallas en la página; aquí solo importa que terminaron.
            return true;
        }
    }
}
