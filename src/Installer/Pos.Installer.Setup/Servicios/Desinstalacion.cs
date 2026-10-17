using System.Diagnostics;
using Pos.Installer.Setup.Logica;

namespace Pos.Installer.Setup.Servicios;

/// <summary>Lo que eligió el dueño en la pantalla de desinstalación.</summary>
public sealed record SolicitudDesinstalacion(bool BorrarDatos, string? Usuario, string? Contrasena, string? Motivo, bool EnviarMotivo);

/// <summary>
/// Desinstalación premium (Fase 13-B §8): backup final opcional (con el migrador, como el backup manual), liberar el equipo de la
/// licencia si hay internet y el dueño entra con su usuario (el motivo viaja a la nube solo si lo autoriza), y el desinstalador de Inno
/// en silencio (detiene y quita los servicios, la regla del firewall y los programas; borra los datos solo si se pidió).
/// </summary>
internal sealed class Desinstalacion(Action<object> avisar)
{
    public static object Estado()
    {
        var carpeta = Sistema.CarpetaInstalada();
        return new
        {
            instalado = carpeta is not null,
            carpeta,
            datos = Sistema.Datos,
            version = Sistema.VersionInstalada(),
            equipoPrincipal = carpeta is not null && File.Exists(Path.Combine(carpeta, "app", "current", "migrator", "Pos.Server.Migrator.exe")),
        };
    }

    /// <summary>Backup final cifrado y verificado en Documentos (sobrevive aunque se borren los datos). Devuelve la ruta o el error.</summary>
    public static async Task<Respuesta<string>> BackupFinalAsync(CancellationToken cancellationToken)
    {
        var carpeta = Sistema.CarpetaInstalada();
        var migrador = carpeta is null ? null : Path.Combine(carpeta, "app", "current", "migrator", "Pos.Server.Migrator.exe");
        if (migrador is null || !File.Exists(migrador))
        {
            return new(null, "En este equipo no hay datos de la tienda para respaldar (es una caja adicional).");
        }

        var destino = Sistema.CarpetaBackupFinal();
        Directory.CreateDirectory(destino);
        var inicio = new ProcessStartInfo(migrador)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "backup", "--kind", "MANUAL", "--output", destino, "--pg-bin", Path.Combine(carpeta!, "pgsql", "bin") },
        };
        using var proceso = Process.Start(inicio)!;
        var salida = proceso.StandardOutput.ReadToEndAsync(cancellationToken);
        var errores = proceso.StandardError.ReadToEndAsync(cancellationToken);
        await proceso.WaitForExitAsync(cancellationToken);
        _ = await salida + await errores;
        var archivo = new DirectoryInfo(destino).GetFiles("*.posbak").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        return proceso.ExitCode == 0 && archivo is not null
            ? new(archivo.FullName)
            : new(null, "No se pudo hacer el backup final. Puedes desinstalar igual; tus datos se conservan si no marcas \"Borrar también los datos\".");
    }

    public async Task EjecutarAsync(SolicitudDesinstalacion solicitud, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        var avisos = new List<string>();

        if (!string.IsNullOrWhiteSpace(solicitud.Usuario) && !string.IsNullOrEmpty(solicitud.Contrasena))
        {
            avisar(new { evento = "progreso", porcentaje = 15, nota = "Liberando este equipo de la licencia…" });
            using var servidor = new ServidorLocal();
            var entrada = await servidor.EntrarAsync(solicitud.Usuario.Trim(), solicitud.Contrasena, cancellationToken);
            var motivo = solicitud.EnviarMotivo && !string.IsNullOrWhiteSpace(solicitud.Motivo)
                ? $"Desinstalado: {solicitud.Motivo.Trim()}"
                : "Desinstalado con el instalador de BusinessPost";
            var liberado = entrada.Ok ? await servidor.LiberarEquipoAsync(motivo, cancellationToken) : null;
            if (liberado is not { Ok: true })
            {
                avisos.Add($"No se pudo liberar el equipo de la licencia ({(entrada.Ok ? liberado?.Mensaje : entrada.Mensaje)}). Si vas a usar la clave en otro computador, libéralo desde tu portal de cliente.");
            }
        }

        var desinstalador = Sistema.DesinstaladorInno();
        if (desinstalador is null)
        {
            avisar(new { evento = "error", mensaje = "No se encontró el desinstalador de BusinessPost. Desinstala desde Configuración → Aplicaciones." });
            return;
        }

        avisar(new { evento = "progreso", porcentaje = 40, nota = "Deteniendo BusinessPost y quitando los programas…" });
        var registro = Path.Combine(Sistema.CarpetaTemporal(), $"desinstalacion-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        using (var proceso = Process.Start(new ProcessStartInfo(desinstalador, ArgumentosInno.Desinstalar(solicitud.BorrarDatos, registro)) { UseShellExecute = false })!)
        {
            await proceso.WaitForExitAsync(cancellationToken);
        }

        // El desinstalador de Inno sigue en otro proceso (se copia a la carpeta temporal): se espera a que quite la instalación.
        for (var i = 0; i < 300 && Sistema.SigueInstalado(); i++)
        {
            avisar(new { evento = "progreso", porcentaje = Math.Min(95, 45 + i), nota = "Quitando los programas…" });
            await Task.Delay(1000, cancellationToken);
        }

        if (Sistema.SigueInstalado())
        {
            avisar(new { evento = "error", mensaje = "La desinstalación no terminó. Reinicia el computador e intenta de nuevo desde Configuración → Aplicaciones.", registro });
            return;
        }

        avisar(new { evento = "terminado", borrados = solicitud.BorrarDatos, datos = Sistema.Datos, avisos });
    }
}
