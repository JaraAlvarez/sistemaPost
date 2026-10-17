namespace Pos.Desktop;

/// <summary>Qué hacer con una dirección a la que la página quiere ir.</summary>
internal enum DecisionNavegacion
{
    /// <summary>Es BusinessPost (o la pantalla de carga): se abre en la ventana.</summary>
    Permitir,

    /// <summary>Otro sitio web (pagar la mensualidad, tutiendanueva.com…): se abre en el navegador predeterminado.</summary>
    AbrirEnNavegador,

    /// <summary>Esquemas locales o desconocidos (file:, edge:, javascript:…): no se abre.</summary>
    Bloquear,
}

/// <summary>
/// La ventana solo muestra BusinessPost (D13B-07): misma dirección de origen (esquema, equipo y puerto) que la página inicial, la
/// pantalla de carga local y los orígenes extra que se configuren. Funciones puras, con pruebas.
/// </summary>
internal static class ReglasNavegacion
{
    /// <summary>Origen ficticio de la pantalla de carga: la sirve el propio programa (nunca sale a la red).</summary>
    public static readonly Uri OrigenCarga = new("https://carga.businesspost.invalid/");

    public static DecisionNavegacion Decidir(Uri app, IReadOnlyList<Uri> extra, string? destino)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(extra);

        if (string.IsNullOrWhiteSpace(destino))
            return DecisionNavegacion.Permitir; // window.open() sin dirección: ventana en blanco del mismo origen (imprimir).
        if (!Uri.TryCreate(destino, UriKind.Absolute, out var uri))
            return DecisionNavegacion.Bloquear;

        switch (uri.Scheme)
        {
            case "about":
                return uri.AbsoluteUri is "about:blank" or "about:srcdoc" ? DecisionNavegacion.Permitir : DecisionNavegacion.Bloquear;
            case "blob":
                // blob:http://localhost:5480/<id> (archivos generados por la página): solo los del mismo origen.
                return Uri.TryCreate(uri.AbsolutePath, UriKind.Absolute, out var interno) && EsPermitido(app, extra, interno)
                    ? DecisionNavegacion.Permitir
                    : DecisionNavegacion.Bloquear;
            case "http" or "https":
                if (EsPermitido(app, extra, uri))
                    return DecisionNavegacion.Permitir;
                return string.IsNullOrEmpty(uri.UserInfo) ? DecisionNavegacion.AbrirEnNavegador : DecisionNavegacion.Bloquear;
            case "mailto" or "tel":
                return DecisionNavegacion.AbrirEnNavegador;
            default:
                return DecisionNavegacion.Bloquear;
        }
    }

    public static bool MismoOrigen(Uri a, Uri b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port;
    }

    public static bool EsCarga(string? destino) =>
        Uri.TryCreate(destino, UriKind.Absolute, out var uri) && MismoOrigen(uri, OrigenCarga);

    private static bool EsPermitido(Uri app, IReadOnlyList<Uri> extra, Uri uri) =>
        MismoOrigen(app, uri) || MismoOrigen(OrigenCarga, uri) || extra.Any(o => MismoOrigen(o, uri));
}
