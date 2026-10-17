using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace Pos.Client.Services;

/// <summary>
/// Filtros de las listas en la dirección (D17C-03): al cambiar un filtro se reemplaza la consulta de la URL sin recargar, para que al volver
/// de un detalle (Atrás o las migas de pan), al recargar o al compartir el enlace, la lista aparezca igual. Los valores vacíos no se escriben.
/// Las páginas leen los valores con <c>[SupplyParameterFromQuery]</c>.
/// </summary>
public static class Filtros
{
    /// <summary>Escribe los filtros en la consulta de la dirección actual (reemplaza la entrada del historial, no agrega una).</summary>
    public static void Actualizar(NavigationManager navigation, IReadOnlyDictionary<string, object?> valores)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(valores);
        var limpios = valores.ToDictionary(v => v.Key, v => (object?)Texto(v.Value), StringComparer.Ordinal);
        var destino = navigation.GetUriWithQueryParameters(limpios);
        if (!string.Equals(destino, navigation.Uri, StringComparison.Ordinal))
        {
            navigation.NavigateTo(destino, replace: true);
        }
    }

    /// <summary>Fecha de la consulta (yyyy-MM-dd) o <c>null</c>.</summary>
    public static DateTime? Fecha(string? valor) =>
        DateTime.TryParseExact(valor, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha) ? fecha : null;

    private static string? Texto(object? valor) => valor switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "true" : null,
        Guid g => g == Guid.Empty ? null : g.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => valor.ToString(),
    };
}
