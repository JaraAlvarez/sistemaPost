using System.Drawing;
using System.Text.Json;

namespace Pos.Desktop;

/// <summary>
/// Tamaño, posición, maximizada y pantalla completa de la ventana, por usuario de Windows, en
/// <c>%LOCALAPPDATA%\BusinessPost\desktop.json</c>.
/// </summary>
internal sealed class PreferenciasVentana
{
    public const int AnchoMinimo = 800;
    public const int AltoMinimo = 560;

    private static readonly JsonSerializerOptions Formato = new() { WriteIndented = true };

    public int X { get; set; }
    public int Y { get; set; }
    public int Ancho { get; set; } = 1280;
    public int Alto { get; set; } = 800;
    public bool Maximizada { get; set; } = true;
    public bool PantallaCompleta { get; set; }

    public static string Carpeta =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusinessPost");

    public static string Archivo => Path.Combine(Carpeta, "desktop.json");

    public static PreferenciasVentana Cargar()
    {
        try
        {
            return File.Exists(Archivo)
                ? JsonSerializer.Deserialize<PreferenciasVentana>(File.ReadAllText(Archivo)) ?? new PreferenciasVentana()
                : new PreferenciasVentana();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PreferenciasVentana();
        }
    }

    public void Guardar()
    {
        try
        {
            Directory.CreateDirectory(Carpeta);
            File.WriteAllText(Archivo, JsonSerializer.Serialize(this, Formato));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Registro.Escribir($"No se pudieron guardar las preferencias de la ventana: {ex.Message}");
        }
    }

    /// <summary>
    /// Rectángulo donde abrir la ventana: el guardado si al menos 100×100 px de él caen en alguna pantalla actual (pudo desconectarse un
    /// monitor), con el tamaño mínimo; si no, centrado en la pantalla principal. Función pura, con pruebas.
    /// </summary>
    public static Rectangle Ajustar(Rectangle guardado, IReadOnlyList<Rectangle> pantallas, Rectangle principal)
    {
        ArgumentNullException.ThrowIfNull(pantallas);
        var ancho = Math.Clamp(guardado.Width, AnchoMinimo, Math.Max(AnchoMinimo, principal.Width));
        var alto = Math.Clamp(guardado.Height, AltoMinimo, Math.Max(AltoMinimo, principal.Height));
        var propuesto = new Rectangle(guardado.X, guardado.Y, ancho, alto);
        var visible = pantallas.Any(p =>
        {
            var comun = Rectangle.Intersect(p, propuesto);
            return comun.Width >= 100 && comun.Height >= 100;
        });
        if (visible && guardado.Width > 0 && guardado.Height > 0)
            return propuesto;

        ancho = Math.Min(ancho, principal.Width);
        alto = Math.Min(alto, principal.Height);
        return new Rectangle(principal.X + (principal.Width - ancho) / 2, principal.Y + (principal.Height - alto) / 2, ancho, alto);
    }
}
