using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Abstractions.Installation;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Licensing.Application;
using Pos.Modules.Licensing.Contracts;

namespace Pos.Modules.Licensing.Infrastructure;

/// <summary>SQL directo sobre el esquema <c>licensing</c>, en la conexión y transacción del contexto EF de la petición.</summary>
internal sealed class LicenseStateStore(PosDbContext context, IInstallationContext installation) : ILicenseStateStore
{
    public async Task<LicenseRecord?> GetAsync(CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
            """
            SELECT s.node_id AS NodeId, s.token AS Token, s.license_key_prefix AS LicenseKeyPrefix, s.activated_at AS ActivatedAt,
                   s.last_checkin_at AS LastCheckinAt, s.last_checkin_error AS LastCheckinError, s.revoked AS Revoked,
                   s.reactivation_required AS ReactivationRequired, s.deactivated AS Deactivated, s.clock_offset_seconds AS ClockOffsetSeconds,
                   s.max_observed_utc AS MaxObservedUtc, i.created_at AS DemoStartedAt
            FROM licensing.license_state s
            CROSS JOIN system.installation i
            WHERE s.node_id = @node
            """,
            new { node = installation.NodeId }, transaction, cancellationToken: cancellationToken));
        return row is null
            ? null
            : new LicenseRecord(
                row.NodeId, row.Token, row.LicenseKeyPrefix, Utc(row.ActivatedAt), Utc(row.LastCheckinAt), row.LastCheckinError, row.Revoked,
                row.ReactivationRequired, row.Deactivated, row.ClockOffsetSeconds, Utc(row.MaxObservedUtc), Utc(row.DemoStartedAt));
    }

    /// <summary>Crea la fila del nodo si no existe (al arrancar). La mayor hora observada empieza en la creación de la instalación.</summary>
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO licensing.license_state (node_id, max_observed_utc, created_at, updated_at)
            SELECT @node, i.created_at, now(), now() FROM system.installation i
            ON CONFLICT (node_id) DO NOTHING
            """,
            new { node = installation.NodeId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<string?> GetLastStateAsync(CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT last_state FROM licensing.license_state WHERE node_id = @node", new { node = installation.NodeId }, transaction,
            cancellationToken: cancellationToken));
    }

    public Task SetLastStateAsync(string state, CancellationToken cancellationToken) =>
        ExecuteAsync("UPDATE licensing.license_state SET last_state = @state, updated_at = now() WHERE node_id = @node", new { state, node = installation.NodeId },
            cancellationToken);

    public Task SaveTokenAsync(string token, string? keyPrefix, DateTimeOffset at, bool activation, long clockOffsetSeconds, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            UPDATE licensing.license_state SET token = @token, license_key_prefix = COALESCE(@keyPrefix, license_key_prefix),
                activated_at = CASE WHEN @activation THEN @at ELSE activated_at END, last_checkin_at = @at, last_checkin_error = NULL,
                revoked = false, reactivation_required = false, deactivated = false, clock_offset_seconds = @clockOffsetSeconds, updated_at = now()
            WHERE node_id = @node
            """,
            new { token, keyPrefix, at, activation, clockOffsetSeconds, node = installation.NodeId }, cancellationToken);

    public Task SaveCheckinErrorAsync(string error, bool? revoked, bool? reactivationRequired, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            UPDATE licensing.license_state SET last_checkin_error = left(@error, 500), revoked = COALESCE(@revoked, revoked),
                reactivation_required = COALESCE(@reactivationRequired, reactivation_required), updated_at = now()
            WHERE node_id = @node
            """,
            new { error, revoked, reactivationRequired, node = installation.NodeId }, cancellationToken);

    public Task ClearTokenAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            UPDATE licensing.license_state SET token = NULL, deactivated = true, revoked = false, reactivation_required = false,
                last_checkin_error = NULL, updated_at = now()
            WHERE node_id = @node
            """,
            new { node = installation.NodeId }, cancellationToken);

    public Task RaiseMaxObservedAsync(DateTimeOffset value, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "UPDATE licensing.license_state SET max_observed_utc = greatest(max_observed_utc, @value) WHERE node_id = @node",
            new { value, node = installation.NodeId }, cancellationToken);

    public Task RecordCheckinAsync(LicenseCheckinDto checkin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkin);
        return ExecuteAsync(
            """
            INSERT INTO licensing.checkins (id, node_id, occurred_at, kind, succeeded, error_code, subscription_status, duration_ms)
            VALUES (@id, @node, @OccurredAt, @Kind, @Succeeded, @ErrorCode, @SubscriptionStatus, @DurationMs)
            """,
            new
            {
                id = Guid.CreateVersion7(), node = installation.NodeId, checkin.OccurredAt, checkin.Kind, checkin.Succeeded, checkin.ErrorCode,
                checkin.SubscriptionStatus, checkin.DurationMs,
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<LicenseCheckinDto>> ListCheckinsAsync(int limit, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<CheckinRow>(new CommandDefinition(
            """
            SELECT occurred_at AS OccurredAt, kind AS Kind, succeeded AS Succeeded, error_code AS ErrorCode, subscription_status AS SubscriptionStatus,
                   duration_ms AS DurationMs
            FROM licensing.checkins WHERE node_id = @node ORDER BY occurred_at DESC LIMIT @limit
            """,
            new { node = installation.NodeId, limit }, transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new LicenseCheckinDto(Utc(r.OccurredAt), r.Kind, r.Succeeded, r.ErrorCode, r.SubscriptionStatus, r.DurationMs))];
    }

    public async Task<LicenseSiteInfo?> SiteAsync(CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return null;
        }

        var (connection, transaction) = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SiteRow>(new CommandDefinition(
            """
            SELECT c.identification_number AS Number, c.check_digit AS CheckDigit, c.legal_name AS LegalName, b.name AS BranchName,
                   (SELECT count(*)::int FROM cash.cash_sessions s WHERE s.status <> 'CLOSED') AS OpenSessions
            FROM org.companies c
            LEFT JOIN org.branches b ON b.id = @branch
            WHERE c.id = @company
            """,
            new { company = companyId, branch = installation.BranchId }, transaction, cancellationToken: cancellationToken));
        return row is null
            ? null
            : new LicenseSiteInfo(row.CheckDigit is null ? row.Number : $"{row.Number}-{row.CheckDigit}", row.LegalName, row.BranchName, row.OpenSessions);
    }

    private async Task ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
    }

    private async Task<(DbConnection Connection, DbTransaction? Transaction)> OpenAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    private sealed record StateRow(
        Guid NodeId, string? Token, string? LicenseKeyPrefix, DateTime? ActivatedAt, DateTime? LastCheckinAt, string? LastCheckinError, bool Revoked,
        bool ReactivationRequired, bool Deactivated, long ClockOffsetSeconds, DateTime MaxObservedUtc, DateTime DemoStartedAt);

    private sealed record CheckinRow(DateTime OccurredAt, string Kind, bool Succeeded, string? ErrorCode, string? SubscriptionStatus, int DurationMs);

    private sealed record SiteRow(string Number, string? CheckDigit, string LegalName, string? BranchName, int OpenSessions);
}
