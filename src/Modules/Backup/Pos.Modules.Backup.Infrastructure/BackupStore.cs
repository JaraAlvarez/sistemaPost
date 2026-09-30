using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Abstractions.Installation;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Backup.Application;
using Pos.Modules.Backup.Contracts;

namespace Pos.Modules.Backup.Infrastructure;

/// <summary>SQL directo sobre el esquema <c>backup</c>, en la conexión y transacción del contexto EF de la petición.</summary>
internal sealed class BackupStore(PosDbContext context, IInstallationContext installation) : IBackupStore
{
    private const string RunColumns = """
        r.id AS Id, r.kind AS Kind, r.started_at AS StartedAt, r.finished_at AS FinishedAt, r.succeeded AS Succeeded, r.verified AS Verified,
        r.file_name AS FileName, r.size_bytes AS SizeBytes, r.schema_version AS SchemaVersion, r.audit_seal_no AS AuditSealNo,
        r.audit_seal_code AS AuditSealCode, r.error AS Error
        """;

    private const string DestinationColumns = """
        id AS Id, kind AS Kind, name AS Name, path AS Path, volume_label AS VolumeLabel, endpoint AS Endpoint, region AS Region, bucket AS Bucket,
        prefix AS Prefix, access_key AS AccessKey, (secret IS NOT NULL) AS HasSecret, on_scheduled AS OnScheduled, on_nightly AS OnNightly,
        on_closing AS OnClosing, on_manual AS OnManual, keep_daily AS KeepDaily, keep_weekly AS KeepWeekly, keep_monthly AS KeepMonthly,
        is_active AS IsActive, last_status AS LastStatus, last_error AS LastError, last_success_at AS LastSuccessAt
        """;

    public async Task<IReadOnlyList<BackupRunDto>> ListRunsAsync(int limit, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var runs = (await connection.QueryAsync<RunRow>(new CommandDefinition(
            $"SELECT {RunColumns} FROM backup.backup_runs r WHERE r.node_id = @node ORDER BY r.started_at DESC LIMIT @limit",
            new { node = installation.NodeId, limit }, transaction, cancellationToken: cancellationToken))).ToList();
        return await WithCopiesAsync(connection, transaction, runs, cancellationToken);
    }

    public async Task<BackupRunDto?> GetRunAsync(Guid id, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var runs = (await connection.QueryAsync<RunRow>(new CommandDefinition(
            $"SELECT {RunColumns} FROM backup.backup_runs r WHERE r.id = @id", new { id }, transaction, cancellationToken: cancellationToken))).ToList();
        return await WithCopiesAsync(connection, transaction, runs, cancellationToken) is [var run, ..] ? run : null;
    }

    public Task<IReadOnlyList<BackupDestinationDto>> ListDestinationsAsync(CancellationToken cancellationToken) =>
        ListDestinationsAsync(installation.CompanyId ?? Guid.Empty, cancellationToken);

    public async Task<IReadOnlyList<BackupDestinationDto>> ListDestinationsAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DestinationRow>(new CommandDefinition(
            $"SELECT {DestinationColumns} FROM backup.destinations WHERE company_id = @company ORDER BY kind = 'LOCAL' DESC, name",
            new { company = companyId }, transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(d => new BackupDestinationDto(
            d.Id, d.Kind, d.Name, d.Path, d.VolumeLabel, d.Endpoint, d.Region, d.Bucket, d.Prefix, d.AccessKey, d.HasSecret, d.OnScheduled, d.OnNightly,
            d.OnClosing, d.OnManual, d.KeepDaily, d.KeepWeekly, d.KeepMonthly, d.IsActive, d.LastStatus, d.LastError,
            d.LastSuccessAt is { } at ? Utc(at) : null))];
    }

    public async Task SaveDestinationAsync(
        Guid id, Guid companyId, DestinationInput input, byte[]? protectedSecret, bool isNew, Guid? actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var sql = isNew
            ? """
              INSERT INTO backup.destinations (id, company_id, kind, name, path, volume_label, endpoint, region, bucket, prefix, access_key, secret,
                  on_scheduled, on_nightly, on_closing, on_manual, keep_daily, keep_weekly, keep_monthly, is_active, created_at, created_by)
              VALUES (@id, @companyId, @Kind, @Name, @Path, @VolumeLabel, @Endpoint, COALESCE(@Region, 'us-east-1'), @Bucket, @Prefix, @AccessKey, @secret,
                  @OnScheduled, @OnNightly, @OnClosing, @OnManual, @KeepDaily, @KeepWeekly, @KeepMonthly, @IsActive, now(),
                  COALESCE(@actorId, (SELECT u.id FROM identity.users u WHERE u.company_id = @companyId AND u.username = 'system')))
              """
            : """
              UPDATE backup.destinations SET name = @Name, path = @Path, volume_label = @VolumeLabel, endpoint = @Endpoint,
                  region = COALESCE(@Region, region), bucket = @Bucket, prefix = @Prefix, access_key = @AccessKey, secret = COALESCE(@secret, secret),
                  on_scheduled = @OnScheduled, on_nightly = @OnNightly, on_closing = @OnClosing, on_manual = @OnManual, keep_daily = @KeepDaily,
                  keep_weekly = @KeepWeekly, keep_monthly = @KeepMonthly, is_active = @IsActive, updated_at = now(), updated_by = @actorId
              WHERE id = @id
              """;
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            id, companyId, input.Kind, input.Name, input.Path, input.VolumeLabel, input.Endpoint, input.Region, input.Bucket, input.Prefix, input.AccessKey,
            secret = protectedSecret, input.OnScheduled, input.OnNightly, input.OnClosing, input.OnManual, input.KeepDaily, input.KeepWeekly,
            input.KeepMonthly, input.IsActive, actorId,
        }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<RestoreTestDto>> ListRestoreTestsAsync(int limit, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<RestoreRow>(new CommandDefinition(
            """
            SELECT t.id AS Id, t.run_id AS RunId, t.started_at AS StartedAt, t.finished_at AS FinishedAt, t.succeeded AS Succeeded, t.audit_valid AS AuditValid,
                   t.seal_matches AS SealMatches, t.counts_match AS CountsMatch, t.error AS Error
            FROM backup.restore_tests t JOIN backup.backup_runs r ON r.id = t.run_id
            WHERE r.node_id = @node ORDER BY t.started_at DESC LIMIT @limit
            """,
            new { node = installation.NodeId, limit }, transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new RestoreTestDto(r.Id, r.RunId, Utc(r.StartedAt), Utc(r.FinishedAt), r.Succeeded, r.AuditValid, r.SealMatches, r.CountsMatch, r.Error))];
    }

