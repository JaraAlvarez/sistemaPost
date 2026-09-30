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
