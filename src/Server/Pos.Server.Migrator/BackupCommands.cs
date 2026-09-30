using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Infrastructure;
using Pos.Infrastructure.Backup;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Security;
using Pos.Modules.Inventory.Infrastructure;
using Pos.Server.Migrations;
using Pos.SharedKernel.Time;

namespace Pos.Server.Migrator;

/// <summary>
/// Comandos de backup de la consola (Fase 11): <c>backup</c>, <c>verify-backup</c>, <c>restore</c> y el backup obligatorio antes de
/// <c>migrate</c> (RN-BAK-03). Restaurar se hace solo aquí, frente al servidor (D11-09).
/// </summary>
internal static class BackupCommands
{
    public const int ExitBackupFailed = 6;

    private static readonly TimeZoneInfo Colombia = BusinessTimeZones.Colombia;

    public static string DataRoot(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("data-root", out var root) && !string.IsNullOrWhiteSpace(root))
        {
            return Path.GetFullPath(root);
        }

        if (Environment.GetEnvironmentVariable("POS_DATA_ROOT") is { Length: > 0 } fromEnvironment)
        {
            return Path.GetFullPath(fromEnvironment);
        }

        var product = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PosProductName")?.Value ?? "PosSupermercado";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), product);
    }

    public static string? BackupConnection(IReadOnlyDictionary<string, string> options, string dataRoot) =>
        options.GetValueOrDefault("backup-connection") is { Length: > 0 } fromOption ? fromOption
        : Environment.GetEnvironmentVariable("POS_BACKUP_CONNECTION") is { Length: > 0 } fromEnvironment ? fromEnvironment
        : ProtectedSecret.Reveal(ReadConfig(dataRoot)?["Pos"]?["Database"]?["BackupConnectionString"]?.GetValue<string>());

    /// <summary>Crea, verifica y registra un backup (si el esquema <c>backup</c> ya existe). Devuelve la ruta del paquete.</summary>
    public static async Task<string> BackupAsync(
        IReadOnlyDictionary<string, string> options, string kind, string appVersion, string? migratorConnection, CancellationToken cancellationToken)
    {
        var dataRoot = DataRoot(options);
        var connection = BackupConnection(options, dataRoot)
            ?? throw new ArgumentException("Falta la conexión del rol pos_backup: --backup-connection, POS_BACKUP_CONNECTION o server.json.");
        var environment = new BackupEnvironment(dataRoot, Path.Combine(dataRoot, "config", "server.json"), appVersion, connection);
        var keys = new BackupKeyStore(environment.KeyFile);
        var engine = new BackupEngine(new PgClientTools(options.GetValueOrDefault("pg-bin")));
        var output = options.GetValueOrDefault("output") ?? environment.LocalDirectory;
        var started = DateTimeOffset.UtcNow;
        var key = keys.GetOrCreate();
        var created = await engine.CreateAsync(
            new BackupSource(connection, environment.ServerConfigFile, appVersion), kind, key, output, TimeProvider.System, Colombia, cancellationToken);
        var verification = await engine.VerifyAsync(created.FilePath, key, cancellationToken);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Backup {kind} verificado: {created.FilePath} ({created.Size / 1024.0 / 1024.0:0.0} MB, {verification.DumpEntries} objetos, sello #{created.Header.AuditSealNo})."));
        if (created.Header.RecoveryKey is null)
        {
            Console.WriteLine("ATENCIÓN: aún no hay código de recuperación; este backup solo se puede restaurar en ESTE equipo.");
        }

        if (migratorConnection is not null && string.Equals(Path.GetFullPath(output), Path.GetFullPath(environment.LocalDirectory), StringComparison.OrdinalIgnoreCase))
        {
            await RegisterAsync(migratorConnection, created, verification, kind, started, cancellationToken);
        }

        return created.FilePath;
    }

    public static async Task<int> VerifyAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var file = Required(options, "file");
        var dataRoot = DataRoot(options);
        BackupHeader header;
        await using (var input = File.OpenRead(file))
        {
            header = await BackupPackage.ReadHeaderAsync(input, cancellationToken);
        }

        PrintHeader(header);
        var key = BackupRestoreProcedure.ChooseKey(header, new RestoreRequest(
            file, options.GetValueOrDefault("recovery-code"), new BackupKeyStore(Path.Combine(dataRoot, "config", "backup.key")), string.Empty, string.Empty,
            string.Empty, string.Empty));
        if (key is null)
        {
            Console.WriteLine("No se pudo abrir: indique el código de recuperación correcto (--recovery-code).");
            return 1;
        }

        var verification = await new BackupEngine(new PgClientTools(options.GetValueOrDefault("pg-bin"))).VerifyAsync(file, key, cancellationToken);
        Console.WriteLine($"Backup íntegro: huellas correctas y volcado legible ({verification.DumpEntries} objetos).");
        return 0;
    }

    /// <summary>
    /// Restauración (D11-09): confirma, respalda el estado actual, restaura en una BD nueva, migra, verifica auditoría, sello, conteos y
    /// kardex, incrementa la vida del nodo, audita y cambia la BD activa en server.json. La BD anterior no se borra.
    /// </summary>
    public static async Task<int> RestoreAsync(
        IReadOnlyDictionary<string, string> options, string appVersion, DatabaseMigrator migrator, CancellationToken cancellationToken)
    {
        var file = Required(options, "file");
        var superuser = Required(options, "superuser");
        var dataRoot = DataRoot(options);
        var config = ReadConfig(dataRoot);
        var current = ProtectedSecret.Reveal(config?["Pos"]?["Database"]?["ConnectionString"]?.GetValue<string>());
        var migratorConnection = options.GetValueOrDefault("migrator-connection")
            ?? ProtectedSecret.Reveal(config?["Pos"]?["Database"]?["MigratorConnectionString"]?.GetValue<string>())
            ?? Environment.GetEnvironmentVariable("POS_MIGRATOR_CONNECTION")
            ?? throw new ArgumentException("Falta la conexión del rol pos_migrator (--migrator-connection o POS_MIGRATOR_CONNECTION).");

        BackupHeader header;
        await using (var input = File.OpenRead(file))
        {
            header = await BackupPackage.ReadHeaderAsync(input, cancellationToken);
        }

        PrintHeader(header);
        if (!options.ContainsKey("yes"))
        {
            Console.WriteLine("Revise los datos. Detenga el servicio del servidor y repita el comando con --yes para restaurar.");
            return 2;
        }

        // 1. Backup del estado actual (por si se eligió mal el archivo).
        if (current is not null && !options.ContainsKey("skip-current-backup"))
        {
            await BackupAsync(options, "PRE_RESTORE", appVersion, migratorConnection, cancellationToken);
        }

        // 2. Restaurar en una BD NUEVA, migrar y verificar.
        var target = options.GetValueOrDefault("database")
            ?? "pos_r" + DateTime.UtcNow.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        var work = Path.Combine(Path.GetTempPath(), "pos-restore-" + Guid.NewGuid().ToString("N"));
        RestoreOutcome outcome;
        try
        {
            outcome = await new BackupRestoreProcedure(new PgClientTools(options.GetValueOrDefault("pg-bin"))).RunAsync(
                new RestoreRequest(file, options.GetValueOrDefault("recovery-code"), new BackupKeyStore(Path.Combine(dataRoot, "config", "backup.key")),
                    superuser, target, ScriptCatalog.Default.LatestVersion ?? string.Empty, work),
                (admin, database, ct) => DatabaseCreator.CreateDatabaseOnlyAsync(admin, database, ct),
                async (connection, ct) => await migrator.MigrateAsync(connection, appVersion, ct),
                migratorConnection,
                cancellationToken);
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }

        Console.WriteLine($"Restaurado en la BD '{target}'. Auditoría: {(outcome.Audit.IsValid ? "íntegra" : "CON HALLAZGOS")} · sello: "
            + $"{(outcome.SealMatches ? "coincide" : "NO COINCIDE")} · conteos: {(outcome.CountsMatch ? "iguales" : "DIFERENTES")}.");
        var targetMigrator = new NpgsqlConnectionStringBuilder(migratorConnection) { Database = target }.ConnectionString;
        await using (var dataSource = NpgsqlDataSource.Create(targetMigrator))
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            var discrepancies = await StockLedgerMaintenance.FindDiscrepanciesAsync(connection, null, cancellationToken);
            Console.WriteLine(discrepancies.Count == 0 ? "Kardex: todos los saldos cuadran." : $"ATENCIÓN: {discrepancies.Count} saldos no cuadran con el kardex.");
        }

        if (!outcome.Audit.IsValid || !outcome.SealMatches)
        {
            Console.WriteLine($"NO se cambió la BD activa: la BD '{target}' queda para revisión.");
            return 4;
        }

        // 3. Vida del nodo + auditoría en la BD restaurada.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPosInfrastructure(Colombia);
        services.AddPosPersistence(new PersistenceOptions { ConnectionString = targetMigrator, RunBackgroundServices = false });
        await using (var provider = services.BuildServiceProvider())
        {
            var previous = current is null ? "(ninguna)" : new NpgsqlConnectionStringBuilder(current).Database ?? "(desconocida)";
            await new BackupRestoreFinisher(provider).FinishAsync(outcome.Header, file, previous, cancellationToken);
        }

        // 4. Cambiar la BD activa en server.json.
        if (config is not null && SwitchDatabase(config, target))
        {
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "config", "server.json"),
                config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            Console.WriteLine($"server.json ahora apunta a '{target}'. Arranque el servicio del servidor.");
        }
        else
        {
            Console.WriteLine($"Actualice la configuración del servidor para usar la BD '{target}' y arranque el servicio.");
        }

        return 0;
    }

    private static bool SwitchDatabase(JsonNode config, string database)
    {
        if (config["Pos"]?["Database"] is not JsonObject section)
        {
            return false;
        }

        foreach (var name in new[] { "ConnectionString", "BackupConnectionString", "MigratorConnectionString" })
        {
            if (section[name]?.GetValue<string>() is not { Length: > 0 } value)
            {
                continue;
            }

            var protectedValue = value.StartsWith(ProtectedSecret.Prefix, StringComparison.Ordinal);
            var plain = ProtectedSecret.Reveal(value)!;
            var updated = new NpgsqlConnectionStringBuilder(plain) { Database = database }.ConnectionString;
            section[name] = protectedValue && OperatingSystem.IsWindows() ? ProtectedSecret.Protect(updated) : updated;
        }

        return true;
    }

    private static async Task RegisterAsync(
        string migratorConnection, BackupCreated created, BackupVerification verification, string kind, DateTimeOffset started, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(migratorConnection);
        await connection.OpenAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('backup.backup_runs') IS NOT NULL"))
        {
            return;
        }

        var runId = Guid.CreateVersion7();
        var header = created.Header;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO backup.backup_runs (id, node_id, company_id, kind, started_at, finished_at, succeeded, file_name, size_bytes, payload_sha256,
                app_version, schema_version, audit_seal_no, audit_seal_code, verified, dump_entries)
            SELECT @runId, installation_id, home_company_id, @kind, @started, now(), true, @fileName, @size, @sha, @appVersion, @schema, @sealNo, @sealCode,
                true, @entries
            FROM system.installation;
            INSERT INTO backup.backup_copies (id, run_id, destination_id, status, location, attempts, copied_at)
            SELECT @copyId, @runId, d.id, 'COPIED', @location, 1, now() FROM backup.destinations d WHERE d.kind = 'LOCAL';
            """,
            new
            {
                runId, kind, started, fileName = Path.GetFileName(created.FilePath), size = created.Size, sha = header.PayloadSha256,
                appVersion = header.AppVersion, schema = header.SchemaVersion, sealNo = header.AuditSealNo, sealCode = header.AuditSealCode,
                entries = verification.DumpEntries, copyId = Guid.CreateVersion7(), location = created.FilePath,
            },
            cancellationToken: cancellationToken));
    }

    private static JsonNode? ReadConfig(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "config", "server.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
    }

    private static void PrintHeader(BackupHeader header)
    {
        Console.WriteLine($"Empresa:  {header.CompanyName} ({header.CompanyIdentification}) · sucursal {header.BranchCode}");
        Console.WriteLine($"Fecha:    {TimeZoneInfo.ConvertTime(header.CreatedAt, Colombia):yyyy-MM-dd HH:mm} · tipo {header.Kind}");
        Console.WriteLine($"Versión:  app {header.AppVersion} · esquema {header.SchemaVersion} · sello de auditoría #{header.AuditSealNo} {header.AuditSealCode}");
        Console.WriteLine($"Datos:    {string.Join(" · ", header.Counts.Select(c => $"{c.Key} {c.Value}"))}");
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Falta la opción --{name}.");
}
