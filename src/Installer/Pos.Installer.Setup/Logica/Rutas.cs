namespace Pos.Installer.Setup.Logica;

/// <summary>Comparaciones de rutas de Windows que no dependen del sistema de archivos.</summary>
public static class Rutas
{
    /// <summary>
    /// ¿<paramref name="ruta"/> está DENTRO de <paramref name="carpeta"/>? Con el separador al final: <c>C:\BusinessPost2\x.exe</c> no está
    /// dentro de <c>C:\BusinessPost</c> (un <c>StartsWith</c> sin separador decía que sí). Sin distinguir mayúsculas.
    /// </summary>
    public static bool EstaDentro(string? ruta, string? carpeta)
    {
        if (string.IsNullOrWhiteSpace(ruta) || string.IsNullOrWhiteSpace(carpeta))
        {
            return false;
        }

        var completa = Path.GetFullPath(ruta);
        var base_ = Path.GetFullPath(carpeta);
        if (!base_.EndsWith(Path.DirectorySeparatorChar))
        {
            base_ += Path.DirectorySeparatorChar;
        }

        return completa.StartsWith(base_, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Nombre de la copia temporal con la que se desinstala (se borra al cerrar).</summary>
    public const string PrefijoCopiaDesinstalar = "BusinessPost-Desinstalar-";

    /// <summary>Archivo temporal con el código de recuperación que lee (y borra) el paquete Inno.</summary>
    public const string PrefijoArchivoCodigo = "recuperacion-";
}
