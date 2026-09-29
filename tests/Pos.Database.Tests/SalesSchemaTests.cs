using Npgsql;

namespace Pos.Database.Tests;

/// <summary>
/// Restricciones de las migraciones V2026.10.016–019 (promociones, ventas, facturación y periféricos de la caja) sobre
/// PostgreSQL real.
/// </summary>
public class SalesSchemaTests(PostgresFixture postgres)
{
    private static readonly string Company = InfrastructureHarness.CompanyId.ToString();
    private static readonly string User = InfrastructureHarness.SystemUserId.ToString();
    private static readonly string Branch = InfrastructureHarness.BranchS01.ToString();
    private static readonly string Warehouse = InfrastructureHarness.WarehouseS01.ToString();
    private static readonly string TerminalC01 = InfrastructureHarness.TerminalC01.ToString();
    private static readonly string TerminalC02 = InfrastructureHarness.TerminalC02.ToString();

    private static string Session(string id, string terminal, string number, string status = "OPEN") =>
        $"""
        INSERT INTO cash.cash_sessions (id, company_id, branch_id, pos_terminal_id, cashier_id, number, business_date, opened_at, opening_float,
            status, blind_count, closing_started_at, closed_at, expected_total, counted_total, difference, created_at, created_by)
        VALUES ('{id}', '{Company}', '{Branch}', '{terminal}', '{User}', '{number}', current_date, now(), 100000, '{status}', true,
            {(status == "OPEN" ? "NULL" : "now()")}, {(status == "CLOSED" ? "now()" : "NULL")}, {(status == "CLOSED" ? "0, 0, 0" : "NULL, NULL, NULL")},
            now(), '{User}')
        """;

    /// <summary>Una venta; completada (o anulada) lleva número, fecha y llave de cierre.</summary>
    private static string Sale(string id, string terminal, string session, string status = "OPEN", string? number = null,
        decimal total = 0m, decimal paid = 0m, decimal change = 0m)
    {
        var completed = status is "COMPLETED" or "VOIDED";
        return $"""
            INSERT INTO sales.sales (id, company_id, branch_id, pos_terminal_id, warehouse_id, cash_session_id, cashier_id, business_date, status,
                return_status, number, customer_name, customer_identification_type, customer_identification, opened_at, completed_at, completion_key,
                voided_at, void_reason, voided_by, total, paid_total, change_total, created_at, created_by)
            VALUES ('{id}', '{Company}', '{Branch}', '{terminal}', '{Warehouse}', '{session}', '{User}', current_date, '{status}', 'NONE',
                {(number is null ? "NULL" : $"'{number}'")}, 'Consumidor final', 'CC', '222222222222', now(),
                {(completed ? "now()" : "NULL")}, {(completed ? $"'{id}'" : "NULL")},
                {(status == "VOIDED" ? $"now(), 'Error', '{User}'" : "NULL, NULL, NULL")},
                {Money(total)}, {Money(paid)}, {Money(change)}, now(), '{User}')
            """;
    }

