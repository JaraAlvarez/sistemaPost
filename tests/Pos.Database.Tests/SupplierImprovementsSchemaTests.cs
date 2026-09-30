using Npgsql;

namespace Pos.Database.Tests;

/// <summary>Restricciones de la migración V2026.10.025 (mejoras de proveedores, Fase 8 · 8.4) y del catálogo ref.banks sobre PostgreSQL real.</summary>
public class SupplierImprovementsSchemaTests(PostgresFixture postgres)
{
    private static readonly string Company = InfrastructureHarness.CompanyId.ToString();
    private static readonly string User = InfrastructureHarness.SystemUserId.ToString();
    private static readonly string Branch = InfrastructureHarness.BranchS01.ToString();
    private static readonly string Other = Guid.CreateVersion7().ToString();

    private static async Task<string> SeedSupplierAsync(InfrastructureHarness harness)
    {
        var party = Guid.CreateVersion7().ToString();
        var supplier = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(
            $"""
            INSERT INTO parties.parties (id, company_id, person_type, identification_type, identification_number, first_names, last_names, tax_regime,
                fiscal_responsibilities, search_text, status, created_at, created_by)
            VALUES ('{party}', '{Company}', 'NATURAL', 'CC', '{Random.Shared.Next(10_000_000, 99_999_999)}', 'Ana', 'Pérez', '49', 'R-99-PN', 'ana perez',
                'ACTIVE', now(), '{User}');
            INSERT INTO purchasing.suppliers (id, company_id, party_id, code, status, minimum_order_amount, order_cutoff_note, created_at, created_by)
            VALUES ('{supplier}', '{Company}', '{party}', 'P{Random.Shared.Next(1000, 9999)}', 'ACTIVE', 250000, 'Martes 10 a. m.', now(), '{User}');
            """,
            harness.Database.AppConnectionString);
        return supplier;
    }

    private static string Account(
        string id, string supplier, string number, string status = "PENDING_VERIFICATION", bool primary = false, string? verifiedBy = null, string bank = "1007") =>
        $"""
        INSERT INTO purchasing.supplier_bank_accounts (id, company_id, supplier_id, bank_code, account_type, account_number, holder_name,
            holder_identification_type, holder_identification_number, status, is_primary, changed_at, changed_by, verified_at, verified_by, created_at, created_by)
        VALUES ('{id}', '{Company}', '{supplier}', '{bank}', 'SAVINGS', '{number}', 'Ana Pérez', 'CC', '52123456', '{status}', {(primary ? "true" : "false")},
            now(), '{User}', {(verifiedBy is null ? "NULL" : "now()")}, {(verifiedBy is null ? "NULL" : $"'{verifiedBy}'")}, now(), '{User}')
        """;

