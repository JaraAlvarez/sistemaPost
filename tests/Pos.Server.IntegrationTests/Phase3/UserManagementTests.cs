using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Identity.Contracts;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase3;

/// <summary>
/// Pantalla de usuarios: PIN propio, restablecer credenciales, jerarquía (nadie gestiona a quien tiene más poder) y último Propietario
/// (docs/fases/pantallas-faltantes-propuesta.md).
/// </summary>
public class UserManagementTests
{
    private const string LicenseManage = "licensing.license.manage";

    private static readonly string[] UserView = ["identity.user.view"];

    private static readonly string[] AdministrationPermissions = ["identity.role.manage", "identity.user.manage", "identity.session.revoke"];

    private static readonly string[] ManagerPermissions = ["identity.user.manage", "identity.user.view", "identity.role.manage", "identity.permission.view"];

    private const string ActiveOwnersSql = """
        SELECT count(DISTINCT u.id) FROM identity.users u
        JOIN identity.user_roles ur ON ur.user_id = u.id AND ur.branch_id IS NULL
        JOIN identity.roles r ON r.id = ur.role_id AND r.code = 'OWNER'
        WHERE u.kind = 'HUMAN' AND u.status <> 'DISABLED'
        """;

    [Fact]
    public async Task Un_usuario_sin_PIN_lo_define_con_su_contrasena_y_luego_lo_cambia_con_el_PIN_actual()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("sinpin", "CASHIER");
        var client = await scenario.LocalClientAsync("sinpin");

        var status = await GetAsync<CredentialStatusDto>(client, "/api/v1/auth/credentials");
        status.PosCode.ShouldBeNull();
        status.HasPin.ShouldBeFalse();
        status.PinLength.ShouldBe(4);
        status.PasswordMinLength.ShouldBe(8);

