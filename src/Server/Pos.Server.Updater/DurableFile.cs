using System.Text;

namespace Pos.Server.Updater;

/// <summary>
/// M2 (ronda 3): escritura que sobrevive a un corte de luz. Se escribe un archivo temporal, se fuerza al disco
/// (<see cref="FileStream.Flush(bool)"/> con <c>flushToDisk: true</c>) y recién entonces se reemplaza el archivo con un renombrado. Si se
/// corta la luz queda el archivo anterior completo o el nuevo completo, nunca uno vacío o a medias.
/// </summary>
internal static class DurableFile
{
    public static void WriteAllText(string path, string content)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
