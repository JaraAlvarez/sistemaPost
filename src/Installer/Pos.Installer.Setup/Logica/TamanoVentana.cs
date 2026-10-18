namespace Pos.Installer.Setup.Logica;

/// <summary>
/// Tamaño de la ventana del instalador. La página está diseñada para 960×600 píxeles CSS; con el escalado de Windows (125 %, 150 %…) la
/// ventana crece en la misma proporción. En una pantalla pequeña (p. ej. 1366×768 al 125 %: 1200×750 en un área útil de ~720 de alto
/// por la barra de tareas) la ventana no cabía y los botones de abajo ("Continuar") quedaban fuera de la pantalla. Ahora, si no cabe en
/// el área útil del monitor, se achica y la página se dibuja con un zoom menor (sigue viéndose completa, solo más pequeña).
/// </summary>
public static class TamanoVentana
{
    /// <summary>Tamaño de diseño de la página, en píxeles CSS.</summary>
    public const int Ancho = 960;

    /// <inheritdoc cref="Ancho"/>
    public const int Alto = 600;

    /// <summary>Margen libre alrededor de la ventana cuando hay que achicarla (píxeles físicos).</summary>
    public const int Margen = 16;

    /// <summary>Zoom mínimo: por debajo la letra ya no se lee bien (la página igual se desplaza por dentro si hiciera falta).</summary>
    public const double ZoomMinimo = 0.6;

    /// <summary>
    /// Tamaño en píxeles físicos y zoom de la página (1 = sin achicar) para la escala de Windows <paramref name="escala"/> (1,25 = 125 %)
    /// y el área útil del monitor.
    /// </summary>
    public static (int Ancho, int Alto, double Zoom) Calcular(double escala, int anchoArea, int altoArea)
    {
        if (escala <= 0)
        {
            escala = 1;
        }

        var ideal = (Ancho: Ancho * escala, Alto: Alto * escala);
        var zoom = 1.0;
        if (anchoArea > 0 && altoArea > 0)
        {
            zoom = Math.Min(1.0, Math.Min((anchoArea - Margen) / ideal.Ancho, (altoArea - Margen) / ideal.Alto));
            zoom = Math.Max(ZoomMinimo, zoom);
        }

        zoom = Math.Round(zoom, 3);
        return ((int)Math.Round(ideal.Ancho * zoom), (int)Math.Round(ideal.Alto * zoom), zoom);
    }
}
