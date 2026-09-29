using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Pos.Server.Migrations;

/// <summary>Una migración registrada en <c>system.schema_migrations</c>.</summary>
public sealed record AppliedMigration(MigrationKind Kind, string? Version, string ScriptName, string Checksum, DateTimeOffset AppliedAt);

/// <summary>Estado del esquema frente a los scripts de esta versión de la aplicación.</summary>
public sealed record MigrationStatus(
    string? DatabaseVersion,
    string? ExpectedVersion,
    IReadOnlyList<MigrationScript> PendingVersioned,
    IReadOnlyList<MigrationScript> PendingRepeatable,
    IReadOnlyList<string> ChecksumMismatches,
    IReadOnlyList<string> UnknownVersions)
{
    public bool IsUpToDate => PendingVersioned.Count == 0 && PendingRepeatable.Count == 0 && !HasErrors;

    public bool HasErrors => ChecksumMismatches.Count > 0 || UnknownVersions.Count > 0;
}

/// <summary>Resultado de <see cref="DatabaseMigrator.MigrateAsync"/>.</summary>
public sealed record MigrationReport(IReadOnlyList<string> AppliedScripts, string? SchemaVersion);

/// <summary>
/// Migrador SQL-first (docs/fases/fase-02-propuesta.md §7):
/// cada script versionado en su propia transacción; checksum inmutable; repetibles cuando cambia su checksum;
/// scripts "always" al final; bloqueo consultivo para que dos procesos no migren a la vez.
/// Se conecta con el rol pos_migrator y crea los objetos como pos_owner (SET ROLE).
/// </summary>
public sealed partial class DatabaseMigrator(ScriptCatalog catalog, ILogger<DatabaseMigrator>? logger = null)
{
    /// <summary>Clave del bloqueo consultivo (pg_advisory_lock) del migrador.</summary>
    public const long AdvisoryLockKey = 7_310_402_001;

    public const string OwnerRole = "pos_owner";

    private const string BootstrapSql = """
        CREATE SCHEMA IF NOT EXISTS system;
        CREATE TABLE IF NOT EXISTS system.schema_migrations (
            id            integer       GENERATED ALWAYS AS IDENTITY,
            kind          varchar(12)   NOT NULL,
            version       varchar(20),
            script_name   varchar(200)  NOT NULL,
            module        varchar(30)   NOT NULL,
            description   varchar(200)  NOT NULL,
            checksum      char(64)      NOT NULL,
            applied_at    timestamptz   NOT NULL,
            applied_by    varchar(60)   NOT NULL,
            execution_ms  integer       NOT NULL,
            app_version   varchar(40)   NOT NULL,
            CONSTRAINT pk_schema_migrations PRIMARY KEY (id),
            CONSTRAINT ck_schema_migrations__kind CHECK (kind IN ('VERSIONED', 'REPEATABLE')),
            CONSTRAINT ck_schema_migrations__version CHECK ((kind = 'VERSIONED') = (version IS NOT NULL))
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_schema_migrations__version
            ON system.schema_migrations (version) WHERE kind = 'VERSIONED';
        """;

    private readonly ILogger _logger = logger ?? NullLogger<DatabaseMigrator>.Instance;

    public ScriptCatalog Catalog { get; } = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public async Task<MigrationReport> MigrateAsync(string connectionString, string appVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await ExecuteAsync(connection, $"SELECT pg_advisory_lock({AdvisoryLockKey})", null, cancellationToken);
        try
        {
            await ExecuteAsync(connection, $"SET ROLE {OwnerRole}", null, cancellationToken);
            await ExecuteAsync(connection, BootstrapSql, null, cancellationToken);

            var status = await GetStatusAsync(connection, cancellationToken);
            ThrowIfInvalid(status);

            var applied = new List<string>();
            foreach (var script in status.PendingVersioned)
            {
                await ApplyAsync(connection, script, appVersion, cancellationToken);
                applied.Add(script.Name);
            }

            foreach (var script in status.PendingRepeatable)
            {
                await ApplyAsync(connection, script, appVersion, cancellationToken);
                applied.Add(script.Name);
            }

            foreach (var script in Catalog.Always)
            {
                await ApplyAsync(connection, script, appVersion, cancellationToken);
            }

            var version = await GetDatabaseVersionAsync(connection, cancellationToken);
            LogCompleted(_logger, applied.Count, version);
            return new MigrationReport(applied, version);
        }
        finally
        {
            await ExecuteAsync(connection, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", null, CancellationToken.None);
        }
    }

    public async Task<MigrationStatus> GetStatusAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        return await GetStatusAsync(connection, cancellationToken);
    }

