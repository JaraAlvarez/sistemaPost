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
/// IN4, IL1 e IL2 con PostgreSQL real: una actualización cuya migración falla a mitad vuelve al backup PRE_UPDATE del esquema ANTERIOR, en
/// una BD nueva con los privilegios de la versión anterior (pos_app puede leer y escribir como antes; el servidor anterior no queda en
/// 503) y verificada solo con lo que trae el backup (sello y conteos), sin el verificador de la versión nueva.
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

    [Fact]
    public async Task Si_la_migracion_falla_a_mitad_se_restaura_el_esquema_anterior_con_sus_privilegios()
    {
        // Versión ANTERIOR: el esquema sin las dos últimas migraciones (la versión nueva las trae).
        var db = await postgres.CreateDatabaseAsync();
        var all = ScriptCatalog.Default;
        var previous = new ScriptCatalog(all.Versioned.Take(all.Versioned.Count - 2).Concat(all.Repeatable).Concat(all.Always));
        await new DatabaseMigrator(previous).MigrateAsync(db.MigratorConnectionString, "1.0.0", Ct);
        await db.ExecuteAsync("INSERT INTO system.installation (installation_id, node_role, created_at) VALUES (gen_random_uuid(), 'ALL_IN_ONE', now())");
        await db.ExecuteAsync($"INSERT INTO system.sync_cursors (peer_node_id, updated_at) VALUES ('{Guid.NewGuid()}', now())", db.AppConnectionString);

        // Backup PRE_UPDATE (servidor detenido).
        var keys = new BackupKeyStore(Path.Combine(_work, "backup.key"));
        var tools = new ContainerPgTools(postgres);
        var created = await new BackupEngine(tools).CreateAsync(
            new BackupSource(db.BackupConnectionString, null, "1.0.0"), RetentionPolicy.PreUpdateKind, keys.GetOrCreate(), _work, TimeProvider.System,
            TimeZoneInfo.Utc, Ct);

        // Versión NUEVA: sus dos migraciones se aplican (cada una en su transacción) y la tercera falla.
        var broken = MigrationScript.Parse("V2099.12.998__system__falla_a_mitad.sql", "SELECT 1 / 0;");
        var next = new ScriptCatalog(all.Versioned.Append(broken).Concat(all.Repeatable).Concat(all.Always));
        var target = "pos_rb_" + Guid.NewGuid().ToString("N")[..12];
        PreUpdateRestoreOutcome? outcome = null;
        var update = await GuardedSchemaUpdate.RunAsync(
            ct => new DatabaseMigrator(next).MigrateAsync(db.MigratorConnectionString, "1.1.0", ct),
            created.FilePath,
            schemaChangesPending: true,
            async (file, ct) =>
            {
                outcome = await new PreUpdateRollback(tools).RestoreAsync(
                    file, keys, postgres.SuperuserConnectionString, db.Name, target, Path.Combine(_work, "restore"), dataDirectory: null,
                    (admin, database, c) => DatabaseCreator.CreateDatabaseOnlyAsync(admin, database, c), ct);
                return outcome.Database;
            },
            Ct);

        update.Outcome.ShouldBe(GuardedUpdateOutcome.FailedRestored, update.RestoreError?.ToString());
        outcome!.IsComplete.ShouldBeTrue("Sello y conteos del backup del esquema anterior.");
        outcome.PrivilegesCopied.ShouldBeGreaterThan(0);

        // La BD activa quedó a medio migrar; la restaurada está en el esquema anterior.
        var restored = postgres.ConnectionString(target);
        const string NewColumn = "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'org' AND table_name = 'companies' AND column_name = 'business_profile'";
        (await db.ScalarAsync<long>(NewColumn)).ShouldBe(1);
        (await db.ScalarAsync<long>(NewColumn, restored)).ShouldBe(0);
        (await db.ScalarAsync<string>("SELECT max(version) FROM system.schema_migrations WHERE kind = 'VERSIONED'", restored))
            .ShouldBe(previous.LatestVersion);
        (await db.ScalarAsync<string>("SELECT max(version) FROM system.schema_migrations WHERE kind = 'VERSIONED'"))
            .ShouldBe(all.LatestVersion, "La migración fallida dejó aplicadas las anteriores (por eso hay que volver al backup).");

        // IL1: pos_app trabaja en la BD restaurada como antes (sin privilegios el servidor anterior respondería 503)...
        var app = postgres.ConnectionString(target, DatabaseCreator.AppRole, PostgresFixture.Passwords.App);
        (await db.ScalarAsync<long>("SELECT count(*) FROM system.sync_cursors", app)).ShouldBe(1);
        (await db.ScalarAsync<long>("SELECT count(*) FROM system.installation", app)).ShouldBe(1);
        await db.ExecuteAsync($"INSERT INTO system.sync_cursors (peer_node_id, updated_at) VALUES ('{Guid.NewGuid()}', now())", app);
        (await db.ScalarAsync<long>("SELECT count(*) FROM sales.sales", app)).ShouldBe(0);

        // ... y con las MISMAS restricciones de la versión anterior (kardex de solo inserción, funciones sin PUBLIC).
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
    }
}
