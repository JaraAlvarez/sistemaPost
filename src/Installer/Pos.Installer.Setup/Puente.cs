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
/// <c>{ id, ok, dato, mensaje, codigo }</c>; los avisos de la instalación llegan como <c>{ evento, … }</c>. Desde 1.2.1 el instalador
/// no pide ningún dato (ni licencia ni cuenta): el primer ingreso a BusinessPost crea al Propietario con la cuenta del portal.
/// </summary>
internal sealed class Puente(
    Opciones opciones, Action<string> enviar, Func<string?> elegirBackup, Func<string, string?> elegirCarpeta, Action<string> accionVentana) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly Lazy<IReadOnlyList<Municipio>> _municipios = new(LeerMunicipios);
    private readonly CancellationTokenSource _cierre = new();
    private Instalacion? _instalacion;


    public bool Trabajando { get; private set; }

    public void Cancelar() => _cierre.Cancel();

    public void Dispose()
    {
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
                    modoInstalado = opciones.Maqueta || opciones.Desinstalar ? null : Sistema.ModoInstalado()?.ToString().ToLowerInvariant(),
                    desinstalacion = opciones.Desinstalar ? (opciones.Maqueta ? new { instalado = true, carpeta = @"C:\Program Files\BusinessPost", datos = Sistema.Datos, version = "1.1.0", equipoPrincipal = true } : Desinstalacion.Estado()) : null,
                }, null, null);

            case "minimizar" or "cerrar" or "arrastrar":
                accionVentana(accion);
                return (null, null, null);

            case "municipios":
                return (Municipios.Buscar(_municipios.Value, Texto(datos, "texto")).Select(m => new { codigo = m.Codigo, nombre = m.Nombre, departamento = m.Departamento }), null, null);

            case "digito":
                return (DigitoVerificacion.Calcular(Texto(datos, "numero")), null, null);

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

            case "reconfigurar":
                return Reconfigurar();

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

                // El desinstalador corre como administrador: "Documentos" sería la carpeta del administrador. Se propone la del usuario
                // que inició sesión y el dueño elige (la ruta completa se muestra al terminar).
                var carpetaBackup = elegirCarpeta(Sistema.CarpetaBackupFinal());
                if (carpetaBackup is null)
                {
                    return (null, null, null);
                }

                Trabajando = true;
                try
                {
                    var backup = await Desinstalacion.BackupFinalAsync(carpetaBackup, ct);
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

    private (object?, string?, string?) Instalar(JsonElement datos)
    {
        var solicitud = datos.Deserialize<SolicitudInstalacion>(Json)!;
        // El instalador ya no pide la licencia ni la cuenta (1.2.1): la tienda queda como única caja, sin Propietario y en prueba. La
        // página solo decide la caja adicional y, al recuperar un backup, si el equipo dañado era el principal.
        solicitud = solicitud with
        {
            Modo = ModoPorLicencia.Elegir(solicitud.Modo, !string.IsNullOrWhiteSpace(solicitud.Backup)),
            Clave = null,
            Propietario = null,
        };
        Trabajando = true;
        Program.RegistrarTrabajo(Task.Run(async () =>
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
        }));
        return (null, null, null);
    }

    /// <summary>
    /// Reintenta SOLO la configuración de la tienda (el paquete ya quedó instalado). Nunca reinstala ni manda al asistente web.
    /// </summary>
    private (object?, string?, string?) Reconfigurar()
    {
        if (opciones.Maqueta)
        {
            Trabajando = true;
            Program.RegistrarTrabajo(Task.Run(async () =>
            {
                try
                {
                    Avisar(new { evento = "progreso", etapa = (int)Etapa.ConfigurandoNegocio, porcentaje = 94, nota = "Dejando lista tu tienda…" });
                    await Task.Delay(1200, _cierre.Token);
                    Avisar(new { evento = "terminado", modo = "unica", empresa = "Mi negocio" });
                }
                catch (OperationCanceledException)
                {
                    // Se cerró la ventana.
                }
                finally
                {
                    Trabajando = false;
                }
            }));
            return (null, null, null);
        }

        if (_instalacion?.Pendiente is not { } pendiente)
        {
            return (null, "No hay una configuración pendiente. Cierra el instalador y ábrelo otra vez.", null);
        }

        var instalacion = _instalacion;
        Trabajando = true;
        Program.RegistrarTrabajo(Task.Run(async () =>
        {
            try
            {
                await instalacion.ReintentarConfiguracionAsync(_cierre.Token);
            }
            catch (OperationCanceledException)
            {
                // Se cerró la ventana.
            }
#pragma warning disable CA1031 // La falla se muestra en la página.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Avisar(new { evento = "error", mensaje = $"Ocurrió un error inesperado: {ex.Message}", registro = instalacion.Registro, reintentar = true });
            }
            finally
            {
                Trabajando = false;
            }
        }));
        return (null, null, null);
    }

    private (object?, string?, string?) Desinstalar(JsonElement datos)
    {
        var solicitud = datos.Deserialize<SolicitudDesinstalacion>(Json)!;
        Trabajando = true;
        Program.RegistrarTrabajo(Task.Run(async () =>
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
        }));
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

        Avisar(new { evento = "progreso", etapa = (int)Etapa.ConfigurandoNegocio, porcentaje = 94, nota = "Dejando lista tu tienda…" });
        await Task.Delay(1200, ct);
        Avisar(new { evento = "terminado", modo = "unica", empresa = "Mi negocio" });
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
