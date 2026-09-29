using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Auditing;

/// <summary>Parámetros del sellado (revisión arquitectónica §4.2).</summary>
public sealed class AuditSealingOptions
{
    public const string SectionName = "Pos:Audit";

    /// <summary>Cada cuánto se toma la foto del último seq entregado.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Espera entre la foto y el sellado. DEBE ser mayor que el transaction_timeout de pos_app (30 s): pasado este
    /// tiempo, toda transacción que tomó un seq de la foto ya confirmó o se revirtió.
    /// </summary>
    public TimeSpan SafetyHorizon { get; set; } = TimeSpan.FromSeconds(45);
}

/// <summary>
/// Sella la bitácora por lotes, con una cadena por nodo:
/// 1. toma S₀ = último seq entregado por la secuencia;
/// 2. pasado el horizonte seguro, sella TODAS las filas visibles del nodo con seq en (seq_to anterior, S₀].
/// Los huecos del rango son rollbacks legítimos; una fila que aparezca después dentro de un rango sellado es manipulación.
/// Una sola instancia por BD (bloqueo consultivo transaccional).
/// </summary>
public sealed partial class AuditSealer(
    NpgsqlDataSource dataSource,
    IInstallationContext installation,
    IOptions<AuditSealingOptions> options,
    TimeProvider time,
    DatabaseReadiness readiness,
    ILogger<AuditSealer> logger) : BackgroundService
{
    private const long SealerLockKey = 7_310_402_002;

    private static readonly TimeSpan PartitionCheckInterval = TimeSpan.FromHours(12);

    private readonly SemaphoreSlim _tick = new(1, 1);

    private (long SeqTo, DateTimeOffset TakenAt)? _snapshot;
    private DateTimeOffset _lastPartitionCheck = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval, time);
        do
        {
            try
            {
                if (readiness.IsReady)
                {
                    await TickAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSealFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Un ciclo: sella la foto anterior si ya pasó el horizonte y toma una foto nueva.</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        // El ciclo lo llaman el temporizador y el sellado a demanda (cierre de caja): nunca a la vez.
        await _tick.WaitAsync(cancellationToken);
        try
        {
            await TickCoreAsync(cancellationToken);
        }
        finally
        {
            _tick.Release();
        }
    }

    /// <summary>Último sello del nodo (o <c>null</c> si aún no hay).</summary>
    public async Task<(long SealNo, string SealHash, DateTime SealedAt)?> LastSealAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<(long, string, DateTime)?>(new CommandDefinition(
            "SELECT seal_no, seal_hash, sealed_at FROM audit.audit_seals WHERE node_id = @nodeId ORDER BY seal_no DESC LIMIT 1",
            new { nodeId = installation.NodeId }, cancellationToken: cancellationToken));
    }

    private async Task TickCoreAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (now - _lastPartitionCheck >= PartitionCheckInterval)
        {
            // Particiones mensuales de los próximos 3 meses (la partición DEFAULT evita fallos si esto no corre).
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("SELECT audit.ensure_partitions(3)", cancellationToken: cancellationToken));
            _lastPartitionCheck = now;
        }

        if (_snapshot is { } snapshot && now - snapshot.TakenAt >= options.Value.SafetyHorizon)
        {
            await SealUpToAsync(snapshot.SeqTo, cancellationToken);
            _snapshot = null;
        }

        if (_snapshot is null)
        {
            _snapshot = (await ReadIssuedSeqAsync(cancellationToken), now);
        }
    }

    /// <summary>Último seq entregado por la secuencia (0 si nunca se usó).</summary>
    public async Task<long> ReadIssuedSeqAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var sequence = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT pg_get_serial_sequence('audit.audit_log', 'seq')", cancellationToken: cancellationToken));
        var (lastValue, isCalled) = await connection.QuerySingleAsync<(long, bool)>(new CommandDefinition(
            $"SELECT last_value, is_called FROM {sequence}", cancellationToken: cancellationToken));
        return isCalled ? lastValue : 0;
    }

    /// <summary>
    /// Sella las filas del nodo hasta <paramref name="seqTo"/> (inclusive). Solo debe llamarse con un seq tomado hace
    /// más que el horizonte seguro. Devuelve el número de sello creado o <c>null</c> si no había rango nuevo.
    /// </summary>
    public async Task<long?> SealUpToAsync(long seqTo, CancellationToken cancellationToken = default)
    {
        var nodeId = installation.NodeId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(@key)", new { key = SealerLockKey }, transaction, cancellationToken: cancellationToken));

        var last = await connection.QuerySingleOrDefaultAsync<(long SealNo, long SeqTo, string SealHash)?>(new CommandDefinition(
            "SELECT seal_no, seq_to, seal_hash FROM audit.audit_seals WHERE node_id = @nodeId ORDER BY seal_no DESC LIMIT 1",
            new { nodeId },
            transaction,
            cancellationToken: cancellationToken));

        var seqFrom = (last?.SeqTo ?? 0) + 1;
        if (seqTo < seqFrom)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var hashes = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT row_hash FROM audit.audit_log WHERE node_id = @nodeId AND seq BETWEEN @seqFrom AND @seqTo ORDER BY seq",
            new { nodeId, seqFrom, seqTo },
            transaction,
            cancellationToken: cancellationToken))).ToList();

        var sealNo = (last?.SealNo ?? 0) + 1;
        var prevHash = last?.SealHash ?? AuditHasher.GenesisHash;
        var sealedAt = AuditHasher.TruncateToMicroseconds(time.GetUtcNow());
        var digest = AuditHasher.ComputeRowsDigest(hashes);
        var sealHash = AuditHasher.ComputeSealHash(nodeId, sealNo, seqFrom, seqTo, hashes.Count, digest, prevHash, sealedAt);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit.audit_seals
                (node_id, seal_no, format_version, seq_from, seq_to, rows_count, rows_digest, prev_seal_hash, seal_hash, sealed_at)
            VALUES (@nodeId, @sealNo, @format, @seqFrom, @seqTo, @count, @digest, @prevHash, @sealHash, @sealedAt)
            """,
            new
            {
                nodeId, sealNo, format = AuditHasher.SealFormatVersion, seqFrom, seqTo, count = hashes.Count, digest, prevHash, sealHash, sealedAt,
            },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);

        LogSealed(logger, sealNo, seqFrom, seqTo, hashes.Count);
        return sealNo;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sello de auditoría {SealNo}: seq {SeqFrom}–{SeqTo} ({Rows} filas)")]
    private static partial void LogSealed(ILogger logger, long sealNo, long seqFrom, long seqTo, int rows);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló el sellado de la auditoría; se reintentará")]
    private static partial void LogSealFailed(ILogger logger, Exception exception);
}

/// <summary>Sellado a demanda para el reporte Z (<see cref="IAuditAnchor"/>).</summary>
internal sealed class AuditAnchor(AuditSealer sealer) : IAuditAnchor
{
    public async Task<AuditSealInfo?> SealNowAsync(CancellationToken cancellationToken = default)
    {
        await sealer.TickAsync(cancellationToken);
        return await sealer.LastSealAsync(cancellationToken) is { } seal
            ? new AuditSealInfo(seal.SealNo, AuditHasher.ShortCode(seal.SealHash), new DateTimeOffset(DateTime.SpecifyKind(seal.SealedAt, DateTimeKind.Utc)))
            : null;
    }
}