    /// <summary>Lanza <see cref="MigrationException"/> si hay checksums alterados, versiones desconocidas o pendientes.</summary>
    public async Task VerifyAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(connectionString, cancellationToken);
        ThrowIfInvalid(status);
        if (!status.IsUpToDate)
        {
            throw new MigrationException(
                MigrationException.PendingMigrations,
                $"Hay {status.PendingVersioned.Count + status.PendingRepeatable.Count} migraciones pendientes.");
        }
    }

    /// <summary>Versión de esquema aplicada (última migración versionada), o <c>null</c> si la BD no está migrada.</summary>
    public static async Task<string?> GetDatabaseVersionAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var exists = new NpgsqlCommand("SELECT to_regclass('system.schema_migrations') IS NOT NULL", connection);
        if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
        {
            return null;
        }

        var versions = await ReadAppliedAsync(connection, cancellationToken);
        return versions.Where(a => a.Kind == MigrationKind.Versioned)
            .Select(a => a.Version!)
            .OrderBy(v => v, Comparer<string>.Create(MigrationScript.CompareVersions))
            .LastOrDefault();
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void ThrowIfInvalid(MigrationStatus status)
    {
        if (status.UnknownVersions.Count > 0)
        {
            throw new MigrationException(
                MigrationException.DatabaseNewer,
                $"La base de datos tiene migraciones que esta versión de la aplicación no conoce ({string.Join(", ", status.UnknownVersions)}). " +
                "Instale la versión de la aplicación correspondiente.");
        }

        if (status.ChecksumMismatches.Count > 0)
        {
            throw new MigrationException(
                MigrationException.ChecksumMismatch,
                $"Scripts ya aplicados fueron modificados: {string.Join(", ", status.ChecksumMismatches)}. " +
                "Los scripts aplicados son inmutables: las correcciones van en un script nuevo.");
        }
    }

    private async Task<MigrationStatus> GetStatusAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = new NpgsqlCommand("SELECT to_regclass('system.schema_migrations') IS NOT NULL", connection);
        var applied = (bool)(await exists.ExecuteScalarAsync(cancellationToken))!
            ? await ReadAppliedAsync(connection, cancellationToken)
            : [];

        var appliedVersioned = applied.Where(a => a.Kind == MigrationKind.Versioned)
            .ToDictionary(a => a.Version!, StringComparer.Ordinal);
        var knownVersions = Catalog.Versioned.Select(s => s.Version!).ToHashSet(StringComparer.Ordinal);

        var mismatches = Catalog.Versioned
            .Where(s => appliedVersioned.TryGetValue(s.Version!, out var a) && a.Checksum != s.Checksum)
            .Select(s => s.Name)
            .ToList();
        var unknown = appliedVersioned.Keys.Where(v => !knownVersions.Contains(v)).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var pendingVersioned = Catalog.Versioned.Where(s => !appliedVersioned.ContainsKey(s.Version!)).ToList();

        // Último checksum aplicado de cada repetible.
        var lastRepeatable = applied.Where(a => a.Kind == MigrationKind.Repeatable)
            .GroupBy(a => a.ScriptName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.AppliedAt).Last().Checksum, StringComparer.Ordinal);
        var pendingRepeatable = Catalog.Repeatable
            .Where(s => !lastRepeatable.TryGetValue(s.Name, out var checksum) || checksum != s.Checksum)
            .ToList();

        var dbVersion = appliedVersioned.Keys
            .OrderBy(v => v, Comparer<string>.Create(MigrationScript.CompareVersions))
            .LastOrDefault();

        return new MigrationStatus(dbVersion, Catalog.LatestVersion, pendingVersioned, pendingRepeatable, mismatches, unknown);
    }

    private static async Task<List<AppliedMigration>> ReadAppliedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT kind, version, script_name, checksum, applied_at FROM system.schema_migrations ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AppliedMigration>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AppliedMigration(
                reader.GetString(0) == "VERSIONED" ? MigrationKind.Versioned : MigrationKind.Repeatable,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return result;
    }

    private async Task ApplyAsync(NpgsqlConnection connection, MigrationScript script, string appVersion, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, script.Sql, transaction, cancellationToken);

            if (script.Kind != MigrationKind.Always)
            {
                await using var record = new NpgsqlCommand(
                    """
                    INSERT INTO system.schema_migrations
                        (kind, version, script_name, module, description, checksum, applied_at, applied_by, execution_ms, app_version)
                    VALUES (@kind, @version, @name, @module, @description, @checksum, now(), session_user, @ms, @appVersion)
                    """,
                    connection,
                    transaction);
                record.Parameters.AddWithValue("kind", script.Kind == MigrationKind.Versioned ? "VERSIONED" : "REPEATABLE");
                record.Parameters.AddWithValue("version", (object?)script.Version ?? DBNull.Value);
                record.Parameters.AddWithValue("name", script.Name);
                record.Parameters.AddWithValue("module", script.Module);
                record.Parameters.AddWithValue("description", script.Description);
                record.Parameters.AddWithValue("checksum", script.Checksum);
                record.Parameters.AddWithValue("ms", (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                record.Parameters.AddWithValue("appVersion", appVersion.Length > 40 ? appVersion[..40] : appVersion);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            LogApplied(_logger, script.Name, elapsedMs);
        }
        catch (PostgresException ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new MigrationException(
                MigrationException.ScriptFailed,
                $"El script {script.Name} falló y no se aplicó ({ex.SqlState}: {ex.MessageText}).",
                ex);
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // El SQL proviene de scripts incrustados y revisados, no de datos del usuario.
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Migración aplicada: {Script} ({ElapsedMs:0} ms)")]
    private static partial void LogApplied(ILogger logger, string script, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Migración completada: {Count} scripts aplicados; versión de esquema {Version}")]
    private static partial void LogCompleted(ILogger logger, int count, string? version);
}
