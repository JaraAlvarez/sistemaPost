using Npgsql;
using Pos.Infrastructure.Auditing;
using Pos.Modules.Inventory.Infrastructure;
using Pos.Server.Migrations;

namespace Pos.Server.Migrator;

/// <summary>
/// Verificación después de una prueba de fallos (Fase 14, D14-03): ninguna venta a medias, la caja y el inventario reflejan cada venta
/// cobrada, el kardex cuadra con los saldos y la auditoría verifica. Solo lee.
/// </summary>
internal static class ConsistencyCommands
{
    public const int ExitInconsistent = 7;

    public static async Task<int> VerifyAsync(string connectionString, CancellationToken cancellationToken)
    {
        var problems = 0;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            foreach (var finding in await ConsistencyChecks.RunAsync(connection, cancellationToken))
            {
                Console.WriteLine(finding.Documents.Count == 0 ? $"  OK      {finding.Check}" : $"  FALLA   {finding.Check}: {string.Join(", ", finding.Documents)}");
                problems += finding.Documents.Count;
            }

            var stock = await StockLedgerMaintenance.FindDiscrepanciesAsync(connection, null, cancellationToken);
            Console.WriteLine(stock.Count == 0 ? "  OK      Saldos de inventario contra el kardex" : $"  FALLA   Saldos que no cuadran con el kardex: {stock.Count}");
            problems += stock.Count;
        }

        var audit = await new AuditVerifier(dataSource).VerifyAsync(cancellationToken);
        Console.WriteLine(audit.IsValid
            ? $"  OK      Auditoría ({audit.RowsChecked} filas, {audit.SealsChecked} sellos)"
            : $"  FALLA   Auditoría: {string.Join(" · ", audit.Findings.Select(f => f.Message))}");
        problems += audit.IsValid ? 0 : Math.Max(1, audit.Findings.Count);

        Console.WriteLine(problems == 0 ? "Todo es consistente." : $"HAY {problems} INCONSISTENCIAS.");
        return problems == 0 ? 0 : ExitInconsistent;
    }
}
