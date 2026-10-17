namespace Pos.Installer.Setup.Logica;

/// <summary>
/// ‑‑maqueta ‑‑pantalla X ‑‑capturar archivo.png [‑‑esperar ms]: abre la pantalla con datos de ejemplo, guarda una imagen PNG de la
/// ventana y se cierra. Solo para las capturas del manual de instalación (docs/imagenes/instalacion). Sin ‑‑maqueta se ignora: nunca
/// instala ni desinstala nada.
/// </summary>
internal sealed record ArgumentosCaptura(string Archivo, int EsperaMs)
{
    public const int EsperaPorDefecto = 2500;
    public const int EsperaMaxima = 60_000;

    public static ArgumentosCaptura? Leer(IReadOnlyList<string> args)
    {
        if (!args.Contains("--maqueta", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var archivo = Valor(args, "--capturar");
        if (string.IsNullOrWhiteSpace(archivo) || !archivo.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var espera = int.TryParse(Valor(args, "--esperar"), out var ms) ? Math.Clamp(ms, 0, EsperaMaxima) : EsperaPorDefecto;
        return new ArgumentosCaptura(Path.GetFullPath(archivo), espera);
    }

    private static string? Valor(IReadOnlyList<string> args, string nombre)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], nombre, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
