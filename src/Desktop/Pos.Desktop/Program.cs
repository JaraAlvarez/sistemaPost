using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Pos.Shared;

namespace Pos.Desktop;

/// <summary>
/// BusinessPost.exe (Fase 13-B, D13B-07). Uso:
/// <c>BusinessPost.exe</c> · <c>BusinessPost.exe --url http://127.0.0.1:7600/</c> (pruebas) · <c>BusinessPost.exe --precalentar</c>.
/// </summary>
internal static class Program
{
    /// <summary>Página oficial de Microsoft para descargar el componente WebView2 (Evergreen).</summary>
    public const string DescargaWebView2 = "https://developer.microsoft.com/es-es/microsoft-edge/webview2/";

    [STAThread]
    private static int Main(string[] args)
    {
        if (Precalentamiento.Pedido(args))
            return Precalentar();

        // Un solo programa abierto a la vez por usuario: si ya hay uno, se trae al frente y este termina.
        using var instancia = new Mutex(initiallyOwned: true, @"Local\BusinessPost.Ventana", out var primera);
        if (!primera)
        {
            InstanciaUnica.TraerAlFrente();
            return 0;
        }

        ApplicationConfiguration.Initialize();

        if (!HayWebView2())
        {
            var respuesta = MessageBox.Show(
                "Para abrir BusinessPost falta un componente de Windows: Microsoft Edge WebView2 Runtime (gratuito, de Microsoft)." +
                Environment.NewLine + Environment.NewLine +
                "¿Desea abrir la página oficial de descarga? Instálelo (\"Evergreen Bootstrapper\") y vuelva a abrir BusinessPost." +
                Environment.NewLine + Environment.NewLine +
                "Si no tiene internet en este equipo, pida ayuda a soporte.",
                "BusinessPost", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (respuesta == DialogResult.Yes)
                AbrirEnNavegador(new Uri(DescargaWebView2));
            return 2;
        }

        var destino = ConfiguracionInicio.Resolver(args, LeerConfig);
        Registro.Escribir($"Inicio: {destino.Direccion} ({destino.Fuente}).");
        Application.Run(new VentanaPrincipal(destino));
        return 0;
    }

    /// <summary>Carpeta de configuración que escribe el instalador: <c>C:\ProgramData\BusinessPost\config</c>.</summary>
    private static string? LeerConfig(string nombre)
    {
        try
        {
            var archivo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BusinessPost", "config", nombre);
            return File.Exists(archivo) ? File.ReadAllText(archivo) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Registro.Escribir($"No se pudo leer {nombre}: {ex.Message}");
            return null;
        }
    }

    private static bool HayWebView2()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Carga los ensamblados y el cargador nativo de WebView2 y termina (código 0), sin abrir ventanas: el antivirus revisa los archivos
    /// ahora y no cuando el cajero abre el programa.
    /// </summary>
    private static int Precalentar()
    {
        var codigo = Precalentamiento.Ejecutar();
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // Sin el componente igual se precalentó el programa; al abrirlo se explica cómo instalarlo.
        }

        return codigo;
    }

    /// <summary>Abre una dirección en el navegador predeterminado (o el programa de correo para mailto:).</summary>
    public static void AbrirEnNavegador(Uri direccion)
    {
        ArgumentNullException.ThrowIfNull(direccion);
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(direccion.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Registro.Escribir($"No se pudo abrir {direccion}: {ex.Message}");
        }
    }
}
