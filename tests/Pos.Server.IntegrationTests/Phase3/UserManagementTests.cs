using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Identity.Contracts;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase3;

/// <summary>Pantalla de usuarios: PIN propio, restablecer credenciales y último Propietario (docs/fases/pantallas-faltantes-propuesta.md).</summary>
public class UserManagementTests
{
    private static readonly string[] ManagerPermissions = ["identity.user.manage", "identity.user.view", "identity.role.manage", "identity.permission.view"];

    [Fact]
    public async Task Un_usuario_sin_PIN_lo_define_con_su_contrasena_y_luego_lo_cambia_con_el_PIN_actual()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("sinpin", "CASHIER");
        var client = await scenario.LocalClientAsync("sinpin");

        var status = await GetAsync<PinStatusDto>(client, "/api/v1/auth/pin");
        status.PosCode.ShouldBeNull();
        status.HasPin.ShouldBeFalse();
        status.PinLength.ShouldBe(4);

        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "5937", posCode = "321", currentPassword = "Clave-Incorrecta-1" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.CURRENT_PASSWORD_INVALID");
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "5937", currentPassword = SecurityScenario.ChangedPassword }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.POS_CODE_REQUIRED");
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "5937", posCode = OwnerPosCode, currentPassword = SecurityScenario.ChangedPassword }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "IDENTITY.POS_CODE_DUPLICATED");
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "1234", posCode = "321", currentPassword = SecurityScenario.ChangedPassword }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.PIN_TOO_SIMPLE");
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "59371", posCode = "321", currentPassword = SecurityScenario.ChangedPassword }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.INVALID_PIN");

        (await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "5937", posCode = "321", currentPassword = SecurityScenario.ChangedPassword }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        status = await GetAsync<PinStatusDto>(client, "/api/v1/auth/pin");
        status.PosCode.ShouldBe("321");
        status.HasPin.ShouldBeTrue();

        // Con PIN ya definido: se exige el actual y el código de caja no se cambia por esta vía.
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = "0000", newPin = "6048" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.CURRENT_PIN_INVALID");
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = "5937", newPin = "6048", posCode = "654" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.POS_CODE_CHANGE_REQUIRES_ADMIN");
        (await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = "5937", newPin = "6048" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, "SELECT count(*) FROM audit.audit_log WHERE action = 'PIN_CHANGED'")).ShouldBe(2);
        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, "SELECT count(*) FROM audit.audit_log WHERE action = 'PIN_CHANGED' AND summary LIKE '%definió su código de caja (321)%'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task El_propio_PIN_no_se_restablece_desde_Usuarios()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);

        await scenario.Owner.PostAsJsonAsync($"/api/v1/identity/users/{scenario.Setup.OwnerUserId}/reset-pin", new { posCode = "555", pin = "5937" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "IDENTITY.USE_CHANGE_PIN");
        await scenario.Owner.PostAsJsonAsync($"/api/v1/identity/users/{scenario.Setup.OwnerUserId}/reset-password", new { temporaryPassword = "Temporal-Segura-77" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "IDENTITY.USE_CHANGE_PASSWORD");
    }

    [Fact]
    public async Task Restablecer_credenciales_no_permite_apoderarse_de_un_usuario_con_mas_permisos()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        (await scenario.Owner.PostAsJsonAsync("/api/v1/identity/roles", new { code = "GERENTE", name = "Gerente", permissions = ManagerPermissions }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await scenario.CreateUserAsync("gerente", "GERENTE");
        var target = await scenario.CreateUserAsync("auxiliar", null);
        var manager = await scenario.LocalClientAsync("gerente");

        await manager.PostAsJsonAsync($"/api/v1/identity/users/{scenario.Setup.OwnerUserId}/reset-password", new { temporaryPassword = "Temporal-Segura-77" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.CREDENTIAL_ESCALATION");
        await manager.PostAsJsonAsync($"/api/v1/identity/users/{scenario.Setup.OwnerUserId}/reset-pin", new { posCode = OwnerPosCode, pin = "5937" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.CREDENTIAL_ESCALATION");

        (await manager.PostAsJsonAsync($"/api/v1/identity/users/{target}/reset-pin", new { posCode = "222", pin = "5937" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await manager.PostAsJsonAsync($"/api/v1/identity/users/{target}/reset-password", new { temporaryPassword = "Temporal-Segura-77" }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var login = await LoginAsync(factory.CreateClient(), "auxiliar", "Temporal-Segura-77");
        login.User.MustChangePassword.ShouldBeTrue();
        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, "SELECT count(*) FROM audit.audit_log WHERE action IN ('PIN_RESET', 'PASSWORD_RESET')")).ShouldBe(2);
    }

    [Fact]
    public async Task Siempre_queda_un_Propietario_activo_aunque_haya_administradores()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("admin2", "ADMIN");
        var admin = await scenario.LocalClientAsync("admin2");
        var adminRole = await scenario.RoleIdAsync("ADMIN");
        var ownerId = scenario.Setup.OwnerUserId;

        await admin.PostAsync($"/api/v1/identity/users/{ownerId}/deactivate", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "IDENTITY.LAST_OWNER");
        await admin.PutAsJsonAsync($"/api/v1/identity/users/{ownerId}/roles", new[] { new { roleId = adminRole, branchId = (Guid?)null } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "IDENTITY.LAST_OWNER");

        // Con un segundo Propietario activo, ya se le puede quitar el rol al primero.
        await scenario.CreateUserAsync("socia", "OWNER");
        var updated = await admin.PutAsJsonAsync($"/api/v1/identity/users/{ownerId}/roles", new[] { new { roleId = adminRole, branchId = (Guid?)null } }, Json, Ct);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));
        (await updated.Content.ReadFromJsonAsync<UserDto>(Json, Ct))!.Roles.Single().RoleCode.ShouldBe("ADMIN");
    }
}
