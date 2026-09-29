using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Inventory.Application;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Inventory.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Inventory.Infrastructure;

/// <summary>
/// SQL de verificación y reconstrucción del kardex (RN-INV-11). Lo usan el módulo y el migrador (<c>verify-stock</c>,
/// <c>rebuild-stock</c>), que no carga los módulos. Detecta: saldo ≠ Σ kardex, valor ≠ valor del último movimiento y
/// cadenas de saldos rotas (el saldo guardado en un movimiento ≠ suma acumulada hasta él).
/// </summary>
public static class StockLedgerMaintenance
{
    private const string DiscrepanciesSql =
        """
        WITH k AS (
            SELECT warehouse_id, product_id, SUM(direction * quantity) AS qty, MAX(seq) AS last_seq
            FROM inventory.stock_movements WHERE lot_id IS NULL GROUP BY warehouse_id, product_id),
        last AS (
            SELECT k.warehouse_id, k.product_id, k.qty, m.balance_quantity, m.balance_value
            FROM k JOIN inventory.stock_movements m ON m.seq = k.last_seq),
        chain AS (
            SELECT warehouse_id, product_id, bool_and(ok) AS ok FROM (
                SELECT warehouse_id, product_id,
                       balance_quantity = SUM(direction * quantity) OVER (PARTITION BY warehouse_id, product_id ORDER BY seq) AS ok
                FROM inventory.stock_movements WHERE lot_id IS NULL) x
            GROUP BY warehouse_id, product_id)
        SELECT COALESCE(b.warehouse_id, l.warehouse_id) AS WarehouseId, COALESCE(b.product_id, l.product_id) AS ProductId,
               COALESCE(b.quantity, 0) AS BalanceQuantity, COALESCE(l.qty, 0) AS KardexQuantity,
               COALESCE(b.total_value, 0) AS BalanceValue, COALESCE(l.balance_value, 0) AS KardexValue
        FROM (SELECT * FROM inventory.stock_balances WHERE lot_id IS NULL) b
        FULL JOIN last l ON l.warehouse_id = b.warehouse_id AND l.product_id = b.product_id
        LEFT JOIN chain c ON c.warehouse_id = COALESCE(b.warehouse_id, l.warehouse_id) AND c.product_id = COALESCE(b.product_id, l.product_id)
        WHERE COALESCE(b.quantity, 0) <> COALESCE(l.qty, 0)
           OR COALESCE(b.total_value, 0) <> COALESCE(l.balance_value, 0)
           OR COALESCE(l.balance_quantity, 0) <> COALESCE(l.qty, 0)
           OR c.ok = false
        ORDER BY 1, 2
        """;

    public static async Task<IReadOnlyList<StockDiscrepancyDto>> FindDiscrepanciesAsync(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken) =>
        [.. await connection.QueryAsync<StockDiscrepancyDto>(new CommandDefinition(DiscrepanciesSql, transaction: transaction, cancellationToken: cancellationToken))];

