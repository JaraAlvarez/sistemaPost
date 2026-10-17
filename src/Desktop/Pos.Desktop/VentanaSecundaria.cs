using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Pos.Desktop;

/// <summary>
/// Ventana que la propia interfaz abre con <c>window.open</c> en el mismo origen (por ejemplo, una página para imprimir). Aplica las
/// mismas reglas que la ventana principal y se cierra sola con <c>window.close()</c>.
/// </summary>
internal sealed class VentanaSecundaria : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public VentanaSecundaria(Icon? icono, Size tamano)
    {
        Text = "BusinessPost";
        Icon = icono;
        StartPosition = FormStartPosition.CenterParent;
        Size = tamano;
        MinimumSize = new Size(480, 360);
        Controls.Add(_web);
    }

    public async Task<CoreWebView2> PrepararAsync(CoreWebView2Environment entorno, Action<CoreWebView2, Form> configurar)
    {
        ArgumentNullException.ThrowIfNull(configurar);
        await _web.EnsureCoreWebView2Async(entorno);
        configurar(_web.CoreWebView2, this);
        _web.CoreWebView2.WindowCloseRequested += (_, _) => BeginInvoke(Close);
        return _web.CoreWebView2;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _web.Dispose();
        base.Dispose(disposing);
    }
}
