using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Pos.Modules.Reporting.Application;
using Pos.Modules.Reporting.Contracts;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Reporting.Infrastructure;

/// <summary>
/// Ejecución de las consultas de reportes (D9-01): conexión propia, transacción de SOLO LECTURA (la BD rechaza cualquier escritura) y
/// <c>statement_timeout</c> local, para que un reporte pesado nunca bloquee ni demore la caja. Solo se leen vistas <c>reporting.*</c>.
/// Desviación documentada en el informe: en lugar de un rol de BD aparte (<c>pos_report</c>) se usa el rol de la aplicación con la
/// transacción de solo lectura; los privilegios de <c>pos_app</c> sobre <c>reporting</c> son únicamente SELECT.
/// </summary>
internal static class ReadOnlySession
{
    public static async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> BeginAsync(
        NpgsqlDataSource dataSource, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await using var set = new NpgsqlCommand(
                $"SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = {Math.Clamp(timeoutSeconds, 1, 600) * 1000}", connection, transaction);
            await set.ExecuteNonQueryAsync(cancellationToken);
            return (connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static bool IsTimeout(PostgresException ex) => ex.SqlState == PostgresErrorCodes.QueryCanceled;
}

internal sealed class ReportQueryRunner(NpgsqlDataSource dataSource) : IReportQueryRunner
{
    public async Task<Result<IReadOnlyList<object?[]>>> RunAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (connection, transaction) = await ReadOnlySession.BeginAsync(dataSource, query.TimeoutSeconds, cancellationToken);
        await using (connection)
        await using (transaction)
        {
            try
            {
                await using var command = new NpgsqlCommand($"{query.Sql}\nLIMIT {query.RowLimit}", connection, transaction);
                foreach (var argument in query.Arguments)
                {
                    command.Parameters.Add(new NpgsqlParameter(argument.Name, DbType(argument.Type)) { Value = argument.Value ?? DBNull.Value });
                }

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var ordinals = query.Columns.Select(c => reader.GetOrdinal(c.Key)).ToArray();
                var rows = new List<object?[]>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new object?[ordinals.Length];
                    for (var i = 0; i < ordinals.Length; i++)
                    {
                        row[i] = Read(reader, ordinals[i], query.Columns[i].Type);
                    }

                    rows.Add(row);
                }

                return rows;
            }
            catch (PostgresException ex) when (ReadOnlySession.IsTimeout(ex))
            {
                return ReportingErrors.Timeout;
            }
        }
    }

    private static object? Read(NpgsqlDataReader reader, int ordinal, ReportColumnType type)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return type switch
        {
            ReportColumnType.Date => reader.GetFieldValue<DateOnly>(ordinal),
            ReportColumnType.DateTime => reader.GetFieldValue<DateTimeOffset>(ordinal),
            ReportColumnType.Count => Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture),
            ReportColumnType.Boolean => reader.GetBoolean(ordinal),
            ReportColumnType.Text => Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    internal static NpgsqlDbType DbType(SqlArgumentType type) => type switch
    {
        SqlArgumentType.Uuid => NpgsqlDbType.Uuid,
        SqlArgumentType.Date => NpgsqlDbType.Date,
        SqlArgumentType.WholeNumber => NpgsqlDbType.Integer,
        _ => NpgsqlDbType.Numeric,
    };
}

/// <summary>Encabezado de los archivos y tablero del día (§6), leídos de las vistas en una transacción de solo lectura.</summary>
internal sealed class ReportingReadModel(NpgsqlDataSource dataSource) : IReportingReadModel
{
    private const string Node = "company_id = @company_id AND branch_id = @branch_id";

