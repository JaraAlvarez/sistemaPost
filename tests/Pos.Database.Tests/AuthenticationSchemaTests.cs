using Npgsql;
using Pos.Infrastructure.Security;

namespace Pos.Database.Tests;

/// <summary>Restricciones de la migración V2026.10.006 y recuperación de emergencia del Propietario.</summary>
public class AuthenticationSchemaTests(PostgresFixture postgres)
{
    private static readonly string Company = InfrastructureHarness.CompanyId.ToString();
    private static readonly string SystemUser = InfrastructureHarness.SystemUserId.ToString();

    [Fact]
    public async Task Un_usuario_humano_activo_necesita_contrasena_y_un_PIN_necesita_codigo_de_cajero()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"""
                INSERT INTO identity.users (id, company_id, username, display_name, kind, status, created_at, created_by)
                VALUES (gen_random_uuid(), '{Company}', 'sinclave', 'Sin clave', 'HUMAN', 'ACTIVE', now(), '{SystemUser}')
                """,
                harness.Database.AppConnectionString)))
            .ConstraintName.ShouldBe("ck_users__human_active_password");

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"""
                INSERT INTO identity.users (id, company_id, username, display_name, kind, status, password_hash, pin_hash, created_at, created_by)
                VALUES (gen_random_uuid(), '{Company}', 'sincodigo', 'x', 'HUMAN', 'ACTIVE', 'h', 'p', now(), '{SystemUser}')
                """,
                harness.Database.AppConnectionString)))
            .ConstraintName.ShouldBe("ck_users__pin_requires_pos_code");
    }

    [Fact]
    public async Task Un_supervisor_no_puede_autorizarse_a_si_mismo_en_la_BD()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var user = await InsertOwnerAsync(harness, "dueno");

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"""
                INSERT INTO identity.authorization_grants (id, company_id, node_id, permission_code, requested_by, authorized_by, action,
                    granted_at, expires_at)
                SELECT gen_random_uuid(), '{Company}', installation_id, 'audit.log.view', '{user}', '{user}', 'POST /x', now(), now() + interval '2 min'
                FROM system.installation
                """,
                harness.Database.AppConnectionString)))
            .ConstraintName.ShouldBe("ck_authorization_grants__not_self");
    }

    [Fact]
    public async Task Un_equipo_activo_necesita_credencial_y_el_codigo_de_cajero_es_unico()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        await harness.Database.ExecuteAsync(
            $"""
            INSERT INTO org.nodes (id, company_id, branch_id, kind, name, number, status, registered_at, created_at, created_by)
            SELECT installation_id, '{Company}', '{InfrastructureHarness.BranchS01}', 'STORE_SERVER', 'Nodo', 1, 'ACTIVE', now(), now(), '{SystemUser}'
            FROM system.installation
            """,
            harness.Database.AppConnectionString);

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"""
                INSERT INTO org.devices (id, company_id, node_id, kind, hostname, machine_fingerprint_hash, paired_at, status, paired_by, created_at, created_by)
                SELECT gen_random_uuid(), '{Company}', installation_id, 'TERMINAL', 'CAJA', repeat('a', 64), now(), 'ACTIVE', '{SystemUser}', now(), '{SystemUser}'
                FROM system.installation
                """,
                harness.Database.AppConnectionString)))
            .ConstraintName.ShouldBe("ck_devices__active_credential");

        await InsertOwnerAsync(harness, "uno", posCode: "100");
        (await Should.ThrowAsync<PostgresException>(() => InsertOwnerAsync(harness, "dos", posCode: "100")))
            .ConstraintName.ShouldBe("ux_users__company_pos_code");
    }

    [Fact]
    public async Task La_recuperacion_de_emergencia_asigna_contrasena_temporal_cierra_sesiones_y_audita()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var owner = await InsertOwnerAsync(harness, "dueno", withOwnerRole: true);
        await harness.Database.ExecuteAsync(
            $"""
            INSERT INTO org.nodes (id, company_id, branch_id, kind, name, number, status, registered_at, created_at, created_by)
            SELECT installation_id, '{Company}', '{InfrastructureHarness.BranchS01}', 'STORE_SERVER', 'Nodo', 1, 'ACTIVE', now(), now(), '{SystemUser}'
            FROM system.installation;
            INSERT INTO identity.user_sessions (id, company_id, node_id, user_id, token_hash, kind, branch_id, security_version,
                idle_timeout_seconds, created_at, last_activity_at, expires_at)
            SELECT gen_random_uuid(), '{Company}', installation_id, '{owner}', repeat('c', 64), 'BACKOFFICE', '{InfrastructureHarness.BranchS01}', 1,
                1800, now(), now(), now() + interval '12 hours'
            FROM system.installation;
            UPDATE identity.users SET status = 'LOCKED', failed_login_count = 5, locked_until = now() + interval '1 hour' WHERE id = '{owner}';
            """,
            harness.Database.AppConnectionString);

        var reset = new OwnerEmergencyReset(harness.Services);
        (await reset.ResetAsync("no.existe", TestContext.Current.CancellationToken)).Succeeded.ShouldBeFalse();
        var result = await reset.ResetAsync("DUENO", TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        result.TemporaryPassword!.Length.ShouldBe(16);
        (await harness.Database.ScalarAsync<string>($"SELECT status FROM identity.users WHERE id = '{owner}'")).ShouldBe("ACTIVE");
        (await harness.Database.ScalarAsync<bool>($"SELECT must_change_password FROM identity.users WHERE id = '{owner}'")).ShouldBeTrue();
        (await harness.Database.ScalarAsync<long>($"SELECT count(*) FROM identity.user_sessions WHERE user_id = '{owner}' AND revoked_at IS NULL")).ShouldBe(0);
        (await harness.Database.ScalarAsync<string>("SELECT severity FROM audit.audit_log WHERE action = 'OWNER_EMERGENCY_RESET'")).ShouldBe("CRITICAL");
    }

    private static async Task<Guid> InsertOwnerAsync(InfrastructureHarness harness, string username, string? posCode = null, bool withOwnerRole = false)
    {
        var id = Guid.CreateVersion7();
        var pos = posCode is null ? "NULL" : $"'{posCode}'";
        await harness.Database.ExecuteAsync(
            $"""
            INSERT INTO identity.users (id, company_id, username, display_name, kind, status, password_hash, pos_code, created_at, created_by)
            VALUES ('{id}', '{Company}', '{username}', 'Usuario {username}', 'HUMAN', 'ACTIVE', 'hash', {pos}, now(), '{SystemUser}');
            """,
            harness.Database.AppConnectionString);
        if (withOwnerRole)
        {
            var role = Guid.CreateVersion7();
            await harness.Database.ExecuteAsync(
                $"""
                INSERT INTO identity.roles (id, company_id, code, name, is_system, created_at, created_by)
                VALUES ('{role}', '{Company}', 'OWNER', 'Propietario', true, now(), '{SystemUser}');
                INSERT INTO identity.user_roles (id, user_id, role_id, granted_at, granted_by)
                VALUES (gen_random_uuid(), '{id}', '{role}', now(), '{SystemUser}');
                """,
                harness.Database.AppConnectionString);
        }

        return id;
    }
}
