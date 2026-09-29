using Npgsql;

namespace Pos.Database.Tests;

/// <summary>Restricciones de las migraciones V2026.10.007–009 (catálogo e inventario) sobre PostgreSQL real.</summary>
public class CatalogInventorySchemaTests(PostgresFixture postgres)
{
    private static readonly string Company = InfrastructureHarness.CompanyId.ToString();
    private static readonly string User = InfrastructureHarness.SystemUserId.ToString();

    private static async Task<(string Category, string Product, string List)> SeedProductAsync(InfrastructureHarness harness)
    {
        var category = Guid.CreateVersion7().ToString();
        var product = Guid.CreateVersion7().ToString();
        var list = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(
            $"""
            INSERT INTO catalog.categories (id, company_id, name, level, path, status, created_at, created_by)
            VALUES ('{category}', '{Company}', 'General', 1, '/{category}/', 'ACTIVE', now(), '{User}');
            INSERT INTO catalog.products (id, company_id, sku, name, short_name, category_id, base_unit_code, sale_mode, product_type, search_text, status, created_at, created_by)
            VALUES ('{product}', '{Company}', 'P1', 'Producto', 'Producto', '{category}', 'UND', 'UNIT', 'STOCKABLE', 'producto', 'ACTIVE', now(), '{User}');
            INSERT INTO catalog.price_lists (id, company_id, code, name, is_default, status, created_at, created_by)
            VALUES ('{list}', '{Company}', 'GENERAL', 'General', true, 'ACTIVE', now(), '{User}');
            """,
            harness.Database.AppConnectionString);
        return (category, product, list);
    }

    private static string Price(string product, string list, string from, string? to, string branch = "NULL") =>
        $"""
        INSERT INTO catalog.product_prices (id, company_id, price_list_id, product_id, branch_id, price, valid_from, valid_to, created_at, created_by)
        VALUES (gen_random_uuid(), '{Company}', '{list}', '{product}', {branch}, 1000, '{from}', {(to is null ? "NULL" : $"'{to}'")}, now(), '{User}')
        """;

    private static string Movement(string product, string type, int direction) =>
        $"""
        INSERT INTO inventory.stock_movements (id, company_id, node_id, branch_id, warehouse_id, product_id, movement_type, direction, quantity,
            unit_cost, total_cost, balance_quantity, balance_value, balance_avg_cost, source_type, source_id, business_date, occurred_at, user_id)
        SELECT gen_random_uuid(), '{Company}', installation_id, '{InfrastructureHarness.BranchS01}', '{InfrastructureHarness.WarehouseS01}', '{product}',
            '{type}', {direction}, 10, 100, 1000, 10, 1000, 100, 'ADJUSTMENT', gen_random_uuid(), current_date, now(), '{User}'
        FROM system.installation
        """;

    [Fact]
    public async Task Dos_precios_no_pueden_solaparse_para_la_misma_combinacion()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var (_, product, list) = await SeedProductAsync(harness);
        var app = harness.Database.AppConnectionString;

        await harness.Database.ExecuteAsync(Price(product, list, "2026-10-01", "2026-10-10"), app);
        await harness.Database.ExecuteAsync(Price(product, list, "2026-10-10", null), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Price(product, list, "2026-10-05", "2026-10-06"), app)))
            .ConstraintName.ShouldBe("ex_product_prices__no_overlap");

        // Un precio especial de sucursal convive con el general en las mismas fechas.
        await harness.Database.ExecuteAsync(Price(product, list, "2026-10-05", "2026-10-06", $"'{InfrastructureHarness.BranchS01}'"), app);
    }

    [Fact]
    public async Task Codigos_de_barras_unicos_y_restricciones_del_producto()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var (category, product, _) = await SeedProductAsync(harness);
        var app = harness.Database.AppConnectionString;
        var barcode =
            $"""
            INSERT INTO catalog.product_barcodes (id, company_id, product_id, code, normalized_code, code_type, is_primary, created_at, created_by)
            VALUES (gen_random_uuid(), '{Company}', '{product}', '7702177000014', '7702177000014', 'EAN13', false, now(), '{User}')
            """;
        await harness.Database.ExecuteAsync(barcode, app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(barcode, app))).ConstraintName.ShouldBe("ux_product_barcodes__company_code");

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"""
                INSERT INTO catalog.products (id, company_id, sku, name, short_name, category_id, base_unit_code, sale_mode, product_type, is_sold_by_scale,
                    search_text, status, created_at, created_by)
                VALUES (gen_random_uuid(), '{Company}', 'P2', 'Tomate', 'Tomate', '{category}', 'KG', 'WEIGHT', 'STOCKABLE', true, 'tomate', 'ACTIVE', now(), '{User}')
                """,
                app)))
            .ConstraintName.ShouldBe("ck_products__decimals");
    }

    [Fact]
    public async Task El_kardex_es_de_solo_insercion_incluso_para_el_dueno_de_las_tablas()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var (_, product, _) = await SeedProductAsync(harness);
        var app = harness.Database.AppConnectionString;
        await harness.Database.ExecuteAsync(Movement(product, "INITIAL_BALANCE", 1), app);

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("UPDATE inventory.stock_movements SET quantity = 1", app)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                "SET ROLE pos_owner; UPDATE inventory.stock_movements SET quantity = 1", harness.Database.MigratorConnectionString)))
            .MessageText.ShouldContain("solo inserción");

        // La dirección debe corresponder al tipo de movimiento.
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Movement(product, "SALE", 1), app)))
            .ConstraintName.ShouldBe("ck_stock_movements__type_direction");
    }
}
