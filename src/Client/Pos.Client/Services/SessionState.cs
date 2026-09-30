using System.Globalization;
using System.Text.Json;
using Microsoft.JSInterop;

namespace Pos.Client.Services;

/// <summary>
/// Sesión de la pestaña (D15-04): el token vive solo en memoria (cerrar la pestaña cierra la sesión); la credencial del equipo emparejado se
/// guarda en el almacenamiento del navegador de ESE equipo.
/// </summary>
public sealed class SessionState(IJSRuntime js)
{
    private const string DeviceKey = "pos.device";

    public string? Token { get; private set; }

    public MeInfo? Me { get; private set; }

    public DeviceCredential? Device { get; private set; }

    public SetupStatus? Setup { get; set; }

    public bool IsSignedIn => Token is not null && Me is not null;

    public bool IsTerminal => Me?.SessionKind == "TERMINAL";

    public bool IsMulti => Setup?.Edition == "MULTI";

    public event Action? Changed;

    public bool Has(string permission) => Me?.Permissions.Contains(permission) == true;

    public void SignIn(LoginResult login)
    {
        ArgumentNullException.ThrowIfNull(login);
        Token = login.Token;
        Me = login.User;
        Changed?.Invoke();
    }

    public void Refresh(MeInfo me)
    {
        Me = me;
        Changed?.Invoke();
    }

    public void SignOut()
    {
        Token = null;
        Me = null;
        Changed?.Invoke();
    }

    public async Task LoadDeviceAsync()
    {
        var json = await js.InvokeAsync<string?>("localStorage.getItem", DeviceKey);
        Device = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<DeviceCredential>(json, ApiClient.Json);
    }

    public async Task SaveDeviceAsync(DeviceCredential device)
    {
        Device = device;
        await js.InvokeVoidAsync("localStorage.setItem", DeviceKey, JsonSerializer.Serialize(device, ApiClient.Json));
        Changed?.Invoke();
    }

    public async Task ForgetDeviceAsync()
    {
        Device = null;
        await js.InvokeVoidAsync("localStorage.removeItem", DeviceKey);
        Changed?.Invoke();
    }
}

/// <summary>Formato de Colombia: pesos sin decimales cuando son enteros, fechas día/mes/año.</summary>
public static class Format
{
    public static readonly CultureInfo Colombia = CultureInfo.GetCultureInfo("es-CO");

    public static string Money(decimal value) =>
        decimal.Truncate(value) == value ? value.ToString("$ #,##0", Colombia) : value.ToString("$ #,##0.00", Colombia);

    public static string Money(decimal? value) => value is { } v ? Money(v) : "—";

    public static string Quantity(decimal value) => value.ToString(decimal.Truncate(value) == value ? "#,##0" : "#,##0.###", Colombia);

    public static string Date(DateTimeOffset? value) => value is { } v ? v.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Colombia) : "—";

    public static string Cell(JsonElement value, string type) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.Number when type is "Money" => Money(value.GetDecimal()),
        JsonValueKind.Number when type is "Percent" => value.GetDecimal().ToString("0.##", Colombia) + " %",
        JsonValueKind.Number => Quantity(value.GetDecimal()),
        JsonValueKind.True => "Sí",
        JsonValueKind.False => "No",
        _ => value.ToString(),
    };
}
