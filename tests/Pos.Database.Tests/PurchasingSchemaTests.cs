using Npgsql;

namespace Pos.Database.Tests;

/// <summary>Restricciones de las migraciones V2026.10.010–013 (terceros, medios de pago, compras y lotes) sobre PostgreSQL real.</summary>
public class PurchasingSchemaTests(PostgresFixture postgres)
{
    private static readonly string Company = InfrastructureHarness.CompanyId.ToString();
    private static readonly string User = InfrastructureHarness.SystemUserId.ToString();
    private static readonly string Branch = InfrastructureHarness.BranchS01.ToString();
    private static readonly string Warehouse = InfrastructureHarness.WarehouseS01.ToString();

    private static string Party(string id, string number) =>
        $"""
        INSERT INTO parties.parties (id, company_id, person_type, identification_type, identification_number, first_names, last_names, tax_regime,
            fiscal_responsibilities, search_text, status, created_at, created_by)
        VALUES ('{id}', '{Company}', 'NATURAL', 'CC', '{number}', 'Ana', 'Pérez', '49', 'R-99-PN', 'ana perez', 'ACTIVE', now(), '{User}')
        """;

    private static async Task<string> SeedSupplierAsync(InfrastructureHarness harness)
    {
        var party = Guid.CreateVersion7().ToString();
        var supplier = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(
            $"""
            {Party(party, "1020304050")};
            INSERT INTO purchasing.suppliers (id, company_id, party_id, code, status, created_at, created_by)
            VALUES ('{supplier}', '{Company}', '{party}', 'P1', 'ACTIVE', now(), '{User}');
            """,
            harness.Database.AppConnectionString);
        return supplier;
    }

    private static string Purchase(string id, string supplier, string invoice, string number, string status = "DRAFT") =>
        $"""
        INSERT INTO purchasing.purchases (id, company_id, branch_id, warehouse_id, supplier_id, number, supplier_invoice_number, invoice_date,
            business_date, due_date, payment_mode, proration_method, status, posted_at, voided_at, created_at, created_by)
        VALUES ('{id}', '{Company}', '{Branch}', '{Warehouse}', '{supplier}', '{number}', '{invoice}', current_date, current_date, current_date, 'CREDIT',
            'VALUE', '{status}', {(status == "DRAFT" ? "NULL" : "now()")}, {(status == "VOIDED" ? "now()" : "NULL")}, now(), '{User}')
        """;

