using System.Diagnostics;
using System.Text;
using Pos.Installer.Setup.Logica;

namespace Pos.Installer.Setup.Servicios;

/// <summary>Lo que la página envía al pulsar "Instalar BusinessPost".</summary>
public sealed record SolicitudInstalacion(
    ModoEquipo Modo,
    string? Backup,
    string? CodigoRecuperacion,
    string? Servidor,
    string? Huella,
    string? Clave,
    DatosNegocio? Negocio,
    DatosPropietario? Propietario,
    string? Edicion,
    bool CambiarModo = false);

/// <summary>
/// Instalación real (Fase 13-B, D13B-08): ejecuta en silencio el paquete Inno de siempre con los parámetros de la página, muestra el
/// avance que deja en su registro y, en el equipo principal o la única caja, configura la tienda por la API local (empresa, sucursal,
/// caja, Propietario, licencia y código de recuperación). Cada aviso a la página es un objeto que se envía como JSON.
/// </summary>
internal sealed class Instalacion(string paquete, Action<object> avisar) : IDisposable
{
    private readonly ServidorLocal _servidor = new();
    private string? _codigo;

    public string? Registro { get; private set; }

    public async Task EjecutarAsync(SolicitudInstalacion solicitud, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        if (!File.Exists(paquete))
        {
            Fallo("No se encontró el paquete de instalación junto al instalador. Descarga de nuevo BusinessPost-Setup desde tu portal de cliente.", null);
            return;
        }

        var temporal = Sistema.CarpetaTemporal();
        Registro = Path.Combine(temporal, $"instalacion-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        string? archivoCodigo = null;
        if (!string.IsNullOrWhiteSpace(solicitud.Backup))
        {
            // El código de recuperación no va en la línea de comandos: Inno lo lee de este archivo y lo borra.
            archivoCodigo = Path.Combine(temporal, $"{Rutas.PrefijoArchivoCodigo}{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(archivoCodigo, solicitud.CodigoRecuperacion ?? string.Empty, cancellationToken);
        }

        var lector = new RegistroInno();
        var argumentos = ArgumentosInno.Instalar(solicitud.Modo, Registro, solicitud.Servidor, solicitud.Huella, solicitud.Backup, archivoCodigo, solicitud.CambiarModo);
        int salida;
        var registroInno = new LectorIncremental();
        Process? proceso = null;
        try
        {
            proceso = Process.Start(new ProcessStartInfo(paquete, argumentos) { UseShellExecute = false })!;
            while (!proceso.HasExited)
            {
                LeerNuevo(Registro, registroInno, lector);
                Avanzar(lector);
                Program.PasoCritico = lector.EnPasoCritico;
                await Task.Delay(700, cancellationToken);
            }

            await proceso.WaitForExitAsync(CancellationToken.None);
            LeerNuevo(Registro, registroInno, lector);
            salida = proceso.ExitCode;
        }
        catch (OperationCanceledException)
        {
            LeerNuevo(Registro, registroInno, lector);
            if (!lector.EnPasoCritico && proceso is { HasExited: false })
            {
                // P3: última lectura del registro JUSTO antes de cortar: si en ese instante entró en la base de datos, no se corta.
                LeerNuevo(Registro, registroInno, lector);
            }

            if (lector.EnPasoCritico)
            {
                // IL5: migrando o restaurando NO se corta (Inno, el migrador o pg_restore a mitad dejan una base a medias): se espera a que
                // el paquete termine solo. Main espera esta tarea (Program.PasoCritico). Si ni así termina, sigue solo: no se mata.
                Program.PasoCritico = true;
                Anotar(Registro, "Instalador premium: se cerró la ventana durante la base de datos; se espera a que el paquete termine.");
                await EsperarSinCortarAsync(proceso, Registro);
            }
            else
            {
                // Antes de la base de datos: el paquete (y lo que haya lanzado) no debe seguir solo en segundo plano. IR5: app\current
                // sigue en la versión anterior, así que vuelven a arrancar los servicios que el paquete había detenido (con el actualizador
                // anterior), en vez de dejarlos en manual.
                var seguiaVivo = proceso is { HasExited: false };
                Detener(proceso);
                if (seguiaVivo && lector.TocoElEquipo)
                {
                    // El actualizador anterior solo se recupera si el paquete ya empezó a copiar (antes su copia .anterior podría ser vieja).
                    Sistema.ReanudarServiciosDetenidos(Registro, restaurarActualizador: lector.PorcentajeCopia > 0);
                }
            }

            throw;
        }
        finally
        {
            Program.PasoCritico = false;
            // El archivo del código se borra SOLO cuando el paquete ya terminó (Inno lo lee y lo borra; esto es por si no llegó a leerlo).
            if (archivoCodigo is not null && (proceso is null || proceso.HasExited))
            {
                BorrarSilencioso(archivoCodigo);
            }

            proceso?.Dispose();
        }

        Avanzar(lector);
        _avisosPaquete = [.. lector.Avisos];
        if (salida != 0 || lector.Resultado == ResultadoInno.Error)
        {
            // IN7: el paquete sale con un código distinto de cero si la base de datos, la recuperación o el cambio de versión fallaron.
            Fallo(SalidaPaquete.Mensaje(salida, lector.Error), Registro);
            return;
        }

        if (Sistema.CarpetaInstalada() is { } carpeta)
        {
            try
            {
                Sistema.RegistrarDesinstaladorPremium(carpeta);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No impide usar BusinessPost: queda el desinstalador clásico.
                File.AppendAllText(Registro, $"{Environment.NewLine}Instalador premium: no se pudo registrar el desinstalador premium: {ex.Message}");
            }
        }

        if (solicitud.Modo == ModoEquipo.Adicional || !string.IsNullOrWhiteSpace(solicitud.Backup))
        {
            Terminar(solicitud, lector.Resultado == ResultadoInno.SinRespuesta
                ? "BusinessPost quedó instalado, pero Windows todavía está revisando los archivos nuevos. Si no abre enseguida, espera unos minutos."
                : null, licenciaActiva: null);
            return;
        }

        await ConfigurarAsync(solicitud, cancellationToken);
    }

    /// <summary>Paso 6 en la página: la tienda queda configurada y se muestra el código de recuperación.</summary>
    private async Task ConfigurarAsync(SolicitudInstalacion solicitud, CancellationToken cancellationToken)
    {
        avisar(new { evento = "progreso", etapa = (int)Etapa.ConfigurandoNegocio, porcentaje = 90, nota = "Dejando lista tu tienda…" });
        // /health/ready (no /health/live): en una actualización el servidor responde enseguida, pero con 503 mientras migra la base.
        var espera = await _servidor.EsperarListoAsync(TimeSpan.FromMinutes(20),
            () => avisar(new { evento = "progreso", etapa = (int)Etapa.ConfigurandoNegocio, porcentaje = 91, nota = "Preparando la base de datos… puede tardar varios minutos en una actualización." }),
            null, cancellationToken);
        if (espera != EsperaServidor.Listo)
        {
            Fallo(espera == EsperaServidor.SinBaseDeDatos
                ? "BusinessPost quedó instalado, pero la base de datos todavía no está lista. Espera unos minutos y abre BusinessPost; si sigue " +
                  "sin abrir, reinicia el computador o comunícate con soporte y comparte el registro."
                : "BusinessPost quedó instalado, pero todavía no responde en este equipo. Reinicia el computador y abre BusinessPost: el " +
                  "asistente te pedirá los datos de tu negocio.", Registro);
            return;
        }

        var configurado = await _servidor.ConfiguradoAsync(cancellationToken);
        if (!configurado.Ok)
        {
            Fallo($"BusinessPost quedó instalado, pero no pudimos revisar si la tienda ya estaba configurada ({configurado.Mensaje}). Abre " +
                  "BusinessPost en unos minutos: si falta, el asistente te pedirá los datos de tu negocio.", Registro);
            return;
        }

        if (configurado.Dato)
        {
            // Reinstalación sobre los datos conservados: la tienda ya estaba configurada.
            Terminar(solicitud, "Tu tienda ya estaba configurada en este equipo: conservamos tus datos, usuarios y licencia.", licenciaActiva: null);
            return;
        }

        var configurada = await _servidor.ConfigurarAsync(solicitud.Negocio!, solicitud.Propietario!, cancellationToken);
        if (!configurada.Ok)
        {
            Fallo($"No pudimos guardar los datos de tu negocio: {configurada.Mensaje}", Registro);
            return;
        }

        avisar(new { evento = "progreso", etapa = (int)Etapa.ConfigurandoNegocio, porcentaje = 94, nota = "Activando tu licencia…" });
        var entrada = await _servidor.EntrarAsync(solicitud.Propietario!.Usuario.Trim().ToLowerInvariant(), solicitud.Propietario.Contrasena, cancellationToken);
        if (!entrada.Ok)
        {
            Fallo($"La tienda quedó configurada, pero no pudimos entrar con tu usuario: {entrada.Mensaje}", Registro);
            return;
        }

        string? avisoLicencia = null;
        bool? licenciaActiva = null;
        if (!string.IsNullOrWhiteSpace(solicitud.Clave))
        {
            var activada = await _servidor.ActivarLicenciaAsync(solicitud.Clave, cancellationToken);
            licenciaActiva = activada.Ok;
            avisoLicencia = activada.Ok ? null
                : $"La licencia no se pudo activar ahora ({activada.Mensaje}). BusinessPost funciona en prueba; actívala luego en Administración → Licencia.";
        }

        var codigo = await _servidor.GenerarCodigoAsync(cancellationToken);
        if (!codigo.Ok)
        {
            Terminar(solicitud, $"No pudimos crear el código de recuperación de los backups ({codigo.Mensaje}). Créalo en Administración → Backups.", licenciaActiva);
            return;
        }

        _codigo = codigo.Dato;
        _ultimaSolicitud = solicitud;
        _licenciaActiva = licenciaActiva;
        avisar(new { evento = "codigo", codigo = codigo.Dato, aviso = avisoLicencia, porcentaje = 98 });
    }

    private SolicitudInstalacion? _ultimaSolicitud;
    private IReadOnlyList<string> _avisosPaquete = [];
    private bool? _licenciaActiva;

    /// <summary>El dueño confirmó que guardó el código: se confirma en el servidor y termina.</summary>
    public async Task<string?> ConfirmarCodigoAsync(CancellationToken cancellationToken)
    {
        if (_codigo is null || _ultimaSolicitud is null)
        {
            return "No hay un código pendiente.";
        }

        var confirmado = await _servidor.ConfirmarCodigoAsync(_codigo, cancellationToken);
        if (!confirmado.Ok)
        {
            return confirmado.Mensaje;
        }

        _codigo = null;
        Terminar(_ultimaSolicitud, null, _licenciaActiva);
        return null;
    }

    private void Terminar(SolicitudInstalacion solicitud, string? aviso, bool? licenciaActiva) =>
        avisar(new
        {
            evento = "terminado",
            // Avisos del paquete (p. ej. red "Pública": las cajas no se conectan) junto con el del final.
            aviso = string.Join(" ", new[] { aviso }.Concat(_avisosPaquete).Where(a => !string.IsNullOrWhiteSpace(a))) is { Length: > 0 } todos ? todos : null,
            licenciaActiva,
            empresa = solicitud.Negocio is { } negocio ? (string.IsNullOrWhiteSpace(negocio.NombreComercial) ? negocio.RazonSocial : negocio.NombreComercial) : null,
            modo = solicitud.Modo.ToString().ToLowerInvariant(),
        });

    public void Dispose() => _servidor.Dispose();

    private void Fallo(string mensaje, string? registro) => avisar(new { evento = "error", mensaje, registro });

    private void Avanzar(RegistroInno lector) =>
        avisar(new
        {
            evento = "progreso",
            etapa = (int)lector.Etapa,
            porcentaje = lector.PorcentajeGeneral,
            nota = lector.Etapa switch
            {
                Etapa.CopiandoProgramas => $"Copiando archivos… {lector.PorcentajeCopia} %",
                Etapa.RevisandoDefender => "La primera vez Windows revisa cada archivo nuevo: puede tardar unos minutos.",
                Etapa.CreandoBaseDeDatos => "Preparando la base de datos de tu negocio en este equipo.",
                Etapa.PreparandoCaja => "Así BusinessPost se inicia solo cada vez que enciendes el equipo.",
                _ => "Esperando a que BusinessPost responda. Puede tardar unos minutos.",
            },
        });

    /// <summary>Lee lo nuevo del registro (Inno lo tiene abierto mientras escribe) y lo pasa al lector línea por línea.</summary>
    private static void LeerNuevo(string ruta, LectorIncremental registro, RegistroInno lector)
    {
        if (!File.Exists(ruta))
        {
            return;
        }

        try
        {
            using var archivo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var linea in registro.Leer(archivo))
            {
                lector.Procesar(linea);
            }
        }
        catch (IOException)
        {
            // Inno lo tiene bloqueado justo ahora: se lee en la próxima vuelta (la posición no avanzó).
        }
    }

    /// <summary>Límite para esperar un paso crítico del paquete (una restauración grande tarda; el migrador tiene su propio límite de 60 min).</summary>
    public static readonly TimeSpan EsperaPasoCritico = TimeSpan.FromMinutes(65);

    private static async Task EsperarSinCortarAsync(Process? proceso, string? registro)
    {
        if (proceso is null)
        {
            return;
        }

        try
        {
            await proceso.WaitForExitAsync(CancellationToken.None).WaitAsync(EsperaPasoCritico);
            Anotar(registro, $"Instalador premium: el paquete terminó (código {proceso.ExitCode}).");
        }
        catch (TimeoutException)
        {
            // IR6: el archivo del código de recuperación NO se borra: Inno lo borra al leerlo, o lo borra la próxima ejecución.
            Program.PaqueteSigue = true;
            Anotar(registro, "Instalador premium: el paquete sigue trabajando; NO se cortó (terminará solo).");
        }
    }

    private static void Anotar(string? registro, string texto)
    {
        if (registro is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(registro, Environment.NewLine + texto);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Inno tiene el registro abierto: el aviso no es indispensable.
        }
    }

    /// <summary>Detiene el paquete Inno y todo lo que lanzó (cancelación).</summary>
    private static void Detener(Process? proceso)
    {
        if (proceso is null)
        {
            return;
        }

        try
        {
            if (!proceso.HasExited)
            {
                proceso.Kill(entireProcessTree: true);
                proceso.WaitForExit(TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Ya terminó o no se puede detener: no hay nada más que hacer.
        }
    }

    private static void BorrarSilencioso(string ruta)
    {
        try
        {
            if (File.Exists(ruta))
            {
                File.Delete(ruta);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Queda en la carpeta temporal del administrador; Inno lo borra al leerlo.
        }
    }
}
