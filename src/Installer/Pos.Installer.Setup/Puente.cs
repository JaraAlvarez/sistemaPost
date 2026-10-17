using System.Text.Json;
using Pos.Installer.Setup.Logica;
using Pos.Installer.Setup.Servicios;
using Pos.Licensing.Contracts;
using Pos.Updates.Contracts;

namespace Pos.Installer.Setup;

/// <summary>Cómo se abrió el programa.</summary>
internal sealed record Opciones(bool Desinstalar, bool Maqueta, string Paquete, string? Pantalla = null, ArgumentosCaptura? Captura = null);

/// <summary>
/// Puente página ↔ Windows (mensajes web de WebView2). La página envía <c>{ id, accion, datos }</c> y recibe
/// <c>{ id, ok, dato, mensaje, codigo }</c>; los avisos de la instalación llegan como <c>{ evento, … }</c>. La contraseña del portal y
/// el enlace firmado se quedan en este proceso (memoria) y solo viajan al servidor local; nunca se guardan en disco.
/// </summary>
internal sealed class Puente(Opciones opciones, Action<string> enviar, Func<string?> elegirBackup, Action<string> accionVentana) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly NubeLicencias _nube = new(Sistema.ServidorLicencias);
    private readonly Lazy<IReadOnlyList<Municipio>> _municipios = new(LeerMunicipios);
    private readonly CancellationTokenSource _cierre = new();
    private Instalacion? _instalacion;
    private string? _enlace;
    private string? _correoEnlace;

    // "Continuar con Google": la espera en curso (una a la vez) y la dirección para "Abrir el navegador otra vez".
    private CancellationTokenSource? _google;
    private string? _direccionGoogle;
    private bool _enlaceDeGoogle;

    public bool Trabajando { get; private set; }

    public void Cancelar() => _cierre.Cancel();

    public void Dispose()
    {
        _google?.Cancel();
        _google?.Dispose();
        _cierre.Dispose();
        _instalacion?.Dispose();
    }

    public async Task RecibirAsync(string json)
    {
        string? id = null;
        try
        {
            using var mensaje = JsonDocument.Parse(json);
            var raiz = mensaje.RootElement;
            id = raiz.TryGetProperty("id", out var i) ? i.GetString() : null;
            var accion = raiz.GetProperty("accion").GetString() ?? string.Empty;
            var datos = raiz.TryGetProperty("datos", out var d) ? d.Clone() : default;
            var (dato, error, codigo) = await AtenderAsync(accion, datos);
            Responder(id, error is null, dato, error, codigo);
        }
#pragma warning disable CA1031 // Cualquier falla se muestra en la página en vez de cerrar el instalador.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Responder(id, false, null, $"Ocurrió un error inesperado: {ex.Message}", null);
        }
    }

    private async Task<(object? Dato, string? Error, string? Codigo)> AtenderAsync(string accion, JsonElement datos)
    {
        var ct = _cierre.Token;
        switch (accion)
        {
            case "inicio":
                return (new
                {
                    modo = opciones.Desinstalar ? "desinstalar" : "instalar",
                    maqueta = opciones.Maqueta,
                    pantalla = opciones.Maqueta ? opciones.Pantalla : null,
                    version = Sistema.Version,
                    nube = _nube.Disponible || opciones.Maqueta,
                    desinstalacion = opciones.Desinstalar ? (opciones.Maqueta ? new { instalado = true, carpeta = @"C:\Program Files\BusinessPost", datos = Sistema.Datos, version = "1.1.0", equipoPrincipal = true } : Desinstalacion.Estado()) : null,
                }, null, null);

            case "minimizar" or "cerrar" or "arrastrar":
                accionVentana(accion);
                return (null, null, null);

            case "municipios":
                return (Municipios.Buscar(_municipios.Value, Texto(datos, "texto")).Select(m => new { codigo = m.Codigo, nombre = m.Nombre, departamento = m.Departamento }), null, null);

            case "digito":
                return (DigitoVerificacion.Calcular(Texto(datos, "numero")), null, null);

            case "vistaPrevia":
            {
                if (opciones.Maqueta)
                {
                    await Task.Delay(600, ct);
                    return (new LicensePreviewResponse("Supermercado La Economía SAS", "900123456-8", "MULTI", "ACTIVE", new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), "Medellín"), null, null);
                }

                var respuesta = await _nube.VistaPreviaAsync(Texto(datos, "clave") ?? string.Empty, ct);
                return (respuesta.Dato, respuesta.Mensaje, respuesta.Codigo);
            }

            case "cuentaPortal":
            {
                if (opciones.Maqueta)
                {
                    await Task.Delay(700, ct);
                    _enlace = "enlace-de-maqueta";
                    _correoEnlace = Texto(datos, "correo")?.Trim().ToLowerInvariant();
                    return (new { email = _correoEnlace, displayName = "Dueño de prueba" }, null, null);
                }

                var respuesta = await _nube.CuentaAsync(
                    Texto(datos, "clave") ?? string.Empty, Texto(datos, "correo") ?? string.Empty, Texto(datos, "contrasena") ?? string.Empty, Texto(datos, "codigo"), ct);
                if (!respuesta.Ok)
                {
                    _enlace = null;
                    return (null, respuesta.Mensaje, respuesta.Codigo);
                }

                _enlace = respuesta.Dato!.Token;
                _correoEnlace = respuesta.Dato.Email;
                _enlaceDeGoogle = false;
                return (new { email = respuesta.Dato.Email, displayName = respuesta.Dato.DisplayName }, null, null);
            }

            case "googleIniciar":
                return await IniciarGoogleAsync(Texto(datos, "clave") ?? string.Empty, ct);

            case "googleAbrir":
                if (_direccionGoogle is not null && !opciones.Maqueta)
                {
                    Sistema.AbrirComoUsuario(_direccionGoogle);
                }

                return (null, null, null);

            case "googleCancelar":
                CancelarGoogle();
                return (null, null, null);

            case "buscarPrincipal":
            {
                if (opciones.Maqueta)
                {
                    await Task.Delay(800, ct);
                    return (new[] { new { url = "https://192.168.1.10:5443/", huella = "3F7A91C20B44E1D8AA00BB11CC22DD33EE44FF5566778899AABBCCDDEEFF0011", nombre = "Equipo principal · CAJA-PRINCIPAL" } }, null, null);
                }

                var servidores = await LanDiscovery.FindAsync(TimeSpan.FromSeconds(3), ct);
                return (servidores.Select(s => new
                {
                    url = s.Url,
                    huella = s.Response.CertificateThumbprint,
                    nombre = $"{s.Response.StoreName ?? "Equipo principal"} · {s.Response.MachineName}",
                }), null, null);
            }

            case "elegirBackup":
                return (elegirBackup(), null, null);

            case "instalar":
                return Instalar(datos);

            case "confirmarCodigo":
                if (opciones.Maqueta)
                {
                    Avisar(new { evento = "terminado", empresa = "La Economía", modo = "principal", licenciaActiva = true });
                    return (null, null, null);
                }

                var error = _instalacion is null ? "No hay una instalación en curso." : await _instalacion.ConfirmarCodigoAsync(ct);
                return (null, error, null);

            case "verRegistro":
                if (Texto(datos, "ruta") is { } ruta && File.Exists(ruta))
                {
                    Sistema.AbrirComoUsuario(ruta);
                }

                return (null, null, null);

            case "abrir":
                Abrir(datos.ValueKind == JsonValueKind.Object && datos.TryGetProperty("aliado", out var aliado) && aliado.GetBoolean(),
                    Texto(datos, "servidor"));
                accionVentana("salir");
                return (null, null, null);

            case "backupFinal":
                if (opciones.Maqueta)
                {
                    await Task.Delay(1500, ct);
                    return (Path.Combine(Sistema.CarpetaBackupFinal(), "BusinessPost-20261017-1030-MANUAL.posbak"), null, null);
                }

                Trabajando = true;
                try
                {
                    var backup = await Desinstalacion.BackupFinalAsync(ct);
                    return (backup.Dato, backup.Mensaje, null);
                }
                finally
                {
                    Trabajando = false;
                }

            case "desinstalar":
                return Desinstalar(datos);

            default:
                return (null, $"Acción desconocida: {accion}", null);
        }
    }

    /// <summary>
    /// "Continuar con Google" (Fase 13-B, parte E): Google no permite su ingreso dentro de un WebView, así que la nube crea una solicitud
    /// y el instalador abre el portal en el NAVEGADOR DEL SISTEMA (con el usuario normal, no como administrador). Solo se abre una dirección
    /// HTTPS del mismo servidor de licencias. Luego consulta en segundo plano y avisa a la página con <c>{ evento: "google", … }</c>.
    /// </summary>
    private async Task<(object?, string?, string?)> IniciarGoogleAsync(string clave, CancellationToken ct)
    {
        CancelarGoogle();
        _enlace = null;
        if (opciones.Maqueta)
        {
            await Task.Delay(500, ct);
            Esperar((_, _) => Task.FromResult(new ResultadoConsulta(OwnerLinkRequestStatuses.Approved, 5,
                new OwnerLinkResponse("enlace-de-maqueta", "dueno@gmail.com", "Dueño de prueba", DateTimeOffset.UtcNow.AddHours(2)))), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));
            return (new { codigo = "K7QF-M2XP", venceEn = 600 }, null, null);
        }

        var respuesta = await _nube.IniciarGoogleAsync(clave, ct);
        if (!respuesta.Ok)
        {
            return (null, respuesta.Mensaje, respuesta.Codigo);
        }

        var inicio = respuesta.Dato!;
        if (!IngresoGoogle.EsDireccionSegura(inicio.BrowserUrl, _nube.Servidor))
        {
            return (null, "El servidor de licencias devolvió una dirección que no es la suya. Usa tu correo y contraseña del portal.", null);
        }

        _direccionGoogle = inicio.BrowserUrl;
        Sistema.AbrirComoUsuario(inicio.BrowserUrl);
        Esperar((_, token) => _nube.ConsultarGoogleAsync(inicio.RequestId, inicio.PollToken, token),
            TimeSpan.FromSeconds(inicio.ExpiresIn), TimeSpan.FromSeconds(inicio.Interval));
        return (new { codigo = inicio.UserCode, venceEn = inicio.ExpiresIn }, null, null);
    }

    /// <summary>Lanza la espera en segundo plano (el resultado llega a la página como aviso).</summary>
    private void Esperar(Func<EsperaAutorizacion, CancellationToken, Task<ResultadoConsulta>> consultar, TimeSpan vigencia, TimeSpan intervalo)
    {
        var cancelacion = CancellationTokenSource.CreateLinkedTokenSource(_cierre.Token);
        _google = cancelacion;
        var espera = new EsperaAutorizacion(DateTimeOffset.UtcNow, vigencia, intervalo);
        _ = Task.Run(async () =>
        {
            var estado = await espera.EjecutarAsync(token => consultar(espera, token), Task.Delay, () => DateTimeOffset.UtcNow, null, cancelacion.Token);
            if (estado == EstadoEspera.Autorizada && !cancelacion.IsCancellationRequested)
            {
                _enlace = espera.Enlace!.Token;
                _correoEnlace = espera.Enlace.Email;
                _enlaceDeGoogle = true;
                Avisar(new { evento = "google", estado = "autorizada", email = espera.Enlace.Email, displayName = espera.Enlace.DisplayName });
                return;
            }

            if (estado != EstadoEspera.Cancelada)
            {
                Avisar(new { evento = "google", estado = estado.ToString().ToLowerInvariant(), mensaje = espera.Mensaje });
            }
        });
    }

    private void CancelarGoogle()
    {
        var actual = _google;
        _google = null;
        _direccionGoogle = null;
        actual?.Cancel();
    }

    private (object?, string?, string?) Instalar(JsonElement datos)
    {
        var solicitud = datos.Deserialize<SolicitudInstalacion>(Json)!;
        if (solicitud.Propietario is { } conGoogle && _enlace is not null && _enlaceDeGoogle && !string.IsNullOrWhiteSpace(solicitud.Clave)
            && IngresoGoogle.ValidarContrasenaLocal(conGoogle.Contrasena, conGoogle.Contrasena, _correoEnlace) is { } problema)
        {
            // Con Google no hay contraseña del portal: la contraseña LOCAL que eligió el dueño debe cumplir la política del portal.
            return (null, problema, null);
        }

        if (solicitud.Propietario is { } propietario && _enlace is not null && !string.IsNullOrWhiteSpace(solicitud.Clave))
        {
            // Con la cuenta del portal: el usuario es el correo VALIDADO por la nube (no el que quede escrito en la página).
            solicitud = solicitud with { Propietario = propietario with { Usuario = _correoEnlace ?? propietario.Usuario, Enlace = _enlace } };
        }
        else if (solicitud.Propietario is { } local)
        {
            solicitud = solicitud with { Propietario = local with { Enlace = null } };
        }

        Trabajando = true;
        _ = Task.Run(async () =>
        {
            try
            {
                if (opciones.Maqueta)
                {
                    await SimularAsync(solicitud, _cierre.Token);
                }
                else
                {
                    _instalacion = new Instalacion(opciones.Paquete, Avisar);
                    await _instalacion.EjecutarAsync(solicitud, _cierre.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Se cerró la ventana.
            }
#pragma warning disable CA1031 // La falla se muestra en la página.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Avisar(new { evento = "error", mensaje = $"Ocurrió un error inesperado: {ex.Message}", registro = _instalacion?.Registro });
            }
            finally
            {
                Trabajando = false;
            }
        });
        return (null, null, null);
    }

    private (object?, string?, string?) Desinstalar(JsonElement datos)
    {
        var solicitud = datos.Deserialize<SolicitudDesinstalacion>(Json)!;
        Trabajando = true;
        _ = Task.Run(async () =>
        {
            try
            {
                if (opciones.Maqueta)
                {
                    for (var p = 10; p <= 100; p += 10)
                    {
                        Avisar(new { evento = "progreso", porcentaje = p, nota = p < 50 ? "Liberando este equipo de la licencia…" : "Quitando los programas…" });
                        await Task.Delay(400, _cierre.Token);
                    }

                    Avisar(new { evento = "terminado", borrados = solicitud.BorrarDatos, datos = Sistema.Datos, avisos = Array.Empty<string>() });
                }
                else
                {
                    await new Desinstalacion(Avisar).EjecutarAsync(solicitud, _cierre.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Se cerró la ventana.
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Avisar(new { evento = "error", mensaje = $"Ocurrió un error inesperado: {ex.Message}" });
            }
            finally
            {
                Trabajando = false;
            }
        });
        return (null, null, null);
    }

    /// <summary>"Abrir BusinessPost": la ventana propia si está instalada; si no, la dirección en el navegador. El aliado solo si se marcó.</summary>
    private void Abrir(bool aliado, string? servidor)
    {
        if (!opciones.Maqueta)
        {
            var carpeta = Sistema.CarpetaInstalada();
            // La ventana va dentro de la versión activa (app\current\desktop); {app}\desktop queda en instalaciones anteriores.
            var ventana = carpeta is null ? null
                : new[] { Path.Combine(carpeta, "app", "current", "desktop", "BusinessPost.exe"), Path.Combine(carpeta, "desktop", "BusinessPost.exe") }
                    .FirstOrDefault(File.Exists);
            Sistema.AbrirComoUsuario(ventana is not null ? ventana
                : !string.IsNullOrWhiteSpace(servidor) ? servidor : ServidorLocal.Direccion.ToString());
        }

        if (aliado)
        {
            Sistema.AbrirComoUsuario("https://tutiendanueva.com");
        }
    }

    /// <summary>‑‑maqueta: recorre las etapas sin instalar nada (para revisar el diseño).</summary>
    private async Task SimularAsync(SolicitudInstalacion solicitud, CancellationToken ct)
    {
        var lector = new RegistroInno();
        string[] lineas =
        [
            .. Enumerable.Range(1, 20).Select(p => $"Progreso: {p * 5}"),
            "Paso: Revisando los archivos con Windows Defender", "Paso: Creando la base de datos…", "Paso: Registrando los servicios de BusinessPost…",
            "Paso: Iniciando BusinessPost…", "Resultado: OK",
        ];
        foreach (var linea in lineas)
        {
            lector.Procesar(linea);
            Avisar(new { evento = "progreso", etapa = (int)lector.Etapa, porcentaje = lector.PorcentajeGeneral, nota = lector.PasoActual ?? $"Copiando archivos… {lector.PorcentajeCopia} %" });
            await Task.Delay(linea.StartsWith("Progreso", StringComparison.Ordinal) ? 150 : 1300, ct);
        }

        if (solicitud.Modo == ModoEquipo.Adicional || solicitud.Backup is not null)
        {
            Avisar(new { evento = "terminado", modo = solicitud.Modo.ToString().ToLowerInvariant() });
            return;
        }

        Avisar(new { evento = "progreso", etapa = (int)Etapa.ConfigurandoNegocio, porcentaje = 94, nota = "Activando tu licencia…" });
        await Task.Delay(1200, ct);
        Avisar(new { evento = "codigo", codigo = "7F3A-91C2-0B44-E1D8-AB12-CD34", porcentaje = 98 });
    }

    private void Avisar(object aviso) => enviar(JsonSerializer.Serialize(aviso, Json));

    private void Responder(string? id, bool ok, object? dato, string? mensaje, string? codigo) =>
        enviar(JsonSerializer.Serialize(new { id, ok, dato, mensaje, codigo }, Json));

    private static string? Texto(JsonElement datos, string nombre) =>
        datos.ValueKind == JsonValueKind.Object && datos.TryGetProperty(nombre, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;

    private static IReadOnlyList<Municipio> LeerMunicipios()
    {
        using var recurso = typeof(Puente).Assembly.GetManifestResourceStream("seed_colombia.sql")!;
        using var lector = new StreamReader(recurso);
        return Municipios.Leer(lector.ReadToEnd());
    }
}
