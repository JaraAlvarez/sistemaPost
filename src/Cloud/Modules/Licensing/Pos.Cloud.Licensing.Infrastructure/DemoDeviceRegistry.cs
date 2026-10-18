using System.Data.Common;
using System.Net;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.Licensing.Application;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.Licensing.Infrastructure;

/// <summary>
/// <c>licensing.demo_devices</c> con SQL directo, en la conexión y transacción del contexto de la petición. Los registros se serializan
/// con un candado de la transacción POR COMPONENTE de la huella (<c>pg_advisory_xact_lock(hashtext(hash))</c>, en orden para no
/// bloquearse entre sí): dos consultas del mismo equipo a la vez no crean dos filas, y equipos distintos no se esperan.
/// </summary>
internal sealed class DemoDeviceRegistry(CloudDbContext context) : IDemoDeviceRegistry
{
    private const string Columns =
        """
        id AS Id, board_hash AS BoardHash, disk_hash AS DiskHash, machine_hash AS MachineHash, first_started_at AS FirstStartedAt,
        registered_at AS RegisteredAt, last_seen_at AS LastSeenAt, requests AS Requests, status AS Status, reset_at AS ResetAt, reset_reason AS ResetReason
        """;

    public async Task<DemoRegistration?> RegisterAsync(
        DeviceFingerprint fingerprint, DateTimeOffset startedAt, IPAddress? ipAddress, int newDevicesPerIpPerDay, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var board = Nullable(fingerprint.Board);
        var disk = Nullable(fingerprint.Disk);
        var machine = Nullable(fingerprint.Machine);
        foreach (var hash in new[] { board, disk, machine }.OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_xact_lock(hashtext(@hash))", new { hash }, transaction, cancellationToken: cancellationToken));
        }

        var candidates = await connection.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM licensing.demo_devices WHERE board_hash = @board OR disk_hash = @disk OR machine_hash = @machine",
            new { board, disk, machine }, transaction, cancellationToken: cancellationToken));
        var matches = candidates
            .Where(r => DemoDeviceIdentity.SameDevice(new DeviceFingerprint(r.BoardHash ?? string.Empty, r.DiskHash ?? string.Empty, r.MachineHash ?? string.Empty), fingerprint))
            .OrderBy(r => r.Status == "RESET" ? 0 : 1)
            .ThenBy(r => r.FirstStartedAt)
            .ToList();
        if (matches is [var first, ..])
        {
            // Reiniciada por soporte: vale el inicio del reinicio (no se vuelve a bajar con lo que recuerde la tienda).
            var reset = first.Status == "RESET";
            var earliest = reset || Utc(first.FirstStartedAt) <= startedAt ? Utc(first.FirstStartedAt) : startedAt;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE licensing.demo_devices
                SET first_started_at = @earliest,
                    board_hash = COALESCE(board_hash, @board), disk_hash = COALESCE(disk_hash, @disk), machine_hash = COALESCE(@machine, machine_hash),
                    last_seen_at = @now, requests = requests + 1
                WHERE id = @id
                """,
                new { earliest, board, disk, machine, now, id = first.Id }, transaction, cancellationToken: cancellationToken));
            return new DemoRegistration(earliest, reset);
        }

        if (ipAddress is not null)
        {
            var today = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM licensing.demo_devices WHERE registered_ip = @ip::inet AND registered_at > @since",
                new { ip = ipAddress.ToString(), since = now.AddDays(-1) }, transaction, cancellationToken: cancellationToken));
            if (today >= newDevicesPerIpPerDay)
            {
                return null;
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO licensing.demo_devices (id, board_hash, disk_hash, machine_hash, first_started_at, registered_at, last_seen_at, registered_ip)
            VALUES (@id, @board, @disk, @machine, @startedAt, @now, @now, @ip::inet)
            """,
            new { id = Guid.CreateVersion7(), board, disk, machine, startedAt, now, ip = ipAddress?.ToString() }, transaction, cancellationToken: cancellationToken));
        return new DemoRegistration(startedAt, false);
    }

    public async Task<bool> ResetAsync(Guid id, string reason, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE licensing.demo_devices
            SET status = 'RESET', first_started_at = @now, reset_at = @now, reset_by = @actorId, reset_reason = @reason
            WHERE id = @id
            """,
            new { id, reason, actorId, now }, transaction, cancellationToken: cancellationToken)) > 0;
    }

    public async Task<int> PurgeStaleAsync(DateTimeOffset seenBefore, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM licensing.demo_devices WHERE requests = 1 AND status = 'ACTIVE' AND last_seen_at < @seenBefore",
            new { seenBefore }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<DemoDeviceDto>> ListAsync(string? supportCode, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var code = new string([.. (supportCode ?? string.Empty).Where(char.IsAsciiHexDigit)]).ToLowerInvariant();
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            code.Length == 0
                ? $"SELECT {Columns} FROM licensing.demo_devices ORDER BY last_seen_at DESC LIMIT 100"
                : $"SELECT {Columns} FROM licensing.demo_devices WHERE disk_hash LIKE @prefix OR board_hash LIKE @prefix OR machine_hash LIKE @prefix ORDER BY last_seen_at DESC LIMIT 100",
            new { prefix = code + "%" }, transaction, cancellationToken: cancellationToken));
        return [.. rows
            .Select(r => new DemoDeviceDto(
                r.Id, DemoDeviceIdentity.SupportCode(new DeviceFingerprint(r.BoardHash ?? string.Empty, r.DiskHash ?? string.Empty, r.MachineHash ?? string.Empty)),
                Utc(r.FirstStartedAt), Utc(r.RegisteredAt), Utc(r.LastSeenAt), r.Requests, r.Status, r.ResetAt is { } at ? Utc(at) : null, r.ResetReason))
            .Where(d => code.Length == 0 || d.SupportCode.Replace("-", string.Empty, StringComparison.Ordinal).StartsWith(code, StringComparison.OrdinalIgnoreCase))];
    }

    private async Task<(DbConnection Connection, DbTransaction? Transaction)> OpenAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }

    private static string? Nullable(string component) => component.Length == 0 ? null : component;

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record Row(
        Guid Id, string? BoardHash, string? DiskHash, string? MachineHash, DateTime FirstStartedAt, DateTime RegisteredAt, DateTime LastSeenAt,
        int Requests, string Status, DateTime? ResetAt, string? ResetReason);
}