    public async Task<RecoveryKeyState?> LatestRecoveryKeyAsync(CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<RecoveryKeyState>(new CommandDefinition(
            """
            SELECT version AS Version, key_id AS KeyId, wrapped_key::text AS WrappedKeyJson, (confirmed_at IS NOT NULL) AS Confirmed
            FROM backup.recovery_keys ORDER BY version DESC LIMIT 1
            """,
            transaction: transaction, cancellationToken: cancellationToken));
    }

    public async Task SaveRecoveryKeyAsync(int version, string keyId, string wrappedKeyJson, Guid userId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO backup.recovery_keys (version, key_id, wrapped_key, created_at, created_by)
            VALUES (@version, @keyId, @wrappedKeyJson::jsonb, @at, @userId)
            """,
            new { version, keyId, wrappedKeyJson, at, userId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task ConfirmRecoveryKeyAsync(int version, Guid userId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE backup.recovery_keys SET confirmed_at = @at, confirmed_by = @userId WHERE version = @version AND confirmed_at IS NULL",
            new { version, userId, at }, transaction, cancellationToken: cancellationToken));
    }

    private async Task<(DbConnection Connection, DbTransaction? Transaction)> OpenAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }

    private static async Task<IReadOnlyList<BackupRunDto>> WithCopiesAsync(
        DbConnection connection, DbTransaction? transaction, List<RunRow> runs, CancellationToken cancellationToken)
    {
        if (runs.Count == 0)
        {
            return [];
        }

        var copies = (await connection.QueryAsync<CopyRow>(new CommandDefinition(
            """
            SELECT c.run_id AS RunId, c.destination_id AS DestinationId, d.name AS DestinationName, c.status AS Status, c.location AS Location,
                   c.copied_at AS CopiedAt, c.error AS Error
            FROM backup.backup_copies c JOIN backup.destinations d ON d.id = c.destination_id
            WHERE c.run_id = ANY(@ids)
            """,
            new { ids = runs.Select(r => r.Id).ToArray() }, transaction, cancellationToken: cancellationToken))).ToLookup(c => c.RunId);
        return [.. runs.Select(r => new BackupRunDto(
            r.Id, r.Kind, Utc(r.StartedAt), Utc(r.FinishedAt), r.Succeeded, r.Verified, r.FileName, r.SizeBytes, r.SchemaVersion, r.AuditSealNo,
            r.AuditSealCode, r.Error,
            [.. copies[r.Id].Select(c => new BackupCopyDto(c.DestinationId, c.DestinationName, c.Status, c.Location, c.CopiedAt is { } at ? Utc(at) : null, c.Error))]))];
    }

    internal static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class RunRow
    {
        public Guid Id { get; set; }

        public string Kind { get; set; } = string.Empty;

        public DateTime StartedAt { get; set; }

        public DateTime FinishedAt { get; set; }

        public bool Succeeded { get; set; }

        public bool Verified { get; set; }

        public string? FileName { get; set; }

        public long? SizeBytes { get; set; }

        public string? SchemaVersion { get; set; }

        public long? AuditSealNo { get; set; }

        public string? AuditSealCode { get; set; }

        public string? Error { get; set; }
    }

    private sealed class DestinationRow
    {
        public Guid Id { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? Path { get; set; }

        public string? VolumeLabel { get; set; }

        public string? Endpoint { get; set; }

        public string? Region { get; set; }

        public string? Bucket { get; set; }

        public string? Prefix { get; set; }

        public string? AccessKey { get; set; }

        public bool HasSecret { get; set; }

        public bool OnScheduled { get; set; }

        public bool OnNightly { get; set; }

        public bool OnClosing { get; set; }

        public bool OnManual { get; set; }

        public int KeepDaily { get; set; }

        public int KeepWeekly { get; set; }

        public int KeepMonthly { get; set; }

        public bool IsActive { get; set; }

        public string? LastStatus { get; set; }

        public string? LastError { get; set; }

        public DateTime? LastSuccessAt { get; set; }
    }

    private sealed class CopyRow
    {
        public Guid RunId { get; set; }

        public Guid DestinationId { get; set; }

        public string DestinationName { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? Location { get; set; }

        public DateTime? CopiedAt { get; set; }

        public string? Error { get; set; }
    }

    private sealed class RestoreRow
    {
        public Guid Id { get; set; }

        public Guid RunId { get; set; }

        public DateTime StartedAt { get; set; }

        public DateTime FinishedAt { get; set; }

        public bool Succeeded { get; set; }

        public bool? AuditValid { get; set; }

        public bool? SealMatches { get; set; }

        public bool? CountsMatch { get; set; }

        public string? Error { get; set; }
    }
}
