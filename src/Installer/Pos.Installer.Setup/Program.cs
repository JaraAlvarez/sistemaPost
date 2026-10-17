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
        if (desinstalar && !maqueta && !args.Contains("--copia", StringComparer.OrdinalIgnoreCase)
            && Sistema.CarpetaInstalada() is { } carpeta
            && Environment.ProcessPath!.StartsWith(carpeta, StringComparison.OrdinalIgnoreCase))
        {
            var copia = Path.Combine(Sistema.CarpetaTemporal(), $"BusinessPost-Desinstalar-{Guid.NewGuid():N}.exe");
            File.Copy(Environment.ProcessPath, copia, overwrite: true);
            Process.Start(new ProcessStartInfo(copia) { UseShellExecute = false, ArgumentList = { "--desinstalar", "--copia" } });
            return 0;
        }

        ApplicationConfiguration.Initialize();
        // ‑‑maqueta ‑‑pantalla <nombre>: abre directo en una pantalla (revisión del diseño).
        var pantalla = Array.FindIndex(args, a => string.Equals(a, "--pantalla", StringComparison.OrdinalIgnoreCase)) is var p and >= 0 && p + 1 < args.Length ? args[p + 1] : null;
        Application.Run(new VentanaInstalador(new Opciones(desinstalar, maqueta, paquete, pantalla, ArgumentosCaptura.Leer(args))));
        return SinWebView2 ? SalidaSinWebView2 : 0;
    }
}
