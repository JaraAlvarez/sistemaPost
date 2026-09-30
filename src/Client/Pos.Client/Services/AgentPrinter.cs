using System.Net.Http.Json;
using System.Text.Json;

namespace Pos.Client.Services;

/// <summary>
/// Impresión por el agente de la caja (D15-05): la interfaz pide al servidor la impresora configurada para la caja y le entrega al agente
/// local (<c>http://localhost:5490</c>) el tiquete neutro. Si el agente no responde, avisa y la venta queda cobrada (se puede reimprimir).
/// </summary>
public sealed class AgentPrinter(ApiClient api)
{
    private static readonly HttpClient Agent = new() { BaseAddress = new Uri("http://localhost:5490/"), Timeout = TimeSpan.FromSeconds(15) };

    private JsonElement? _printer;

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
}
