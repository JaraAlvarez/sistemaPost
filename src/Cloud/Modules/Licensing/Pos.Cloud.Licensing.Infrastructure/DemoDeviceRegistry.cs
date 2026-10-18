using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Cloud.Infrastructure.Persistence;
using Pos.Cloud.Licensing.Application;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.Licensing.Infrastructure;

/// <summary>
/// <c>licensing.demo_devices</c> con SQL directo, en la conexión y transacción del contexto de la petición. Un candado de la transacción
/// (<c>pg_advisory_xact_lock</c>) serializa los registros: dos consultas del mismo equipo a la vez no crean dos filas.
/// </summary>
internal sealed class DemoDeviceRegistry(CloudDbContext context) : IDemoDeviceRegistry
{
    // Constante arbitraria y estable del candado de la demo por equipo.
    private const long LockKey = 0x504F_5344_454D_4F01;

    public async Task<DateTimeOffset> RegisterAsync(
        DeviceFingerprint fingerprint, DateTimeOffset startedAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        await context.Database.OpenConnectionAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@key)", new { key = LockKey }, transaction, cancellationToken: cancellationToken));

        var board = Nullable(fingerprint.Board);
        var disk = Nullable(fingerprint.Disk);
        var machine = Nullable(fingerprint.Machine);
        var candidates = await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT id AS Id, board_hash AS BoardHash, disk_hash AS DiskHash, machine_hash AS MachineHash, first_started_at AS FirstStartedAt
            FROM licensing.demo_devices
            WHERE board_hash = @board OR disk_hash = @disk OR machine_hash = @machine
            """,
            new { board, disk, machine }, transaction, cancellationToken: cancellationToken));

        // Mismo equipo = 2 de 3 componentes (reinstalar Windows cambia solo MachineGuid; cambiar el disco, solo el disco).
        var matches = candidates
            .Where(r => new DeviceFingerprint(r.BoardHash ?? string.Empty, r.DiskHash ?? string.Empty, r.MachineHash ?? string.Empty).Matches(fingerprint))
            .OrderBy(r => r.FirstStartedAt)
            .ToList();
        if (matches is [var first, ..])
        {
            var earliest = Utc(first.FirstStartedAt) < startedAt ? Utc(first.FirstStartedAt) : startedAt;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE licensing.demo_devices
                SET first_started_at = LEAST(first_started_at, @earliest),
                    board_hash = COALESCE(board_hash, @board), disk_hash = COALESCE(disk_hash, @disk), machine_hash = @machineNow,
                    last_seen_at = @now, requests = requests + 1
                WHERE id = @id
                """,
                new { earliest, board, disk, machineNow = machine ?? first.MachineHash, now, id = first.Id }, transaction,
                cancellationToken: cancellationToken));
            return earliest;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO licensing.demo_devices (id, board_hash, disk_hash, machine_hash, first_started_at, registered_at, last_seen_at)
            VALUES (@id, @board, @disk, @machine, @startedAt, @now, @now)
            """,
            new { id = Guid.CreateVersion7(), board, disk, machine, startedAt, now }, transaction, cancellationToken: cancellationToken));
        return startedAt;
    }

    private static string? Nullable(string component) => component.Length == 0 ? null : component;

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record Row(Guid Id, string? BoardHash, string? DiskHash, string? MachineHash, DateTime FirstStartedAt);
}
