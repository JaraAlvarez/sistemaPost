using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Npgsql;
using Pos.Modules.Catalog.Contracts;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase4;

/// <summary>
/// Criterios de rendimiento de la propuesta §11: con 50.000 productos y 100.000 códigos, escaneo &lt; 20 ms y búsqueda
/// &lt; 100 ms (p95, medidos de punta a punta en el servidor en memoria, con sesión y permisos); importación de 5.000 filas
/// dentro del límite de 30 s por transacción del rol de la aplicación.
/// </summary>
[Collection(SequentialPerformance.Name)]
public class PerformanceTests
{
    [Fact]
    public async Task Escaneo_y_busqueda_con_cincuenta_mil_productos()
    {
        await using var factory = new PosServerFactory();
        var scenario = await CatalogScenario.CreateAsync(factory);
        var category = (await GetAsync<List<CategoryDto>>(scenario.Owner, "/api/v1/catalog/categories")).Single().Id;
        var list = (await GetAsync<List<PriceListDto>>(scenario.Owner, "/api/v1/catalog/price-lists")).Single().Id;
        await SeedAsync(factory, scenario.Setup.CompanyId, category, list, scenario.Setup.OwnerUserId);

        var random = new Random(4);
        var scans = new List<double>();
        var searches = new List<double>();
        for (var i = 0; i < 60; i++)
        {
            var n = random.Next(1, 50_001);
            scans.Add(await MeasureAsync(scenario.Owner, $"/api/v1/catalog/scan/B{n:D6}A"));
            searches.Add(await MeasureAsync(scenario.Owner, $"/api/v1/catalog/products?search=producto%20{n}%20marca&pageSize=20"));
        }

        var scanP95 = Percentile(scans, 0.95);
        var searchP95 = Percentile(searches, 0.95);
        TestContext.Current.SendDiagnosticMessage($"Escaneo p95 = {scanP95:0.0} ms · búsqueda p95 = {searchP95:0.0} ms");
        scanP95.ShouldBeLessThan(20);
        searchP95.ShouldBeLessThan(100);
    }

    [Fact]
    public async Task Importacion_de_cinco_mil_productos_en_una_transaccion()
    {
        await using var factory = new PosServerFactory();
        var scenario = await CatalogScenario.CreateAsync(factory);
        var csv = new StringBuilder("sku;nombre;categoria;marca;iva;codigo_barras;precio\r\n");
        for (var i = 1; i <= 5_000; i++)
        {
            csv.Append(System.Globalization.CultureInfo.InvariantCulture, $"IMP-{i:D5};Producto importado {i};Importados > Grupo {i % 20};Marca {i % 50};IVA19;C{i:D8};{1_000 + i}\r\n");
        }

        var watch = Stopwatch.StartNew();
        var preview = await scenario.UploadAsync("/api/v1/catalog/imports?kind=products", Encoding.UTF8.GetBytes(csv.ToString()), "grande.csv");
        preview.StatusCode.ShouldBe(HttpStatusCode.Created, await preview.Content.ReadAsStringAsync(Ct));
        var batch = (await preview.Content.ReadFromJsonAsync<ImportBatchDto>(Json, Ct))!;
        batch.CreateRows.ShouldBe(5_000);
        var previewTime = watch.Elapsed;

        watch.Restart();
        var applied = await scenario.Owner.PostAsync($"/api/v1/catalog/imports/{batch.Id}/apply", null, Ct);
        applied.StatusCode.ShouldBe(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        var applyTime = watch.Elapsed;
        TestContext.Current.SendDiagnosticMessage($"Vista previa {previewTime.TotalSeconds:0.0} s · aplicación {applyTime.TotalSeconds:0.0} s");

        applyTime.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        (await GetAsync<PagedResult<ProductSummaryDto>>(scenario.Owner, "/api/v1/catalog/products?search=importado&pageSize=1")).TotalCount.ShouldBe(5_000);
    }

    private static async Task SeedAsync(PosServerFactory factory, Guid companyId, Guid categoryId, Guid priceListId, Guid userId)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO catalog.products (id, company_id, sku, name, short_name, category_id, base_unit_code, sale_mode, product_type, search_text, status, created_at, created_by)
            SELECT gen_random_uuid(), '{companyId}', 'P' || lpad(g::text, 6, '0'), 'Producto de prueba ' || g || ' marca ' || (g % 97), 'Producto ' || g,
                   '{categoryId}', 'UND', 'UNIT', 'STOCKABLE', 'producto de prueba ' || g || ' marca ' || (g % 97) || ' p' || lpad(g::text, 6, '0'),
                   'ACTIVE', now(), '{userId}'
            FROM generate_series(1, 50000) g;

            INSERT INTO catalog.product_barcodes (id, company_id, product_id, code, normalized_code, code_type, is_primary, created_at, created_by)
            SELECT gen_random_uuid(), company_id, id, 'B' || substr(sku, 2) || s, 'B' || substr(sku, 2) || s, 'CODE128', s = 'A', now(), '{userId}'
            FROM catalog.products CROSS JOIN (VALUES ('A'), ('B')) AS suffix(s) WHERE sku LIKE 'P%';

            INSERT INTO catalog.product_taxes (id, company_id, product_id, tax_id, created_at, created_by)
            SELECT gen_random_uuid(), p.company_id, p.id, t.id, now(), '{userId}'
            FROM catalog.products p JOIN catalog.taxes t ON t.code = 'IVA19' WHERE p.sku LIKE 'P%';

            INSERT INTO catalog.product_prices (id, company_id, price_list_id, product_id, price, valid_from, created_at, created_by)
            SELECT gen_random_uuid(), company_id, '{priceListId}', id, 1000 + (random() * 9000)::int, now() - interval '1 day', now(), '{userId}'
            FROM catalog.products WHERE sku LIKE 'P%';
            """,
            connection);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(Ct);

        // Estadísticas actualizadas (lo que hace el autovacuum en producción).
        var owner = new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Username = "pos_migrator", Password = "migrator-test-password" };
        await using var admin = new NpgsqlConnection(owner.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var analyze = new NpgsqlCommand(
            "SET ROLE pos_owner; ANALYZE catalog.products; ANALYZE catalog.product_barcodes; ANALYZE catalog.product_prices; ANALYZE catalog.product_taxes;", admin);
        await analyze.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<double> MeasureAsync(HttpClient client, string url)
    {
        var watch = Stopwatch.StartNew();
        var response = await client.GetAsync(url, Ct);
        watch.Stop();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double Percentile(List<double> values, double percentile)
    {
        var ordered = values.Order().ToList();
        return ordered[(int)Math.Ceiling(percentile * ordered.Count) - 1];
    }
}
