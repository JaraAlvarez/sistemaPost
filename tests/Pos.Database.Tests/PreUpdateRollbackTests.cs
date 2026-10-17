using Npgsql;
using Pos.Infrastructure.Backup;
using Pos.Server.Migrations;

namespace Pos.Database.Tests;

/// <summary>pg_dump y pg_restore ejecutados dentro del contenedor de PostgreSQL 18 de las pruebas.</summary>
internal sealed class ContainerPgTools(PostgresFixture postgres) : IPgTools
{
    public async Task DumpAsync(string connectionString, string outputFile, string? snapshot, CancellationToken cancellationToken)
    {
        var remote = $"/tmp/{Guid.NewGuid():N}.dump";
        List<string> command = ["env", $"PGPASSWORD={Password(connectionString)}", "pg_dump", "--format=custom", "--compress=6", "--no-owner", "--no-privileges",
            $"--file={remote}"];
        if (snapshot is not null)
        {
            command.Add($"--snapshot={snapshot}");
        }

        command.Add($"--dbname={Inside(connectionString)}");
        await RunAsync(command, cancellationToken);
        await File.WriteAllBytesAsync(outputFile, await postgres.Container.ReadFileAsync(remote, cancellationToken), cancellationToken);
    }

    public async Task<int> ListAsync(string dumpFile, CancellationToken cancellationToken)
    {
        var remote = await UploadAsync(dumpFile, cancellationToken);
        var output = await RunAsync(["pg_restore", "--list", remote], cancellationToken);
        return output.Split('\n').Count(l => l.Length > 0 && !l.StartsWith(';'));
    }

    public async Task RestoreAsync(string connectionString, string dumpFile, string? role, CancellationToken cancellationToken)
    {
        var remote = await UploadAsync(dumpFile, cancellationToken);
        List<string> command = ["env", $"PGPASSWORD={Password(connectionString)}", "pg_restore", "--no-owner", "--no-privileges", "--disable-triggers",
            "--exit-on-error", "--single-transaction", $"--dbname={Inside(connectionString)}"];
        if (role is not null)
        {
            command.Add($"--role={role}");
        }

        command.Add(remote);
        await RunAsync(command, cancellationToken);
    }

    private static string Inside(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        return $"host=localhost port=5432 dbname={b.Database} user={b.Username}";
    }

    private static string? Password(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString).Password;

    private async Task<string> UploadAsync(string file, CancellationToken cancellationToken)
    {
        var remote = $"/tmp/{Guid.NewGuid():N}.dump";
        await postgres.Container.CopyAsync(await File.ReadAllBytesAsync(file, cancellationToken), remote, ct: cancellationToken);
        return remote;
    }

    private async Task<string> RunAsync(List<string> command, CancellationToken cancellationToken)
    {
        var result = await postgres.Container.ExecAsync(command, cancellationToken);
        return result.ExitCode == 0 ? result.Stdout : throw new InvalidOperationException($"{command[0]} {command.ElementAtOrDefault(2)}: {result.Stderr}");
    }
}

