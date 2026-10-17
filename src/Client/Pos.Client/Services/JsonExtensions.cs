using System.Text.Json;

namespace Pos.Client.Services;

/// <summary>Lectura cómoda del JSON de la API en las pantallas que solo muestran datos.</summary>
public static class JsonExtensions
{
    public static string Str(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";

    public static decimal Dec(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : 0m;

    public static bool Bool(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static JsonElement Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    /// <summary>La lista de una respuesta: el arreglo mismo o su propiedad <c>items</c> (resultados paginados).</summary>
    public static List<JsonElement> Items(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? [.. element.EnumerateArray()]
        : element.ValueKind == JsonValueKind.Object && element.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? [.. items.EnumerateArray()]
        : [];

    public static DateTimeOffset? Date(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var date)
            ? date : null;
}

/// <summary>Nombres de las categorías del catálogo: la API da <c>path</c> como cadena de ids (<c>/id1/id2/</c>), no de nombres.</summary>
public static class Categorias
{
    /// <summary>"Abarrotes › Granos" a partir de la lista completa de categorías (la de <c>api/v1/catalog/categories</c>).</summary>
    public static string Nombre(IReadOnlyList<JsonElement> todas, JsonElement categoria)
    {
        ArgumentNullException.ThrowIfNull(todas);
        var nombres = todas.Where(c => c.ValueKind == JsonValueKind.Object).ToDictionary(c => c.Str("id"), c => c.Str("name"), StringComparer.OrdinalIgnoreCase);
        var partes = categoria.Str("path").Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => nombres.GetValueOrDefault(id))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
        return partes.Count > 0 ? string.Join(" › ", partes) : categoria.Str("name");
    }

    /// <summary>Nombre de la categoría con ese id, o vacío.</summary>
    public static string Nombre(IReadOnlyList<JsonElement> todas, Guid id)
    {
        ArgumentNullException.ThrowIfNull(todas);
        var categoria = todas.FirstOrDefault(c => c.Prop("id").TryGetGuid(out var g) && g == id);
        return categoria.ValueKind == JsonValueKind.Object ? Nombre(todas, categoria) : string.Empty;
    }
}
