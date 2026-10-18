using System.Text;

namespace Pos.Installer.Setup.Logica;

/// <summary>
/// Lee por partes un archivo de texto UTF-8 que otro proceso sigue escribiendo (el registro de Inno). Recuerda la posición EXACTA hasta
/// donde leyó (no la longitud del archivo al terminar, que puede haber crecido mientras tanto: se saltaban bytes) y usa un decodificador
/// con estado, así una letra con tilde partida entre dos lecturas no se pierde ni se corrompe. Entrega solo líneas completas.
/// </summary>
public sealed class LectorIncremental
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Decoder _utf8 = Utf8.GetDecoder();
    private readonly StringBuilder _pendiente = new();

    /// <summary>Bytes ya leídos desde el inicio del archivo.</summary>
    public long Posicion { get; private set; }

    /// <summary>Lee desde <see cref="Posicion"/> hasta el final actual del flujo y devuelve las líneas completas nuevas (sin <c>\r</c>).</summary>
    public IReadOnlyList<string> Leer(Stream flujo)
    {
        ArgumentNullException.ThrowIfNull(flujo);
        if (flujo.Length < Posicion)
        {
            // El archivo se reemplazó por uno más corto: se empieza de nuevo.
            Posicion = 0;
            _pendiente.Clear();
            _utf8.Reset();
        }

        flujo.Seek(Posicion, SeekOrigin.Begin);
        var bloque = new byte[8192];
        var caracteres = new char[Utf8.GetMaxCharCount(bloque.Length) + 4];
        int leidos;
        while ((leidos = flujo.Read(bloque, 0, bloque.Length)) > 0)
        {
            Posicion += leidos;
            // flush: false: los bytes de una letra incompleta quedan en el decodificador hasta la próxima lectura.
            var n = _utf8.GetChars(bloque, 0, leidos, caracteres, 0, flush: false);
            _pendiente.Append(caracteres, 0, n);
        }

        var texto = _pendiente.ToString();
        var ultimo = texto.LastIndexOf('\n');
        if (ultimo < 0)
        {
            return [];
        }

        _pendiente.Clear().Append(texto, ultimo + 1, texto.Length - ultimo - 1);
        return [.. texto[..ultimo].Split('\n').Select(l => l.TrimEnd('\r'))];
    }
}