        var password = SecurityScenario.ChangedPassword;
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "5937", posCode = "4071", currentPassword = "Clave-Incorrecta-1" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.CURRENT_PASSWORD_INVALID");
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "5937", currentPassword = password }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.POS_CODE_REQUIRED");
        // PU12: el código de caja ya lo usa otro usuario (el Propietario).
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "5937", posCode = OwnerPosCode, currentPassword = password }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "IDENTITY.POS_CODE_DUPLICATED");
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "1234", posCode = "4071", currentPassword = password }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.PIN_TOO_SIMPLE");
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "2580", posCode = "4071", currentPassword = password }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.PIN_TOO_COMMON");
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "4071", posCode = "4071", currentPassword = password }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.PIN_EQUALS_POS_CODE");
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "59371", posCode = "4071", currentPassword = password }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.INVALID_PIN");

        (await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { newPin = "5937", posCode = "4071", currentPassword = password }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        status = await GetAsync<CredentialStatusDto>(client, "/api/v1/auth/credentials");
        status.PosCode.ShouldBe("4071");
        status.HasPin.ShouldBeTrue();

        // Con PIN ya definido: se exige el actual y el código de caja no se cambia por esta vía.
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = "0000", newPin = "6048" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.CURRENT_PIN_INVALID");
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = "5937", newPin = "6048", posCode = "654" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.POS_CODE_CHANGE_REQUIRES_ADMIN");
        (await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = "5937", newPin = "6048" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, "SELECT count(*) FROM audit.audit_log WHERE action = 'PIN_CHANGED'")).ShouldBe(2);
        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory,
            "SELECT count(*) FROM audit.audit_log WHERE action = 'PIN_CHANGED' AND summary LIKE '%definió su código de caja (4071)%'")).ShouldBe(1);
        // PU5: los intentos fallidos con la credencial actual quedan registrados como en el ingreso.
        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, "SELECT count(*) FROM identity.login_attempts WHERE NOT succeeded AND failure_reason = 'BAD_SECRET'"))
            .ShouldBe(2);
    }

    [Fact]
    public async Task Fallar_la_contrasena_actual_al_cambiarla_bloquea_como_el_ingreso()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("olvidadiza", "INVENTORY");
        var client = await scenario.LocalClientAsync("olvidadiza");
        var bad = new { currentPassword = "Clave-Incorrecta-1", newPassword = "Otra-Clave-Nueva-2026" };

        for (var i = 0; i < 4; i++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/change-password", bad, Json, Ct)
                .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.CURRENT_PASSWORD_INVALID");
        }

        await client.PostAsJsonAsync("/api/v1/auth/change-password", bad, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "IDENTITY.CREDENTIAL_LOCKED");
        await client.PostAsJsonAsync("/api/v1/auth/change-password",
                new { currentPassword = SecurityScenario.ChangedPassword, newPassword = "Otra-Clave-Nueva-2026" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "IDENTITY.CREDENTIAL_LOCKED");
        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { username = "olvidadiza", password = SecurityScenario.ChangedPassword }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.USER_LOCKED");
        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, "SELECT count(*) FROM audit.audit_log WHERE action = 'USER_LOCKED'")).ShouldBe(1);
    }

    [Fact]
    public async Task Cambiar_la_contrasena_o_el_PIN_con_exito_reinicia_los_intentos_fallidos()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("distraida", "CASHIER", posCode: "400", pin: "5937");
        var client = await scenario.LocalClientAsync("distraida");
        const string FailedCount = "SELECT failed_login_count::int FROM identity.users WHERE username = 'distraida'";

        for (var i = 0; i < 4; i++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = "Clave-Incorrecta-1", newPassword = "Otra-Clave-Nueva-2026" }, Json, Ct)
                .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.CURRENT_PASSWORD_INVALID");
        }

        (await Phase2.OrganizationApiTests.ScalarAsync<int>(factory, FailedCount)).ShouldBe(4);
        (await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = SecurityScenario.ChangedPassword, newPassword = "Otra-Clave-Nueva-2026" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Phase2.OrganizationApiTests.ScalarAsync<int>(factory, FailedCount)).ShouldBe(0);

        for (var i = 0; i < 2; i++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = "0000", newPin = "6048" }, Json, Ct)
                .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.CURRENT_PIN_INVALID");
        }

        (await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = "5937", newPin = "6048" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Phase2.OrganizationApiTests.ScalarAsync<int>(factory, FailedCount)).ShouldBe(0);

        // Sin el reinicio, este tercer fallo bloquearía el PIN (máximo 3).
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = "0000", newPin = "7152" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.CURRENT_PIN_INVALID");
    }

    [Fact]
    public async Task Fallar_el_PIN_actual_al_cambiarlo_bloquea_el_PIN()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("cajera", "CASHIER", posCode: "300", pin: "5937");
        var client = await scenario.LocalClientAsync("cajera");

        for (var i = 0; i < 2; i++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = "0000", newPin = "6048" }, Json, Ct)
                .ShouldFailWithAsync(HttpStatusCode.BadRequest, "IDENTITY.CURRENT_PIN_INVALID");
        }

        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = "0000", newPin = "6048" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "IDENTITY.CREDENTIAL_LOCKED");
        await client.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = "5937", newPin = "6048" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "IDENTITY.CREDENTIAL_LOCKED");
        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, "SELECT count(*) FROM audit.audit_log WHERE action = 'USER_LOCKED'")).ShouldBe(1);
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
    public async Task Restablecer_el_PIN_o_cambiarlo_cierra_las_sesiones_de_caja()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var cashierId = await scenario.CreateUserAsync("cajero", "CASHIER", posCode: "200", pin: "5937");
        var backoffice = await scenario.LocalClientAsync("cajero");
        var (terminal, _) = await scenario.PairTerminalAsync(scenario.Setup.PosTerminalId);
        await SecurityScenario.PosLoginAsync(terminal, "200", "5937");

        (await scenario.Owner.PostAsJsonAsync($"/api/v1/identity/users/{cashierId}/reset-pin", new { posCode = "200", pin = "6048" }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await terminal.GetAsync("/api/v1/auth/me", Ct).ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.REQUIRED");
        (await backoffice.GetAsync("/api/v1/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await SecurityScenario.PosLoginAsync(terminal, "200", "6048");
        (await backoffice.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = "6048", newPin = "7152" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await terminal.GetAsync("/api/v1/auth/me", Ct).ShouldFailWithAsync(HttpStatusCode.Unauthorized, "AUTH.REQUIRED");
        (await backoffice.GetAsync("/api/v1/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Nadie_gestiona_a_quien_tiene_mas_poder()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        (await scenario.Owner.PostAsJsonAsync("/api/v1/identity/roles", new { code = "GERENTE", name = "Gerente", permissions = ManagerPermissions }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await scenario.CreateUserAsync("gerente", "GERENTE");
        var target = await scenario.CreateUserAsync("auxiliar", null);
        var manager = await scenario.LocalClientAsync("gerente");
        var owner = scenario.Setup.OwnerUserId;

        await manager.PostAsJsonAsync($"/api/v1/identity/users/{owner}/reset-password", new { temporaryPassword = "Temporal-Segura-77" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");
        await manager.PostAsJsonAsync($"/api/v1/identity/users/{owner}/reset-pin", new { posCode = OwnerPosCode, pin = "5937" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");
        // PU3: tampoco editarlo (el correo sirve para recuperar la cuenta), desbloquearlo ni activarlo/inactivarlo.
        await manager.PutAsJsonAsync($"/api/v1/identity/users/{owner}", new { displayName = "Otro", email = "gerente@ejemplo.co", employeeId = (Guid?)null }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");
        await manager.PostAsync($"/api/v1/identity/users/{owner}/unlock", null, Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");
        await manager.PostAsync($"/api/v1/identity/users/{owner}/activate", null, Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");

        (await manager.PostAsJsonAsync($"/api/v1/identity/users/{target}/reset-pin", new { posCode = "222", pin = "5937" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await manager.PostAsJsonAsync($"/api/v1/identity/users/{target}/reset-password", new { temporaryPassword = "Temporal-Segura-77" }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var login = await LoginAsync(factory.CreateClient(), "auxiliar", "Temporal-Segura-77");
        login.User.MustChangePassword.ShouldBeTrue();
        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, "SELECT count(*) FROM audit.audit_log WHERE action IN ('PIN_RESET', 'PASSWORD_RESET')")).ShouldBe(2);
    }

    [Fact]
    public async Task Un_DENY_no_rebaja_al_Propietario_ni_quitarlo_sirve_para_conceder()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("admin2", "ADMIN");
        var admin = await scenario.LocalClientAsync("admin2");
        var owner = scenario.Setup.OwnerUserId;

        // La cadena DENY → reset-password → quitar DENY se corta en el primer paso y también en el segundo.
        await admin.PutAsJsonAsync($"/api/v1/identity/users/{owner}/overrides",
                new[] { new { permissionCode = LicenseManage, effect = "Deny", branchId = (Guid?)null, reason = "Rebajar al dueño" } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");
        await admin.PostAsJsonAsync($"/api/v1/identity/users/{owner}/reset-password", new { temporaryPassword = "Temporal-Segura-77" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");

        // Aunque el Propietario mismo tuviera un DENY (puesto por otro Propietario), su poder se mide sin él.
        await scenario.CreateUserAsync("socia", "OWNER");
        var partner = await scenario.LocalClientAsync("socia");
        (await partner.PutAsJsonAsync($"/api/v1/identity/users/{owner}/overrides",
                new[] { new { permissionCode = LicenseManage, effect = "Deny", branchId = (Guid?)null, reason = "Prueba" } }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await admin.PostAsJsonAsync($"/api/v1/identity/users/{owner}/reset-password", new { temporaryPassword = "Temporal-Segura-77" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");
        await LoginAsync(factory.CreateClient(), OwnerUsername, OwnerPassword);

        // Quitar un DENY de un permiso que uno no tiene equivale a concederlo.
        var cashier = await scenario.CreateUserAsync("cajero", "CASHIER");
        (await scenario.Owner.PutAsJsonAsync($"/api/v1/identity/users/{cashier}/overrides",
                new[] { new { permissionCode = LicenseManage, effect = "Deny", branchId = (Guid?)null, reason = "Nunca" } }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await admin.PutAsJsonAsync($"/api/v1/identity/users/{cashier}/overrides", Array.Empty<object>(), Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.PRIVILEGE_ESCALATION");
        (await admin.PutAsJsonAsync($"/api/v1/identity/users/{cashier}/overrides",
                new[]
                {
                    new { permissionCode = LicenseManage, effect = "Deny", branchId = (Guid?)null, reason = "Nunca" },
                    new { permissionCode = "sales.sale.void", effect = "Deny", branchId = (Guid?)null, reason = "Solo supervisor" },
                },
                Json,
                Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Un_Propietario_degrada_a_otro_pero_un_subordinado_no()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var adminRole = await scenario.RoleIdAsync("ADMIN");
        var ownerId = scenario.Setup.OwnerUserId;
        await scenario.CreateUserAsync("admin2", "ADMIN");
        var admin = await scenario.LocalClientAsync("admin2");

        // PU2: el Administrador no degrada ni inactiva al Propietario.
        await admin.PutAsJsonAsync($"/api/v1/identity/users/{ownerId}/roles", new[] { new { roleId = adminRole, branchId = (Guid?)null } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");
        await admin.PostAsync($"/api/v1/identity/users/{ownerId}/deactivate", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");

        // Otro Propietario sí puede, porque sigue quedando uno (ella).
        var partnerId = await scenario.CreateUserAsync("socia", "OWNER");
        var partner = await scenario.LocalClientAsync("socia");
        var updated = await partner.PutAsJsonAsync($"/api/v1/identity/users/{ownerId}/roles", new[] { new { roleId = adminRole, branchId = (Guid?)null } }, Json, Ct);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));
        (await updated.Content.ReadFromJsonAsync<UserDto>(Json, Ct))!.Roles.Single().RoleCode.ShouldBe("ADMIN");

        // Ya como Administrador, el antiguo Propietario no puede degradar a la única Propietaria.
        await scenario.Owner.PutAsJsonAsync($"/api/v1/identity/users/{partnerId}/roles", new[] { new { roleId = adminRole, branchId = (Guid?)null } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");
        (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, ActiveOwnersSql)).ShouldBe(1);
    }

    [Fact]
    public async Task Editar_o_borrar_el_rol_de_un_superior_no_sirve_para_degradarlo()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("admin2", "ADMIN");
        var admin = await scenario.LocalClientAsync("admin2");

        // SOCIO: copia del rol Propietario (todos los permisos), asignada a un socio.
        var clone = await scenario.Owner.PostAsJsonAsync(
            $"/api/v1/identity/roles/{await scenario.RoleIdAsync("OWNER")}/clone", new { code = "SOCIO", name = "Socio" }, Json, Ct);
        clone.StatusCode.ShouldBe(HttpStatusCode.Created, await clone.Content.ReadAsStringAsync(Ct));
        var socio = (await clone.Content.ReadFromJsonAsync<RoleDto>(Json, Ct))!;
        await scenario.CreateUserAsync("socio", "SOCIO");

        var fewer = socio.Permissions.Where(p => p != LicenseManage).ToArray();
        await admin.PutAsJsonAsync($"/api/v1/identity/roles/{socio.Id}", new { name = "Socio", permissions = fewer }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");
        await admin.DeleteAsync($"/api/v1/identity/roles/{socio.Id}", Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");

        // El mensaje dice qué permisos faltan.
        var problem = await SecurityScenario.ProblemAsync(await admin.DeleteAsync($"/api/v1/identity/roles/{socio.Id}", Ct));
        problem.GetProperty("detail").GetString()!.ShouldContain("tiene permisos que usted no tiene:");

        // Un rol cuyos permisos el Administrador sí tiene, pero que también tiene el Propietario: tampoco.
        var helper = await scenario.Owner.PostAsJsonAsync(
            "/api/v1/identity/roles", new { code = "AYUDANTE", name = "Ayudante", permissions = UserView }, Json, Ct);
        helper.StatusCode.ShouldBe(HttpStatusCode.Created, await helper.Content.ReadAsStringAsync(Ct));
        var helperId = (await helper.Content.ReadFromJsonAsync<RoleDto>(Json, Ct))!.Id;
        var ownerRoles = new[]
        {
            new { roleId = await scenario.RoleIdAsync("OWNER"), branchId = (Guid?)null },
            new { roleId = helperId, branchId = (Guid?)null },
        };
        await scenario.CreateUserAsync("dueno2", "OWNER");
        var partner = await scenario.LocalClientAsync("dueno2");
        (await partner.PutAsJsonAsync($"/api/v1/identity/users/{scenario.Setup.OwnerUserId}/roles", ownerRoles, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await admin.DeleteAsync($"/api/v1/identity/roles/{helperId}", Ct).ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");
        await admin.PutAsJsonAsync($"/api/v1/identity/roles/{helperId}", new { name = "Ayudante", permissions = Array.Empty<string>() }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");

        // Un rol sin superiores y con permisos que tiene: sí.
        var free = await admin.PostAsJsonAsync("/api/v1/identity/roles", new { code = "LIBRE", name = "Libre", permissions = UserView }, Json, Ct);
        free.StatusCode.ShouldBe(HttpStatusCode.Created, await free.Content.ReadAsStringAsync(Ct));
        var freeId = (await free.Content.ReadFromJsonAsync<RoleDto>(Json, Ct))!.Id;
        (await admin.PutAsJsonAsync($"/api/v1/identity/roles/{freeId}", new { name = "Libre 2", permissions = Array.Empty<string>() }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.DeleteAsync($"/api/v1/identity/roles/{freeId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Un_DENY_por_sucursal_no_se_esquiva_con_un_objetivo_global()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var adminId = await scenario.CreateUserAsync("admin2", "ADMIN");
        var deny = new[] { new { permissionCode = "sales.sale.void", effect = "Deny", branchId = (Guid?)scenario.Setup.BranchId, reason = "Solo supervisores anulan aquí" } };
        (await scenario.Owner.PutAsJsonAsync($"/api/v1/identity/users/{adminId}/overrides", deny, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var admin = await scenario.LocalClientAsync("admin2");
        var supervisor = await scenario.CreateUserAsync("supervisora", "CASH_SUPERVISOR");
        var target = await scenario.CreateUserAsync("nueva", null);
        var supervisorRole = new[] { new { roleId = await scenario.RoleIdAsync("CASH_SUPERVISOR"), branchId = (Guid?)null } };

        // Asignar en TODAS las sucursales un rol que anula ventas incluiría la sucursal donde él no puede.
        await admin.PutAsJsonAsync($"/api/v1/identity/users/{target}/roles", supervisorRole, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.PRIVILEGE_ESCALATION");
        // Y quien anula en todas las sucursales tiene más poder que él.
        await admin.PostAsJsonAsync($"/api/v1/identity/users/{supervisor}/reset-pin", new { posCode = "333", pin = "5937" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");
        // Lo que no niega, sí.
        var cashierRole = new[] { new { roleId = await scenario.RoleIdAsync("CASHIER"), branchId = (Guid?)null } };
        (await admin.PutAsJsonAsync($"/api/v1/identity/users/{target}/roles", cashierRole, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task No_se_cierra_la_sesion_de_quien_tiene_mas_poder_y_la_politica_se_lee_con_clave_temporal()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("admin2", "ADMIN");
        var admin = await scenario.LocalClientAsync("admin2");
        var ownerSession = (await GetAsync<MeDto>(scenario.Owner, "/api/v1/auth/me")).SessionId;

        await admin.PostAsync($"/api/v1/identity/sessions/{ownerSession}/revoke", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.TARGET_OUTRANKS");
        (await scenario.Owner.GetAsync("/api/v1/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // UR4: con la contraseña temporal pendiente de cambio también se lee la política (la pantalla muestra la longitud real).
        await scenario.CreateUserAsync("temporal", "CASHIER");
        var temporary = factory.CreateClient();
        (await LoginAsync(temporary, "temporal", SecurityScenario.UserPassword)).User.MustChangePassword.ShouldBeTrue();
        (await GetAsync<CredentialStatusDto>(temporary, "/api/v1/auth/credentials")).PasswordHistory.ShouldBe(5);
    }

    [Fact]
    public async Task Un_Propietario_unico_con_un_DENY_heredado_no_queda_sin_salida()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var ownerId = scenario.Setup.OwnerUserId;
        var adminId = await scenario.CreateUserAsync("admin2", "ADMIN");

        // Una socia le deja un DENY al Propietario y luego se va: queda un Propietario único con un DENY heredado.
        var partnerId = await scenario.CreateUserAsync("socia", "OWNER");
        var partner = await scenario.LocalClientAsync("socia");
        var inherited = new[] { new { permissionCode = "sales.sale.void", effect = "Deny", branchId = (Guid?)null, reason = "Heredado" } };
        (await partner.PutAsJsonAsync($"/api/v1/identity/users/{ownerId}/overrides", inherited, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await scenario.Owner.PostAsync($"/api/v1/identity/users/{partnerId}/deactivate", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Su DENY no le resta en la jerarquía: sigue gestionando al Administrador (que sí puede anular ventas).
        (await scenario.Owner.PostAsJsonAsync($"/api/v1/identity/users/{adminId}/reset-pin", new { posCode = "555", pin = "5937" }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Puede quitarse el DENY (y solo quitar: agregarse uno sigue prohibido).
        var extra = new[] { new { permissionCode = "sales.sale.void", effect = "Deny", branchId = (Guid?)null, reason = "Heredado" },
                            new { permissionCode = LicenseManage, effect = "Deny", branchId = (Guid?)null, reason = "Nuevo" } };
        await scenario.Owner.PutAsJsonAsync($"/api/v1/identity/users/{ownerId}/overrides", extra, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.SELF_MODIFICATION");
        var removed = await scenario.Owner.PutAsJsonAsync($"/api/v1/identity/users/{ownerId}/overrides", Array.Empty<object>(), Json, Ct);
        removed.StatusCode.ShouldBe(HttpStatusCode.OK, await removed.Content.ReadAsStringAsync(Ct));
        (await removed.Content.ReadFromJsonAsync<UserDto>(Json, Ct))!.Overrides.ShouldBeEmpty();

        // Un Administrador no puede quitarse sus DENY: la excepción es solo del Propietario.
        var admin = await scenario.LocalClientAsync("admin2");
        (await scenario.Owner.PutAsJsonAsync($"/api/v1/identity/users/{adminId}/overrides", inherited, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await admin.PutAsJsonAsync($"/api/v1/identity/users/{adminId}/overrides", Array.Empty<object>(), Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "IDENTITY.SELF_MODIFICATION");
    }

    [Fact]
    public async Task Al_ultimo_administrador_no_se_le_niega_administrar()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        // SOCIO: copia del Propietario que no cuenta como Propietario ni Administrador; el dueño sigue siendo el último administrador.
        var clone = await scenario.Owner.PostAsJsonAsync(
            $"/api/v1/identity/roles/{await scenario.RoleIdAsync("OWNER")}/clone", new { code = "SOCIO", name = "Socio" }, Json, Ct);
        clone.StatusCode.ShouldBe(HttpStatusCode.Created);
        await scenario.CreateUserAsync("socio", "SOCIO");
        var partner = await scenario.LocalClientAsync("socio");
        var ownerId = scenario.Setup.OwnerUserId;

        foreach (var permission in AdministrationPermissions)
        {
            await partner.PutAsJsonAsync($"/api/v1/identity/users/{ownerId}/overrides",
                    new[] { new { permissionCode = permission, effect = "Deny", branchId = (Guid?)null, reason = "Prueba" } }, Json, Ct)
                .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "IDENTITY.LAST_ADMINISTRATOR_LOCKOUT");
        }

        (await partner.PutAsJsonAsync($"/api/v1/identity/users/{ownerId}/overrides",
                new[] { new { permissionCode = "sales.sale.void", effect = "Deny", branchId = (Guid?)null, reason = "Otra cosa sí" } }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revocar_un_equipo_cierra_todas_sus_sesiones_y_audita_a_quienes()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("cajero", "CASHIER", posCode: "200", pin: "5937");
        var (terminal, device) = await scenario.PairTerminalAsync(scenario.Setup.PosTerminalId);
        await SecurityScenario.PosLoginAsync(terminal, "200", "5937");

        (await scenario.Owner.PostAsync($"/api/v1/devices/{device.DeviceId}/revoke", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Phase2.OrganizationApiTests.ScalarAsync<string>(factory,
            "SELECT summary FROM audit.audit_log WHERE action = 'USER_SESSIONS_REVOKED' AND entity_type = 'Device'"))!.ShouldContain("(cajero)");
    }

    [Fact]
    public async Task Con_autorizacion_de_supervisor_el_rechazo_por_jerarquia_no_lista_permisos()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("cajera", "CASHIER", posCode: "200", pin: "5937");
        await scenario.CreateUserAsync("supervisor", "CASH_SUPERVISOR", posCode: "300", pin: "7152");
        var (terminal, _) = await scenario.PairTerminalAsync(scenario.Setup.PosTerminalId);
        await SecurityScenario.PosLoginAsync(terminal, "200", "5937");
        var ownerSession = (await GetAsync<MeDto>(scenario.Owner, "/api/v1/auth/me")).SessionId;
        var revokeUrl = $"/api/v1/identity/sessions/{ownerSession}/revoke";

        var denied = await terminal.PostAsync(revokeUrl, null, Ct);
        var action = (await SecurityScenario.ProblemAsync(denied)).GetProperty("action").GetString()!;
        var granted = await terminal.PostAsJsonAsync("/api/v1/auth/authorizations", new
        {
            supervisorCode = "300", supervisorPin = "7152", permissionCode = IdentityPermissions.SessionRevoke, action,
            targetId = ownerSession, targetType = "Session", reason = "Prueba",
        }, Json, Ct);
        granted.StatusCode.ShouldBe(HttpStatusCode.Created, await granted.Content.ReadAsStringAsync(Ct));
        var grant = (await granted.Content.ReadFromJsonAsync<AuthorizationGrantDto>(Json, Ct))!;

        var withGrant = new HttpRequestMessage(HttpMethod.Post, revokeUrl);
        withGrant.Headers.Add("X-Authorization-Grant", grant.GrantId.ToString());
        var response = await terminal.SendAsync(withGrant, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await SecurityScenario.ProblemAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("IDENTITY.TARGET_OUTRANKS");
        problem.GetProperty("detail").GetString().ShouldBe("El supervisor que autorizó no puede cerrar la sesión de esa persona.");
        (await scenario.Owner.GetAsync("/api/v1/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Dos_Propietarios_que_se_degradan_a_la_vez_no_dejan_la_empresa_sin_Propietario()
    {
        await using var factory = new PosServerFactory();
        var scenario = await SecurityScenario.CreateAsync(factory);
        var adminRole = await scenario.RoleIdAsync("ADMIN");
        var ownerRole = await scenario.RoleIdAsync("OWNER");
        var ownerId = scenario.Setup.OwnerUserId;
        var partnerId = await scenario.CreateUserAsync("socia", "OWNER");
        var partner = await scenario.LocalClientAsync("socia");
        var toAdmin = new[] { new { roleId = adminRole, branchId = (Guid?)null } };

        // Varias rondas: cada Propietario intenta degradar al otro al mismo tiempo (PU4: pg_advisory_xact_lock antes de contar).
        for (var round = 0; round < 5; round++)
        {
            var responses = await Task.WhenAll(
                scenario.Owner.PutAsJsonAsync($"/api/v1/identity/users/{partnerId}/roles", toAdmin, Json, Ct),
                partner.PutAsJsonAsync($"/api/v1/identity/users/{ownerId}/roles", toAdmin, Json, Ct));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1, $"ronda {round}");
            var loser = responses.Single(r => r.StatusCode != HttpStatusCode.OK);
            (await ErrorCodeAsync(loser)).ShouldBeOneOf("IDENTITY.LAST_OWNER", "IDENTITY.TARGET_OUTRANKS");
            (await Phase2.OrganizationApiTests.ScalarAsync<long>(factory, ActiveOwnersSql)).ShouldBe(1);

            // Quien siguió siendo Propietario devuelve el rol al otro para la siguiente ronda.
            var (winner, demoted) = responses[0].StatusCode == HttpStatusCode.OK ? (scenario.Owner, partnerId) : (partner, ownerId);
            (await winner.PutAsJsonAsync($"/api/v1/identity/users/{demoted}/roles", new[] { new { roleId = ownerRole, branchId = (Guid?)null } }, Json, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }
}
