using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
public sealed class NubeLicencias(Uri? servidor)
{
    private readonly HttpClient? _http = servidor is null ? null : new HttpClient { BaseAddress = servidor, Timeout = TimeSpan.FromSeconds(20) };

    public bool Disponible => _http is not null;

    public Task<Respuesta<LicensePreviewResponse>> VistaPreviaAsync(string clave, CancellationToken cancellationToken) =>
        EnviarAsync<LicensePreviewResponse>(LicensingRoutes.LicensePreviews, new LicensePreviewRequest(clave), cancellationToken);

    public Task<Respuesta<OwnerLinkResponse>> CuentaAsync(string clave, string correo, string contrasena, string? codigo, CancellationToken cancellationToken) =>
        EnviarAsync<OwnerLinkResponse>(LicensingRoutes.OwnerLinks, new OwnerLinkRequest(clave, correo, contrasena, codigo), cancellationToken);

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
                return new(await response.Content.ReadFromJsonAsync<T>(Http.Json, cancellationToken));
            }

            var (codigo, detalle) = await Http.ProblemaAsync(response, cancellationToken);
            return new(default, Mensaje(codigo, detalle, response.StatusCode), codigo);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
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
        _ when !string.IsNullOrWhiteSpace(detalle) => detalle,
        _ => $"El servidor de licencias respondió con un error ({(int)estado}). Intenta de nuevo en unos minutos.",
    };
}

/// <summary>Datos del negocio que configura el instalador (cuerpo del asistente inicial del servidor local).</summary>
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
    string? Correo);

/// <summary>Propietario: con la cuenta del portal (<c>Enlace</c> firmado por la nube) o local (prueba de 30 días).</summary>
public sealed record DatosPropietario(string Usuario, string Nombre, string Contrasena, string? Enlace);

/// <summary>Servidor de BusinessPost en este equipo (http://localhost:5480): asistente inicial, ingreso, licencia y código de recuperación.</summary>
public sealed class ServidorLocal : IDisposable
{
    public static readonly Uri Direccion = new("http://localhost:5480/");

    private readonly HttpClient _http = new() { BaseAddress = Direccion, Timeout = TimeSpan.FromSeconds(60) };

    public async Task<bool> RespondeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var respuesta = await _http.GetAsync(new Uri("health/live", UriKind.Relative), cancellationToken);
            return respuesta.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<bool> EsperarAsync(TimeSpan maximo, CancellationToken cancellationToken)
    {
        var fin = DateTime.UtcNow + maximo;
        while (DateTime.UtcNow < fin)
        {
            if (await RespondeAsync(cancellationToken))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        return false;
    }

    /// <summary>¿El asistente inicial ya se hizo? (reinstalación sobre los datos conservados).</summary>
    public async Task<bool> ConfiguradoAsync(CancellationToken cancellationToken)
    {
        var estado = await _http.GetFromJsonAsync<JsonElement>(new Uri("api/v1/setup/status", UriKind.Relative), Http.Json, cancellationToken);
        return estado.TryGetProperty("isCompleted", out var completo) && completo.GetBoolean();
    }

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
