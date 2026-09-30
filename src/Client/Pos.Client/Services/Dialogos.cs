using MudBlazor;
using Pos.Client.Shared;

namespace Pos.Client.Services;

/// <summary>Diálogos comunes de la interfaz (docs/guia-diseno.md §6).</summary>
public static class Dialogos
{
    /// <summary>
    /// Pide confirmar una acción. <paramref name="peligro"/> pinta el botón en rojo (anular, inactivar, cancelar…). Devuelve true si la
    /// persona confirmó.
    /// </summary>
    public static async Task<bool> ConfirmarAsync(this IDialogService dialogs, string titulo, string mensaje, string accion, bool peligro = false)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        var parameters = new DialogParameters<Confirmacion>
        {
            { c => c.Titulo, titulo },
            { c => c.Mensaje, mensaje },
            { c => c.Accion, accion },
            { c => c.Peligro, peligro },
        };
        var dialog = await dialogs.ShowAsync<Confirmacion>(titulo, parameters, new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true, CloseOnEscapeKey = true });
        var result = await dialog.Result;
        return result is { Canceled: false };
    }
}
