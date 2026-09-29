using Pos.Cloud.Abstractions;
using static Pos.Cloud.Database.Tests.CloudSeed;

namespace Pos.Cloud.Database.Tests;

/// <summary>
/// Nada se borra en la nube (L-09): el rol de la aplicación no tiene DELETE en ninguna tabla; los eventos de suscripción, los
/// check-ins y la auditoría son de solo inserción (privilegios + disparadores que bloquean incluso al dueño).
/// </summary>
public class PrivilegeAndAppendOnlyTests(CloudPostgresFixture postgres)
{
    [Fact]
    public async Task El_rol_de_la_aplicacion_no_tiene_DELETE_ni_TRUNCATE_en_ninguna_tabla()
    {
        var db = await postgres.CreateDatabaseAsync();

        var deletable = await db.ListAsync<string>(
            """
            SELECT (n.nspname || '.' || c.relname)::text FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('system','audit','portal','licensing') AND c.relkind IN ('r','p')
              AND (has_table_privilege('pos_app', c.oid, 'DELETE') OR has_table_privilege('pos_app', c.oid, 'TRUNCATE'))
            """);

        deletable.ShouldBeEmpty();
    }

    [Fact]
    public async Task El_rol_de_la_aplicacion_solo_agrega_y_lee_el_historial_los_checkins_y_la_auditoria()
    {
        var db = await postgres.CreateDatabaseAsync();

        var updatable = await db.ListAsync<string>(
            """
            SELECT (n.nspname || '.' || c.relname)::text FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE (n.nspname, c.relname) IN (('licensing','subscription_events'), ('licensing','checkins'), ('audit','audit_log'),
                                            ('audit','audit_seals'), ('system','cloud_node'), ('system','schema_migrations'))
              AND has_table_privilege('pos_app', c.oid, 'UPDATE')
            """);
        updatable.ShouldBeEmpty();

        foreach (var table in new[] { "licensing.subscription_events", "licensing.checkins", "audit.audit_log", "audit.audit_seals" })
        {
            (await db.ScalarAsync<bool>($"SELECT has_table_privilege('pos_app', '{table}', 'INSERT')")).ShouldBeTrue(table);
            (await db.ScalarAsync<bool>($"SELECT has_table_privilege('pos_app', '{table}', 'SELECT')")).ShouldBeTrue(table);
        }

        // Las tablas de trabajo del portal y de licencias sí se actualizan (los cambios de estado son UPDATE + evento).
        (await db.ScalarAsync<bool>("SELECT has_table_privilege('pos_app', 'licensing.subscriptions', 'UPDATE')")).ShouldBeTrue();
        (await db.ScalarAsync<bool>("SELECT has_table_privilege('pos_app', 'portal.portal_sessions', 'UPDATE')")).ShouldBeTrue();
        (await db.ScalarAsync<bool>("SELECT has_schema_privilege('pos_app', 'licensing', 'CREATE')")).ShouldBeFalse();
    }

    [Fact]
    public async Task Borrar_con_el_rol_de_la_aplicacion_se_rechaza()
    {
        var (sql, ids) = FullChain();
        var db = await postgres.CreateDatabaseAsync();
        await db.ExecuteAsync(sql, db.AppConnectionString);

        foreach (var statement in new[]
                 {
                     $"DELETE FROM licensing.activations WHERE id = '{ids.Activation}'",
                     $"DELETE FROM licensing.licenses WHERE id = '{ids.License}'",
                     $"DELETE FROM licensing.organizations WHERE id = '{ids.Organization}'",
                     $"DELETE FROM portal.portal_users WHERE id = '{SystemUser}'",
                 })
        {
            (await db.FailsAsync(statement, db.AppConnectionString)).SqlState.ShouldBe("42501", statement);
        }
    }

