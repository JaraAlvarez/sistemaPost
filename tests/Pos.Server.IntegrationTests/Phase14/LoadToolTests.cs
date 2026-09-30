using Npgsql;
using Pos.Infrastructure.Auditing;
using Pos.LoadTest;
using Pos.Modules.Inventory.Infrastructure;
using Pos.Server.Migrations;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase14;

/// <summary>
/// Verificación de coherencia de la Fase 14 (corta, según tu forma de trabajo): la herramienta de carga prepara una tienda Multicaja
/// pequeña por la API, tres cajas venden y cierran a la vez, y después todo cuadra (inventario, caja, kardex y auditoría). Las corridas
/// largas (30.000 productos, 5 cajas, 72 h) las haces tú con la consola.
/// </summary>
public class LoadToolTests
{
    [Fact]
    public async Task Tres_cajas_venden_y_cierran_a_la_vez_y_todo_cuadra()
    {
        await using var factory = new PosServerFactory();
        PosApi.HandlerFactory = () => factory.Server.CreateHandler();
        try
        {
            var server = factory.Server.BaseAddress;
            var store = await Seed.RunAsync(server, OwnerUsername, OwnerPassword, products: 300, terminals: 3, Ct);
            store.Cashiers.Count.ShouldBe(3);
            store.Cashiers.ShouldAllBe(c => c.DeviceId != null);

            var (metrics, sales, elapsed) = await Run.ExecuteAsync(
                store, new RunOptions(TimeSpan.FromMinutes(5), MaxSalesPerCashier: 15, MaxLines: 12, ThinkMilliseconds: 0, Close: true, OwnerUsername, OwnerPassword), Ct);
            var summary = metrics.Summary(elapsed, sales);
            TestContext.Current.SendDiagnosticMessage(summary);
            sales.ShouldBe(45, summary);
            summary.ShouldNotContain("error ", Case.Sensitive, summary);
            summary.ShouldContain("cerrar-jornada");

            await using var connection = new NpgsqlConnection(factory.ConnectionString);
            await connection.OpenAsync(Ct);
            (await ConsistencyChecks.RunAsync(connection, Ct)).ShouldAllBe(f => f.Documents.Count == 0);
            (await StockLedgerMaintenance.FindDiscrepanciesAsync(connection, null, Ct)).ShouldBeEmpty();
            await using var dataSource = NpgsqlDataSource.Create(factory.ConnectionString);
            (await new AuditVerifier(dataSource).VerifyAsync(Ct)).IsValid.ShouldBeTrue();
        }
        finally
        {
            PosApi.HandlerFactory = null;
        }
    }
}
