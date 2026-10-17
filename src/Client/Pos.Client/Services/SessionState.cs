using System.Globalization;
using System.Text.Json;
using Microsoft.JSInterop;

namespace Pos.Client.Services;

/// <summary>
/// Sesión de la pestaña (D17C-01, reemplaza D15-04): el token y el usuario se guardan en el almacenamiento DE LA PESTAÑA
/// (<c>sessionStorage</c>): recargar (F5) no cierra la sesión; cerrar la pestaña sí, y no se comparte con otras pestañas ni queda en el
/// disco. La credencial del equipo emparejado se guarda en el almacenamiento del navegador de ESE equipo.
/// </summary>
public sealed class SessionState
{
    private const string DeviceKey = "pos.device";
    private const string SessionKey = "pos.session";

    private readonly IJSRuntime js;

    public SessionState(IJSRuntime js)
    {
        this.js = js;
        Restaurar();
    }

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
        Guardar();
        Changed?.Invoke();
    }

    public void Refresh(MeInfo me)
    {
        Me = me;
        Guardar();
        Changed?.Invoke();
    }

    public void SignOut()
    {
        Token = null;
        Me = null;
        Guardar();
        Changed?.Invoke();
    }

    /// <summary>
    /// Ruta local a la que se vuelve después de ingresar (<c>?volver=</c>); solo rutas de esta aplicación ("/admin/…", "/caja"), nunca
    /// otro sitio.
    /// </summary>
    public static string? RutaSegura(string? ruta) =>
        ruta is { Length: > 1 } r && r[0] == '/' && r[1] is not ('/' or '\\') && !r.Contains("://", StringComparison.Ordinal) ? r : null;

    private sealed record Guardada(string Token, MeInfo Me);

    private void Guardar()
    {
        if (js is not IJSInProcessRuntime local)
        {
            return;
        }

        if (Token is null || Me is null)
        {
            local.InvokeVoid("sessionStorage.removeItem", SessionKey);
        }
        else
        {
            local.InvokeVoid("sessionStorage.setItem", SessionKey, JsonSerializer.Serialize(new Guardada(Token, Me), ApiClient.Json));
        }
    }

    private void Restaurar()
    {
        if (js is not IJSInProcessRuntime local)
        {
            return;
        }

        try
        {
            // La sesión es del equipo que la abrió (el servidor lo exige): al recargar, la credencial del equipo emparejado debe estar
            // cargada antes de la primera petición, no solo en la pantalla de ingreso.
            var device = local.Invoke<string?>("localStorage.getItem", DeviceKey);
            Device = string.IsNullOrEmpty(device) ? null : JsonSerializer.Deserialize<DeviceCredential>(device, ApiClient.Json);

            var json = local.Invoke<string?>("sessionStorage.getItem", SessionKey);
            if (!string.IsNullOrEmpty(json) && JsonSerializer.Deserialize<Guardada>(json, ApiClient.Json) is { } guardada)
            {
                Token = guardada.Token;
                Me = guardada.Me;
            }
        }
        catch (JsonException)
        {
            // Formato viejo o dañado: se ingresa de nuevo.
        }
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
