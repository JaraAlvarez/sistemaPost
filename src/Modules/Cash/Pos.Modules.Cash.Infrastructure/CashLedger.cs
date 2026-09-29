using System.Data.Common;
using System.Globalization;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Cash.Application;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Cash.Domain;

namespace Pos.Modules.Cash.Infrastructure;

internal static class CashDb
{
    public static async Task<(DbConnection Connection, DbTransaction? Transaction)> OpenAsync(PosDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }

    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>Movimientos de una jornada en la transacción de la petición (ven lo que el comando ya guardó).</summary>
internal sealed class CashLedger(PosDbContext context) : ICashLedger
{
    public async Task<int> LockAndNextLineAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await CashDb.OpenAsync(context, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT id FROM cash.cash_sessions WHERE id = @sessionId FOR UPDATE", new { sessionId }, transaction, cancellationToken: cancellationToken));
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(line_no), 0) + 1 FROM cash.cash_movements WHERE session_id = @sessionId", new { sessionId }, transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<MethodMovements>> ExpectedAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await CashDb.OpenAsync(context, cancellationToken);
        var rows = await connection.QueryAsync<(Guid PaymentMethodId, int Direction, decimal Amount)>(new CommandDefinition(
            "SELECT payment_method_id, direction::int, amount FROM cash.cash_movements WHERE session_id = @sessionId",
            new { sessionId }, transaction, cancellationToken: cancellationToken));
        return CashCalculator.Expected(rows);
    }
}

/// <summary>
/// Lecturas de pantalla y reportes con SQL directo. Cruza con org e identity para mostrar nombres: es un modelo de
/// lectura; las escrituras nunca tocan otros esquemas.
/// </summary>
internal sealed class CashReadModel(PosDbContext context) : ICashReadModel
{
    public async Task<SessionHeader?> GetHeaderAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await CashDb.OpenAsync(context, cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<SessionHeader>(new CommandDefinition(
            """
            SELECT c.trade_name AS CompanyName, b.name AS BranchName, t.code AS TerminalCode, u.display_name AS CashierName
            FROM cash.cash_sessions s
            JOIN org.companies c ON c.id = s.company_id
            JOIN org.branches b ON b.id = s.branch_id
            JOIN org.pos_terminals t ON t.id = s.pos_terminal_id
            JOIN identity.users u ON u.id = s.cashier_id
            WHERE s.id = @sessionId
            """,
            new { sessionId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<CashMovementDto>> GetMovementsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await CashDb.OpenAsync(context, cancellationToken);
        var rows = await connection.QueryAsync<MovementRow>(new CommandDefinition(
            """
            SELECT m.id AS Id, m.line_no AS LineNo, m.movement_type AS MovementType, m.payment_method_id AS PaymentMethodId, p.name AS PaymentMethodName,
                   m.direction AS Direction, m.amount AS Amount, m.reason AS Reason, m.source_type AS SourceType, m.source_id AS SourceId,
                   m.source_number AS SourceNumber, m.user_id AS UserId, u.display_name AS UserName, m.authorized_by AS AuthorizedBy,
                   a.display_name AS AuthorizedByName, m.occurred_at AS OccurredAt
            FROM cash.cash_movements m
            JOIN cash.payment_methods p ON p.id = m.payment_method_id
            LEFT JOIN identity.users u ON u.id = m.user_id
            LEFT JOIN identity.users a ON a.id = m.authorized_by
            WHERE m.session_id = @sessionId
            ORDER BY m.line_no
            """,
            new { sessionId }, transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new CashMovementDto(
            r.Id, r.LineNo, r.MovementType, r.PaymentMethodId, r.PaymentMethodName, r.Direction, r.Amount, r.Reason, r.SourceType, r.SourceId, r.SourceNumber,
            r.UserId, r.UserName, r.AuthorizedBy, r.AuthorizedByName, CashDb.Utc(r.OccurredAt)))];
    }

    public async Task<IReadOnlyList<CashSessionSummaryDto>> ListSessionsAsync(SessionFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var (connection, transaction) = await CashDb.OpenAsync(context, cancellationToken);
        var rows = await connection.QueryAsync<SessionRow>(new CommandDefinition(
            """
            SELECT s.id AS Id, s.number AS Number, t.code AS TerminalCode, u.display_name AS CashierName, s.business_date AS BusinessDate, s.status AS Status,
                   s.difference AS Difference, s.review_required AS ReviewRequired, s.reviewed_at IS NOT NULL AS Reviewed, s.opened_at AS OpenedAt,
                   s.closed_at AS ClosedAt
            FROM cash.cash_sessions s
            JOIN org.pos_terminals t ON t.id = s.pos_terminal_id
            JOIN identity.users u ON u.id = s.cashier_id
            WHERE s.branch_id = @branchId
              AND (@status::text IS NULL OR s.status = @status)
              AND (@from::date IS NULL OR s.business_date >= @from::date)
              AND (@to::date IS NULL OR s.business_date <= @to::date)
              AND (NOT @pending OR (s.review_required AND s.reviewed_at IS NULL))
            ORDER BY s.opened_at DESC
            LIMIT 1000
            """,
            new
            {
                branchId = filter.BranchId, status = filter.Status?.ToUpperInvariant(), from = Text(filter.From), to = Text(filter.To),
                pending = filter.PendingReviewOnly,
            },
            transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new CashSessionSummaryDto(
            r.Id, r.Number, r.TerminalCode, r.CashierName, DateOnly.FromDateTime(r.BusinessDate), r.Status, r.Difference, r.ReviewRequired, r.Reviewed,
            CashDb.Utc(r.OpenedAt), r.ClosedAt is { } closed ? CashDb.Utc(closed) : null))];
    }

    private static string? Text(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class MovementRow
    {
        public Guid Id { get; set; }

        public int LineNo { get; set; }

        public string MovementType { get; set; } = string.Empty;

        public Guid PaymentMethodId { get; set; }

        public string PaymentMethodName { get; set; } = string.Empty;

        public short Direction { get; set; }

        public decimal Amount { get; set; }

        public string? Reason { get; set; }

        public string? SourceType { get; set; }

        public Guid? SourceId { get; set; }

        public string? SourceNumber { get; set; }

        public Guid UserId { get; set; }

        public string? UserName { get; set; }

        public Guid? AuthorizedBy { get; set; }

        public string? AuthorizedByName { get; set; }

        public DateTime OccurredAt { get; set; }
    }

    private sealed class SessionRow
    {
        public Guid Id { get; set; }

        public string Number { get; set; } = string.Empty;

        public string TerminalCode { get; set; } = string.Empty;

        public string CashierName { get; set; } = string.Empty;

        public DateTime BusinessDate { get; set; }

        public string Status { get; set; } = string.Empty;

        public decimal? Difference { get; set; }

        public bool ReviewRequired { get; set; }

        public bool Reviewed { get; set; }

        public DateTime OpenedAt { get; set; }

        public DateTime? ClosedAt { get; set; }
    }
}
