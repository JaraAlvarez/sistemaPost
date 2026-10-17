using System.Globalization;
using System.Text.Json;

namespace Pos.Desktop;

/// <summary>A dónde se conecta la ventana y cómo se confía en ese equipo.</summary>
/// <param name="Direccion">Página inicial (p. ej. <c>http://localhost:5480/</c> o <c>https://192.168.1.10:5443/</c>).</param>
/// <param name="HuellaCertificado">Huella SHA-256 del certificado propio del equipo principal (caja adicional); <c>null</c> si no aplica.</param>
/// <param name="OrigenesExtra">Otros sitios que pueden abrirse dentro de la ventana (por defecto ninguno).</param>
/// <param name="Fuente">De dónde salió la dirección (para el registro de soporte).</param>
internal sealed record DestinoInicio(Uri Direccion, string? HuellaCertificado, IReadOnlyList<Uri> OrigenesExtra, string Fuente)
{
    /// <summary>Comprobación de que el programa responde (la misma que usa el instalador).</summary>
    public Uri Salud => new(Direccion, "/health/live");
}

/// <summary>
/// Resuelve la página inicial sin interfaz (funciones puras, con pruebas). Orden:
/// 1. <c>--url &lt;dirección&gt;</c> (y opcional <c>--huella &lt;sha256&gt;</c>) en la línea de comandos, para pruebas y soporte.
/// 2. <c>config\desktop.json</c> (<c>StartUrl</c>, <c>ServerCertificateThumbprint</c>, <c>AllowedOrigins</c>), opcional.
/// 3. Caja adicional: <c>config\updater.json</c> que escribe el instalador (<c>Pos:Updates:Mode = Terminal</c>, <c>ServerUrl</c> y
///    <c>ServerCertificateThumbprint</c>): la dirección https del equipo principal.
/// 4. Equipo principal o única caja: <c>http://localhost:&lt;Pos:Server:Port&gt;/</c> de <c>config\server.json</c> (5480 por defecto).
/// </summary>
internal static class ConfiguracionInicio
{
    public const int PuertoPorDefecto = 5480;
    public static readonly Uri PorDefecto = new($"http://localhost:{PuertoPorDefecto}/");

    public const string ArchivoEscritorio = "desktop.json";
    public const string ArchivoActualizador = "updater.json";
    public const string ArchivoServidor = "server.json";

    private static readonly JsonDocumentOptions Tolerante = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <param name="args">Argumentos de la línea de comandos.</param>
    /// <param name="leerConfig">Devuelve el texto de un archivo de <c>C:\ProgramData\BusinessPost\config</c> o <c>null</c> si no existe.</param>
    public static DestinoInicio Resolver(IReadOnlyList<string> args, Func<string, string?> leerConfig)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(leerConfig);

        var escritorio = Leer(leerConfig(ArchivoEscritorio));
        var extra = escritorio is { } e ? Origenes(Buscar(e, "AllowedOrigins")) : [];

        if (Argumento(args, "--url") is { } textoUrl && DireccionValida(textoUrl) is { } url)
            return new DestinoInicio(url, Huella(Argumento(args, "--huella")), extra, "línea de comandos");

        if (escritorio is { } config && DireccionValida(Texto(Buscar(config, "StartUrl"))) is { } urlEscritorio)
            return new DestinoInicio(urlEscritorio, Huella(Texto(Buscar(config, "ServerCertificateThumbprint"))), extra, ArchivoEscritorio);

        if (Leer(leerConfig(ArchivoActualizador)) is { } actualizador
            && string.Equals(Texto(Buscar(actualizador, "Pos", "Updates", "Mode")), "Terminal", StringComparison.OrdinalIgnoreCase)
            && DireccionValida(Texto(Buscar(actualizador, "Pos", "Updates", "ServerUrl"))) is { } urlPrincipal)
        {
            return new DestinoInicio(urlPrincipal, Huella(Texto(Buscar(actualizador, "Pos", "Updates", "ServerCertificateThumbprint"))), extra,
                $"{ArchivoActualizador} (caja adicional)");
        }

        if (Leer(leerConfig(ArchivoServidor)) is { } servidor
            && int.TryParse(Texto(Buscar(servidor, "Pos", "Server", "Port")), NumberStyles.None, CultureInfo.InvariantCulture, out var puerto)
            && puerto is > 0 and <= 65535)
        {
            return new DestinoInicio(new Uri($"http://localhost:{puerto}/"), null, extra, ArchivoServidor);
        }

        return new DestinoInicio(PorDefecto, null, extra, "valor por defecto");
    }

    /// <summary>Solo direcciones absolutas http o https, sin usuario ni contraseña.</summary>
    public static Uri? DireccionValida(string? texto) =>
        !string.IsNullOrWhiteSpace(texto)
        && Uri.TryCreate(texto.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(uri.UserInfo)
            ? uri
            : null;

    /// <summary>Huella SHA-256 en hexadecimal sin separadores (acepta "AB:CD:…" como la muestra la administración).</summary>
    public static string? Huella(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
            return null;
        var limpia = texto.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        return limpia.Length == 64 && limpia.All(Uri.IsHexDigit) ? limpia.ToUpperInvariant() : null;
    }

    /// <summary>Valor de <c>--nombre valor</c> o <c>--nombre=valor</c>.</summary>
    public static string? Argumento(IReadOnlyList<string> args, string nombre)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], nombre, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Count ? args[i + 1] : null;
            if (args[i].StartsWith(nombre + "=", StringComparison.OrdinalIgnoreCase))
                return args[i][(nombre.Length + 1)..];
        }

        return null;
    }

    private static JsonElement? Leer(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var documento = JsonDocument.Parse(json, Tolerante);
            return documento.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Busca una ruta de propiedades sin distinguir mayúsculas (como la configuración de .NET).</summary>
    private static JsonElement? Buscar(JsonElement raiz, params string[] ruta)
    {
        var actual = raiz;
        foreach (var nombre in ruta)
        {
            if (actual.ValueKind != JsonValueKind.Object)
                return null;
            JsonElement? siguiente = null;
            foreach (var propiedad in actual.EnumerateObject())
            {
                if (string.Equals(propiedad.Name, nombre, StringComparison.OrdinalIgnoreCase))
                {
                    siguiente = propiedad.Value;
                    break;
                }
            }

            if (siguiente is not { } valor)
                return null;
            actual = valor;
        }

        return actual;
    }

    private static string? Texto(JsonElement? valor) => valor switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        _ => null,
    };

    private static List<Uri> Origenes(JsonElement? lista) =>
        lista is { ValueKind: JsonValueKind.Array } arreglo
            ? [.. arreglo.EnumerateArray().Select(v => DireccionValida(Texto(v))).OfType<Uri>()]
            : [];
}
