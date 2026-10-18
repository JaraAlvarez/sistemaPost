using Pos.Cloud.Migrations;
using Pos.Server.Migrations;
using static Pos.Cloud.Database.Tests.CloudSeed;

namespace Pos.Cloud.Database.Tests;

/// <summary>
/// V2026.10.015 (revisión, ronda 2): lo ya recibido de las tiendas pierde la cédula/NIT del cliente de las ventas y el costo promedio de las
/// existencias; lo demás no cambia.
/// </summary>
public class SyncDataCleanupMigrationTests(CloudPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Las_ventas_y_existencias_ya_recibidas_quedan_sin_cedula_ni_costo_promedio()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: false);
        var all = CloudScripts.Catalog;
        var migration = all.Versioned.Single(s => s.Name.StartsWith("V2026.10.015__", StringComparison.Ordinal));
        var before = new ScriptCatalog(all.Versioned.Where(s => s != migration).Concat(all.Repeatable).Concat(all.Always));
        await new DatabaseMigrator(before).MigrateAsync(db.MigratorConnectionString, "tests", Ct);

        var account = Guid.CreateVersion7();
        var organization = Guid.CreateVersion7();
        var installation = Guid.CreateVersion7();
        await db.ExecuteAsync(
            Account(account) + Organization(organization, account) +
            $$$"""
            INSERT INTO sync.documents (installation_id, kind, doc_id, organization_id, version, data, received_at, received_via)
            VALUES ('{{{installation}}}', 'SALE', 'v1', '{{{organization}}}', now(), '{"customer": "Laura", "customerIdentification": "1020304050", "total": 10}', now(), 'ONLINE'),
                   ('{{{installation}}}', 'STOCK', 's1', '{{{organization}}}', now(), '{"quantity": 3, "averageCost": 1200, "totalValue": 3600}', now(), 'ONLINE');
            """);

        await new DatabaseMigrator(all).MigrateAsync(db.MigratorConnectionString, "tests", Ct);

        (await db.ScalarAsync<string>("SELECT data::text FROM sync.documents WHERE doc_id = 'v1'"))!.ShouldNotContain("1020304050");
        (await db.ScalarAsync<string>("SELECT data->>'customer' FROM sync.documents WHERE doc_id = 'v1'")).ShouldBe("Laura");
        (await db.ScalarAsync<bool>("SELECT data ? 'averageCost' FROM sync.documents WHERE doc_id = 's1'")).ShouldBeFalse();
        (await db.ScalarAsync<string>("SELECT data->>'totalValue' FROM sync.documents WHERE doc_id = 's1'")).ShouldBe("3600");
    }
}
