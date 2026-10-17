namespace Pos.Infrastructure.Backup;

/// <summary>
/// Código de recuperación entregado en un ARCHIVO temporal (instalador → migrador): así no aparece en la línea de comandos del proceso
/// ni en el registro de la instalación. Se lee una vez y se borra enseguida.
/// </summary>
public static class RecoveryCodeFile
{
    /// <summary>Lee el código (sin espacios alrededor) y borra el archivo. <c>Deleted</c> es <c>false</c> si no se pudo borrar.</summary>
    public static (string Code, bool Deleted) ReadAndDelete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No se encontró el archivo con el código de recuperación.", path);
        }

        string code;
        var deleted = false;
        try
        {
            code = File.ReadAllText(path).Trim();
        }
        finally
        {
            // Se borra aunque la lectura falle: el código no debe quedar en disco.
            deleted = TryDelete(path);
        }

        return (code, deleted);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
