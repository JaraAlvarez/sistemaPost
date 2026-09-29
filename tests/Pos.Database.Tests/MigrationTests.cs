using Npgsql;
using Pos.Server.Migrations;

namespace Pos.Database.Tests;

/// <summary>Criterios de aceptación de migraciones (docs/fases/fase-02-propuesta.md §7 y §22).</summary>
public class MigrationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrar_desde_cero_crea_el_esquema_completo_y_queda_al_dia()
    {
        var db = await postgres.CreateDatabaseAsync();
        var migrator = new DatabaseMigrator(ScriptCatalog.Default);

        var report = await migrator.MigrateAsync(db.MigratorConnectionString, "tests", Ct);

        report.SchemaVersion.ShouldBe(ScriptCatalog.Default.LatestVersion);
        report.AppliedScripts.Count.ShouldBe(ScriptCatalog.Default.Versioned.Count + ScriptCatalog.Default.Repeatable.Count);
        (await migrator.GetStatusAsync(db.MigratorConnectionString, Ct)).IsUpToDate.ShouldBeTrue();
        await migrator.VerifyAsync(db.MigratorConnectionString, Ct);

        var schemas = await db.ListAsync<string>(
            "SELECT nspname FROM pg_namespace WHERE nspname IN ('system','ref','org','identity','audit') ORDER BY 1");
        schemas.ShouldBe(["audit", "identity", "org", "ref", "system"]);
    }

    [Fact]
    public async Task Reejecutar_la_migracion_no_aplica_nada()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);

        var report = await new DatabaseMigrator(ScriptCatalog.Default).MigrateAsync(db.MigratorConnectionString, "tests", Ct);

        report.AppliedScripts.ShouldBeEmpty();
        (await db.ScalarAsync<long>("SELECT count(*) FROM system.schema_migrations"))
            .ShouldBe(ScriptCatalog.Default.Versioned.Count + ScriptCatalog.Default.Repeatable.Count);
    }

    [Fact]
    public async Task Un_script_aplicado_y_modificado_bloquea_el_migrador()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        var first = ScriptCatalog.Default.Versioned[0];
        var altered = MigrationScript.Parse(first.Name, first.Sql + "\n-- modificado\n");
        var catalog = new ScriptCatalog(ScriptCatalog.Default.Versioned.Skip(1).Append(altered)
            .Concat(ScriptCatalog.Default.Repeatable).Concat(ScriptCatalog.Default.Always));

        var ex = await Should.ThrowAsync<MigrationException>(
            () => new DatabaseMigrator(catalog).MigrateAsync(db.MigratorConnectionString, "tests", Ct));

        ex.Code.ShouldBe(MigrationException.ChecksumMismatch);
    }

    [Fact]
    public async Task Un_script_con_error_deja_la_base_de_datos_en_la_version_anterior()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        var broken = MigrationScript.Parse(
            "V2099.12.999__system__broken.sql",
            "CREATE TABLE system.half_done (id int);\nSELECT * FROM tabla_que_no_existe;");
        var catalog = new ScriptCatalog(ScriptCatalog.Default.Versioned.Append(broken)
            .Concat(ScriptCatalog.Default.Repeatable).Concat(ScriptCatalog.Default.Always));

        var ex = await Should.ThrowAsync<MigrationException>(
            () => new DatabaseMigrator(catalog).MigrateAsync(db.MigratorConnectionString, "tests", Ct));

        ex.Code.ShouldBe(MigrationException.ScriptFailed);
        (await db.ScalarAsync<bool>("SELECT to_regclass('system.half_done') IS NULL")).ShouldBeTrue();
        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        (await DatabaseMigrator.GetDatabaseVersionAsync(connection, Ct)).ShouldBe(ScriptCatalog.Default.LatestVersion);
    }

    [Fact]
    public async Task Una_base_de_datos_mas_nueva_que_la_aplicacion_se_rechaza()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        var catalog = new ScriptCatalog(ScriptCatalog.Default.Versioned.Take(ScriptCatalog.Default.Versioned.Count - 1));

        var ex = await Should.ThrowAsync<MigrationException>(
            () => new DatabaseMigrator(catalog).MigrateAsync(db.MigratorConnectionString, "tests", Ct));

        ex.Code.ShouldBe(MigrationException.DatabaseNewer);
    }

    [Fact]
    public async Task Un_repetible_modificado_se_reejecuta()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        var original = ScriptCatalog.Default.Repeatable.Single(s => s.Name == "R__system__document_types.sql");
        var changed = MigrationScript.Parse(original.Name, original.Sql + "\n-- cambio de contenido\n");
        var catalog = new ScriptCatalog(ScriptCatalog.Default.Versioned
            .Concat(ScriptCatalog.Default.Repeatable.Where(s => s != original)).Append(changed)
            .Concat(ScriptCatalog.Default.Always));

        var report = await new DatabaseMigrator(catalog).MigrateAsync(db.MigratorConnectionString, "tests", Ct);

        report.AppliedScripts.ShouldBe([original.Name]);
    }

    [Fact]
    public async Task Los_objetos_pertenecen_a_pos_owner_y_la_bd_usa_la_colacion_builtin()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);

        var owners = await db.ListAsync<string>(
            """
            SELECT DISTINCT pg_get_userbyid(c.relowner)::text FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('system','ref','org','identity','audit')
            """);
        owners.ShouldBe([DatabaseMigrator.OwnerRole]);

        (await db.ScalarAsync<string>($"SELECT datlocprovider::text FROM pg_database WHERE datname = '{db.Name}'")).ShouldBe("b");
        (await db.ScalarAsync<string>($"SELECT datlocale FROM pg_database WHERE datname = '{db.Name}'")).ShouldBe("C.UTF-8");
    }
}
