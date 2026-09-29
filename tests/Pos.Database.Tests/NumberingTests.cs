using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Abstractions.Numbering;
using Pos.Infrastructure.Persistence;

namespace Pos.Database.Tests;

/// <summary>Numeración interna (docs/fases/fase-02-propuesta.md §14 y §22).</summary>
public class NumberingTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Mil_asignaciones_concurrentes_sobre_la_misma_serie_no_repiten_ni_dejan_huecos()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        await ProvisionTerminalSeriesAsync(harness, InfrastructureHarness.TerminalC01, "C01");

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            var numbers = new List<long>();
            for (var i = 0; i < 50; i++)
            {
                await using var scope = harness.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
                await using var transaction = await context.Database.BeginTransactionAsync(Ct);
                var number = await scope.ServiceProvider.GetRequiredService<IDocumentNumberAllocator>()
                    .NextForTerminalAsync("SALE", InfrastructureHarness.TerminalC01, Ct);
                await transaction.CommitAsync(Ct);
                numbers.Add(number.SequenceNumber);
            }

            return numbers;
        }, Ct));

        var all = (await Task.WhenAll(tasks)).SelectMany(n => n).Order().ToList();

        all.Count.ShouldBe(1000);
        all.ShouldBe(Enumerable.Range(1, 1000).Select(n => (long)n).ToList());
    }

    [Fact]
    public async Task Una_transaccion_revertida_no_consume_el_numero()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        await ProvisionTerminalSeriesAsync(harness, InfrastructureHarness.TerminalC01, "C01");

        await using (var scope = harness.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            (await scope.ServiceProvider.GetRequiredService<IDocumentNumberAllocator>()
                .NextForTerminalAsync("SALE", InfrastructureHarness.TerminalC01, Ct)).SequenceNumber.ShouldBe(1);
            await transaction.RollbackAsync(Ct);
        }

        await using (var scope = harness.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            var number = await scope.ServiceProvider.GetRequiredService<IDocumentNumberAllocator>()
                .NextForTerminalAsync("SALE", InfrastructureHarness.TerminalC01, Ct);
            await transaction.CommitAsync(Ct);

            number.SequenceNumber.ShouldBe(1);
            number.Number.ShouldBe("S01C01-000001");
        }
    }

    [Fact]
    public async Task Cada_caja_tiene_su_propia_serie_y_el_prefijo_incluye_sucursal_y_caja()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        await ProvisionTerminalSeriesAsync(harness, InfrastructureHarness.TerminalC01, "C01");
        await ProvisionTerminalSeriesAsync(harness, InfrastructureHarness.TerminalC02, "C02");

        await using var scope = harness.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var allocator = scope.ServiceProvider.GetRequiredService<IDocumentNumberAllocator>();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        (await allocator.NextForTerminalAsync("SALE", InfrastructureHarness.TerminalC01, Ct)).Number.ShouldBe("S01C01-000001");
        (await allocator.NextForTerminalAsync("SALE", InfrastructureHarness.TerminalC02, Ct)).Number.ShouldBe("S01C02-000001");
        (await allocator.NextForTerminalAsync("SALE", InfrastructureHarness.TerminalC01, Ct)).Number.ShouldBe("S01C01-000002");
        await transaction.CommitAsync(Ct);
    }

    [Fact]
    public async Task Recrear_una_caja_con_el_mismo_codigo_continua_la_numeracion()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        await ProvisionTerminalSeriesAsync(harness, InfrastructureHarness.TerminalC01, "C01");
        await using (var scope = harness.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<IDocumentNumberAllocator>().NextForTerminalAsync("SALE", InfrastructureHarness.TerminalC01, Ct);
            await scope.ServiceProvider.GetRequiredService<IDocumentSeriesProvisioner>().DeactivateTerminalSeriesAsync(InfrastructureHarness.TerminalC01, Ct);
            await context.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        // La caja C01 se reemplaza por otra con el mismo código (la anterior quedó eliminada lógicamente).
        var replacement = Guid.CreateVersion7();
        await harness.Database.ExecuteAsync(
            $"""
            UPDATE org.pos_terminals SET deleted_at = now(), deleted_by = '{InfrastructureHarness.SystemUserId}' WHERE id = '{InfrastructureHarness.TerminalC01}';
            INSERT INTO org.pos_terminals (id, company_id, branch_id, code, name, warehouse_id, status, created_at, created_by)
            VALUES ('{replacement}', '{InfrastructureHarness.CompanyId}', '{InfrastructureHarness.BranchS01}', 'C01', 'Caja 1 nueva',
                    '{InfrastructureHarness.WarehouseS01}', 'ACTIVE', now(), '{InfrastructureHarness.SystemUserId}');
            """,
            harness.Database.AppConnectionString);
        await ProvisionTerminalSeriesAsync(harness, replacement, "C01");

        await using (var scope = harness.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            (await scope.ServiceProvider.GetRequiredService<IDocumentNumberAllocator>().NextForTerminalAsync("SALE", replacement, Ct))
                .Number.ShouldBe("S01C01-000002");
            await transaction.CommitAsync(Ct);
        }
    }

    internal static async Task ProvisionTerminalSeriesAsync(InfrastructureHarness harness, Guid terminalId, string code)
    {
        await using var scope = harness.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<IDocumentSeriesProvisioner>().CreateTerminalSeriesAsync(
            InfrastructureHarness.CompanyId, InfrastructureHarness.BranchS01, "S01", terminalId, code, Ct);
        await context.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
    }
}
