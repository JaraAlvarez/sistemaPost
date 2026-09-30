using MudBlazor;

namespace Pos.Cloud.Host.Components.Shared;

/// <summary>Diálogos de confirmación uniformes del portal (<see cref="ConfirmDialog"/>).</summary>
public static class PortalDialogs
{
    private static readonly DialogOptions Options = new()
    {
        MaxWidth = MaxWidth.Small, FullWidth = true, CloseButton = true, CloseOnEscapeKey = true, BackdropClick = false,
    };

    /// <summary>Pide confirmar una acción. Devuelve <c>true</c> si la persona confirmó.</summary>
    public static async Task<bool> ConfirmAsync(
        this IDialogService dialogs, string title, string message, string confirmText, Color color = Color.Error, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        return await ShowAsync(dialogs, title, message, confirmText, color, detail, askReason: false, reasonLabel: null, initialReason: null) is not null;
    }

    /// <summary>Pide confirmar una acción con su motivo. Devuelve el motivo, o <c>null</c> si se canceló.</summary>
    public static Task<string?> ConfirmWithReasonAsync(
        this IDialogService dialogs, string title, string message, string confirmText, string reasonLabel, Color color = Color.Error,
        string? detail = null, string? initialReason = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        return ShowAsync(dialogs, title, message, confirmText, color, detail, askReason: true, reasonLabel, initialReason);
    }

    private static async Task<string?> ShowAsync(
        IDialogService dialogs, string title, string message, string confirmText, Color color, string? detail, bool askReason, string? reasonLabel,
        string? initialReason)
    {
        var parameters = new DialogParameters<ConfirmDialog>
        {
            { d => d.Title, title },
            { d => d.Message, message },
            { d => d.Detail, detail },
            { d => d.ConfirmText, confirmText },
            { d => d.Color, color },
            { d => d.Icon, color == Color.Error ? Icons.Material.Filled.ReportGmailerrorred : Icons.Material.Filled.WarningAmber },
            { d => d.AskReason, askReason },
            { d => d.ReasonLabel, reasonLabel ?? "Motivo" },
            { d => d.InitialReason, initialReason },
        };
        var reference = await dialogs.ShowAsync<ConfirmDialog>(title, parameters, Options);
        var result = await reference.Result;
        return result is { Canceled: false } ? result.Data as string ?? string.Empty : null;
    }
}
