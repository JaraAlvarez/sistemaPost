using System.Diagnostics;
using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Pos.Modules.Reporting.Application;
using Pos.Modules.Reporting.Contracts;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase9;

/// <summary>
/// Medición (no corre en la suite normal: <c>--explicit only</c>). Clona con SQL directo una venta real hasta tener unos 100.000
/// tiquetes (300.000 líneas) en 400 días y mide los comparativos (122 días, el máximo) y el ABC (366 días) por la API, más el plan
/// (EXPLAIN ANALYZE) del comparativo por día y del ABC. Los tiempos quedan en <c>POS_PERF_OUT</c> (o en la carpeta temporal).
/// </summary>
[Collection(SequentialPerformance.Name)]
public class AnalyticsReportsPerformanceTests
{
    private const int Tickets = 100_000;

    [Fact(Explicit = true)]
    public async Task Medir_comparativos_y_ABC_con_100_mil_tiquetes()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        var today = Phase5.PurchasingScenario.Today;
        var template = await shop.StartAsync();
        await shop.AddAsync(template.Id, shop.Rice, 2);
        await shop.AddAsync(template.Id, shop.Soda, 1);
        template = await shop.AddAsync(template.Id, shop.Yogurt, 1);
        await shop.CompleteAsync(template.Id, [Pay(shop.Cash, 50_000m)]);

        var output = new StringBuilder();
        var superuser = await TestPostgres.SuperuserConnectionStringAsync(factory.ConnectionString);
        await using var connection = new NpgsqlConnection(superuser);
        await connection.OpenAsync(Ct);
        var clock = Stopwatch.StartNew();
        await ExecuteAsync(connection, $"""
            CREATE TEMP TABLE gen AS
            SELECT g, gen_random_uuid() AS id, (DATE '{today:yyyy-MM-dd}' - (g % 400))::date AS d,
                   ((DATE '{today:yyyy-MM-dd}' - (g % 400))::date + time '07:00' + (g % 14) * interval '1 hour') AT TIME ZONE 'America/Bogota' AS at
            FROM generate_series(1, {Tickets}) g
            """);
        await ExecuteAsync(connection, await CloneSqlAsync(connection, "sales", new Dictionary<string, string>
        {
            ["id"] = "gen.id", ["number"] = "'PERF-' || gen.g", ["completion_key"] = "'perf-' || gen.g", ["business_date"] = "gen.d",
            ["completed_at"] = "gen.at", ["opened_at"] = "gen.at - interval '2 minutes'", ["created_at"] = "gen.at - interval '2 minutes'",
            ["updated_at"] = "gen.at",
        }, $"t.id = '{template.Id}'"));
        await ExecuteAsync(connection, await CloneSqlAsync(connection, "sale_lines", new Dictionary<string, string>
        {
            ["id"] = "gen_random_uuid()", ["sale_id"] = "gen.id",
        }, $"t.sale_id = '{template.Id}'"));
        await ExecuteAsync(connection, "ANALYZE sales.sales; ANALYZE sales.sale_lines;");
        var counts = $"{await ScalarAsync(connection, "SELECT count(*) FROM sales.sales")} tiquetes, {await ScalarAsync(connection, "SELECT count(*) FROM sales.sale_lines")} líneas";
        output.AppendLine(CultureInfo.InvariantCulture, $"Datos: {counts}, generados en {clock.Elapsed.TotalSeconds:0.0} s; {today:yyyy-MM-dd} − 400 días.");

