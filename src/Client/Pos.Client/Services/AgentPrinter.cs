using System.Net.Http.Json;
using System.Text.Json;

namespace Pos.Client.Services;

/// <summary>
/// Impresión por el agente de la caja (D15-05): la interfaz pide al servidor la impresora configurada para la caja y le entrega al agente
/// local (<c>http://localhost:5490</c>) el tiquete neutro. Si el agente no responde, avisa y la venta queda cobrada (se puede reimprimir).
/// </summary>
public sealed partial class AgentPrinter(ApiClient api)
{
    private static readonly HttpClient Agent = new() { BaseAddress = new Uri("http://localhost:5490/"), Timeout = TimeSpan.FromSeconds(15) };

    private JsonElement? _printer;

    /// <summary>
    /// Imprime un reporte de texto ya formateado al ancho del tiquete (reportes X y Z de la jornada): cada línea va como texto del
    /// documento neutro, con corte al final y sin abrir el cajón.
    /// </summary>
    public Task<string?> PrintTextAsync(Guid terminalId, string title, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Una línea con un valor separado por varios espacios ("Fondo inicial      $300.000") va como columnas: la impresora alinea el valor
        // a la derecha (como texto, el agente juntaba las palabras con un solo espacio y se perdía la alineación).
        var elements = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Select(line => Columnas().Match(line.TrimEnd()) is { Success: true } m
                ? (object)new { type = "columns", left = m.Groups[1].Value, right = m.Groups[2].Value }
                : new { type = "text", text = line.TrimEnd() })
            .Append(new { type = "feed", lines = 2 })
            .ToList();
        var ticket = JsonSerializer.SerializeToElement(new { title, elements, cut = true, openDrawer = false }, ApiClient.Json);
        return PrintAsync(terminalId, ticket);
    }

    /// <summary>Imprime; devuelve un mensaje de error en español o <c>null</c> si salió bien.</summary>
    public async Task<string?> PrintAsync(Guid terminalId, JsonElement ticket)
    {
        try
        {
            _printer ??= await api.GetAsync<JsonElement>($"api/v1/organization/terminals/{terminalId}/receipt-printer");
            using var response = await Agent.PostAsJsonAsync("print", new { ticket, printer = _printer }, ApiClient.Json);
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var problem = await response.Content.ReadAsStringAsync();
            return $"La impresora no respondió bien ({(int)response.StatusCode}). {problem}";
        }
        catch (HttpRequestException)
        {
            return "No se pudo hablar con el agente de impresión de esta caja (servicio BusinessPost-TerminalAgent). La venta quedó registrada: reimprima cuando vuelva.";
        }
        catch (TaskCanceledException)
        {
            return "La impresora no respondió a tiempo. La venta quedó registrada: reimprima cuando vuelva.";
        }
    }

    /// <summary>Texto, dos o más espacios y el valor final (el último bloque sin espacios dobles).</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(.*\S)\s{2,}(\S(?:.*\S)?)$")]
    private static partial System.Text.RegularExpressions.Regex Columnas();
}
