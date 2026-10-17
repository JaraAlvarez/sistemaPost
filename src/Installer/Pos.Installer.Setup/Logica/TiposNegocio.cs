namespace Pos.Installer.Setup.Logica;

/// <summary>
/// Tipos de negocio del paso "¿Qué tipo de negocio tienes?" (Fase 7-C parte A). Son los mismos códigos que acepta el asistente inicial del
/// servidor (<c>POST /api/v1/setup</c>, campo <c>businessProfile</c>). No es un plan de precios: solo enciende funciones.
/// </summary>
public static class TiposNegocio
{
    /// <summary>El que queda elegido si la página no envía uno (el producto nació para supermercados).</summary>
    public const string PorDefecto = "SUPERMARKET";

    public static IReadOnlyList<string> Todos { get; } = ["SUPERMARKET", "FRESH_MARKET", "PHARMACY", "APPAREL", "HARDWARE", "OTHER"];

    /// <summary>Código en mayúsculas si es conocido; si no (vacío o alterado), el tipo por defecto.</summary>
    public static string Normalizar(string? tipo)
    {
        var codigo = tipo?.Trim().ToUpperInvariant();
        return codigo is not null && Todos.Contains(codigo, StringComparer.Ordinal) ? codigo : PorDefecto;
    }
}
