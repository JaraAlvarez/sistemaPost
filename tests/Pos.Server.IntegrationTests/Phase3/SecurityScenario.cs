using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Server.IntegrationTests.Phase2;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase3;

/// <summary>Escenario con Propietario, usuarios de prueba y equipos emparejados.</summary>
public sealed class SecurityScenario
{
    public const string UserPassword = "Clave-Inicial-2026";
    public const string ChangedPassword = "Clave-Definitiva-2026";

    private SecurityScenario(PosServerFactory factory, HttpClient owner, SetupResultDto setup)
    {
        Factory = factory;
        Owner = owner;
        Setup = setup;
    }

    public PosServerFactory Factory { get; }

    /// <summary>Cliente local autenticado como Propietario.</summary>
    public HttpClient Owner { get; }

    public SetupResultDto Setup { get; }

    public static async Task<SecurityScenario> CreateAsync(PosServerFactory factory)
    {
        var owner = factory.CreateClient();
        var setup = await SetupAsync(owner);
        return new SecurityScenario(factory, owner, setup);
    }

    /// <summary>Escenario sobre una instalación ya configurada (el Propietario ya autenticado).</summary>
    public static SecurityScenario ForExisting(PosServerFactory factory, HttpClient owner, SetupResultDto setup) => new(factory, owner, setup);

    public async Task<Guid> RoleIdAsync(string code) =>
        (await GetAsync<List<RoleDto>>(Owner, "/api/v1/identity/roles")).Single(r => r.Code == code).Id;

    /// <summary>Crea un usuario con un rol (en todas las sucursales o en una) y devuelve su Id.</summary>
    public async Task<Guid> CreateUserAsync(string username, string? roleCode, string? posCode = null, string? pin = null, Guid? branchId = null)
    {
        var roles = roleCode is null ? Array.Empty<object>() : [new { roleId = await RoleIdAsync(roleCode), branchId }];
        var response = await Owner.PostAsJsonAsync(
            "/api/v1/identity/users",
            new { username, displayName = $"Usuario {username}", password = UserPassword, posCode, pin, roles },
            Json,
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<UserDto>(Json, Ct))!.Id;
    }

    /// <summary>Cliente local autenticado como el usuario (ya con la contraseña definitiva).</summary>
    public async Task<HttpClient> LocalClientAsync(string username)
    {
        var client = Factory.CreateClient();
        var login = await LoginAsync(client, username, UserPassword);
        if (login.User.MustChangePassword)
        {
            var changed = await client.PostAsJsonAsync(
                "/api/v1/auth/change-password", new { currentPassword = UserPassword, newPassword = ChangedPassword }, Json, Ct);
            changed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync(Ct));
        }

        return client;
    }

    /// <summary>Empareja un equipo tipo caja para la caja indicada y devuelve un cliente "remoto" por HTTPS con su credencial.</summary>
    public async Task<(HttpClient Client, PairedDeviceDto Device)> PairTerminalAsync(Guid posTerminalId, string ip = "192.168.1.21")
    {
        var code = await Owner.PostAsJsonAsync("/api/v1/devices/pairing-codes", new { kind = "Terminal", posTerminalId }, Json, Ct);
        code.StatusCode.ShouldBe(HttpStatusCode.Created, await code.Content.ReadAsStringAsync(Ct));
        var pairing = (await code.Content.ReadFromJsonAsync<PairingCodeDto>(Json, Ct))!;

        var remote = RemoteClient(ip);
        var paired = await remote.PostAsJsonAsync(
            "/api/v1/devices/pair",
            new { code = pairing.Code, hostname = "CAJA-01", machineFingerprintHash = new string('a', 64), osVersion = "Windows 11", appVersion = "0.3.0" },
            Json,
            Ct);
        paired.StatusCode.ShouldBe(HttpStatusCode.Created, await paired.Content.ReadAsStringAsync(Ct));
        var device = (await paired.Content.ReadFromJsonAsync<PairedDeviceDto>(Json, Ct))!;
        remote.DefaultRequestHeaders.Add("X-Device-Id", device.DeviceId.ToString());
        remote.DefaultRequestHeaders.Add("X-Device-Secret", device.DeviceSecret);
        return (remote, device);
    }

    /// <summary>Cliente que simula un equipo de la LAN que llega por HTTPS.</summary>
    public HttpClient RemoteClient(string ip = "192.168.1.21", bool https = true)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(PosServerFactory.SimulatedRemoteIpHeader, ip);
        if (https)
        {
            client.DefaultRequestHeaders.Add(PosServerFactory.SimulatedHttpsHeader, "1");
        }

        return client;
    }

    public static async Task<LoginResultDto> PosLoginAsync(HttpClient client, string posCode, string pin)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/pos-login", new { posCode, pin }, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var login = (await response.Content.ReadFromJsonAsync<LoginResultDto>(Json, Ct))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return login;
    }

    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
}
