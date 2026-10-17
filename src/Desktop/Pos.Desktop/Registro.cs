using System.Globalization;

namespace Pos.Desktop;

/// <summary>Registro mínimo para soporte: <c>%LOCALAPPDATA%\BusinessPost\desktop.log</c> (se reinicia al pasar de 1 MB).</summary>
internal static class Registro
{
    private static readonly Lock Candado = new();

    public static void Escribir(string mensaje)
    {
        try
        {
            lock (Candado)
            {
                var archivo = Path.Combine(PreferenciasVentana.Carpeta, "desktop.log");
                Directory.CreateDirectory(PreferenciasVentana.Carpeta);
                if (File.Exists(archivo) && new FileInfo(archivo).Length > 1024 * 1024)
                    File.Delete(archivo);
                File.AppendAllText(archivo,
                    $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)} {mensaje}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mejor esfuerzo: el registro nunca debe impedir abrir la ventana.
        }
    }
}