    public static Task<int> CountBalancesAsync(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::int FROM inventory.stock_balances", transaction: transaction, cancellationToken: cancellationToken));

    /// <summary>Recalcula el saldo desde el kardex: cantidad = Σ, valor y promedio = los del último movimiento.</summary>
    public static async Task<(StockState Before, StockState After)> RebuildAsync(
        DbConnection connection, DbTransaction? transaction, Guid warehouseId, Guid productId, CancellationToken cancellationToken)
    {
        var before = await connection.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
            """
            SELECT quantity AS Quantity, total_value AS Value, average_cost AS AverageCost FROM inventory.stock_balances
            WHERE warehouse_id = @warehouseId AND product_id = @productId AND lot_id IS NULL FOR UPDATE
            """,
            new { warehouseId, productId }, transaction, cancellationToken: cancellationToken)) ?? new StateRow();
        var after = await connection.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
            """
            SELECT (SELECT SUM(direction * quantity) FROM inventory.stock_movements
                    WHERE warehouse_id = @warehouseId AND product_id = @productId AND lot_id IS NULL) AS Quantity,
                   balance_value AS Value, balance_avg_cost AS AverageCost, seq AS Seq, occurred_at AS OccurredAt
            FROM inventory.stock_movements
            WHERE warehouse_id = @warehouseId AND product_id = @productId AND lot_id IS NULL
            ORDER BY seq DESC LIMIT 1
            """,
            new { warehouseId, productId }, transaction, cancellationToken: cancellationToken)) ?? new StateRow();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE inventory.stock_balances
            SET quantity = @Quantity, total_value = @Value, average_cost = @AverageCost, last_seq = @Seq, last_movement_at = @OccurredAt
            WHERE warehouse_id = @warehouseId AND product_id = @productId AND lot_id IS NULL
            """,
            new { after.Quantity, after.Value, after.AverageCost, after.Seq, after.OccurredAt, warehouseId, productId }, transaction,
            cancellationToken: cancellationToken));
        return (new StockState(before.Quantity, before.Value, before.AverageCost), new StockState(after.Quantity, after.Value, after.AverageCost));
    }

    private sealed class StateRow
    {
        public decimal Quantity { get; set; }

        public decimal Value { get; set; }

        public decimal AverageCost { get; set; }

        public long Seq { get; set; }

        public DateTime? OccurredAt { get; set; }
    }
}

internal sealed class StockVerifier(PosDbContext context, IInstallationContext installation, IAuditWriter audit, IIdGenerator ids, IClock clock) : IStockVerifier
{
    public async Task<VerificationDto> VerifyAsync(string kind, Guid? triggeredBy, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        var started = clock.UtcNow;
        var discrepancies = await StockLedgerMaintenance.FindDiscrepanciesAsync(connection, transaction, cancellationToken);
        var checkedBalances = await StockLedgerMaintenance.CountBalancesAsync(connection, transaction, cancellationToken);
        var id = ids.NewId();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO inventory.stock_verification_runs (id, company_id, node_id, kind, started_at, finished_at, checked_balances, discrepancies, details, triggered_by)
            VALUES (@id, @companyId, @nodeId, @kind, @started, @finished, @checkedBalances, @count, @details::jsonb, @triggeredBy)
            """,
            new
            {
                id, companyId = installation.CompanyId, nodeId = installation.NodeId, kind, started, finished = clock.UtcNow, checkedBalances,
                count = discrepancies.Count, details = JsonSerializer.Serialize(discrepancies.Take(200)), triggeredBy,
            },
            transaction, cancellationToken: cancellationToken));

        if (discrepancies.Count > 0)
        {
            // Nunca se corrige en silencio: queda como incidente crítico en la auditoría (RN-INV-11).
            await audit.WriteAsync(
                new AuditEntry("inventory", "STOCK_VERIFICATION_FAILED", "StockVerification", id, $"Verificación {kind}",
                    $"El saldo de {discrepancies.Count} producto(s) no coincide con el kardex. Revise y reconstruya con autorización.",
                    Severity: AuditSeverity.Critical),
                cancellationToken);
        }

        return new VerificationDto(id, kind, started, checkedBalances, discrepancies.Count, discrepancies);
    }

    public async Task<IReadOnlyList<VerificationDto>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        var rows = await connection.QueryAsync<RunRow>(new CommandDefinition(
            """
            SELECT id AS Id, kind AS Kind, started_at AS StartedAt, checked_balances AS CheckedBalances, discrepancies AS Discrepancies, details::text AS Details
            FROM inventory.stock_verification_runs WHERE company_id = @companyId ORDER BY started_at DESC LIMIT @limit
            """,
            new { companyId = installation.CompanyId, limit }, transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new VerificationDto(
            r.Id, r.Kind, new DateTimeOffset(DateTime.SpecifyKind(r.StartedAt, DateTimeKind.Utc)), r.CheckedBalances, r.Discrepancies,
            JsonSerializer.Deserialize<List<StockDiscrepancyDto>>(r.Details) ?? []))];
    }

    public async Task<(StockState Before, StockState After)> RebuildAsync(Guid warehouseId, Guid productId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        return await StockLedgerMaintenance.RebuildAsync(connection, transaction, warehouseId, productId, cancellationToken);
    }

    private sealed class RunRow
    {
        public Guid Id { get; set; }

        public string Kind { get; set; } = string.Empty;

        public DateTime StartedAt { get; set; }

        public int CheckedBalances { get; set; }

        public int Discrepancies { get; set; }

        public string Details { get; set; } = "[]";
    }
}

/// <summary>
/// Verificación diaria automática (RN-INV-11): cada 15 minutos revisa si ya pasó la hora configurada
/// (<c>inventory.verification_hour</c>) y si no hubo una verificación automática en las últimas 20 horas.
/// </summary>
internal sealed partial class StockVerificationScheduler(
    IServiceScopeFactory scopes, DatabaseReadiness readiness, IInstallationContext installation, IClock clock, ILogger<StockVerificationScheduler> logger)
    : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunIfDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    internal async Task<bool> RunIfDueAsync(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady || installation.CompanyId is not { } companyId)
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var hour = await services.GetRequiredService<ISettingsReader>().GetAsync(InventorySettings.VerificationHour, new SettingContext(companyId), cancellationToken);
        var now = clock.UtcNow;
        if (clock.ToBusinessTime(now).Hour < hour)
        {
            return false;
        }

        var context = services.GetRequiredService<PosDbContext>();
        var (connection, _) = await DbSession.OpenAsync(context, cancellationToken);
        var last = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT max(started_at) FROM inventory.stock_verification_runs WHERE kind = 'AUTOMATIC'", cancellationToken: cancellationToken));
        if (last is { } previous && now - new DateTimeOffset(DateTime.SpecifyKind(previous, DateTimeKind.Utc)) < TimeSpan.FromHours(20))
        {
            return false;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var result = await services.GetRequiredService<IStockVerifier>().VerifyAsync("AUTOMATIC", null, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogCompleted(logger, result.CheckedBalances, result.Discrepancies);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló la verificación automática del kardex.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Verificación automática del kardex: {Checked} saldos revisados, {Discrepancies} diferencias.")]
    private static partial void LogCompleted(ILogger logger, int @checked, int discrepancies);
}