    [Fact]
    public async Task Los_eventos_de_suscripcion_y_los_checkins_son_de_solo_insercion_incluso_para_el_dueno()
    {
        var (sql, ids) = FullChain();
        var db = await postgres.CreateDatabaseAsync();
        await db.ExecuteAsync(sql, db.AppConnectionString);
        await db.ExecuteAsync(
            Checkin(ids.Installation) +
            $"""
            INSERT INTO licensing.subscription_events (id, subscription_id, type, occurred_at, actor_id, new_value)
            VALUES (gen_random_uuid(), '{ids.Subscription}', 'CREATED', now(), '{SystemUser}', 'TRIAL');
            """,
            db.AppConnectionString);

        // pos_app: sin privilegio.
        (await db.FailsAsync("UPDATE licensing.checkins SET active_terminals = 9", db.AppConnectionString)).SqlState.ShouldBe("42501");
        (await db.FailsAsync("UPDATE licensing.subscription_events SET reason = 'x'", db.AppConnectionString)).SqlState.ShouldBe("42501");

        // Superusuario (dueño o no): los disparadores lo impiden igual.
        foreach (var statement in new[]
                 {
                     "UPDATE licensing.checkins SET active_terminals = 9",
                     "DELETE FROM licensing.checkins",
                     "TRUNCATE licensing.checkins",
                     "UPDATE licensing.subscription_events SET reason = 'x'",
                     "DELETE FROM licensing.subscription_events",
                     "TRUNCATE licensing.subscription_events",
                 })
        {
            var ex = await db.FailsAsync(statement);
            ex.SqlState.ShouldBe("42501", statement);
            ex.MessageText.ShouldContain("solo inserción", customMessage: statement);
        }

        (await db.ScalarAsync<long>("SELECT count(*) FROM licensing.checkins")).ShouldBe(1);
    }

    [Fact]
    public async Task La_auditoria_de_la_nube_es_de_solo_insercion()
    {
        var db = await postgres.CreateDatabaseAsync();
        var node = await db.ScalarAsync<Guid>("SELECT node_id FROM system.cloud_node");
        await db.ExecuteAsync(
            $"""
            INSERT INTO audit.audit_log (id, occurred_at, node_id, hash_version, module, action, severity, row_hash)
            VALUES (gen_random_uuid(), now(), '{node}', 1, 'licensing', 'PRUEBA', 'INFO', '{new string('a', 64)}')
            """,
            db.AppConnectionString);

        (await db.FailsAsync("UPDATE audit.audit_log SET summary = 'alterado'", db.AppConnectionString)).SqlState.ShouldBe("42501");
        (await db.FailsAsync("UPDATE audit.audit_log SET summary = 'alterado'")).MessageText.ShouldContain("solo inserción");
        (await db.FailsAsync("DELETE FROM audit.audit_log")).MessageText.ShouldContain("solo inserción");
        (await db.FailsAsync("TRUNCATE audit.audit_seals")).MessageText.ShouldContain("solo inserción");
    }

    [Fact]
    public async Task El_usuario_tecnico_existe_deshabilitado_y_nunca_puede_entrar()
    {
        var db = await postgres.CreateDatabaseAsync();

        (await db.ScalarAsync<string>($"SELECT kind || '|' || role || '|' || status FROM portal.portal_users WHERE id = '{SystemActor.Id}'"))
            .ShouldBe("SYSTEM|SUPERADMIN|DISABLED");
        (await db.ScalarAsync<bool>($"SELECT password_hash IS NULL AND totp_secret_protected IS NULL FROM portal.portal_users WHERE id = '{SystemActor.Id}'"))
            .ShouldBeTrue();

        (await db.FailsAsync(
            $"UPDATE portal.portal_users SET password_hash = 'x', status = 'ACTIVE' WHERE id = '{SystemActor.Id}'", db.AppConnectionString))
            .ConstraintName.ShouldBe("ck_portal_users__system");

        // Un humano siempre tiene contraseña; el correo se guarda en minúsculas.
        (await db.FailsAsync(
            $"""
            INSERT INTO portal.portal_users (id, email, display_name, kind, role, status, created_at, created_by)
            VALUES (gen_random_uuid(), 'soporte@empresa.co', 'Soporte', 'HUMAN', 'SUPPORT', 'ACTIVE', now(), '{SystemActor.Id}')
            """,
            db.AppConnectionString)).ConstraintName.ShouldBe("ck_portal_users__human_password");
        (await db.FailsAsync(
            $"""
            INSERT INTO portal.portal_users (id, email, display_name, kind, role, status, password_hash, created_at, created_by)
            VALUES (gen_random_uuid(), 'Soporte@Empresa.co', 'Soporte', 'HUMAN', 'SUPPORT', 'ACTIVE', 'h', now(), '{SystemActor.Id}')
            """,
            db.AppConnectionString)).ConstraintName.ShouldBe("ck_portal_users__email");
    }

    [Fact]
    public async Task El_distribuidor_debe_tener_su_cuenta_y_los_demas_roles_no()
    {
        var db = await postgres.CreateDatabaseAsync();

        (await db.FailsAsync(
            $"""
            INSERT INTO portal.portal_users (id, email, display_name, kind, role, status, password_hash, created_at, created_by)
            VALUES (gen_random_uuid(), 'distribuidor@empresa.co', 'Distribuidor', 'HUMAN', 'RESELLER', 'ACTIVE', 'h', now(), '{SystemActor.Id}')
            """,
            db.AppConnectionString)).ConstraintName.ShouldBe("ck_portal_users__reseller");
    }
}
