using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Installer.Setup.Logica;
using Pos.Licensing.Contracts;

namespace Pos.Installer.Setup.Servicios;

/// <summary>Respuesta para la página: el dato o un mensaje en lenguaje sencillo (y el código estable, para decidir qué mostrar).</summary>
public sealed record Respuesta<T>(T? Dato, string? Mensaje = null, string? Codigo = null)
{
    public bool Ok => Mensaje is null;
}

internal static class Http
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Lee un ProblemDetails (<c>code</c>, <c>detail</c> y los errores de campos) sin fallar si no lo es.</summary>
    public static async Task<(string? Codigo, string? Detalle)> ProblemaAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var texto = await response.Content.ReadAsStringAsync(cancellationToken);
            using var documento = JsonDocument.Parse(texto);
            var raiz = documento.RootElement;
            var codigo = raiz.TryGetProperty("code", out var c) ? c.GetString() : null;
            var detalle = raiz.TryGetProperty("detail", out var d) ? d.GetString() : raiz.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (raiz.TryGetProperty("errors", out var errores) && errores.ValueKind == JsonValueKind.Object)
            {
                var campos = errores.EnumerateObject().SelectMany(e => e.Value.ValueKind == JsonValueKind.Array
                    ? e.Value.EnumerateArray().Select(x => x.GetString()) : []).Where(x => x is not null);
                detalle = string.Join(' ', new[] { detalle }.Concat(campos).Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            return (codigo, detalle);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}

/// <summary>
/// Datos del negocio que configura el instalador (cuerpo del asistente inicial del servidor local). <c>TipoNegocio</c> (Fase 7-C parte A):
/// SUPERMARKET, FRESH_MARKET, PHARMACY, APPAREL, HARDWARE u OTHER.
/// </summary>
public sealed record DatosNegocio(
    string RazonSocial,
    string NombreComercial,
    string TipoPersona,
    string TipoDocumento,
    string Numero,
    string? Dv,
    string Regimen,
    string Municipio,
    string Direccion,
    string? Telefono,
    string? Correo,
    string? TipoNegocio = null);

/// <summary>
/// Propietario: con la cuenta del portal (<c>Enlace</c> firmado por la nube; <c>Portal</c> = true, lo que se supone si la página no lo
/// dice) o local (<c>Portal</c> = false: "Crear mi cuenta" en "Tu cuenta", con o sin clave de licencia).
/// </summary>
public sealed record DatosPropietario(string Usuario, string Nombre, string Contrasena, string? Enlace, string? Confirmacion = null, bool Portal = true);

/// <summary>Resultado de esperar al servidor local después de instalar.</summary>
public enum EsperaServidor
{
    /// <summary><c>/health/ready</c> respondió 200: la base de datos está lista.</summary>
    Listo,

    /// <summary>El proceso responde, pero la base de datos no quedó lista a tiempo (migración larga o con error).</summary>
    SinBaseDeDatos,

    /// <summary>El servidor no respondió.</summary>
    SinRespuesta,
}

/// <summary>Servidor de BusinessPost en este equipo (http://localhost:5480): asistente inicial e ingreso (para liberar la licencia al desinstalar).</summary>
public sealed class ServidorLocal : IDisposable
{
    public static readonly Uri Direccion = new("http://localhost:5480/");

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _esperar;

    public ServidorLocal()
        : this(new HttpClientHandler(), Task.Delay)
    {
    }

    /// <summary>Para las pruebas: otro manejador HTTP y otra espera (sin red ni reloj reales).</summary>
    internal ServidorLocal(HttpMessageHandler manejador, Func<TimeSpan, CancellationToken, Task> esperar)
    {
        _http = new HttpClient(manejador) { BaseAddress = Direccion, Timeout = TimeSpan.FromSeconds(60) };
        _esperar = esperar;
    }

    /// <summary>Pausa entre consultas mientras el servidor arranca o migra la base de datos.</summary>
    internal static readonly TimeSpan Pausa = TimeSpan.FromSeconds(3);

    /// <summary>Estado de una consulta de salud.</summary>
    public async Task<HttpStatusCode?> SaludAsync(string ruta, CancellationToken cancellationToken)
    {
        try
        {
            using var respuesta = await _http.GetAsync(new Uri(ruta, UriKind.Relative), cancellationToken);
            return respuesta.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    public async Task<bool> RespondeAsync(CancellationToken cancellationToken) => await SaludAsync("health/live", cancellationToken) == HttpStatusCode.OK;

    /// <summary>
    /// Espera a que el servidor esté LISTO (<c>/health/ready</c>: la base de datos respondió y su esquema es el esperado), no solo vivo.
    /// En una actualización (p. ej. 1.1.x → 1.2.0) el servidor arranca enseguida pero responde 503 mientras aplica las migraciones; antes
    /// el instalador seguía con <c>/health/live</c> y fallaba con "Ocurrió un error inesperado" aunque BusinessPost quedara instalado.
    /// <paramref name="alEsperarBase"/> se llama cuando el proceso ya responde pero la base de datos aún no (para el aviso en la página).
    /// </summary>
    public async Task<EsperaServidor> EsperarListoAsync(TimeSpan maximo, Action? alEsperarBase, Func<DateTimeOffset>? reloj, CancellationToken cancellationToken)
    {
        reloj ??= () => DateTimeOffset.UtcNow;
        var fin = reloj() + maximo;
        var vivo = false;
        var avisado = false;
        while (reloj() < fin)
        {
            var listo = await SaludAsync("health/ready", cancellationToken);
            if (listo == HttpStatusCode.OK)
            {
                return EsperaServidor.Listo;
            }

            vivo = listo is not null || await RespondeAsync(cancellationToken);
            if (vivo && !avisado)
            {
                avisado = true;
                alEsperarBase?.Invoke();
            }

            await _esperar(Pausa, cancellationToken);
        }

        return vivo ? EsperaServidor.SinBaseDeDatos : EsperaServidor.SinRespuesta;
    }

    /// <summary>
    /// ¿El asistente inicial ya se hizo? (reinstalación sobre los datos conservados). Tolera lo pasajero (503 mientras migra, sin
    /// conexión, una respuesta que no es JSON): reintenta hasta <paramref name="intentos"/> veces y devuelve un mensaje claro si no.
    /// </summary>
    public async Task<Respuesta<bool>> ConfiguradoAsync(CancellationToken cancellationToken, int intentos = 20)
    {
        string? ultimo = null;
        for (var intento = 1; intento <= Math.Max(1, intentos); intento++)
        {
            try
            {
                using var respuesta = await _http.GetAsync(new Uri("api/v1/setup/status", UriKind.Relative), cancellationToken);
                if (respuesta.IsSuccessStatusCode)
                {
                    var texto = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                    using var estado = JsonDocument.Parse(texto);
                    return new(estado.RootElement.ValueKind == JsonValueKind.Object
                               && estado.RootElement.TryGetProperty("isCompleted", out var completo)
                               && completo.ValueKind == JsonValueKind.True);
                }

                if (!EsPasajero(respuesta.StatusCode))
                {
                    var (codigo, detalle) = await Http.ProblemaAsync(respuesta, cancellationToken);
                    return new(false, string.IsNullOrWhiteSpace(detalle) ? $"BusinessPost respondió con un error ({(int)respuesta.StatusCode})." : detalle, codigo);
                }

                ultimo = "BusinessPost todavía está preparando la base de datos.";
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                ultimo = "BusinessPost no responde en este equipo todavía.";
            }

            if (intento < intentos)
            {
                await _esperar(Pausa, cancellationToken);
            }
        }

        return new(false, ultimo ?? "BusinessPost no responde en este equipo todavía.");
    }

    /// <summary>Errores que se arreglan esperando (el servidor arranca, migra o se reinicia).</summary>
    internal static bool EsPasajero(HttpStatusCode estado) =>
        estado is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests;

    /// <summary>Asistente inicial en una sola llamada: empresa, sucursal "Principal" (S01), "Caja 1" (C01) y, si viene, el Propietario.</summary>
    public Task<Respuesta<JsonElement>> ConfigurarAsync(DatosNegocio negocio, DatosPropietario? propietario, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(negocio);
        var nit = negocio.TipoDocumento == "NIT";
        var cuerpo = new
        {
            company = new
            {
                legalName = negocio.RazonSocial.Trim(),
                tradeName = string.IsNullOrWhiteSpace(negocio.NombreComercial) ? negocio.RazonSocial.Trim() : negocio.NombreComercial.Trim(),
                personType = negocio.TipoPersona,
                identificationType = negocio.TipoDocumento,
                identificationNumber = negocio.Numero.Trim(),
                checkDigit = nit ? negocio.Dv : null,
                taxRegime = negocio.Regimen,
                fiscalResponsibilities = new[] { "R-99-PN" },
                municipalityCode = negocio.Municipio,
                address = negocio.Direccion.Trim(),
                phone = Vacio(negocio.Telefono),
                email = Vacio(negocio.Correo),
            },
            branch = new { code = "S01", name = "Principal", municipalityCode = negocio.Municipio, address = negocio.Direccion.Trim(), phone = Vacio(negocio.Telefono) },
            // Sin Propietario (instalador 1.2.1): lo crea el primer ingreso con la cuenta del portal.
            owner = propietario is null ? null : new
            {
                username = propietario.Usuario.Trim().ToLowerInvariant(),
                displayName = propietario.Nombre.Trim(),
                password = propietario.Contrasena,
                email = propietario.Usuario.Contains('@', StringComparison.Ordinal) ? propietario.Usuario.Trim().ToLowerInvariant() : null,
                portalLinkToken = propietario.Enlace,
            },
            terminal = new { code = "C01", name = "Caja 1" },
            businessProfile = TiposNegocio.Normalizar(negocio.TipoNegocio),
        };
        return EnviarAsync<JsonElement>(HttpMethod.Post, "api/v1/setup", cuerpo, cancellationToken);
    }

    public async Task<Respuesta<string>> EntrarAsync(string usuario, string contrasena, CancellationToken cancellationToken)
    {
        var respuesta = await EnviarAsync<JsonElement>(HttpMethod.Post, "api/v1/auth/login", new { username = usuario, password = contrasena }, cancellationToken);
        if (!respuesta.Ok)
        {
            return new(null, respuesta.Mensaje, respuesta.Codigo);
        }

        var token = respuesta.Dato.GetProperty("token").GetString()!;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new(token);
    }

    public Task<Respuesta<JsonElement>> LiberarEquipoAsync(string? motivo, CancellationToken cancellationToken) =>
        EnviarAsync<JsonElement>(HttpMethod.Post, "api/v1/license/deactivate", new { reason = motivo }, cancellationToken);

    public void Dispose() => _http.Dispose();

    private static string? Vacio(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private async Task<Respuesta<T>> EnviarAsync<T>(HttpMethod metodo, string ruta, object? cuerpo, CancellationToken cancellationToken)
    {
        try
        {
            using var mensaje = new HttpRequestMessage(metodo, new Uri(ruta, UriKind.Relative));
            if (cuerpo is not null)
            {
                mensaje.Content = JsonContent.Create(cuerpo, options: Http.Json);
            }

            using var respuesta = await _http.SendAsync(mensaje, cancellationToken);
            if (respuesta.IsSuccessStatusCode)
            {
                var texto = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                return new(string.IsNullOrWhiteSpace(texto) ? default : JsonSerializer.Deserialize<T>(texto, Http.Json));
            }

            var (codigo, detalle) = await Http.ProblemaAsync(respuesta, cancellationToken);
            return new(default, string.IsNullOrWhiteSpace(detalle) ? $"BusinessPost respondió con un error ({(int)respuesta.StatusCode})." : detalle, codigo);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(default, "BusinessPost no responde en este equipo todavía.");
        }
    }
}
