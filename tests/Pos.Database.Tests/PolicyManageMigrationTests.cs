using Pos.Server.Migrations;

namespace Pos.Database.Tests;

/// <summary>
/// V2026.10.047 (Reabastecimiento): al actualizar, todo rol que podía guardar mínimos y máximos (inventory.adjustment.manage) recibe
/// el permiso nuevo inventory.policy.manage; los demás roles no cambian.
/// </summary>
public class PolicyManageMigrationTests(PostgresFixture postgres)
{
    private const string CompanyId = "01920000-0000-7000-8000-000000000001";
    private const string WithAdjustments = "01920000-0000-7000-8000-0000000000a1";
    private const string WithoutAdjustments = "01920000-0000-7000-8000-0000000000a2";
    private const string Keeper = "01920000-0000-7000-8000-0000000000b1";
    private const string Courier = "01920000-0000-7000-8000-0000000000b2";
    private const string SystemUser = "01920000-0000-7000-8000-0000000000ff";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Los_roles_con_ajustes_de_inventario_reciben_el_permiso_de_minimos_y_maximos()
    {
        var db = await postgres.CreateDatabaseAsync();
        var all = ScriptCatalog.Default;
        var migration = all.Versioned.Single(s => s.Name.StartsWith("V2026.10.047__", StringComparison.Ordinal));
        var before = new ScriptCatalog(all.Versioned.Where(s => s != migration).Concat(all.Repeatable).Concat(all.Always));
        await new DatabaseMigrator(before).MigrateAsync(db.MigratorConnectionString, "tests", Ct);

        // Roles creados por la empresa antes de la actualización.
        await db.ExecuteAsync(
            $"""
            INSERT INTO org.companies (id, legal_name, trade_name, person_type, identification_type, identification_number,
                check_digit, tax_regime, country_code, municipality_code, address, currency_code, timezone, status, created_at, created_by)
            VALUES ('{CompanyId}', 'Supermercado Prueba SAS', 'Súper Prueba', 'LEGAL', 'NIT', '900123456', '8', '48', 'CO',
                '05001', 'Calle 1 # 2-3', 'COP', 'America/Bogota', 'ACTIVE', now(), '{SystemUser}');
            INSERT INTO identity.roles (id, company_id, code, name, is_system, created_at, created_by)
            VALUES ('{WithAdjustments}', '{CompanyId}', 'BODEGUERO', 'Bodeguero', false, now(), '{SystemUser}'),
                   ('{WithoutAdjustments}', '{CompanyId}', 'MENSAJERO', 'Mensajero', false, now(), '{SystemUser}');
            INSERT INTO identity.role_permissions (role_id, permission_code)
            VALUES ('{WithAdjustments}', 'inventory.adjustment.manage'), ('{WithAdjustments}', 'inventory.stock.view'),
                   ('{WithoutAdjustments}', 'inventory.stock.view');
            INSERT INTO identity.users (id, company_id, username, display_name, kind, status, password_hash, created_at, created_by)
            VALUES ('{Keeper}', '{CompanyId}', 'bodeguero', 'Bodeguero', 'HUMAN', 'ACTIVE', 'hash', now(), '{SystemUser}'),
                   ('{Courier}', '{CompanyId}', 'mensajero', 'Mensajero', 'HUMAN', 'ACTIVE', 'hash', now(), '{SystemUser}');
            INSERT INTO identity.user_roles (id, user_id, role_id, branch_id, granted_at, granted_by)
            VALUES (gen_random_uuid(), '{Keeper}', '{WithAdjustments}', NULL, now(), '{SystemUser}'),
                   (gen_random_uuid(), '{Courier}', '{WithoutAdjustments}', NULL, now(), '{SystemUser}');
            """);

        var report = await new DatabaseMigrator(all).MigrateAsync(db.MigratorConnectionString, "tests", Ct);

        report.AppliedScripts.ShouldContain(migration.Name);
        (await db.ScalarAsync<long>(
                $"SELECT count(*) FROM identity.role_permissions WHERE role_id = '{WithAdjustments}' AND permission_code = 'inventory.policy.manage'"))
            .ShouldBe(1);
        (await db.ScalarAsync<long>(
                $"SELECT count(*) FROM identity.role_permissions WHERE role_id = '{WithoutAdjustments}' AND permission_code = 'inventory.policy.manage'"))
            .ShouldBe(0);
        // Las sesiones del usuario del rol cambiado toman el permiso nuevo (versión de seguridad + 1); las demás no cambian.
        (await db.ScalarAsync<long>($"SELECT security_version FROM identity.users WHERE id = '{Keeper}'")).ShouldBe(2);
        (await db.ScalarAsync<long>($"SELECT security_version FROM identity.users WHERE id = '{Courier}'")).ShouldBe(1);
        (await db.ScalarAsync<bool>("SELECT NOT is_deprecated FROM identity.permissions WHERE code = 'inventory.policy.manage'")).ShouldBeTrue();
    }
}