        var from122 = today.AddDays(-121);
        var from366 = today.AddDays(-365);
        foreach (var (code, from) in new[]
                 {
                     ("COMPARE_BY_DAY", from122), ("COMPARE_BY_CATEGORY", from122), ("COMPARE_BY_CASHIER", from122),
                     ("ABC_BY_SALES", from366), ("ABC_BY_PROFIT", from366), ("SALES_HEATMAP", from366), ("SALES_BY_PRODUCT", from366),
                 })
        {
            var times = new List<double>();
            ReportResultDto? result = null;
            for (var run = 0; run < 2; run++)
            {
                clock.Restart();
                result = await GetAsync<ReportResultDto>(shop.Owner, $"/api/v1/reports/{code}?from={from:yyyy-MM-dd}&to={today:yyyy-MM-dd}&pageSize=10");
                times.Add(clock.Elapsed.TotalSeconds);
            }

            output.AppendLine(CultureInfo.InvariantCulture,
                $"{code,-20} {from:yyyy-MM-dd}..{today:yyyy-MM-dd}  1ª {times[0]:0.000} s  2ª {times[1]:0.000} s  filas {result!.TotalRows}");
            times.Max().ShouldBeLessThan(30d, code);
        }

        // Planes con los mismos argumentos que arma el motor.
        var ids = await ReadIdsAsync(connection, template.Id);
        foreach (var (code, from) in new[] { ("COMPARE_BY_DAY", from122), ("ABC_BY_SALES", from366) })
        {
            var definition = ReportCatalog.Find(code)!;
            var periods = ComparisonPeriods.For(from, today, LastYearRule.SameWeekday364);
            await using var explain = new NpgsqlCommand($"EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) {definition.Sql}", connection);
            explain.Parameters.Add(new NpgsqlParameter("company_id", NpgsqlDbType.Uuid) { Value = ids.Company });
            explain.Parameters.Add(new NpgsqlParameter("branch_id", NpgsqlDbType.Uuid) { Value = ids.Branch });
            explain.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = from });
            explain.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = today });
            explain.Parameters.Add(new NpgsqlParameter("prev_from", NpgsqlDbType.Date) { Value = periods.Previous.From });
            explain.Parameters.Add(new NpgsqlParameter("prev_to", NpgsqlDbType.Date) { Value = periods.Previous.To });
            explain.Parameters.Add(new NpgsqlParameter("ly_from", NpgsqlDbType.Date) { Value = periods.LastYear.From });
            explain.Parameters.Add(new NpgsqlParameter("ly_to", NpgsqlDbType.Date) { Value = periods.LastYear.To });
            explain.Parameters.Add(new NpgsqlParameter("prev_shift", NpgsqlDbType.Integer) { Value = periods.PreviousShiftDays });
            explain.Parameters.Add(new NpgsqlParameter("ly_shift", NpgsqlDbType.Integer) { Value = periods.LastYearShiftDays });
            output.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"--- EXPLAIN {code}");
            await using var reader = await explain.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                output.AppendLine(reader.GetString(0));
            }
        }

        var path = Environment.GetEnvironmentVariable("POS_PERF_OUT") ?? Path.Combine(Path.GetTempPath(), "pos-perf-reportes.txt");
        await File.WriteAllTextAsync(path, output.ToString(), Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(output.ToString());
    }

    /// <summary>INSERT … SELECT que copia la fila (o filas) de la plantilla por cada fila de <c>gen</c>, cambiando las columnas dadas.</summary>
    private static async Task<string> CloneSqlAsync(NpgsqlConnection connection, string table, Dictionary<string, string> replace, string where)
    {
        var columns = new List<string>();
        await using (var command = new NpgsqlCommand(
                         $"SELECT column_name::text FROM information_schema.columns WHERE table_schema = 'sales' AND table_name = '{table}' ORDER BY ordinal_position",
                         connection))
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                columns.Add(reader.GetString(0));
            }
        }

        var list = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var values = string.Join(", ", columns.Select(c => replace.TryGetValue(c, out var expression) ? expression : $"t.\"{c}\""));
        return $"INSERT INTO sales.{table} ({list}) SELECT {values} FROM sales.{table} t CROSS JOIN gen WHERE {where}";
    }

    private static async Task<(Guid Company, Guid Branch)> ReadIdsAsync(NpgsqlConnection connection, Guid saleId)
    {
        await using var command = new NpgsqlCommand($"SELECT company_id, branch_id FROM sales.sales WHERE id = '{saleId}'", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetGuid(0), reader.GetGuid(1));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(Ct);
    }
}
