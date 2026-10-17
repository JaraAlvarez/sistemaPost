namespace Pos.Server.Updater;

/// <summary>
/// La ventana propia <c>BusinessPost.exe</c> dentro del paquete de actualización (va en <c>app\{versión}\desktop</c>). Los accesos
/// directos nuevos abren <c>app\current\desktop\BusinessPost.exe</c>: al cambiar el enlace <c>app\current</c> la próxima apertura usa la
/// versión nueva, la vuelta atrás devuelve también la ventana anterior y nunca se sobrescribe un .exe en uso (la ventana abierta sigue
/// corriendo desde la carpeta de su versión). Las instalaciones hechas antes de este cambio tienen los accesos directos (y quizá la
/// ventana anclada a la barra de tareas) en <c>{InstallRoot}\desktop\BusinessPost.exe</c>: esa copia se refresca al cambiar de versión.
/// </summary>
internal static class VentanaEscritorio
{
    public const string Carpeta = "desktop";

    public const string Programa = "BusinessPost.exe";

    private const string SufijoAnterior = ".anterior";

    private const string SufijoNuevo = ".nuevo";

    /// <summary>
    /// Copia los archivos de <c>{versionDirectory}\desktop</c> a la carpeta de compatibilidad <c>{installRoot}\desktop</c>, si existe.
    /// Si la ventana está abierta desde esa carpeta, su .exe se renombra (Windows lo permite con el programa corriendo) y el nuevo queda
    /// en su lugar para la próxima apertura; el renombrado se borra en un cambio posterior, cuando ya nadie lo usa. Mejor esfuerzo:
    /// devuelve los avisos para el registro y nunca lanza (los servicios ya cambiaron de versión).
    /// </summary>
    public static IReadOnlyList<string> RefrescarCopiaAnterior(string installRoot, string versionDirectory)
    {
        var avisos = new List<string>();
        var anterior = Path.Combine(installRoot, Carpeta);
        var nueva = Path.Combine(versionDirectory, Carpeta);
        if (!Directory.Exists(anterior) || !File.Exists(Path.Combine(nueva, Programa)))
        {
            return avisos;
        }

        foreach (var resto in Directory.EnumerateFiles(anterior, "*" + SufijoAnterior).Concat(Directory.EnumerateFiles(anterior, "*" + SufijoNuevo)))
        {
            try
            {
                File.Delete(resto);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sigue abierto: se intenta en el próximo cambio de versión.
            }
        }

        foreach (var origen in Directory.EnumerateFiles(nueva))
        {
            var destino = Path.Combine(anterior, Path.GetFileName(origen));
            try
            {
                if (Iguales(origen, destino))
                {
                    continue;
                }

                File.Copy(origen, destino + SufijoNuevo, overwrite: true);
                if (File.Exists(destino))
                {
                    try
                    {
                        File.Delete(destino);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Abierto (la ventana corriendo): se aparta con otro nombre para dejar el nuevo en su lugar.
                        File.Move(destino, $"{destino}.{DateTime.UtcNow:yyyyMMddHHmmss}{SufijoAnterior}", overwrite: true);
                    }
                }

                File.Move(destino + SufijoNuevo, destino);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                avisos.Add($"No se pudo refrescar {destino}: {ex.Message}");
                try
                {
                    File.Delete(destino + SufijoNuevo);
                }
                catch (Exception borrar) when (borrar is IOException or UnauthorizedAccessException)
                {
                    // Mejor esfuerzo.
                }
            }
        }

        return avisos;
    }

    /// <summary>¿Está abierta la ventana de esa versión? Entonces su carpeta no se borra todavía (se intentará en la próxima limpieza).</summary>
    public static bool EnUso(string versionDirectory)
    {
        var programa = Path.Combine(versionDirectory, Carpeta, Programa);
        if (!File.Exists(programa))
        {
            return false;
        }

        try
        {
            // Un .exe en ejecución no se puede abrir para escribir: no se cambia nada, solo se prueba.
            using var prueba = new FileStream(programa, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool Iguales(string origen, string destino)
    {
        if (!File.Exists(destino))
        {
            return false;
        }

        var (a, b) = (new FileInfo(origen), new FileInfo(destino));
        return a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
    }
}