    [Fact]
    public async Task La_identificacion_del_tercero_es_unica_salvo_fusionados()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        var first = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(Party(first, "123456"), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Party(Guid.CreateVersion7().ToString(), "123456"), app)))
            .ConstraintName.ShouldBe("ux_parties__identification");

        // El duplicado fusionado apunta al que se conserva y deja de ocupar la identificación.
        var duplicate = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(
            $"""
            INSERT INTO parties.parties (id, company_id, person_type, identification_type, identification_number, first_names, last_names, tax_regime,
                fiscal_responsibilities, search_text, status, merged_into_id, created_at, created_by)
            VALUES ('{duplicate}', '{Company}', 'NATURAL', 'CC', '123456', 'Ana', 'P', '49', 'R-99-PN', 'ana', 'MERGED', '{first}', now(), '{User}')
            """,
            app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"""
                INSERT INTO parties.parties (id, company_id, person_type, identification_type, identification_number, legal_name, tax_regime,
                    fiscal_responsibilities, search_text, status, created_at, created_by)
                VALUES (gen_random_uuid(), '{Company}', 'LEGAL', 'NIT', '900123456', 'Sin DV', '48', 'O-13', 'x', 'ACTIVE', now(), '{User}')
                """,
                app)))
            .ConstraintName.ShouldBe("ck_parties__nit_check_digit");
    }

    [Fact]
    public async Task Factura_unica_por_proveedor_salvo_anuladas_y_libro_de_cartera_de_solo_insercion()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        var supplier = await SeedSupplierAsync(harness);
        var voided = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(Purchase(voided, supplier, "FE-1", "C-1", "VOIDED"), app);
        var posted = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(Purchase(posted, supplier, "FE-1", "C-2", "POSTED"), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Purchase(Guid.CreateVersion7().ToString(), supplier, "FE-1", "C-3"), app)))
            .ConstraintName.ShouldBe("ux_purchases__supplier_invoice");

        var account = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(
            $"""
            INSERT INTO purchasing.accounts_payable (id, company_id, branch_id, supplier_id, purchase_id, document_number, issue_date, due_date,
                original_amount, balance, status, created_at, created_by)
            VALUES ('{account}', '{Company}', '{Branch}', '{supplier}', '{posted}', 'FE-1', current_date, current_date, 1000, 1000, 'OPEN', now(), '{User}');
            INSERT INTO purchasing.payable_entries (id, account_id, entry_type, amount, balance_after, source_type, source_id, occurred_at, user_id)
            VALUES (gen_random_uuid(), '{account}', 'CHARGE', 1000, 1000, 'PURCHASE', '{posted}', now(), '{User}');
            """,
            app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("UPDATE purchasing.payable_entries SET amount = 1", app)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("DELETE FROM purchasing.payable_entries")))
            .MessageText.ShouldContain("solo inserción");

        // El signo del asiento lo fija su tipo: un pago no puede aumentar la deuda.
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"""
                INSERT INTO purchasing.payable_entries (id, account_id, entry_type, amount, balance_after, source_type, source_id, occurred_at, user_id)
                VALUES (gen_random_uuid(), '{account}', 'PAYMENT', 100, 1100, 'PAYABLE_PAYMENT', gen_random_uuid(), now(), '{User}')
                """,
                app)))
            .ConstraintName.ShouldBe("ck_payable_entries__sign");
    }

    [Fact]
    public async Task Lotes_por_sucursal_filas_de_lote_sin_valor_y_una_sola_reversion()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        var category = Guid.CreateVersion7().ToString();
        var product = Guid.CreateVersion7().ToString();
        var lot = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(
            $"""
            INSERT INTO catalog.categories (id, company_id, name, level, path, status, created_at, created_by)
            VALUES ('{category}', '{Company}', 'General', 1, '/{category}/', 'ACTIVE', now(), '{User}');
            INSERT INTO catalog.products (id, company_id, sku, name, short_name, category_id, base_unit_code, sale_mode, product_type, tracks_lots,
                search_text, status, created_at, created_by)
            VALUES ('{product}', '{Company}', 'Y1', 'Yogurt', 'Yogurt', '{category}', 'UND', 'UNIT', 'STOCKABLE', true, 'yogurt', 'ACTIVE', now(), '{User}');
            INSERT INTO inventory.inventory_lots (id, company_id, branch_id, product_id, lot_number, expiry_date, status, created_at, created_by)
            VALUES ('{lot}', '{Company}', '{Branch}', '{product}', 'L1', current_date + 30, 'AVAILABLE', now(), '{User}');
            """,
            app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"""
                INSERT INTO inventory.inventory_lots (id, company_id, branch_id, product_id, lot_number, status, created_at, created_by)
                VALUES (gen_random_uuid(), '{Company}', '{Branch}', '{product}', 'L1', 'AVAILABLE', now(), '{User}')
                """,
                app)))
            .ConstraintName.ShouldBe("ux_inventory_lots__branch_product_lot");

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"""
                INSERT INTO inventory.stock_balances (id, company_id, node_id, branch_id, warehouse_id, product_id, lot_id, quantity, total_value, average_cost)
                SELECT gen_random_uuid(), '{Company}', installation_id, '{Branch}', '{Warehouse}', '{product}', '{lot}', 5, 500, 100 FROM system.installation
                """,
                app)))
            .ConstraintName.ShouldBe("ck_stock_balances__lot_quantity_only");

        var original = Guid.CreateVersion7().ToString();
        string Movement(string id, string type, int direction, string? reverses) =>
            $"""
            INSERT INTO inventory.stock_movements (id, company_id, node_id, branch_id, warehouse_id, product_id, lot_id, movement_type, direction, quantity,
                unit_cost, total_cost, balance_quantity, balance_value, balance_avg_cost, source_type, source_id, reverses_movement_id, business_date,
                occurred_at, user_id)
            SELECT '{id}', '{Company}', installation_id, '{Branch}', '{Warehouse}', '{product}', '{lot}', '{type}', {direction}, 5, 100, 500, 5, 500, 100,
                'PURCHASE', gen_random_uuid(), {(reverses is null ? "NULL" : $"'{reverses}'")}, current_date, now(), '{User}'
            FROM system.installation
            """;
        await harness.Database.ExecuteAsync(Movement(original, "PURCHASE_RECEIPT", 1, null), app);
        await harness.Database.ExecuteAsync(Movement(Guid.CreateVersion7().ToString(), "REVERSAL", -1, original), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Movement(Guid.CreateVersion7().ToString(), "REVERSAL", -1, original), app)))
            .ConstraintName.ShouldBe("ux_stock_movements__reverses");
    }

    [Fact]
    public async Task Solo_el_efectivo_afecta_el_cajon_y_los_tipos_de_documento_nuevos_existen()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"""
                INSERT INTO cash.payment_methods (id, company_id, code, name, kind, affects_cash_drawer, status, created_at, created_by)
                VALUES (gen_random_uuid(), '{Company}', 'NEQUI', 'Nequi', 'WALLET', true, 'ACTIVE', now(), '{User}')
                """,
                harness.Database.AppConnectionString)))
            .ConstraintName.ShouldBe("ck_payment_methods__drawer");
        (await harness.Database.ScalarAsync<long>(
                "SELECT count(*) FROM system.document_types WHERE code IN ('PURCHASE_ORDER', 'PAYABLE_PAYMENT', 'PURCHASE', 'SUPPLIER_RETURN')"))
            .ShouldBe(4);
    }
}
