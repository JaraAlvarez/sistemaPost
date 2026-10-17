using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Purchasing.Application;

namespace Pos.Modules.Purchasing.Infrastructure;

/// <summary>
/// Pedido sugerido: candado consultivo por sucursal (dura lo que la transacción del comando) y claves de idempotencia en
/// system.idempotency_keys (RN-GEN-09), en la misma transacción que crea las órdenes.
/// </summary>
internal sealed class SuggestedOrderGate(PosDbContext context) : ISuggestedOrderGate
{
    private const string Scope = "purchasing.suggested_orders";

    public async Task LockAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtext('purchasing:suggested_orders:' || @branch))", new { branch = branchId.ToString("D") },
            transaction, cancellationToken: cancellationToken));
    }

    public async Task<StoredSuggestionBatch?> FindAsync(string key, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<BatchRow>(new CommandDefinition(
            "SELECT request_hash AS RequestHash, response_body::text AS ResponseBody FROM system.idempotency_keys WHERE scope = @scope AND key = @key",
            new { scope = Scope, key }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : new StoredSuggestionBatch(row.RequestHash, row.ResponseBody ?? "[]");
    }

    public async Task SaveAsync(string key, string requestHash, string responseJson, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (connection, transaction) = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO system.idempotency_keys (scope, key, request_hash, response_status, response_body, created_at, expires_at)
            VALUES (@scope, @key, @requestHash, 200, @body::jsonb, @now, @expires)
            """,
            new { scope = Scope, key, requestHash, body = responseJson, now, expires = now.AddDays(30) }, transaction, cancellationToken: cancellationToken));
    }

    private async Task<(DbConnection Connection, DbTransaction? Transaction)> OpenAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        return (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());
    }

    private sealed class BatchRow
    {
        public string RequestHash { get; set; } = string.Empty;

        public string? ResponseBody { get; set; }
    }
}
