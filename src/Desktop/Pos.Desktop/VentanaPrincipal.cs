using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Pos.Desktop;

/// <summary>
/// Ventana de BusinessPost: muestra la pantalla de carga de la marca mientras el programa de fondo responde en <c>/health/live</c> y
/// luego la interfaz de siempre. Nunca muestra las páginas de error de Edge: si la conexión falla vuelve a la pantalla de carga.
/// </summary>
internal sealed class VentanaPrincipal : Form
{
    private static readonly Color FondoMarca = Color.FromArgb(0x11, 0x15, 0x7D);

    private readonly DestinoInicio _destino;
    private readonly PreferenciasVentana _preferencias;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = FondoMarca };
    private readonly System.Windows.Forms.Timer _reloj = new() { Interval = (int)EstadoCarga.Intervalo.TotalMilliseconds };
    private readonly HttpClient _http;
    private readonly Stopwatch _espera = new();

    private CoreWebView2Environment? _entorno;
    private bool _enCarga;
    private bool _comprobando;
    private int _fallosSeguidos;
    private FaseCarga _fase;
    private Rectangle _limitesNormales;
    private bool _pantallaCompleta;
    private FormWindowState _estadoAntesDePantallaCompleta = FormWindowState.Maximized;

    public VentanaPrincipal(DestinoInicio destino)
    {
        _destino = destino ?? throw new ArgumentNullException(nameof(destino));
        _preferencias = PreferenciasVentana.Cargar();
        _http = CrearCliente(destino.HuellaCertificado);

        Text = "BusinessPost";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = FondoMarca;
        MinimumSize = new Size(PreferenciasVentana.AnchoMinimo, PreferenciasVentana.AltoMinimo);
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        KeyDown += Tecla;
        _web.KeyDown += Tecla; // Teclas con el foco en la página (WebView2 las reenvía como aceleradores).

        var pantallas = Screen.AllScreens.Select(s => s.WorkingArea).ToList();
        var principal = Screen.PrimaryScreen?.WorkingArea ?? pantallas[0];
        _limitesNormales = PreferenciasVentana.Ajustar(
            new Rectangle(_preferencias.X, _preferencias.Y, _preferencias.Ancho, _preferencias.Alto), pantallas, principal);
        Bounds = _limitesNormales;
        if (_preferencias.Maximizada)
            WindowState = FormWindowState.Maximized;

        Controls.Add(_web);
        _reloj.Tick += Comprobar;
        Load += async (_, _) => await IniciarAsync();
        ResizeEnd += (_, _) => RecordarLimites();
        Move += (_, _) => RecordarLimites();
        FormClosing += (_, _) => GuardarPreferencias();
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // Arranque del motor (WebView2)
    // ---------------------------------------------------------------------------------------------------------------------------------

    private async Task IniciarAsync()
    {
        if (_preferencias.PantallaCompleta)
            PonerPantallaCompleta(true);

        try
        {
            // Datos del navegador por usuario (la carpeta del programa en Archivos de programa es de solo lectura).
            var datos = Path.Combine(PreferenciasVentana.Carpeta, "WebView2");
            _entorno = await CoreWebView2Environment.CreateAsync(null, datos, new CoreWebView2EnvironmentOptions { Language = "es-CO" });
            await _web.EnsureCoreWebView2Async(_entorno);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            Registro.Escribir($"No se pudo iniciar WebView2: {ex}");
            var respuesta = MessageBox.Show(this,
                "No se pudo abrir la ventana de BusinessPost porque falta o está dañado un componente de Windows (Microsoft Edge WebView2 " +
                "Runtime)." + Environment.NewLine + Environment.NewLine + "¿Desea abrir la página oficial de descarga para instalarlo?",
                "BusinessPost", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (respuesta == DialogResult.Yes)
                Program.AbrirEnNavegador(new Uri(Program.DescargaWebView2));
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        Configurar(core, this);
        core.AddWebResourceRequestedFilter(ReglasNavegacion.OrigenCarga.AbsoluteUri + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += ServirPantallaDeCarga;
        core.WebMessageReceived += MensajeDeLaPantallaDeCarga;
        core.NavigationCompleted += NavegacionTerminada;
        core.ProcessFailed += ProcesoFallido;
        MostrarCarga();
    }

    /// <summary>Reglas comunes a la ventana principal y a las secundarias (imprimir).</summary>
    private void Configurar(CoreWebView2 core, Form ventana)
    {
        var ajustes = core.Settings;
        ajustes.IsZoomControlEnabled = false;          // Ctrl + rueda y Ctrl + +/- no cambian el tamaño.
        ajustes.IsPinchZoomEnabled = false;            // Pantallas táctiles de caja.
        ajustes.IsSwipeNavigationEnabled = false;
        ajustes.IsStatusBarEnabled = false;
        ajustes.IsBuiltInErrorPageEnabled = false;     // Nunca la página de error de Edge.
        ajustes.IsPasswordAutosaveEnabled = false;     // Equipos de caja compartidos.
        ajustes.IsGeneralAutofillEnabled = false;
#if !DEBUG
        ajustes.AreDevToolsEnabled = false;
        core.ContextMenuRequested += MenuSoloEdicion;
#endif

        core.NavigationStarting += (_, e) =>
        {
            switch (ReglasNavegacion.Decidir(_destino.Direccion, _destino.OrigenesExtra, e.Uri))
            {
                case DecisionNavegacion.AbrirEnNavegador:
                    e.Cancel = true;
                    Program.AbrirEnNavegador(new Uri(e.Uri));
                    break;
                case DecisionNavegacion.Bloquear:
                    e.Cancel = true;
                    Registro.Escribir($"Navegación bloqueada: {e.Uri}");
                    break;
            }
        };
        core.NewWindowRequested += (_, e) => NuevaVentana(e, ventana);
        core.DownloadStarting += (_, e) => Descarga(e, ventana);
        core.ServerCertificateErrorDetected += CertificadoPropio;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // Pantalla de carga y espera de /health/live
    // ---------------------------------------------------------------------------------------------------------------------------------

    private void MostrarCarga()
    {
        _enCarga = true;
        _fase = FaseCarga.Iniciando;
        _espera.Restart();
        _web.DefaultBackgroundColor = FondoMarca;
        _web.CoreWebView2.Navigate(ReglasNavegacion.OrigenCarga.AbsoluteUri);
        _reloj.Start();
    }

    private async void Comprobar(object? sender, EventArgs e)
    {
        if (!_enCarga || _comprobando)
            return;
        _comprobando = true;
        try
        {
            var fase = _fallosSeguidos >= 3 ? FaseCarga.Error : EstadoCarga.Fase(_espera.Elapsed);
            if (fase != _fase)
            {
                _fase = fase;
                EnviarEstado();
            }

            // Tras 3 fallos seguidos al abrir la interfaz (responde /health/live pero la página no carga) espera a "Reintentar".
            if (_fallosSeguidos < 3 && await RespondeAsync())
            {
                _enCarga = false;
                _reloj.Stop();
                _web.DefaultBackgroundColor = Color.White; // Fondo normal de las páginas de la interfaz.
                _web.CoreWebView2.Navigate(_destino.Direccion.AbsoluteUri);
            }
        }
        finally
        {
            _comprobando = false;
        }
    }

    private async Task<bool> RespondeAsync()
    {
        try
        {
            using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var respuesta = await _http.GetAsync(_destino.Salud, limite.Token);
            return respuesta.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    private void EnviarEstado()
    {
        if (_web.CoreWebView2 is not { } core || !ReglasNavegacion.EsCarga(core.Source))
            return;
        var estado = new
        {
            fase = EstadoCarga.Nombre(_fase),
            direccion = _destino.Direccion.GetLeftPart(UriPartial.Authority),
            cajaAdicional = !_destino.Direccion.IsLoopback,
        };
        core.PostWebMessageAsJson(JsonSerializer.Serialize(estado));
    }

    private void MensajeDeLaPantallaDeCarga(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!ReglasNavegacion.EsCarga(e.Source))
            return;
        switch (e.TryGetWebMessageAsString())
        {
            case "lista":
                EnviarEstado();
                break;
            case "reintentar":
                _fallosSeguidos = 0;
                _fase = FaseCarga.Iniciando;
                _espera.Restart();
                _enCarga = true;
                _reloj.Start();
                EnviarEstado();
                break;
        }
    }

    private void ServirPantallaDeCarga(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !ReglasNavegacion.MismoOrigen(uri, ReglasNavegacion.OrigenCarga))
            return;
        (string? recurso, string tipo) = uri.AbsolutePath switch
        {
            "/" or "/index.html" => ("carga.html", "text/html; charset=utf-8"),
            "/inter-latin.woff2" => ("inter-latin.woff2", "font/woff2"),
            "/inter-latin-ext.woff2" => ("inter-latin-ext.woff2", "font/woff2"),
            "/OFL.txt" => ("OFL.txt", "text/plain; charset=utf-8"),
            _ => (null, "text/plain"),
        };
        // WebView2 lee el flujo por su cuenta y lo libera al terminar.
        var flujo = recurso is null ? null : typeof(VentanaPrincipal).Assembly.GetManifestResourceStream(recurso);
        e.Response = _entorno!.CreateWebResourceResponse(flujo, flujo is null ? 404 : 200, flujo is null ? "Not Found" : "OK",
            $"Content-Type: {tipo}\r\nCache-Control: no-store");
    }

    private void NavegacionTerminada(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var fuente = _web.CoreWebView2.Source;
        if (ReglasNavegacion.EsCarga(fuente))
            return;
        if (e.IsSuccess)
        {
            _fallosSeguidos = 0;
            _web.Focus();
            return;
        }

        // Cancelada por la propia ventana (enlace externo, descarga) o por otra navegación: no es un fallo de conexión.
        if (e.WebErrorStatus is CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired)
            return;
        _fallosSeguidos++;
        Registro.Escribir($"No cargó {fuente}: {e.WebErrorStatus} (fallo {_fallosSeguidos}).");
        MostrarCarga();
    }

    private void ProcesoFallido(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Registro.Escribir($"Falló un proceso de WebView2: {e.ProcessFailedKind} ({e.Reason}).");
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            MessageBox.Show(this, "La ventana de BusinessPost se cerró por un error de Windows. Vuelva a abrir BusinessPost desde el escritorio.",
                "BusinessPost", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        else if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            MostrarCarga();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // Ventanas nuevas, descargas, certificado de la caja y menú contextual
    // ---------------------------------------------------------------------------------------------------------------------------------

    private async void NuevaVentana(CoreWebView2NewWindowRequestedEventArgs e, Form duena)
    {
        switch (ReglasNavegacion.Decidir(_destino.Direccion, _destino.OrigenesExtra, e.Uri))
        {
            case DecisionNavegacion.AbrirEnNavegador:
                e.Handled = true;
                Program.AbrirEnNavegador(new Uri(e.Uri));
                return;
            case DecisionNavegacion.Bloquear:
                e.Handled = true;
                return;
        }

        // Mismo origen o ventana en blanco (p. ej. una página para imprimir): ventana propia con las mismas reglas.
        var aplazo = e.GetDeferral();
        var tamano = e.WindowFeatures.HasSize
            ? new Size((int)Math.Max(480, e.WindowFeatures.Width), (int)Math.Max(360, e.WindowFeatures.Height))
            : new Size(900, 700);
#pragma warning disable CA2000 // La ventana se libera sola al cerrarse (Form.Show).
        var secundaria = new VentanaSecundaria(Icon, tamano);
#pragma warning restore CA2000
        secundaria.Show(duena);
        try
        {
            e.NewWindow = await secundaria.PrepararAsync(_entorno!, Configurar);
            e.Handled = true;
        }
        finally
        {
            aplazo.Complete();
        }
    }

    /// <summary>Reportes y exportaciones: el cuadro estándar "Guardar como" de Windows y la barra de descargas de Edge.</summary>
    private static void Descarga(CoreWebView2DownloadStartingEventArgs e, Form duena)
    {
        var aplazo = e.GetDeferral();
        duena.BeginInvoke(() =>
        {
            try
            {
                var extension = Path.GetExtension(e.ResultFilePath);
                using var dialogo = new SaveFileDialog
                {
                    Title = "Guardar archivo",
                    FileName = Path.GetFileName(e.ResultFilePath),
                    InitialDirectory = Path.GetDirectoryName(e.ResultFilePath),
                    Filter = string.IsNullOrEmpty(extension) ? "Todos los archivos|*.*" : $"Archivo {extension}|*{extension}|Todos los archivos|*.*",
                    OverwritePrompt = true,
                };
                if (dialogo.ShowDialog(duena) == DialogResult.OK)
                    e.ResultFilePath = dialogo.FileName;
                else
                    e.Cancel = true;
            }
            finally
            {
                aplazo.Complete();
            }
        });
    }

    /// <summary>
    /// Caja adicional: el equipo principal usa un certificado propio. Se acepta solo si su huella SHA-256 es la que se fijó al instalar
    /// (la misma regla del actualizador, ADR-0018); cualquier otro certificado inválido se rechaza y se ve la pantalla de carga.
    /// </summary>
    private void CertificadoPropio(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        if (_destino.HuellaCertificado is not { } huella
            || !Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var uri)
            || !ReglasNavegacion.MismoOrigen(uri, _destino.Direccion))
        {
            Registro.Escribir($"Certificado rechazado para {e.RequestUri}: {e.ErrorStatus}.");
            return;
        }

        using var certificado = e.ServerCertificate.ToX509Certificate2();
        if (string.Equals(certificado.GetCertHashString(HashAlgorithmName.SHA256), huella, StringComparison.OrdinalIgnoreCase))
            e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        else
            Registro.Escribir($"La huella del certificado de {uri.Authority} no coincide con la del equipo principal.");
    }

#if !DEBUG
    /// <summary>Sin el menú del navegador (Atrás, Inspeccionar, Guardar como…): solo Cortar, Copiar, Pegar, Deshacer y Seleccionar todo.</summary>
    private void MenuSoloEdicion(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        string[] permitidos = ["cut", "copy", "paste", "pasteAndMatchStyle", "undo", "redo", "selectAll"];
        var conservar = e.MenuItems.Where(i => i.Kind != CoreWebView2ContextMenuItemKind.Separator && permitidos.Contains(i.Name)).ToList();
        if (conservar.Count == 0)
        {
            e.Handled = true;
            return;
        }

        e.MenuItems.Clear();
        foreach (var item in conservar)
            e.MenuItems.Add(item);
    }
#endif

    // ---------------------------------------------------------------------------------------------------------------------------------
    // Pantalla completa (F11) y preferencias
    // ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// F11: pantalla completa. Con KeyPreview la ventana recibe también las teclas que WebView2 reenvía como "aceleradores" (F1–F12,
    /// combinaciones con Ctrl o Alt) cuando el foco está en la página.
    /// </summary>
    private void Tecla(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.F11 || e.Modifiers != Keys.None)
            return;
        e.Handled = true;
        e.SuppressKeyPress = true;
        PonerPantallaCompleta(!_pantallaCompleta);
    }

    private void PonerPantallaCompleta(bool activar)
    {
        if (activar == _pantallaCompleta)
            return;
        if (activar)
        {
            _estadoAntesDePantallaCompleta = WindowState == FormWindowState.Minimized ? FormWindowState.Maximized : WindowState;
            var pantalla = Screen.FromControl(this).Bounds;
            _pantallaCompleta = true;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = pantalla;
        }
        else
        {
            _pantallaCompleta = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            if (_estadoAntesDePantallaCompleta == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                WindowState = FormWindowState.Normal;
                Bounds = _limitesNormales;
            }
        }

        _preferencias.PantallaCompleta = _pantallaCompleta;
        _preferencias.Guardar();
    }

    private void RecordarLimites()
    {
        if (!_pantallaCompleta && WindowState == FormWindowState.Normal)
            _limitesNormales = Bounds;
    }

    private void GuardarPreferencias()
    {
        _preferencias.X = _limitesNormales.X;
        _preferencias.Y = _limitesNormales.Y;
        _preferencias.Ancho = _limitesNormales.Width;
        _preferencias.Alto = _limitesNormales.Height;
        _preferencias.Maximizada = _pantallaCompleta
            ? _estadoAntesDePantallaCompleta == FormWindowState.Maximized
            : WindowState == FormWindowState.Maximized;
        _preferencias.PantallaCompleta = _pantallaCompleta;
        _preferencias.Guardar();
    }

    private static HttpClient CrearCliente(string? huella)
    {
        var manejador = new HttpClientHandler();
        if (huella is not null)
        {
            // Mismo criterio que el actualizador (ADR-0018): se confía solo en el certificado con la huella fijada al instalar.
#pragma warning disable CA5359 // La validación es por huella fijada, no se desactiva.
            manejador.ServerCertificateCustomValidationCallback = (_, certificado, _, errores) =>
                errores == System.Net.Security.SslPolicyErrors.None
                || (certificado is not null && string.Equals(certificado.GetCertHashString(HashAlgorithmName.SHA256), huella, StringComparison.OrdinalIgnoreCase));
#pragma warning restore CA5359
        }

        return new HttpClient(manejador) { Timeout = TimeSpan.FromSeconds(5) };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reloj.Dispose();
            _http.Dispose();
            _web.Dispose();
        }

        base.Dispose(disposing);
    }
}
