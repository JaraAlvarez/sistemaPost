using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Host.Security;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Host.Components.Shared;

/// <summary>Base de las pantallas interactivas: ejecuta casos de uso (cada uno en su propio ámbito) y muestra el resultado.</summary>
public abstract class PortalPageBase : ComponentBase
{
    private static readonly TimeZoneInfo Colombia = BusinessTimeZones.Colombia;

    [Inject]
    protected ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    internal PortalOperations Operations { get; set; } = default!;

    /// <summary>Último error al cargar (se muestra en la página).</summary>
    protected string? LoadError { get; private set; }

    protected bool Busy { get; private set; }

    /// <summary>Fecha y hora en la zona del negocio (Colombia).</summary>
    public static string Date(DateTimeOffset? value) =>
        value is { } v ? TimeZoneInfo.ConvertTime(v, Colombia).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    public static string Day(DateTimeOffset? value) =>
        value is { } v ? TimeZoneInfo.ConvertTime(v, Colombia).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—";

    public static string StatusName(string? status) => status switch
    {
        "TRIAL" => "En prueba",
        "ACTIVE" => "Activa",
        "PAST_DUE" => "En gracia",
        "SUSPENDED" => "Suspendida",
        "CANCELLED" => "Cancelada",
        "EXPIRED" => "Vencida",
        "REVOKED" => "Revocada",
        "RELEASED" => "Liberada",
        "INACTIVE" => "Inactiva",
        null => "—",
        _ => status,
    };

    public static Color StatusColor(string? status) => status switch
    {
        "ACTIVE" => Color.Success,
        "TRIAL" => Color.Info,
        "PAST_DUE" => Color.Warning,
        "SUSPENDED" or "EXPIRED" or "REVOKED" => Color.Error,
        _ => Color.Default,
    };

    public static string EditionName(string? edition) => edition switch
    {
        "SINGLE" => "Caja Única",
        "MULTI" => "Multicaja",
        _ => edition ?? "—",
    };

    protected async Task<T?> LoadAsync<T>(IRequest<Result<T>> query)
    {
        var result = await Operations.SendAsync(query);
        LoadError = result.IsFailure ? result.Error.Message : null;
        return result.IsSuccess ? result.Value : default;
    }

    /// <summary>Ejecuta un comando y avisa el resultado. Devuelve <c>true</c> si se completó.</summary>
    protected async Task<bool> RunAsync(IRequest<Result> command, string successMessage)
    {
        Busy = true;
        try
        {
            var result = await Operations.SendAsync(command);
            Notify(result, successMessage);
            return result.IsSuccess;
        }
        finally
        {
            Busy = false;
        }
    }

    protected async Task<Result<T>> RunAsync<T>(IRequest<Result<T>> command, string successMessage)
    {
        Busy = true;
        try
        {
            var result = await Operations.SendAsync(command);
            Notify(result, successMessage);
            return result;
        }
        finally
        {
            Busy = false;
        }
    }

    private void Notify(Result result, string successMessage)
    {
        if (result.IsSuccess)
        {
            Snackbar.Add(successMessage, Severity.Success);
            return;
        }

        var detail = result.Error.FieldErrors.Count > 0 ? $" ({string.Join("; ", result.Error.FieldErrors.Select(f => f.Message))})" : string.Empty;
        Snackbar.Add($"{result.Error.Message}{detail} [{result.Error.Code}]", Severity.Error);
    }
}
