using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Identity.Contracts;
using Pos.Server.IntegrationTests.Phase2;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase3;

/// <summary>Emparejamiento de equipos, HTTPS en la LAN, entrada en caja y autorización de supervisor (D3-04, D3-05, D3-07).</summary>
public class DeviceAndSupervisorTests
{
    [Fact]
    public async Task Desde_la_LAN_solo_equipos_emparejados_y_por_HTTPS()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);

        await scenario.RemoteClient(https: false).GetAsync("/api/v1/reference/departments", Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "SECURITY.HTTPS_REQUIRED");
        await scenario.RemoteClient().GetAsync("/api/v1/reference/departments", Ct)
            .ShouldFailWithAsync(HttpStatusCode.Unauthorized, "DEVICE.NOT_PAIRED");
        await scenario.RemoteClient().PostAsJsonAsync("/api/v1/auth/login", new { username = OwnerUsername, password = OwnerPassword }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Unauthorized, "DEVICE.NOT_PAIRED");
        (await scenario.RemoteClient().GetAsync("/api/v1/system/info", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var forged = scenario.RemoteClient();
        forged.DefaultRequestHeaders.Add("X-Device-Id", Guid.CreateVersion7().ToString());
        forged.DefaultRequestHeaders.Add("X-Device-Secret", "inventado");
        await forged.GetAsync("/api/v1/reference/departments", Ct).ShouldFailWithAsync(HttpStatusCode.Unauthorized, "DEVICE.INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Emparejar_una_caja_entrar_con_PIN_y_revocar_el_equipo()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);

        var (device, paired) = await scenario.PairTerminalAsync(scenario.Setup.PosTerminalId);
        paired.PosTerminalId.ShouldBe(scenario.Setup.PosTerminalId);
        paired.CertificateFingerprint!.ShouldMatch("^[0-9a-f]{64}$");

        // Un PIN no vale desde el servidor Multicaja (no es una caja).
        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/pos-login", new { posCode = OwnerPosCode, pin = OwnerPin }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PIN_REQUIRES_TERMINAL");

        var login = await SecurityScenario.PosLoginAsync(device, OwnerPosCode, OwnerPin);
        login.User.SessionKind.ShouldBe("TERMINAL");
        login.User.PosTerminalId.ShouldBe(scenario.Setup.PosTerminalId);
        (await device.GetAsync("/api/v1/organization/company", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // El token de esa sesión no sirve desde otro equipo.
        var stolen = factory.CreateClient();
        stolen.DefaultRequestHeaders.Authorization = device.DefaultRequestHeaders.Authorization;
        await stolen.GetAsync("/api/v1/auth/me", Ct).ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.REQUIRED");

        (await scenario.Owner.PostAsync($"/api/v1/devices/{paired.DeviceId}/revoke", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await device.GetAsync("/api/v1/auth/me", Ct).ShouldFailWithAsync(HttpStatusCode.Unauthorized, "DEVICE.INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Un_codigo_de_emparejamiento_sirve_una_sola_vez()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var code = await scenario.Owner.PostAsJsonAsync("/api/v1/devices/pairing-codes", new { kind = "AdminWorkstation" }, Json, Ct);
        var pairing = (await code.Content.ReadFromJsonAsync<Pos.Modules.Organization.Contracts.PairingCodeDto>(Json, Ct))!;
        var body = new { code = pairing.Code, hostname = "PC-GERENCIA", machineFingerprintHash = new string('b', 64) };

        (await scenario.RemoteClient().PostAsJsonAsync("/api/v1/devices/pair", body, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await scenario.RemoteClient().PostAsJsonAsync("/api/v1/devices/pair", body, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Unauthorized, "DEVICE.INVALID_PAIRING_CODE");
        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, "SELECT count(*) FROM org.devices WHERE kind = 'ADMIN_WORKSTATION'")).ShouldBe(1);
    }

    [Fact]
    public async Task Autorizacion_de_supervisor_de_un_solo_uso()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("cajera", "CASHIER", posCode: "200", pin: "5937");
        var supervisorId = await scenario.CreateUserAsync("supervisor", "CASH_SUPERVISOR", posCode: "300", pin: "7152");
        var (terminal, _) = await scenario.PairTerminalAsync(scenario.Setup.PosTerminalId);
        await SecurityScenario.PosLoginAsync(terminal, "200", "5937");

        // Objetivo: la sesión de backoffice de otro usuario, que la cajera no puede cerrar sin supervisor.
        var target = factory.CreateClient();
        var targetLogin = await LoginAsync(target, OwnerUsername, OwnerPassword);
        var revokeUrl = $"/api/v1/identity/sessions/{targetLogin.User.SessionId}/revoke";

        var denied = await terminal.PostAsync(revokeUrl, null, Ct);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await SecurityScenario.ProblemAsync(denied);
        problem.GetProperty("code").GetString().ShouldBe("AUTH.AUTHORIZATION_REQUIRED");
        var action = problem.GetProperty("action").GetString()!;

        // La cajera no se autoriza a sí misma; un PIN equivocado no autoriza.
        await terminal.PostAsJsonAsync("/api/v1/auth/authorizations", Grant("200", "5937", action, targetLogin.User.SessionId), Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.SELF_AUTHORIZATION");
        await terminal.PostAsJsonAsync("/api/v1/auth/authorizations", Grant("300", "0000", action, targetLogin.User.SessionId), Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.INVALID_CREDENTIALS");

        var granted = await terminal.PostAsJsonAsync("/api/v1/auth/authorizations", Grant("300", "7152", action, targetLogin.User.SessionId), Json, Ct);
        granted.StatusCode.ShouldBe(HttpStatusCode.Created, await granted.Content.ReadAsStringAsync(Ct));
        var grant = (await granted.Content.ReadFromJsonAsync<AuthorizationGrantDto>(Json, Ct))!;

        var withGrant = new HttpRequestMessage(HttpMethod.Post, revokeUrl);
        withGrant.Headers.Add("X-Authorization-Grant", grant.GrantId.ToString());
        (await terminal.SendAsync(withGrant, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await target.GetAsync("/api/v1/auth/me", Ct).ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.REQUIRED");

        // Reutilizarla no funciona.
        var reuse = new HttpRequestMessage(HttpMethod.Post, revokeUrl);
        reuse.Headers.Add("X-Authorization-Grant", grant.GrantId.ToString());
        await terminal.SendAsync(reuse, Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.AUTHORIZATION_REQUIRED");

        (await Phase2.OrganizationApiTests.ScalarAsync<Guid>(factory, "SELECT authorized_by FROM audit.audit_log WHERE action = 'SESSION_REVOKED'"))
            .ShouldBe(supervisorId);
    }

    [Fact]
    public async Task Caja_Unica_entra_con_PIN_en_el_propio_equipo_y_no_atiende_la_LAN()
    {
        await using var factory = new SingleTerminalServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);

        var cashier = factory.CreateClient();
        var login = await SecurityScenario.PosLoginAsync(cashier, OwnerPosCode, OwnerPin);
        login.User.PosTerminalId.ShouldBe(scenario.Setup.PosTerminalId);

        await scenario.RemoteClient().GetAsync("/api/v1/system/info", Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "SECURITY.REMOTE_NOT_ALLOWED");
        await scenario.Owner.PostAsJsonAsync("/api/v1/devices/pairing-codes", new { kind = "Terminal", posTerminalId = scenario.Setup.PosTerminalId }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "DEVICE.PAIRING_REQUIRES_MULTI");
    }

    private static object Grant(string code, string pin, string action, Guid target) => new
    {
        supervisorCode = code,
        supervisorPin = pin,
        permissionCode = IdentityPermissions.SessionRevoke,
        action,
        targetId = target,
        targetType = "Session",
        reason = "Cierre de sesión olvidada",
    };
}