/// <summary>
/// IN4, IL1, IL2 e IR2 con PostgreSQL real: una actualización cuya migración falla a mitad vuelve al backup PRE_UPDATE del esquema
/// ANTERIOR, en una BD nueva con los privilegios de la versión anterior (pos_app puede leer y escribir como antes; el servidor anterior no
/// queda en 503) y verificada solo con lo que trae el backup (sello y conteos). IR1: la "versión anterior" es el catálogo actual completo y
/// la "versión nueva" son migraciones PROPIAS de la prueba, así que no depende de cuáles sean las últimas migraciones del producto.
/// </summary>
public sealed class PreUpdateRollbackTests(PostgresFixture postgres) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _work = Path.Combine(Path.GetTempPath(), "pos-rollback-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    /// <summary>Migración de la "versión nueva" que se aplica bien: una tabla y una columna que el esquema anterior no tiene.</summary>
    private static readonly MigrationScript NewTable = MigrationScript.Parse(
        "V2099.12.990__system__prueba_nueva.sql",
        "CREATE TABLE system.prueba_version_nueva (id int PRIMARY KEY);\nALTER TABLE system.sync_cursors ADD COLUMN prueba_nueva int;");

    [Fact]
    public async Task Si_una_migracion_versionada_falla_a_mitad_se_restaura_el_esquema_anterior_con_sus_privilegios()
    {
        var broken = MigrationScript.Parse("V2099.12.991__system__falla_a_mitad.sql", "SELECT 1 / 0;");
        var all = ScriptCatalog.Default;
        var next = new ScriptCatalog(all.Versioned.Append(NewTable).Append(broken).Concat(all.Repeatable).Concat(all.Always));

        var (db, target, outcome, update) = await UpdateThatFailsAsync(next);

        update.Outcome.ShouldBe(GuardedUpdateOutcome.FailedRestored, update.RestoreError?.ToString());
        outcome.IsComplete.ShouldBeTrue("Sello y conteos del backup del esquema anterior.");
        outcome.PrivilegesCopied.ShouldBeGreaterThan(0);

        // La BD activa quedó a medio migrar (con la tabla nueva); la restaurada está en el esquema anterior.
        var restored = postgres.ConnectionString(target);
        (await db.ScalarAsync<bool>("SELECT to_regclass('system.prueba_version_nueva') IS NOT NULL")).ShouldBeTrue();
        (await db.ScalarAsync<bool>("SELECT to_regclass('system.prueba_version_nueva') IS NULL", restored)).ShouldBeTrue();
        (await db.ScalarAsync<string>("SELECT max(version) FROM system.schema_migrations WHERE kind = 'VERSIONED'", restored)).ShouldBe(all.LatestVersion);

        // IR3: el servidor anterior, sobre la BD a medio migrar, ve migraciones que no conoce (y no atiende); sobre la restaurada, nada.
        await using (var active = new NpgsqlConnection(db.AppConnectionString))
        {
            await active.OpenAsync(Ct);
            (await new DatabaseMigrator(all).GetStatusAsync(active, Ct)).UnknownVersions.ShouldBe(["2099.12.990"]);
        }

        await using (var back = new NpgsqlConnection(postgres.ConnectionString(target, DatabaseCreator.AppRole, PostgresFixture.Passwords.App)))
        {
            await back.OpenAsync(Ct);
            var status = await new DatabaseMigrator(all).GetStatusAsync(back, Ct);
            (status.UnknownVersions.Count, status.PendingVersioned.Count).ShouldBe((0, 0));
        }

        // IL1: pos_app trabaja en la BD restaurada como antes (sin privilegios el servidor anterior respondería 503)...
        var app = postgres.ConnectionString(target, DatabaseCreator.AppRole, PostgresFixture.Passwords.App);
        (await db.ScalarAsync<long>("SELECT count(*) FROM system.sync_cursors", app)).ShouldBe(1);
        (await db.ScalarAsync<long>("SELECT count(*) FROM system.installation", app)).ShouldBe(1);
        await db.ExecuteAsync($"INSERT INTO system.sync_cursors (peer_node_id, updated_at) VALUES ('{Guid.NewGuid()}', now())", app);
        (await db.ScalarAsync<long>("SELECT count(*) FROM sales.sales", app)).ShouldBe(0);
        await AppReadsReportingAsync(db, target, app);

        // ... y con las MISMAS restricciones de la versión anterior (kardex de solo inserción, catálogos de solo lectura, funciones sin PUBLIC).
        (await db.ScalarAsync<bool>("SELECT has_table_privilege('pos_app', 'inventory.stock_movements', 'UPDATE')", restored)).ShouldBeFalse();
        (await db.ScalarAsync<bool>("SELECT has_table_privilege('pos_app', 'inventory.stock_movements', 'INSERT')", restored)).ShouldBeTrue();
        (await db.ScalarAsync<bool>("SELECT has_table_privilege('pos_app', 'ref.countries', 'INSERT')", restored)).ShouldBeFalse();
        (await db.ScalarAsync<bool>("SELECT has_function_privilege('public', 'audit.ensure_partitions(integer)', 'EXECUTE')", restored)).ShouldBeFalse();
        (await db.ScalarAsync<bool>("SELECT has_function_privilege('pos_app', 'audit.ensure_partitions(integer)', 'EXECUTE')", restored)).ShouldBeTrue();
        await using (var connection = new NpgsqlConnection(app))
        {
            await connection.OpenAsync(Ct);
            await using var update1 = new NpgsqlCommand("UPDATE inventory.stock_movements SET id = id", connection);
            (await Should.ThrowAsync<PostgresException>(() => update1.ExecuteNonQueryAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        // IR2: el migrador de la versión anterior deja después sus privilegios exactos (apply-always) sobre la BD restaurada.
        await new DatabaseMigrator(all).ApplyAlwaysAsync(
            postgres.ConnectionString(target, DatabaseCreator.MigratorRole, PostgresFixture.Passwords.Migrator), "1.0.0", Ct);
        await AppReadsReportingAsync(db, target, app);
        (await db.ScalarAsync<bool>("SELECT has_table_privilege('pos_app', 'inventory.stock_movements', 'UPDATE')", restored)).ShouldBeFalse();
    }

    /// <summary>
    /// IR2: la falla está en un REPETIBLE que corre después de R__reporting__views (que borra y vuelve a crear las vistas): en la BD activa
    /// las vistas quedaron sin privilegios, así que la copia tampoco los trae; la restauración igual deja a pos_app leyendo los reportes.
    /// </summary>
    [Fact]
    public async Task Si_falla_un_repetible_despues_de_las_vistas_de_reportes_pos_app_sigue_leyendolas()
    {
        var all = ScriptCatalog.Default;
        var views = all.Repeatable.Single(s => s.Name.Contains("reporting__views", StringComparison.Ordinal));
        var changedViews = MigrationScript.Parse(views.Name, views.Sql + "\n-- versión nueva de las vistas\n");
        var brokenRepeatable = MigrationScript.Parse("R__zzzz__falla_de_prueba.sql", "SELECT 1 / 0;");
        var next = new ScriptCatalog(all.Versioned.Append(NewTable)
            .Concat(all.Repeatable.Where(s => s.Name != views.Name)).Append(changedViews).Append(brokenRepeatable)
            .Concat(all.Always));

        var (db, target, outcome, update) = await UpdateThatFailsAsync(next);

        update.Outcome.ShouldBe(GuardedUpdateOutcome.FailedRestored, update.RestoreError?.ToString());
        outcome.IsComplete.ShouldBeTrue();
        var view = await db.ScalarAsync<string>("SELECT table_name FROM information_schema.views WHERE table_schema = 'reporting' ORDER BY table_name LIMIT 1");
        (await db.ScalarAsync<bool>($"SELECT has_table_privilege('pos_app', 'reporting.{view}', 'SELECT')"))
            .ShouldBeFalse("En la BD activa las vistas se recrearon sin privilegios: es el caso que se prueba.");
        await AppReadsReportingAsync(db, target, postgres.ConnectionString(target, DatabaseCreator.AppRole, PostgresFixture.Passwords.App));
    }

    /// <summary>
    /// Esquema "anterior" (el catálogo actual), datos mínimos, backup PRE_UPDATE y la "versión nueva" <paramref name="next"/>, que falla;
    /// la vuelta atrás restaura con <see cref="PreUpdateRollback"/>.
    /// </summary>
    private async Task<(TestDatabase Db, string Target, PreUpdateRestoreOutcome Outcome, GuardedUpdateResult Update)> UpdateThatFailsAsync(ScriptCatalog next)
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        await db.ExecuteAsync("INSERT INTO system.installation (installation_id, node_role, created_at) VALUES (gen_random_uuid(), 'ALL_IN_ONE', now())");
        await db.ExecuteAsync($"INSERT INTO system.sync_cursors (peer_node_id, updated_at) VALUES ('{Guid.NewGuid()}', now())", db.AppConnectionString);

        var keys = new BackupKeyStore(Path.Combine(_work, Guid.NewGuid().ToString("N") + ".key"));
        var tools = new ContainerPgTools(postgres);
        var created = await new BackupEngine(tools).CreateAsync(
            new BackupSource(db.BackupConnectionString, null, "1.0.0"), RetentionPolicy.PreUpdateKind, keys.GetOrCreate(), _work, TimeProvider.System,
            TimeZoneInfo.Utc, Ct);

        var target = "pos_rb_" + Guid.NewGuid().ToString("N")[..12];
        PreUpdateRestoreOutcome? outcome = null;
        var update = await GuardedSchemaUpdate.RunAsync(
            ct => new DatabaseMigrator(next).MigrateAsync(db.MigratorConnectionString, "1.1.0", ct),
            created.FilePath,
            schemaChangesPending: true,
            async (file, ct) =>
            {
                outcome = await new PreUpdateRollback(tools).RestoreAsync(
                    file, keys, postgres.SuperuserConnectionString, db.Name, target, Path.Combine(_work, Guid.NewGuid().ToString("N")), dataDirectory: null,
                    (admin, database, c) => DatabaseCreator.CreateDatabaseOnlyAsync(admin, database, c), ct);
                return outcome.Database;
            },
            Ct);
        outcome.ShouldNotBeNull(update.MigrationError?.ToString());
        return (db, target, outcome, update);
    }

    private static async Task AppReadsReportingAsync(TestDatabase db, string target, string app)
    {
        var views = await db.ListAsync<string>(
            "SELECT table_name::text FROM information_schema.views WHERE table_schema = 'reporting' ORDER BY table_name",
            db.Fixture.ConnectionString(target));
        views.ShouldNotBeEmpty();
        foreach (var view in views)
        {
            await db.ExecuteAsync($"SELECT * FROM reporting.\"{view}\" LIMIT 0", app);
        }
    }
}
