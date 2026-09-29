using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Pos.Infrastructure.Outbox;

/// <summary>
/// Procesa en segundo plano los mensajes LOCAL del outbox: los reclama con <c>FOR UPDATE SKIP LOCKED</c> (varios
/// procesos no toman el mismo), ejecuta su handler y reintenta con espera exponencial si falla. Los mensajes SYNC los
/// consume la sincronización con la nube (fase propia) y no se tocan aquí.
/// </summary>
internal sealed partial class OutboxProcessor(
    NpgsqlDataSource dataSource,
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    public const int BatchSize = 20;
    public const int MaxBackoffMinutes = 60;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                while (await ProcessBatchAsync(stoppingToken) == BatchSize)
                {
                    // Hay más trabajo: seguir sin esperar.
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogBatchFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Procesa un lote; devuelve cuántos mensajes tomó.</summary>
    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, string Type, string Payload, int Attempts)> claimed;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            claimed = (await connection.QueryAsync<(Guid, string, string, int)>(new CommandDefinition(
                """
                UPDATE system.outbox_messages m
                SET status = 'PROCESSING', locked_until = now() + interval '5 minutes'
                WHERE m.id IN (
                    SELECT id FROM system.outbox_messages
                    WHERE destination = 'LOCAL'
                      AND status IN ('PENDING', 'FAILED', 'PROCESSING')
                      AND next_attempt_at <= now()
                      AND (locked_until IS NULL OR locked_until < now())
                    ORDER BY node_seq
                    LIMIT @batch
                    FOR UPDATE SKIP LOCKED)
                RETURNING m.id, m.type, m.payload::text, m.attempts
                """,
                new { batch = BatchSize },
                cancellationToken: cancellationToken))).ToList();
        }

        foreach (var (id, type, payload, attempts) in claimed)
        {
            await ProcessAsync(id, type, payload, attempts, cancellationToken);
        }

        return claimed.Count;
    }

    private async Task ProcessAsync(Guid id, string type, string payload, int attempts, CancellationToken cancellationToken)
    {
        string? error = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handlers = scope.ServiceProvider.GetServices<IOutboxMessageHandler>().Where(h => h.MessageType == type).ToList();
            using var document = JsonDocument.Parse(payload);
            foreach (var handler in handlers)
            {
                await handler.HandleAsync(document.RootElement, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            LogMessageFailed(logger, type, id, ex);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (error is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE system.outbox_messages SET status = 'PROCESSED', processed_at = now(), locked_until = NULL WHERE id = @id",
                new { id },
                cancellationToken: cancellationToken));
        }
        else
        {
            var backoffMinutes = Math.Min(MaxBackoffMinutes, Math.Pow(2, attempts));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE system.outbox_messages
                SET status = 'FAILED', attempts = attempts + 1, last_error = @error, locked_until = NULL,
                    next_attempt_at = now() + make_interval(mins => @minutes)
                WHERE id = @id
                """,
                new { id, error = error.Length > 2000 ? error[..2000] : error, minutes = (int)backoffMinutes },
                cancellationToken: cancellationToken));
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló el procesamiento de un lote del outbox")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falló el mensaje {Type} ({Id}) del outbox; se reintentará")]
    private static partial void LogMessageFailed(ILogger logger, string type, Guid id, Exception exception);
}
