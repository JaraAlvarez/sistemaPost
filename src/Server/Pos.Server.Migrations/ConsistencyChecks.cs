using Npgsql;

namespace Pos.Server.Migrations;

/// <summary>Un hallazgo de <see cref="ConsistencyChecks"/>: la regla y hasta 20 documentos que la incumplen.</summary>
public sealed record ConsistencyFinding(string Check, IReadOnlyList<string> Documents);

/// <summary>
/// Invariantes entre módulos que se revisan después de una prueba de fallos (Fase 14, D14-03): ninguna venta a medias y cada venta
/// cobrada con su salida de inventario y su movimiento de caja. Solo lee. El kardex y la auditoría tienen sus propios verificadores.
/// </summary>
public static class ConsistencyChecks
{
    private static readonly (string Name, string Sql)[] Checks =
    [
        ("Ventas cobradas sin su salida de inventario",
            """
            SELECT s.number FROM sales.sales s
            WHERE s.status IN ('COMPLETED', 'VOIDED')
              AND EXISTS (SELECT 1 FROM sales.sale_lines l WHERE l.sale_id = s.id AND l.is_stockable AND l.status = 'ACTIVE')
              AND NOT EXISTS (SELECT 1 FROM inventory.stock_movements m WHERE m.source_type = 'SALE' AND m.source_id = s.id)
            LIMIT 20
            """),
        ("Ventas cuyos pagos aplicados no suman el total",
            """
            SELECT s.number FROM sales.sales s
            WHERE s.status IN ('COMPLETED', 'VOIDED')
              AND s.total <> COALESCE((SELECT sum(p.applied) FROM sales.sale_payments p WHERE p.sale_id = s.id), 0) + s.exchange_credit
            LIMIT 20
            """),
        ("Ventas en efectivo sin su movimiento de caja",
            """
            SELECT s.number FROM sales.sales s
            WHERE s.status IN ('COMPLETED', 'VOIDED')
              AND EXISTS (SELECT 1 FROM sales.sale_payments p WHERE p.sale_id = s.id AND p.affects_cash_drawer AND p.applied > 0)
              AND NOT EXISTS (SELECT 1 FROM cash.cash_movements m WHERE m.movement_type = 'SALE' AND m.source_id = s.id)
            LIMIT 20
            """),
        ("Ventas abiertas o suspendidas en una jornada ya cerrada",
            """
            SELECT COALESCE(s.number, s.id::text) FROM sales.sales s JOIN cash.cash_sessions c ON c.id = s.cash_session_id
            WHERE s.status IN ('OPEN', 'ON_HOLD') AND c.status = 'CLOSED'
            LIMIT 20
            """),
    ];

    public static async Task<IReadOnlyList<ConsistencyFinding>> RunAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var findings = new List<ConsistencyFinding>();
        foreach (var (name, sql) in Checks)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var documents = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                documents.Add(reader.GetString(0));
            }

            findings.Add(new ConsistencyFinding(name, documents));
        }

        return findings;
    }
}