    [Fact]
    public async Task Los_bancos_de_Colombia_estan_cargados_y_pos_app_no_los_modifica()
    {
        var db = await postgres.CreateDatabaseAsync(migrate: true);

        (await db.ScalarAsync<string>("SELECT name FROM ref.banks WHERE code = '1007'")).ShouldBe("Bancolombia");
        (await db.ScalarAsync<long>("SELECT count(*) FROM ref.banks WHERE is_active", db.AppConnectionString)).ShouldBeGreaterThanOrEqualTo(25);
        (await Should.ThrowAsync<PostgresException>(() => db.ExecuteAsync("UPDATE ref.banks SET name = 'X'", db.AppConnectionString)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(
                () => db.ExecuteAsync("INSERT INTO ref.banks (code, country_code, name) VALUES ('9999', 'CO', 'Banco falso')", db.AppConnectionString)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task La_cuenta_verificada_exige_verificador_distinto_de_quien_la_cambio()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        var supplier = await SeedSupplierAsync(harness);

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                Account(Guid.CreateVersion7().ToString(), supplier, "123456789", "VERIFIED", verifiedBy: User), app)))
            .ConstraintName.ShouldBe("ck_supplier_bank_accounts__verifier");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                Account(Guid.CreateVersion7().ToString(), supplier, "123456789", "VERIFIED"), app)))
            .ConstraintName.ShouldBe("ck_supplier_bank_accounts__verified");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                Account(Guid.CreateVersion7().ToString(), supplier, "12AB"), app)))
            .ConstraintName.ShouldBe("ck_supplier_bank_accounts__number");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                Account(Guid.CreateVersion7().ToString(), supplier, "123456789", "INACTIVE", primary: true), app)))
            .ConstraintName.ShouldBe("ck_supplier_bank_accounts__primary");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                Account(Guid.CreateVersion7().ToString(), supplier, "123456789", bank: "0000"), app)))
            .ConstraintName.ShouldBe("fk_supplier_bank_accounts__bank");

        await harness.Database.ExecuteAsync(Account(Guid.CreateVersion7().ToString(), supplier, "123456789", "VERIFIED", verifiedBy: Other), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                Account(Guid.CreateVersion7().ToString(), supplier, "123456789"), app)))
            .ConstraintName.ShouldBe("ux_supplier_bank_accounts__number");
        // El mismo número en otro banco es otra cuenta.
        await harness.Database.ExecuteAsync(Account(Guid.CreateVersion7().ToString(), supplier, "123456789", bank: "1051"), app);
    }

    [Fact]
    public async Task Una_sola_cuenta_principal_por_proveedor_y_el_cambio_de_principal_es_un_solo_guardado()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        var supplier = await SeedSupplierAsync(harness);
        var first = Guid.CreateVersion7().ToString();
        var second = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(Account(first, supplier, "11111111", primary: true), app);
        await harness.Database.ExecuteAsync(Account(second, supplier, "22222222"), app);

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"UPDATE purchasing.supplier_bank_accounts SET is_primary = true WHERE id = '{second}'", app)))
            .ConstraintName.ShouldBe("ex_supplier_bank_accounts__primary");

        // Restricción diferida: marcar la nueva antes de desmarcar la anterior, en la misma transacción, es válido.
        await harness.Database.ExecuteAsync(
            $"""
            UPDATE purchasing.supplier_bank_accounts SET is_primary = true WHERE id = '{second}';
            UPDATE purchasing.supplier_bank_accounts SET is_primary = false WHERE id = '{first}';
            """,
            app);
        (await harness.Database.ScalarAsync<string>(
                $"SELECT id::text FROM purchasing.supplier_bank_accounts WHERE supplier_id = '{supplier}' AND is_primary", app))
            .ShouldBe(second);
    }

    [Fact]
    public async Task Agenda_sin_entradas_repetidas_con_sucursal_opcional_y_borrado_logico()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        var supplier = await SeedSupplierAsync(harness);
        string Entry(string id, int day, string? branch, string kind = "VISIT") =>
            $"""
            INSERT INTO purchasing.supplier_schedules (id, company_id, supplier_id, branch_id, day_of_week, kind, created_at, created_by)
            VALUES ('{id}', '{Company}', '{supplier}', {(branch is null ? "NULL" : $"'{branch}'")}, {day}, '{kind}', now(), '{User}')
            """;

        var all = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(Entry(all, 1, null), app);
        await harness.Database.ExecuteAsync(Entry(Guid.CreateVersion7().ToString(), 1, Branch), app);
        await harness.Database.ExecuteAsync(Entry(Guid.CreateVersion7().ToString(), 1, null, "DELIVERY"), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Entry(Guid.CreateVersion7().ToString(), 1, null), app)))
            .ConstraintName.ShouldBe("ux_supplier_schedules__entry");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Entry(Guid.CreateVersion7().ToString(), 8, null), app)))
            .ConstraintName.ShouldBe("ck_supplier_schedules__day");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Entry(Guid.CreateVersion7().ToString(), 2, null, "LUNCH"), app)))
            .ConstraintName.ShouldBe("ck_supplier_schedules__kind");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Entry(Guid.CreateVersion7().ToString(), 2, Other), app)))
            .ConstraintName.ShouldBe("fk_supplier_schedules__branch");

        // Borrada lógicamente, la entrada deja de ocupar su lugar.
        await harness.Database.ExecuteAsync(
            $"UPDATE purchasing.supplier_schedules SET deleted_at = now(), deleted_by = '{User}' WHERE id = '{all}'", app);
        await harness.Database.ExecuteAsync(Entry(Guid.CreateVersion7().ToString(), 1, null), app);
    }

    [Fact]
    public async Task Retenciones_sugeridas_una_por_tipo_con_tarifa_positiva_y_pedido_minimo_no_negativo()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        var supplier = await SeedSupplierAsync(harness);
        string Default(string kind, decimal rate) =>
            $"""
            INSERT INTO purchasing.supplier_withholding_defaults (id, company_id, supplier_id, kind, rate, created_at, created_by)
            VALUES (gen_random_uuid(), '{Company}', '{supplier}', '{kind}', {rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}, now(), '{User}')
            """;

        await harness.Database.ExecuteAsync(Default("RETEFUENTE", 2.5m), app);
        await harness.Database.ExecuteAsync(Default("RETEICA", 0.414m), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Default("RETEFUENTE", 3.5m), app)))
            .ConstraintName.ShouldBe("ux_supplier_withholding_defaults__kind");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Default("RETEIVA", 0m), app)))
            .ConstraintName.ShouldBe("ck_supplier_withholding_defaults__rate");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Default("IVA", 1m), app)))
            .ConstraintName.ShouldBe("ck_supplier_withholding_defaults__kind");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"UPDATE purchasing.suppliers SET minimum_order_amount = -1 WHERE id = '{supplier}'", app)))
            .ConstraintName.ShouldBe("ck_suppliers__minimum_order");
    }
}