    private static string Money(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Siembra jornadas abiertas en C01 y cerrada en C02 (la única jornada abierta es por cajero).</summary>
    private static async Task<(string C01, string C02)> SessionsAsync(InfrastructureHarness harness)
    {
        var c01 = Guid.CreateVersion7().ToString();
        var c02 = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync($"{Session(c02, TerminalC02, "J-2", "CLOSED")}; {Session(c01, TerminalC01, "J-1")}",
            harness.Database.AppConnectionString);
        return (c01, c02);
    }

    private static async Task<string> PaymentMethodAsync(InfrastructureHarness harness, string code, string kind, bool affectsDrawer)
    {
        var method = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(
            $"""
            INSERT INTO cash.payment_methods (id, company_id, code, name, kind, affects_cash_drawer, status, created_at, created_by)
            VALUES ('{method}', '{Company}', '{code}', '{code}', '{kind}', {(affectsDrawer ? "true" : "false")}, 'ACTIVE', now(), '{User}')
            """,
            harness.Database.AppConnectionString);
        return method;
    }

    [Fact]
    public async Task El_numero_de_venta_es_unico_por_caja_y_hay_una_sola_venta_en_curso_por_caja()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        var (c01, c02) = await SessionsAsync(harness);

        await harness.Database.ExecuteAsync(Sale(Guid.CreateVersion7().ToString(), TerminalC01, c01, "COMPLETED", "V-1", 1000m, 1000m), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                Sale(Guid.CreateVersion7().ToString(), TerminalC01, c01, "VOIDED", "V-1", 1000m, 1000m), app)))
            .ConstraintName.ShouldBe("ux_sales__terminal_number");
        // La serie es de cada caja: otra caja puede usar el mismo número.
        await harness.Database.ExecuteAsync(Sale(Guid.CreateVersion7().ToString(), TerminalC02, c02, "COMPLETED", "V-1", 1000m, 1000m), app);

        // Una sola venta OPEN por caja; las suspendidas no cuentan y otra caja tiene la suya.
        await harness.Database.ExecuteAsync(Sale(Guid.CreateVersion7().ToString(), TerminalC01, c01), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Sale(Guid.CreateVersion7().ToString(), TerminalC01, c01), app)))
            .ConstraintName.ShouldBe("ux_sales__terminal_open");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Sale(Guid.CreateVersion7().ToString(), TerminalC01, c01, "ON_HOLD"), app)))
            .ConstraintName.ShouldBe("ck_sales__hold");
        await harness.Database.ExecuteAsync(
            Sale(Guid.CreateVersion7().ToString(), TerminalC01, c01, "ON_HOLD").Replace("INSERT INTO sales.sales (id,", "INSERT INTO sales.sales (held_at, id,")
                .Replace("VALUES ('", "VALUES (now(), '"),
            app);
        await harness.Database.ExecuteAsync(Sale(Guid.CreateVersion7().ToString(), TerminalC02, c02), app);

        // Completar exige número, fecha y llave de cierre.
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                "UPDATE sales.sales SET status = 'COMPLETED' WHERE status = 'OPEN' AND number IS NULL", app)))
            .ConstraintName.ShouldBe("ck_sales__completed");
    }

    [Fact]
    public async Task Una_venta_completada_cumple_pagado_menos_cambio_igual_total_y_solo_el_efectivo_da_cambio()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        var (c01, _) = await SessionsAsync(harness);

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                Sale(Guid.CreateVersion7().ToString(), TerminalC01, c01, "COMPLETED", "V-1", total: 10_000m, paid: 20_000m, change: 5_000m), app)))
            .ConstraintName.ShouldBe("ck_sales__paid");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                Sale(Guid.CreateVersion7().ToString(), TerminalC01, c01, "VOIDED", "V-1", total: 10_000m, paid: 9_000m), app)))
            .ConstraintName.ShouldBe("ck_sales__paid");
        var sale = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(Sale(sale, TerminalC01, c01, "COMPLETED", "V-1", total: 10_000m, paid: 20_000m, change: 10_000m), app);
        // Una venta en curso todavía no cuadra (sin pagos).
        await harness.Database.ExecuteAsync(Sale(Guid.CreateVersion7().ToString(), TerminalC01, c01, total: 5_000m), app);

        var cash = await PaymentMethodAsync(harness, "EFECTIVO", "CASH", affectsDrawer: true);
        var card = await PaymentMethodAsync(harness, "DEBITO", "DEBIT_CARD", affectsDrawer: false);
        string Payment(int line, string method, string code, string kind, bool drawer, decimal tendered, decimal applied, decimal change, string last4 = "NULL") =>
            $"""
            INSERT INTO sales.sale_payments (id, sale_id, line_no, payment_method_id, method_code, method_kind, affects_cash_drawer, tendered, applied,
                change, card_last4)
            VALUES (gen_random_uuid(), '{sale}', {line}, '{method}', '{code}', '{kind}', {(drawer ? "true" : "false")}, {Money(tendered)},
                {Money(applied)}, {Money(change)}, {last4})
            """;

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Payment(1, card, "DEBITO", "DEBIT_CARD", false, 12_000m, 10_000m, 2_000m), app)))
            .ConstraintName.ShouldBe("ck_sale_payments__change");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Payment(1, cash, "EFECTIVO", "CASH", true, 20_000m, 10_000m, 5_000m), app)))
            .ConstraintName.ShouldBe("ck_sale_payments__amounts");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Payment(1, card, "DEBITO", "DEBIT_CARD", false, 1m, 1m, 0m, "'12a4'"), app)))
            .ConstraintName.ShouldBe("ck_sale_payments__card");
        await harness.Database.ExecuteAsync(Payment(1, cash, "EFECTIVO", "CASH", true, 20_000m, 10_000m, 10_000m), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Payment(1, card, "DEBITO", "DEBIT_CARD", false, 1m, 1m, 0m, "'1234'"), app)))
            .ConstraintName.ShouldBe("ux_sale_payments__line");
    }

    [Fact]
    public async Task Un_solo_cambio_de_mercancia_en_borrador_por_venta()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        var (c01, _) = await SessionsAsync(harness);
        var original = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(Sale(original, TerminalC01, c01, "COMPLETED", "V-1", 10_000m, 10_000m), app);

        // Un cambio (EXCHANGE) exige la venta nueva que lo paga; la FK es diferida (se crean en la misma transacción).
        string Return(string status, string kind = "EXCHANGE", string? replacement = null, string? number = null) =>
            $"""
            INSERT INTO sales.customer_returns (id, company_id, branch_id, pos_terminal_id, cash_session_id, original_sale_id, original_sale_number, kind,
                status, number, business_date, reason, credit_total, replacement_sale_id, received_by, received_at, completed_at, cancelled_at,
                created_at, created_by)
            VALUES (gen_random_uuid(), '{Company}', '{Branch}', '{TerminalC01}', '{c01}', '{original}', 'V-1', '{kind}', '{status}',
                {(number is null ? "NULL" : $"'{number}'")}, current_date, 'Talla equivocada', 5000,
                {(replacement is null ? "NULL" : $"'{replacement}'")}, '{User}', now(),
                {(status == "COMPLETED" ? "now()" : "NULL")}, {(status == "CANCELLED" ? "now()" : "NULL")}, now(), '{User}')
            """;

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Return("DRAFT"), app)))
            .ConstraintName.ShouldBe("ck_customer_returns__exchange");

        var replacement = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync($"BEGIN; {Return("DRAFT", replacement: replacement)}; {Sale(replacement, TerminalC01, c01)}; COMMIT;", app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Return("DRAFT", "WARRANTY_REFUND"), app)))
            .ConstraintName.ShouldBe("ux_customer_returns__draft");
        // Los cancelados no ocupan el borrador.
        await harness.Database.ExecuteAsync(Return("CANCELLED", "WARRANTY_REFUND"), app);

        // La venta nueva debe existir al confirmar.
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"UPDATE sales.customer_returns SET status = 'CANCELLED', cancelled_at = now(), replacement_sale_id = gen_random_uuid() WHERE status = 'DRAFT'",
                app)))
            .ConstraintName.ShouldBe("fk_customer_returns__replacement_sale");

        // Un reintegro por garantía completado exige el medio con que se devolvió el dinero.
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Return("COMPLETED", "WARRANTY_REFUND", number: "D-1"), app)))
            .ConstraintName.ShouldBe("ck_customer_returns__refund");
    }

    [Fact]
    public async Task Los_eventos_de_los_documentos_fiscales_son_de_solo_insercion()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        var document = Guid.CreateVersion7().ToString();
        var sale = Guid.CreateVersion7().ToString();
        string Document(string id, string type = "INTERNAL_RECEIPT", string status = "NOT_REQUIRED") =>
            $"""
            INSERT INTO billing.fiscal_documents (id, company_id, branch_id, pos_terminal_id, source, source_id, source_number, document_type, status,
                business_date, buyer_name, buyer_identification_type, buyer_identification, subtotal, tax_total, total, issued_at, created_at, created_by)
            VALUES ('{id}', '{Company}', '{Branch}', '{TerminalC01}', 'SALE', '{sale}', 'V-1', '{type}', '{status}', current_date, 'Consumidor final',
                'CC', '222222222222', 1000, 190, 1190, now(), now(), '{User}')
            """;

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Document(document, status: "PENDING"), app)))
            .ConstraintName.ShouldBe("ck_fiscal_documents__internal");
        await harness.Database.ExecuteAsync(Document(document), app);
        // Un documento por origen.
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Document(Guid.CreateVersion7().ToString()), app)))
            .ConstraintName.ShouldBe("ux_fiscal_documents__source");

        await harness.Database.ExecuteAsync(
            $"""
            INSERT INTO billing.fiscal_document_events (id, fiscal_document_id, event_type, detail, occurred_at, user_id)
            VALUES (gen_random_uuid(), '{document}', 'ISSUED', 'Comprobante interno', now(), '{User}')
            """,
            app);

        // El rol de la aplicación no tiene el privilegio; ni el superusuario pasa el disparador.
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("UPDATE billing.fiscal_document_events SET detail = 'x'", app)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("DELETE FROM billing.fiscal_document_events", app)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("UPDATE billing.fiscal_document_events SET detail = 'x'")))
            .MessageText.ShouldContain("solo inserción");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("DELETE FROM billing.fiscal_document_events")))
            .MessageText.ShouldContain("solo inserción");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("TRUNCATE billing.fiscal_document_events")))
            .MessageText.ShouldContain("solo inserción");
        (await harness.Database.ScalarAsync<long>("SELECT count(*) FROM billing.fiscal_document_events")).ShouldBe(1);
    }

    [Fact]
    public async Task Las_promociones_validan_su_regla_vigencia_dias_horario_y_objetivo()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        string Promotion(string id, string number, string type = "MULTI_BUY", string rule = "3, 2, NULL, NULL, NULL", int days = 127,
            string hours = "NULL, NULL", string validTo = "NULL", string status = "DRAFT", string activatedAt = "NULL") =>
            $"""
            INSERT INTO promotions.promotions (id, company_id, number, name, type, status, valid_from, valid_to, days, start_time, end_time, all_branches,
                buy_quantity, pay_quantity, price, percent, min_quantity, activated_at, created_at, created_by)
            VALUES ('{id}', '{Company}', '{number}', 'Promo', '{type}', '{status}', now(), {validTo}, {days}, {hours}, true, {rule}, {activatedAt},
                now(), '{User}')
            """;
        async Task<string?> Violation(string sql) =>
            (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(sql, app))).ConstraintName;

        (await Violation(Promotion(Guid.CreateVersion7().ToString(), "P-1", rule: "2, 2, NULL, NULL, NULL"))).ShouldBe("ck_promotions__rule");
        (await Violation(Promotion(Guid.CreateVersion7().ToString(), "P-1", "PERCENT_OFF", "NULL, NULL, NULL, 120, NULL"))).ShouldBe("ck_promotions__rule");
        (await Violation(Promotion(Guid.CreateVersion7().ToString(), "P-1", "QUANTITY_PRICE", "NULL, NULL, 5000, NULL, 0"))).ShouldBe("ck_promotions__rule");

        // V020: un parámetro nulo no pasa la regla (un CHECK que da NULL no falla).
        (await Violation(Promotion(Guid.CreateVersion7().ToString(), "P-1", rule: "3, NULL, NULL, NULL, NULL"))).ShouldBe("ck_promotions__rule");
        (await Violation(Promotion(Guid.CreateVersion7().ToString(), "P-1", "PERCENT_OFF", "NULL, NULL, NULL, NULL, NULL"))).ShouldBe("ck_promotions__rule");
        (await Violation(Promotion(Guid.CreateVersion7().ToString(), "P-1", "QUANTITY_PRICE", "NULL, NULL, 5000, NULL, NULL"))).ShouldBe("ck_promotions__rule");
        (await Violation(Promotion(Guid.CreateVersion7().ToString(), "P-1", days: 0))).ShouldBe("ck_promotions__days");
        (await Violation(Promotion(Guid.CreateVersion7().ToString(), "P-1", days: 128))).ShouldBe("ck_promotions__days");
        (await Violation(Promotion(Guid.CreateVersion7().ToString(), "P-1", hours: "'08:00', NULL"))).ShouldBe("ck_promotions__hours");
        (await Violation(Promotion(Guid.CreateVersion7().ToString(), "P-1", hours: "'08:00', '08:00'"))).ShouldBe("ck_promotions__hours");
        (await Violation(Promotion(Guid.CreateVersion7().ToString(), "P-1", validTo: "now() - interval '1 day'"))).ShouldBe("ck_promotions__validity");
        (await Violation(Promotion(Guid.CreateVersion7().ToString(), "P-1", status: "ACTIVE"))).ShouldBe("ck_promotions__active");

        // Horario nocturno (cruza la medianoche) y activa con su fecha de activación: válidas.
        var promotion = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(Promotion(promotion, "P-1", hours: "'22:00', '02:00'", status: "ACTIVE", activatedAt: "now()"), app);
        (await Violation(Promotion(Guid.CreateVersion7().ToString(), "P-1"))).ShouldBe("ux_promotions__company_number");

        // Cada ítem apunta exactamente a un producto, una categoría o una marca.
        (await Violation($"INSERT INTO promotions.promotion_items (id, promotion_id, quantity) VALUES (gen_random_uuid(), '{promotion}', 1)"))
            .ShouldBe("ck_promotion_items__target");

        await harness.Database.ExecuteAsync(
            $"INSERT INTO promotions.promotion_branches (id, promotion_id, branch_id) VALUES (gen_random_uuid(), '{promotion}', '{Branch}')", app);
        (await Violation($"INSERT INTO promotions.promotion_branches (id, promotion_id, branch_id) VALUES (gen_random_uuid(), '{promotion}', '{Branch}')"))
            .ShouldBe("ux_promotion_branches__branch");
    }

    [Fact]
    public async Task Cada_caja_tiene_una_sola_impresora_de_tiquetes_con_configuracion_valida()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        string Printer(string terminal, string connection = "FILE", string address = "NULL", int paper = 80, string codePage = "PC850",
            string pin = "PIN2") =>
            $"""
            INSERT INTO org.terminal_devices (id, company_id, pos_terminal_id, kind, connection, address, paper_width_mm, code_page, auto_cut,
                drawer_connected, drawer_pin, created_at, created_by)
            VALUES (gen_random_uuid(), '{Company}', '{terminal}', 'RECEIPT_PRINTER', '{connection}', {address}, {paper}, '{codePage}', true, true,
                '{pin}', now(), '{User}')
            """;
        async Task<string?> Violation(string sql) =>
            (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(sql, app))).ConstraintName;

        (await Violation(Printer(TerminalC01, "NETWORK"))).ShouldBe("ck_terminal_devices__address");
        (await Violation(Printer(TerminalC01, "USB", "'x'"))).ShouldBe("ck_terminal_devices__connection");
        (await Violation(Printer(TerminalC01, paper: 76))).ShouldBe("ck_terminal_devices__paper");
        (await Violation(Printer(TerminalC01, codePage: "UTF8"))).ShouldBe("ck_terminal_devices__code_page");
        (await Violation(Printer(TerminalC01, pin: "PIN3"))).ShouldBe("ck_terminal_devices__drawer_pin");

        await harness.Database.ExecuteAsync(Printer(TerminalC01, "NETWORK", "'192.168.1.50:9100'"), app);
        await harness.Database.ExecuteAsync(Printer(TerminalC02, paper: 58), app);
        (await Violation(Printer(TerminalC01))).ShouldBe("ux_terminal_devices__kind");
        (await Violation(Printer(Guid.CreateVersion7().ToString()))).ShouldBe("fk_terminal_devices__terminal");
    }
}
