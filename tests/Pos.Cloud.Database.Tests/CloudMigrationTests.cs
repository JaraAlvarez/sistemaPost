using Pos.Cloud.Infrastructure;
using Pos.Cloud.Migrations;
using Pos.Server.Migrations;

namespace Pos.Cloud.Database.Tests;

/// <summary>Las migraciones de la BD de la nube se aplican con el migrador SQL-first del producto (docs/fases/fase-12a-propuesta.md §4).</summary>
public class CloudMigrationTests(CloudPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] CloudSchemas = ["audit", "licensing", "portal", "system"];

    [Fact]
    public async Task Migrar_desde_cero_crea_el_esquema_de_la_nube_y_queda_al_dia()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: false);
        var migrator = CloudDatabase.CreateMigrator();

        var report = await migrator.MigrateAsync(db.MigratorConnectionString, "tests", Ct);

        report.SchemaVersion.ShouldBe(CloudScripts.Catalog.LatestVersion);
        report.AppliedScripts.Count.ShouldBe(CloudScripts.Catalog.Versioned.Count + CloudScripts.Catalog.Repeatable.Count);
        (await migrator.GetStatusAsync(db.MigratorConnectionString, Ct)).IsUpToDate.ShouldBeTrue();
        await migrator.VerifyAsync(db.MigratorConnectionString, Ct);

        (await db.ListAsync<string>("SELECT nspname::text FROM pg_namespace WHERE nspname IN ('system','audit','portal','licensing') ORDER BY 1"))
            .ShouldBe(CloudSchemas);

        // Es una BD independiente de la del POS: no hay ninguno de sus esquemas.
        (await db.ScalarAsync<long>("SELECT count(*) FROM pg_namespace WHERE nspname IN ('org','identity','catalog','sales','ref')")).ShouldBe(0);
    }

    [Fact]
    public async Task Reejecutar_la_migracion_no_aplica_nada_y_el_nodo_es_unico_e_inmutable()
    {
        var db = await postgres.CreateDatabaseAsync();

        var report = await CloudDatabase.CreateMigrator().MigrateAsync(db.MigratorConnectionString, "tests", Ct);

        report.AppliedScripts.ShouldBeEmpty();
        (await db.ScalarAsync<long>("SELECT count(*) FROM system.cloud_node")).ShouldBe(1);
        (await db.FailsAsync("UPDATE system.cloud_node SET node_id = gen_random_uuid()")).SqlState.ShouldBe("P0001");
        (await db.FailsAsync("INSERT INTO system.cloud_node (id, node_id, created_at) VALUES (false, gen_random_uuid(), now())"))
            .ConstraintName.ShouldBe("ck_cloud_node__single_row");
    }

    [Fact]
    public async Task Los_objetos_pertenecen_a_pos_owner()
    {
        var db = await postgres.CreateDatabaseAsync();

        var owners = await db.ListAsync<string>(
            """
            SELECT DISTINCT pg_get_userbyid(c.relowner)::text FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('system','audit','portal','licensing')
            """);

        owners.ShouldBe([DatabaseMigrator.OwnerRole]);
    }

    [Fact]
    public async Task Todas_las_claves_foraneas_tienen_indice()
    {
        var db = await postgres.CreateDatabaseAsync();

        var unindexed = await db.ListAsync<string>(
            """
            SELECT (c.conrelid::regclass || ' ' || c.conname)::text
            FROM pg_constraint c
            WHERE c.contype = 'f'
              AND NOT EXISTS (
                  SELECT 1 FROM pg_index i
                  WHERE i.indrelid = c.conrelid
                    AND (i.indkey::int2[])[0:array_length(c.conkey, 1) - 1] @> c.conkey
                    AND (i.indkey::int2[])[0:array_length(c.conkey, 1) - 1] <@ c.conkey)
            """);

        unindexed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Las_restricciones_tienen_nombre_explicito()
    {
        var db = await postgres.CreateDatabaseAsync();

        var generated = await db.ListAsync<string>(
            """
            SELECT (conrelid::regclass || ' ' || conname)::text FROM pg_constraint c
            JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE n.nspname IN ('system','audit','portal','licensing')
              AND c.contype IN ('p','f','u','c')
              AND c.conname !~ '^(pk|fk|ux|ck|ex)_'
              AND c.conrelid::regclass::text NOT LIKE 'audit.audit_log_%'
            """);

        generated.ShouldBeEmpty();
    }

    [Fact]
    public async Task La_auditoria_tiene_particiones_mensuales_y_la_particion_por_defecto()
    {
        var db = await postgres.CreateDatabaseAsync();

        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid WHERE i.inhparent = 'audit.audit_log'::regclass"))
            .ShouldBeGreaterThanOrEqualTo(4);
        (await db.ScalarAsync<int>("SELECT audit.ensure_partitions(3)", db.AppConnectionString)).ShouldBe(0);
    }
}
