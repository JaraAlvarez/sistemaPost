using Npgsql;

namespace Pos.Database.Tests;

/// <summary>Fase 9 (D9-02): las vistas del esquema reporting son el contrato de los reportes; todas compilan y pos_app solo las lee.</summary>
public class ReportingSchemaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Todas_las_vistas_de_reporting_se_consultan_y_pos_app_no_puede_escribirlas()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);

        var views = await db.ListAsync<string>("SELECT viewname::text FROM pg_views WHERE schemaname = 'reporting' ORDER BY 1");
        views.Count.ShouldBe(36);
        (await db.ScalarAsync<long>("SELECT count(*) FROM pg_tables WHERE schemaname = 'reporting'")).ShouldBe(0);

        foreach (var view in views)
        {
            await db.ExecuteAsync($"SELECT * FROM reporting.{view} LIMIT 1", db.AppConnectionString);
        }

        var ex = await Should.ThrowAsync<PostgresException>(() => db.ExecuteAsync("DELETE FROM reporting.users", db.AppConnectionString));
        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }
}
