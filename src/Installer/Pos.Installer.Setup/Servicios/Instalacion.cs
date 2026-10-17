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
    string? Edicion);

/// <summary>
/// Instalación real (Fase 13-B, D13B-08): ejecuta en silencio el paquete Inno de siempre con los parámetros de la página, muestra el
/// avance que deja en su registro y, en el equipo principal o la única caja, configura la tienda por la API local (empresa, sucursal,
/// caja, Propietario, licencia y código de recuperación). Cada aviso a la página es un objeto que se envía como JSON.
/// </summary>
internal sealed class Instalacion(string paquete, Action<object> avisar) : IDisposable
{
    private readonly ServidorLocal _servidor = new();
    private readonly Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
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
            archivoCodigo = Path.Combine(temporal, $"recuperacion-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(archivoCodigo, solicitud.CodigoRecuperacion ?? string.Empty, cancellationToken);
        }

        var lector = new RegistroInno();
        var argumentos = ArgumentosInno.Instalar(solicitud.Modo, Registro, solicitud.Servidor, solicitud.Huella, solicitud.Backup, archivoCodigo);
        int salida;
        try
        {
            using var proceso = Process.Start(new ProcessStartInfo(paquete, argumentos) { UseShellExecute = false })!;
            long posicion = 0;
            var pendiente = new StringBuilder();
            while (!proceso.HasExited)
            {
                posicion = LeerNuevo(Registro, posicion, pendiente, lector, _utf8);
                Avanzar(lector);
                await Task.Delay(700, cancellationToken);
            }

            LeerNuevo(Registro, posicion, pendiente, lector, _utf8);
            salida = proceso.ExitCode;
        }
        finally
        {
            if (archivoCodigo is not null && File.Exists(archivoCodigo))
            {
                File.Delete(archivoCodigo);
            }
        }

        Avanzar(lector);
        if (salida != 0 || lector.Resultado == ResultadoInno.Error)
        {
            Fallo(lector.Error is { } paso
                ? $"La instalación no pudo terminar el paso \"{paso}\"."
                : $"La instalación no pudo terminar (código {salida}).", Registro);
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
        if (!await _servidor.EsperarAsync(TimeSpan.FromMinutes(10), cancellationToken))
        {
            Fallo("BusinessPost quedó instalado, pero todavía no responde en este equipo. Reinicia el computador y abre BusinessPost: el " +
                  "asistente te pedirá los datos de tu negocio.", Registro);
            return;
        }

        if (await _servidor.ConfiguradoAsync(cancellationToken))
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
            aviso,
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
    private static long LeerNuevo(string ruta, long posicion, StringBuilder pendiente, RegistroInno lector, Decoder utf8)
    {
        if (!File.Exists(ruta))
        {
            return posicion;
        }

        try
        {
            using var archivo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (archivo.Length <= posicion)
            {
                return posicion;
            }

            archivo.Seek(posicion, SeekOrigin.Begin);
            var bytes = new byte[archivo.Length - posicion];
            archivo.ReadExactly(bytes);
            var caracteres = new char[utf8.GetCharCount(bytes, 0, bytes.Length)];
            utf8.GetChars(bytes, 0, bytes.Length, caracteres, 0);
            pendiente.Append(caracteres);
            var texto = pendiente.ToString();
            var ultimo = texto.LastIndexOf('\n');
            if (ultimo >= 0)
            {
                foreach (var linea in texto[..ultimo].Split('\n'))
                {
                    lector.Procesar(linea.TrimEnd('\r'));
                }

                pendiente.Clear().Append(texto[(ultimo + 1)..]);
            }

            return archivo.Length;
        }
        catch (IOException)
        {
            return posicion;
        }
    }
}
