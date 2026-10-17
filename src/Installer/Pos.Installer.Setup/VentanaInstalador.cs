using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Pos.Installer.Setup;

/// <summary>
/// Ventana del instalador: 960×600 sin el marco de Windows (la barra con arrastrar, minimizar y cerrar la dibuja la página), esquinas
/// redondeadas en Windows 11 y WebView2 con las páginas incrustadas en el programa (nunca se carga nada de internet).
/// </summary>
internal sealed partial class VentanaInstalador : Form
{
    private const string Origen = "https://instalador.businesspost/";

    private readonly Opciones _opciones;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(17, 21, 125) };
    private readonly Puente _puente;
    private bool _puedeCerrar;

    public VentanaInstalador(Opciones opciones)
    {
        _opciones = opciones;
        Text = opciones.Desinstalar ? "Desinstalar BusinessPost" : "Instalar BusinessPost";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(17, 21, 125);
        AutoScaleMode = AutoScaleMode.None;
        Controls.Add(_web);
        _puente = new Puente(opciones, Enviar, ElegirBackup, AccionVentana);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var escala = DeviceDpi / 96f;
        ClientSize = new Size((int)(960 * escala), (int)(600 * escala));
        CenterToScreen();
        var redondeo = 2; // DWMWCP_ROUND (Windows 11; en Windows 10 se ignora).
        _ = DwmSetWindowAttribute(Handle, 33, ref redondeo, sizeof(int));
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            var datos = Path.Combine(Path.GetTempPath(), "BusinessPost-Setup", "WebView2");
            var entorno = await CoreWebView2Environment.CreateAsync(null, datos);
            await _web.EnsureCoreWebView2Async(entorno);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            // Sin WebView2 (Windows muy desactualizado): el paquete descargable abre el asistente clásico.
            Program.SinWebView2 = true;
            _puedeCerrar = true;
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = _opciones.Maqueta;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = _opciones.Maqueta;
        core.AddWebResourceRequestedFilter(Origen + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += Servir;
        core.WebMessageReceived += async (_, args) => await _puente.RecibirAsync(args.WebMessageAsJson is { } json ? Desenvolver(json) : "{}");
        core.NavigationStarting += (_, args) =>
        {
            // Solo las páginas del instalador; los enlaces externos se abren aparte, con el usuario normal.
            if (!args.Uri.StartsWith(Origen, StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        if (_opciones.Maqueta && _opciones.Captura is { } captura)
        {
            core.NavigationCompleted += async (_, _) => await CapturarAsync(captura);
        }

        core.Navigate(Origen + "instalador.html");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Cerrar se confirma en la página (y no se permite mientras instala).
        if (!_puedeCerrar && e.CloseReason == CloseReason.UserClosing && _web.CoreWebView2 is not null)
        {
            e.Cancel = true;
            Enviar("{\"evento\":\"pedirCierre\"}");
            return;
        }

        _puente.Cancelar();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _puente.Dispose();
        }

        base.Dispose(disposing);
    }

    private void AccionVentana(string accion) => BeginInvoke(() =>
    {
        switch (accion)
        {
            case "minimizar":
                WindowState = FormWindowState.Minimized;
                break;
            case "arrastrar":
                _ = ReleaseCapture();
                _ = SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
                break;
            case "cerrar" when _puente.Trabajando:
                Enviar("{\"evento\":\"pedirCierre\"}");
                break;
            default:
                _puedeCerrar = true;
                Close();
                break;
        }
    });

    /// <summary>Solo con ‑‑maqueta: guarda la pantalla como PNG (capturas del manual de instalación) y cierra la ventana.</summary>
    private async Task CapturarAsync(Logica.ArgumentosCaptura captura)
    {
        try
        {
            await Task.Delay(captura.EsperaMs);
            Directory.CreateDirectory(Path.GetDirectoryName(captura.Archivo)!);
            await using var archivo = File.Create(captura.Archivo);
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, archivo);
        }
        finally
        {
            _puedeCerrar = true;
            Close();
        }
    }

    private string? ElegirBackup()
    {
        string? ruta = null;
        Invoke(() =>
        {
            using var dialogo = new OpenFileDialog
            {
                Title = "Elige el backup de BusinessPost",
                Filter = "Backups de BusinessPost (*.posbak)|*.posbak",
                CheckFileExists = true,
            };
            if (dialogo.ShowDialog(this) == DialogResult.OK)
            {
                ruta = dialogo.FileName;
            }
        });
        return ruta;
    }

    private void Enviar(string json)
    {
        if (IsDisposed)
        {
            return;
        }

        BeginInvoke(() => _web.CoreWebView2?.PostWebMessageAsJson(json));
    }

    /// <summary>La página envía el mensaje como texto JSON (postMessage de una cadena): llega como cadena JSON dentro de JSON.</summary>
    private static string Desenvolver(string json) =>
        json.StartsWith('"') ? System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "{}" : json;

    private void Servir(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        var nombre = Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) ? uri.AbsolutePath.TrimStart('/') : string.Empty;
        var recurso = typeof(VentanaInstalador).Assembly.GetManifestResourceStream(nombre);
        if (recurso is null)
        {
            args.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "No existe", "Content-Type: text/plain");
            return;
        }

        var tipo = Path.GetExtension(nombre) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".png" => "image/png",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
        args.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(recurso, 200, "OK",
            $"Content-Type: {tipo}\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; font-src 'self'");
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
