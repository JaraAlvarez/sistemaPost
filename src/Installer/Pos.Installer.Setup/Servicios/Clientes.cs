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

/// <summary>Servidor de licencias en la nube: vista previa de la clave y cuenta del portal del dueño (Fase 13-B).</summary>
public sealed class NubeLicencias(Uri? servidor) : IDisposable
{
    /// <summary>Respuesta 200 que no es de BusinessPost (p. ej. la página de una red Wi-Fi que pide iniciar sesión).</summary>
    internal const string RespuestaAjena =
        "La respuesta de internet no es de BusinessPost (¿la red pide iniciar sesión en una página?). Revisa la conexión de este equipo e intenta de nuevo.";

    private readonly HttpClient? _http = servidor is null ? null : new HttpClient { BaseAddress = servidor, Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Para las pruebas: otro manejador HTTP (sin red real).</summary>
    internal NubeLicencias(Uri servidor, HttpMessageHandler manejador)
        : this(servidor)
    {
        _http = new HttpClient(manejador) { BaseAddress = servidor, Timeout = TimeSpan.FromSeconds(20) };
    }

    public bool Disponible => _http is not null;

    public void Dispose() => _http?.Dispose();

    public Task<Respuesta<LicensePreviewResponse>> VistaPreviaAsync(string clave, CancellationToken cancellationToken) =>
        EnviarAsync<LicensePreviewResponse>(LicensingRoutes.LicensePreviews, new LicensePreviewRequest(clave), cancellationToken);

    public Task<Respuesta<OwnerLinkResponse>> CuentaAsync(string clave, string correo, string contrasena, string? codigo, CancellationToken cancellationToken) =>
        EnviarAsync<OwnerLinkResponse>(LicensingRoutes.OwnerLinks, new OwnerLinkRequest(clave, correo, contrasena, codigo), cancellationToken);

    /// <summary>Servidor de licencias configurado (la dirección del navegador debe ser de este mismo servidor).</summary>
    public Uri? Servidor => servidor;

    /// <summary>"Continuar con Google": crea la solicitud que el dueño autoriza en el navegador del sistema.</summary>
    public Task<Respuesta<OwnerLinkRequestStarted>> IniciarGoogleAsync(string clave, CancellationToken cancellationToken) =>
        EnviarAsync<OwnerLinkRequestStarted>(LicensingRoutes.OwnerLinkRequests, new OwnerLinkRequestStart(clave), cancellationToken);

    /// <summary>Una consulta del estado de la solicitud (el secreto va en el cuerpo, nunca en la URL).</summary>
    public async Task<ResultadoConsulta> ConsultarGoogleAsync(Guid solicitud, string secreto, CancellationToken cancellationToken)
    {
        var respuesta = await EnviarAsync<OwnerLinkPollResponse>(LicensingRoutes.OwnerLinkRequestPoll(solicitud), new OwnerLinkPoll(secreto), cancellationToken);
        return respuesta is { Ok: true, Dato: { } dato } ? ResultadoConsulta.Con(dato) : ResultadoConsulta.Fallo(respuesta.Codigo, respuesta.Mensaje);
    }

    private async Task<Respuesta<T>> EnviarAsync<T>(string ruta, object cuerpo, CancellationToken cancellationToken)
    {
        if (_http is null)
        {
            return new(default, "Esta versión del instalador no tiene servidor de licencias. Usa \"Probar gratis 30 días\" y activa la clave después.");
        }

        try
        {
            using var response = await _http.PostAsJsonAsync(ruta.TrimStart('/'), cuerpo, Http.Json, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                // Una red con página de inicio de sesión (hotel, café) responde 200 con HTML: no es JSON de BusinessPost.
                var dato = await response.Content.ReadFromJsonAsync<T>(Http.Json, cancellationToken);
                return dato is null ? new(default, RespuestaAjena) : new(dato);
            }

            var (codigo, detalle) = await Http.ProblemaAsync(response, cancellationToken);
            return new(default, Mensaje(codigo, detalle, response.StatusCode), codigo);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new(default, RespuestaAjena);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new(default, "No hay conexión con BusinessPost en internet. Revisa el internet de este equipo e intenta de nuevo, o elige \"Probar gratis 30 días\".");
        }
    }

    internal static string Mensaje(string? codigo, string? detalle, HttpStatusCode estado) => codigo switch
    {
        LicenseErrorCodes.KeyFormatInvalid => "La clave no está completa o tiene un error. Se ve así: POS-XXXXX-XXXXX-XXXXX-XXXXX.",
        LicenseErrorCodes.KeyInvalid => "Esa clave no es válida. Revísala letra por letra o pídela de nuevo en tu portal de cliente.",
        LicenseErrorCodes.SubscriptionInactive => "La suscripción de esta clave no está al día. Entra a tu portal de cliente o comunícate con soporte.",
        LicenseErrorCodes.TooManyRequests or "SECURITY.TOO_MANY_REQUESTS" => "Hubo demasiados intentos seguidos. Espera unos minutos e intenta de nuevo.",
        LicenseErrorCodes.SigningUnavailable => "El servidor de licencias está en mantenimiento. Intenta en unos minutos.",
        LicenseErrorCodes.OwnerLinkRequestUsed => "Esa autorización ya se usó. Pulsa de nuevo \"Continuar con Google\".",
        LicenseErrorCodes.OwnerLinkRequestNotFound => "La solicitud ya no existe. Pulsa de nuevo \"Continuar con Google\".",
        _ when !string.IsNullOrWhiteSpace(detalle) => detalle,
        _ => $"El servidor de licencias respondió con un error ({(int)estado}). Intenta de nuevo en unos minutos.",
    };
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

/// <summary>Propietario: con la cuenta del portal (<c>Enlace</c> firmado por la nube) o local (prueba de 30 días).</summary>
public sealed record DatosPropietario(string Usuario, string Nombre, string Contrasena, string? Enlace, string? Confirmacion = null);

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

/// <summary>Servidor de BusinessPost en este equipo (http://localhost:5480): asistente inicial, ingreso, licencia y código de recuperación.</summary>
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

    /// <summary>Asistente inicial en una sola llamada: empresa, sucursal "Principal" (S01), "Caja 1" (C01) y el Propietario.</summary>
    public Task<Respuesta<JsonElement>> ConfigurarAsync(DatosNegocio negocio, DatosPropietario propietario, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(negocio);
        ArgumentNullException.ThrowIfNull(propietario);
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
            owner = new
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

    public Task<Respuesta<JsonElement>> ActivarLicenciaAsync(string clave, CancellationToken cancellationToken) =>
        EnviarAsync<JsonElement>(HttpMethod.Post, "api/v1/license/activate", new { licenseKey = clave }, cancellationToken);

    public async Task<Respuesta<string>> GenerarCodigoAsync(CancellationToken cancellationToken)
    {
        var respuesta = await EnviarAsync<JsonElement>(HttpMethod.Post, "api/v1/backups/recovery-code", null, cancellationToken);
        return respuesta.Ok ? new(respuesta.Dato.GetProperty("code").GetString()) : new(null, respuesta.Mensaje, respuesta.Codigo);
    }

    public Task<Respuesta<JsonElement>> ConfirmarCodigoAsync(string codigo, CancellationToken cancellationToken) =>
        EnviarAsync<JsonElement>(HttpMethod.Post, "api/v1/backups/recovery-code/confirm", new { code = codigo }, cancellationToken);

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
