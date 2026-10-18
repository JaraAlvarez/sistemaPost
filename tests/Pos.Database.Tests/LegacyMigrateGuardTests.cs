using Pos.Infrastructure.Backup;
using Pos.Server.Migrations;

namespace Pos.Database.Tests;

/// <summary>
/// Integración 1.2.0 (ALTA 1) con PostgreSQL real: el actualizador 1.0.x/1.1.0 llama al migrador nuevo con
/// <c>migrate --no-backup --data-root D</c> y, si falla, solo vuelve a arrancar el servidor anterior. El migrador nuevo hace su propio backup
/// PRE_UPDATE, restaura si una migración falla a mitad, y no vuelve a intentar la misma versión sobre los mismos datos.
/// </summary>
public sealed class LegacyMigrateGuardTests(PostgresFixture postgres) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _work = Path.Combine(Path.GetTempPath(), "pos-legacy-migrate-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    private static readonly MigrationScript NewTable = MigrationScript.Parse(
        "V2099.12.990__system__prueba_nueva.sql",
        "CREATE TABLE system.prueba_version_nueva (id int PRIMARY KEY);");

    private static readonly MigrationScript Broken = MigrationScript.Parse("V2099.12.991__system__falla_a_mitad.sql", "SELECT 1 / 0;");

    [Fact]
    public async Task Si_la_migracion_falla_se_restaura_el_backup_propio_y_la_misma_version_no_se_reintenta()
    {
        var all = ScriptCatalog.Default;
        var next = new ScriptCatalog(all.Versioned.Append(NewTable).Append(Broken).Concat(all.Repeatable).Concat(all.Always));
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        await db.ExecuteAsync("INSERT INTO system.installation (installation_id, node_role, created_at) VALUES (gen_random_uuid(), 'ALL_IN_ONE', now())");
        var dataRoot = Path.Combine(_work, "data");
        var keys = new BackupKeyStore(Path.Combine(_work, "backup.key"));
        var tools = new ContainerPgTools(postgres);
        var active = db.Name;
        var backups = 0;
        var migrations = 0;
        var fromVersion = all.LatestVersion;

        Task<LegacyMigrateResult> RunAsync(bool retryFailed = false) => LegacyMigrateGuard.RunAsync(
            dataRoot, "1.2.0", fromVersion, schemaChangesPending: true, retryFailed,
            async ct =>
            {
                backups++;
                var created = await new BackupEngine(tools).CreateAsync(
                    new BackupSource(postgres.ConnectionString(active, DatabaseCreator.BackupRole, PostgresFixture.Passwords.Backup), null, "1.1.0"),
                    RetentionPolicy.PreUpdateKind, keys.GetOrCreate(), _work, TimeProvider.System, TimeZoneInfo.Utc, ct);
                return created.FilePath;
            },
            ct =>
            {
                migrations++;
                return new DatabaseMigrator(next).MigrateAsync(
                    postgres.ConnectionString(active, DatabaseCreator.MigratorRole, PostgresFixture.Passwords.Migrator), "1.2.0", ct);
            },
            async (file, ct) =>
            {
                var target = "pos_rb_" + Guid.NewGuid().ToString("N")[..12];
                var outcome = await new PreUpdateRollback(tools).RestoreAsync(
                    file, keys, postgres.SuperuserConnectionString, active, target, Path.Combine(_work, Guid.NewGuid().ToString("N")), dataDirectory: null,
                    (admin, database, c) => DatabaseCreator.CreateDatabaseOnlyAsync(admin, database, c), ct);
                outcome.IsComplete.ShouldBeTrue("Sello y conteos del backup.");
                active = outcome.Database; // Lo que hace RestorePreUpdateAsync con server.json.
                return outcome.Database;
            },
            TimeProvider.System,
            Ct);

        var first = await RunAsync();

        first.Outcome.ShouldBe(LegacyMigrateOutcome.FailedRestored, first.Error?.ToString());
        first.ExitCode.ShouldBe(LegacyMigrateGuard.ExitFailedRestored);
        (backups, migrations).ShouldBe((1, 1));
        first.RestoredDatabase.ShouldBe(active);
        // La BD que quedó activa está en el esquema anterior (el servidor viejo arranca sano); la original quedó a medio migrar.
        var restored = postgres.ConnectionString(active);
        (await db.ScalarAsync<bool>("SELECT to_regclass('system.prueba_version_nueva') IS NULL", restored)).ShouldBeTrue();
        (await db.ScalarAsync<string>("SELECT max(version) FROM system.schema_migrations WHERE kind = 'VERSIONED'", restored)).ShouldBe(fromVersion);
        (await db.ScalarAsync<long>("SELECT count(*) FROM system.installation", restored)).ShouldBe(1);
        (await db.ScalarAsync<bool>("SELECT to_regclass('system.prueba_version_nueva') IS NOT NULL")).ShouldBeTrue();
        var failure = LegacyMigrateGuard.ReadFailure(dataRoot).ShouldNotBeNull();
        (failure.Version, failure.FromSchemaVersion, failure.NeedsSupport).ShouldBe(("1.2.0", fromVersion, false));

        // La noche siguiente el actualizador viejo lo vuelve a pedir: no se hace backup, no se migra, la BD no se toca.
        var again = await RunAsync();
        again.Outcome.ShouldBe(LegacyMigrateOutcome.AlreadyFailed);
        again.ExitCode.ShouldBe(LegacyMigrateGuard.ExitAlreadyFailed);
        (backups, migrations).ShouldBe((1, 1));

        // Con otra versión del migrador (la siguiente publicación) sí se intenta; también con --retry-failed (soporte).
        LegacyMigrateGuard.AlreadyFailed(failure, "1.2.1", fromVersion).ShouldBeFalse();
        var retried = await RunAsync(retryFailed: true);
        retried.Outcome.ShouldBe(LegacyMigrateOutcome.FailedRestored, retried.Error?.ToString());
        (backups, migrations).ShouldBe((2, 2));
    }

    [Fact]
    public async Task Si_la_migracion_termina_bien_se_borra_la_marca()
    {
        var all = ScriptCatalog.Default;
        var next = new ScriptCatalog(all.Versioned.Append(NewTable).Concat(all.Repeatable).Concat(all.Always));
        var db = await postgres.CreateDatabaseAsync(migrate: true);
        await db.ExecuteAsync("INSERT INTO system.installation (installation_id, node_role, created_at) VALUES (gen_random_uuid(), 'ALL_IN_ONE', now())");
        var dataRoot = Path.Combine(_work, "data");
        Directory.CreateDirectory(Path.Combine(dataRoot, "updates"));
        await File.WriteAllTextAsync(
            Path.Combine(dataRoot, "updates", LegacyMigrateGuard.FailureFile),
            """{ "version": "1.2.0", "fromSchemaVersion": "otra", "needsSupport": false, "at": "2026-10-01T00:00:00Z" }""", Ct);
        var keys = new BackupKeyStore(Path.Combine(_work, "backup.key"));
        var tools = new ContainerPgTools(postgres);

        var result = await LegacyMigrateGuard.RunAsync(
            dataRoot, "1.2.0", all.LatestVersion, schemaChangesPending: true, retryFailed: false,
            async ct => (await new BackupEngine(tools).CreateAsync(
                new BackupSource(db.BackupConnectionString, null, "1.1.0"), RetentionPolicy.PreUpdateKind, keys.GetOrCreate(), _work, TimeProvider.System,
                TimeZoneInfo.Utc, ct)).FilePath,
            ct => new DatabaseMigrator(next).MigrateAsync(db.MigratorConnectionString, "1.2.0", ct),
            (_, _) => throw new InvalidOperationException("No debe restaurar."),
            TimeProvider.System,
            Ct);

        result.Outcome.ShouldBe(LegacyMigrateOutcome.Updated, result.Error?.ToString());
        result.ExitCode.ShouldBe(0);
        (await db.ScalarAsync<bool>("SELECT to_regclass('system.prueba_version_nueva') IS NOT NULL")).ShouldBeTrue();
        LegacyMigrateGuard.ReadFailure(dataRoot).ShouldBeNull();
    }

    [Fact]
    public void Solo_la_llamada_del_actualizador_viejo_activa_la_proteccion()
    {
        var dataRoot = Path.Combine(_work, "instalacion");
        Directory.CreateDirectory(Path.Combine(dataRoot, "config"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "updates"));
        var legacy = new Dictionary<string, string> { ["no-backup"] = "true", ["data-root"] = dataRoot };

        LegacyMigrateGuard.IsLegacyInvocation(legacy, dataRoot).ShouldBeFalse("Sin server.json no es una instalación.");
        File.WriteAllText(Path.Combine(dataRoot, "config", "server.json"), "{}");
        LegacyMigrateGuard.IsLegacyInvocation(legacy, dataRoot).ShouldBeTrue();
        LegacyMigrateGuard.IsLegacyInvocation(new Dictionary<string, string>(legacy) { ["connection"] = "Host=x" }, dataRoot).ShouldBeFalse();
        LegacyMigrateGuard.IsLegacyInvocation(new Dictionary<string, string>(legacy) { ["no-guard"] = "true" }, dataRoot).ShouldBeFalse();
        LegacyMigrateGuard.IsLegacyInvocation(new Dictionary<string, string> { ["data-root"] = dataRoot }, dataRoot)
            .ShouldBeFalse("Sin --no-backup el migrador ya hace su backup.");

        // El actualizador 1.2.0 o posterior deja su registro en curso durante todo su migrate: él hace la vuelta atrás.
        File.WriteAllText(Path.Combine(dataRoot, "updates", LegacyMigrateGuard.UpdaterJournal), "{}");
        LegacyMigrateGuard.IsLegacyInvocation(legacy, dataRoot).ShouldBeFalse();
    }
}
