using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Identity.Contracts;
using Pos.Server.IntegrationTests.Phase2;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase3;

/// <summary>Entrada, sesiones, bloqueo y reglas RN-SEC (docs/fases/fase-03-propuesta.md §5 y §15).</summary>
public class AuthenticationTests
{
    private static readonly string[] ManagerPermissions = ["identity.user.manage", "identity.user.view", "identity.role.manage", "identity.permission.view"];
    private static readonly string[] CompanyManage = ["organization.company.manage"];
    private static readonly string[] BranchView = ["organization.branch.view"];

    [Fact]
    public async Task Credenciales_incorrectas_no_revelan_si_el_usuario_existe()
    {
        await using var factory = new PosServerFactory();
        await SecurityScenario.CreateAsync(factory);
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "no.existe", password = "Cualquier-Clave-1" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.INVALID_CREDENTIALS");
        await client.PostAsJsonAsync("/api/v1/auth/login", new { username = OwnerUsername, password = "Clave-Incorrecta-1" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Cinco_intentos_fallidos_bloquean_y_el_administrador_desbloquea()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var userId = await scenario.CreateUserAsync("bodeguero", "INVENTORY");
        var client = factory.CreateClient();

        for (var i = 0; i < 4; i++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "bodeguero", password = "Mala-Clave-00" }, Json, Ct)
                .ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.INVALID_CREDENTIALS");
        }

        await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "bodeguero", password = "Mala-Clave-00" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.USER_LOCKED");
        await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "bodeguero", password = SecurityScenario.UserPassword }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.USER_LOCKED");

        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, "SELECT count(*) FROM audit.audit_log WHERE action = 'USER_LOCKED'")).ShouldBe(1);
        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, "SELECT count(*) FROM identity.login_attempts WHERE NOT succeeded")).ShouldBeGreaterThanOrEqualTo(6);

        (await scenario.Owner.PostAsync($"/api/v1/identity/users/{userId}/unlock", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await LoginAsync(client, "bodeguero", SecurityScenario.UserPassword);
    }

    [Fact]
    public async Task Un_usuario_nuevo_debe_cambiar_su_contrasena_antes_de_trabajar()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("contador", "ACCOUNTANT");
        var client = factory.CreateClient();

        var login = await LoginAsync(client, "contador", SecurityScenario.UserPassword);
        login.User.MustChangePassword.ShouldBeTrue();
        (await client.GetAsync("/api/v1/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.GetAsync("/api/v1/organization/company", Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PASSWORD_CHANGE_REQUIRED");

        await client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = SecurityScenario.UserPassword, newPassword = SecurityScenario.UserPassword }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.PASSWORD_REUSED");
        await client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = SecurityScenario.UserPassword, newPassword = "123456789" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.PASSWORD_TOO_COMMON");
        (await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = SecurityScenario.UserPassword, newPassword = SecurityScenario.ChangedPassword }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.GetAsync("/api/v1/organization/company", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await GetAsync<MeDto>(client, "/api/v1/auth/me");
        me.Permissions.ShouldContain("audit.log.view");
        me.Permissions.ShouldNotContain("organization.company.manage");
    }

    [Fact]
    public async Task Salir_y_desactivar_cierran_las_sesiones_de_inmediato()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var userId = await scenario.CreateUserAsync("supervisora", "CASH_SUPERVISOR");
        var first = await scenario.LocalClientAsync("supervisora");
        var second = factory.CreateClient();
        await LoginAsync(second, "supervisora", SecurityScenario.ChangedPassword);

        (await first.PostAsync("/api/v1/auth/logout", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await first.GetAsync("/api/v1/auth/me", Ct).ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.REQUIRED");
        (await second.GetAsync("/api/v1/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await scenario.Owner.PostAsync($"/api/v1/identity/users/{userId}/deactivate", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await second.GetAsync("/api/v1/auth/me", Ct).ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.REQUIRED");
        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { username = "supervisora", password = SecurityScenario.ChangedPassword }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Nadie_se_modifica_a_si_mismo_ni_deja_la_empresa_sin_administrador()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);

        await scenario.Owner.PostAsync($"/api/v1/identity/users/{scenario.Setup.OwnerUserId}/deactivate", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.SELF_MODIFICATION");

        // Un gerente con permisos de administración de usuarios (sin ser administrador) no puede dejar sin Propietario.
        var managerRole = await scenario.Owner.PostAsJsonAsync(
            "/api/v1/identity/roles",
            new { code = "GERENTE", name = "Gerente", permissions = ManagerPermissions },
            Json,
            Ct);
        managerRole.StatusCode.ShouldBe(HttpStatusCode.Created);
        await scenario.CreateUserAsync("gerente", "GERENTE");
        var manager = await scenario.LocalClientAsync("gerente");

        await manager.PostAsync($"/api/v1/identity/users/{scenario.Setup.OwnerUserId}/deactivate", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "IDENTITY.LAST_ADMINISTRATOR");

        // RN-SEC-05: no puede asignar un rol con permisos que él no tiene, ni crear uno.
        var target = await scenario.CreateUserAsync("nuevo", null);
        await manager.PutAsJsonAsync($"/api/v1/identity/users/{target}/roles", new[] { new { roleId = await scenario.RoleIdAsync("ADMIN"), branchId = (Guid?)null } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.PRIVILEGE_ESCALATION");
        await manager.PostAsJsonAsync("/api/v1/identity/roles", new { code = "SUPER", name = "Súper", permissions = CompanyManage }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.PRIVILEGE_ESCALATION");
    }

    [Fact]
    public async Task Una_excepcion_DENY_gana_y_un_rol_de_otra_sucursal_no_aplica()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var s02 = await scenario.Owner.PostAsJsonAsync("/api/v1/organization/branches", new { code = "S02", name = "Norte", municipalityCode = "11001", address = "Cra 7" }, Json, Ct);
        var s02Id = (await s02.Content.ReadFromJsonAsync<Pos.Modules.Organization.Contracts.BranchDetailDto>(Json, Ct))!.Branch.Id;

        var denied = await scenario.CreateUserAsync("negado", "INVENTORY");
        (await scenario.Owner.PutAsJsonAsync($"/api/v1/identity/users/{denied}/overrides",
            new[] { new { permissionCode = "organization.branch.view", effect = "Deny", branchId = (Guid?)null, reason = "Prueba de DENY" } }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var deniedClient = await scenario.LocalClientAsync("negado");
        await deniedClient.GetAsync("/api/v1/organization/branches", Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");

        await scenario.CreateUserAsync("norteno", "INVENTORY", branchId: s02Id);
        var northClient = await scenario.LocalClientAsync("norteno");
        await northClient.GetAsync("/api/v1/organization/branches", Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");
    }

    [Fact]
    public async Task Un_cambio_de_rol_se_aplica_en_la_siguiente_peticion()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var userId = await scenario.CreateUserAsync("cambiante", "CASHIER");
        var client = await scenario.LocalClientAsync("cambiante");
        await client.GetAsync("/api/v1/organization/branches", Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "AUTH.PERMISSION_DENIED");

        (await scenario.Owner.PutAsJsonAsync($"/api/v1/identity/users/{userId}/roles",
            new[] { new { roleId = await scenario.RoleIdAsync("INVENTORY"), branchId = (Guid?)null } }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.GetAsync("/api/v1/organization/branches", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Los_roles_de_sistema_se_clonan_y_no_se_editan()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var cashier = await scenario.RoleIdAsync("CASHIER");

        await scenario.Owner.PutAsJsonAsync($"/api/v1/identity/roles/{cashier}", new { name = "Otro", permissions = Array.Empty<string>() }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "IDENTITY.SYSTEM_ROLE_IMMUTABLE");
        var clone = await scenario.Owner.PostAsJsonAsync($"/api/v1/identity/roles/{cashier}/clone", new { code = "CAJERO_NOCHE", name = "Cajero de noche" }, Json, Ct);
        clone.StatusCode.ShouldBe(HttpStatusCode.Created);
        var cloneId = (await clone.Content.ReadFromJsonAsync<RoleDto>(Json, Ct))!.Id;
        (await scenario.Owner.PutAsJsonAsync($"/api/v1/identity/roles/{cloneId}",
            new { name = "Cajero nocturno", permissions = BranchView }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await scenario.Owner.DeleteAsync($"/api/v1/identity/roles/{cloneId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await scenario.Owner.DeleteAsync($"/api/v1/identity/roles/{cashier}", Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "IDENTITY.SYSTEM_ROLE_IMMUTABLE");
    }

    [Fact]
    public async Task La_auditoria_registra_usuario_sesion_y_quien_hizo_cada_cambio()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var me = await GetAsync<MeDto>(scenario.Owner, "/api/v1/auth/me");

        (await scenario.Owner.PutAsJsonAsync($"/api/v1/organization/branches/{scenario.Setup.BranchId}",
            new { name = "Centro Principal", municipalityCode = "05001", address = "Calle 50" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Phase2.OrganizationApiTests.ScalarAsync<Guid>(factory, "SELECT user_id FROM audit.audit_log WHERE action = 'BRANCH_UPDATED'"))
            .ShouldBe(scenario.Setup.OwnerUserId);
        (await Phase2.OrganizationApiTests.ScalarAsync<Guid>(factory, "SELECT session_id FROM audit.audit_log WHERE action = 'BRANCH_UPDATED'"))
            .ShouldBe(me.SessionId);
        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, "SELECT count(*) FROM audit.audit_log WHERE action = 'LOGIN_SUCCEEDED'"))
            .ShouldBeGreaterThanOrEqualTo(1);
        var verification = await scenario.Owner.PostAsync("/api/v1/audit/verify", null, Ct);
        (await SecurityScenario.ProblemAsync(verification)).GetProperty("isValid").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task El_Propietario_se_crea_solo_desde_el_servidor_y_solo_si_falta()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var owner = new { username = "otro.dueno", displayName = "Otro", password = "Otra-Clave-Segura-9" };

        await factory.CreateClient().PostAsJsonAsync("/api/v1/setup/owner", owner, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "SETUP.OWNER_ALREADY_EXISTS");

        // Instalación sin Propietario activo (como las configuradas en la Fase 2).
        await Phase2.OrganizationApiTests.ScalarAsync<int>(factory,
            $"UPDATE identity.users SET status = 'DISABLED' WHERE id = '{scenario.Setup.OwnerUserId}' RETURNING 1");
        var status = await GetAsync<Pos.Modules.Organization.Contracts.SetupStatusDto>(factory.CreateClient(), "/api/v1/setup/status");
        status.OwnerPending.ShouldBeTrue();
        await factory.CreateClient().GetAsync("/api/v1/organization/company", Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "SETUP.OWNER_REQUIRED");

        (await factory.CreateClient().PostAsJsonAsync("/api/v1/setup/owner", owner, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var client = factory.CreateClient();
        await LoginAsync(client, "otro.dueno", "Otra-Clave-Segura-9");
        (await client.GetAsync("/api/v1/organization/company", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Veinte_ingresos_simultaneos_funcionan()
    {
        await using var factory = new PosServerFactory();
        await SecurityScenario.CreateAsync(factory);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { username = OwnerUsername, password = OwnerPassword }, Json, Ct)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
    }
}