    public async Task<ReportHeader> GetHeaderAsync(Guid companyId, Guid branchId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await ReadOnlySession.BeginAsync(dataSource, 10, cancellationToken);
        await using (connection)
        await using (transaction)
        {
            await using var command = Command(
                """
                SELECT COALESCE(NULLIF(trade_name, ''), company_name), identification_number || COALESCE('-' || check_digit, ''), branch_code || ' · ' || branch_name
                FROM reporting.branches WHERE company_id = @company_id AND branch_id = @branch_id
                """,
                connection, transaction, companyId, branchId, null);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new ReportHeader(reader.GetString(0), reader.GetString(1), reader.GetString(2))
                : new ReportHeader(string.Empty, string.Empty, string.Empty);
        }
    }

    public async Task<Result<DashboardDto>> GetDashboardAsync(DashboardRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (connection, transaction) = await ReadOnlySession.BeginAsync(dataSource, request.TimeoutSeconds, cancellationToken);
        await using (connection)
        await using (transaction)
        {
            try
            {
                var lastWeek = request.Today.AddDays(-7);
                var sales = new Dictionary<DateOnly, DashboardSalesDto>();
                await using (var command = Command(
                    $"""
                    WITH s AS (
                        SELECT business_date, count(*) FILTER (WHERE status = 'COMPLETED') AS tickets,
                               COALESCE(sum(total) FILTER (WHERE status = 'COMPLETED'), 0) AS total, count(*) FILTER (WHERE status = 'VOIDED') AS voided
                        FROM reporting.sales WHERE {Node} AND business_date IN (@today, @last_week) AND status IN ('COMPLETED', 'VOIDED')
                        GROUP BY business_date),
                    r AS (
                        SELECT business_date, sum(credit_total) AS returned FROM reporting.returns
                        WHERE {Node} AND business_date IN (@today, @last_week) AND status = 'COMPLETED' GROUP BY business_date)
                    SELECT s.business_date, s.tickets, s.total - COALESCE(r.returned, 0), s.voided, round(s.total / NULLIF(s.tickets, 0), 2)
                    FROM s LEFT JOIN r USING (business_date)
                    """,
                    connection, transaction, request.CompanyId, request.BranchId, request.Today, ("last_week", SqlArgumentType.Date, lastWeek)))
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var date = reader.GetFieldValue<DateOnly>(0);
                        sales[date] = new DashboardSalesDto(
                            date, (int)reader.GetInt64(1), reader.GetDecimal(2), reader.IsDBNull(4) ? 0m : reader.GetDecimal(4), (int)reader.GetInt64(3));
                    }
                }

                decimal? profit = null;
                if (request.IncludeProfit)
                {
                    profit = await ScalarAsync<decimal>(
                        $"""
                        SELECT COALESCE(sum(tax_base - cost_total), 0) FROM reporting.sale_lines
                        WHERE {Node} AND business_date = @today AND sale_status = 'COMPLETED' AND line_status = 'ACTIVE'
                        """,
                        connection, transaction, request, cancellationToken);
                }

                var hours = new List<DashboardHourDto>();
                await using (var command = Command(
                    $"""
                    SELECT extract(hour FROM completed_local)::int, count(*), sum(total) FROM reporting.sales
                    WHERE {Node} AND business_date = @today AND status = 'COMPLETED' GROUP BY 1 ORDER BY 1
                    """,
                    connection, transaction, request.CompanyId, request.BranchId, request.Today))
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        hours.Add(new DashboardHourDto(reader.GetInt32(0), (int)reader.GetInt64(1), reader.GetDecimal(2)));
                    }
                }

                var top = new List<DashboardProductDto>();
                await using (var command = Command(
                    $"""
                    SELECT product_id, max(sku), max(product_name), sum(base_quantity), sum(total) FROM reporting.sale_lines
                    WHERE {Node} AND business_date = @today AND sale_status = 'COMPLETED' AND line_status = 'ACTIVE'
                    GROUP BY product_id ORDER BY sum(total) DESC LIMIT 10
                    """,
                    connection, transaction, request.CompanyId, request.BranchId, request.Today))
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        top.Add(new DashboardProductDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3), reader.GetDecimal(4)));
                    }
                }

                var openSessions = await ScalarAsync<long>(
                    $"SELECT count(*) FROM reporting.cash_sessions WHERE {Node} AND status <> 'CLOSED'", connection, transaction, request, cancellationToken);
                var unreviewed = await ScalarAsync<long>(
                    $"SELECT count(*) FROM reporting.cash_sessions WHERE {Node} AND status = 'CLOSED' AND review_required AND reviewed_at IS NULL",
                    connection, transaction, request, cancellationToken);
                var belowMinimum = await ScalarAsync<long>(
                    $"""
                    SELECT count(*) FROM reporting.stock_policies sp
                    WHERE sp.company_id = @company_id AND sp.branch_id = @branch_id
                      AND COALESCE((SELECT sum(b.quantity) FROM reporting.stock_balances b
                                    WHERE b.warehouse_id = sp.warehouse_id AND b.product_id = sp.product_id), 0) <= COALESCE(sp.reorder_point, sp.min_qty)
                    """,
                    connection, transaction, request, cancellationToken);
                var expiring = await ScalarAsync<long>(
                    $"""
                    SELECT count(DISTINCT b.lot_id) FROM reporting.stock_balances b JOIN reporting.lots l ON l.lot_id = b.lot_id
                    WHERE b.company_id = @company_id AND b.branch_id = @branch_id AND b.quantity > 0 AND l.expiry_date <= @today + {request.ExpiringDays}
                    """,
                    connection, transaction, request, cancellationToken);
                var incidents = await ScalarAsync<long>(
                    "SELECT count(*) FROM reporting.integrity_incidents WHERE (company_id = @company_id OR company_id IS NULL) AND is_open",
                    connection, transaction, request, cancellationToken);
                var requests = await ScalarAsync<long>(
                    "SELECT count(*) FROM reporting.data_requests WHERE company_id = @company_id AND status = 'OPEN' AND due_on <= @today + 5",
                    connection, transaction, request, cancellationToken);

                return new DashboardDto(
                    sales.GetValueOrDefault(request.Today) ?? new DashboardSalesDto(request.Today, 0, 0m, 0m, 0),
                    sales.GetValueOrDefault(lastWeek) ?? new DashboardSalesDto(lastWeek, 0, 0m, 0m, 0),
                    profit,
                    hours,
                    top,
                    (int)openSessions,
                    (int)unreviewed,
                    (int)belowMinimum,
                    (int)expiring,
                    (int)requests,
                    (int)incidents,
                    default);
            }
            catch (PostgresException ex) when (ReadOnlySession.IsTimeout(ex))
            {
                return ReportingErrors.Timeout;
            }
        }
    }

    private static async Task<T> ScalarAsync<T>(
        string sql, NpgsqlConnection connection, NpgsqlTransaction transaction, DashboardRequest request, CancellationToken cancellationToken)
    {
        await using var command = Command(sql, connection, transaction, request.CompanyId, request.BranchId, request.Today);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync(cancellationToken))!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static NpgsqlCommand Command(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid branchId,
        DateOnly? today,
        (string Name, SqlArgumentType Type, object Value)? extra = null)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("company_id", NpgsqlDbType.Uuid) { Value = companyId });
        command.Parameters.Add(new NpgsqlParameter("branch_id", NpgsqlDbType.Uuid) { Value = branchId });
        command.Parameters.Add(new NpgsqlParameter("today", NpgsqlDbType.Date) { Value = (object?)today ?? DBNull.Value });
        if (extra is { } e)
        {
            command.Parameters.Add(new NpgsqlParameter(e.Name, ReportQueryRunner.DbType(e.Type)) { Value = e.Value });
        }

        return command;
    }
}

public static class ReportingInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IReportQueryRunner, ReportQueryRunner>();
        services.AddSingleton<IReportingReadModel, ReportingReadModel>();
        services.AddSingleton<IReportExporter, ReportExporter>();
        services.AddScoped<ReportEngine>();
    }
}
