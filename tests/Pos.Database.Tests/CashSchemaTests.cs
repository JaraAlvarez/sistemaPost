using Npgsql;

namespace Pos.Database.Tests;

/// <summary>Restricciones de las migraciones V2026.10.014–015 (caja y gastos) sobre PostgreSQL real.</summary>
public class CashSchemaTests(PostgresFixture postgres)
{
    private static readonly string Company = InfrastructureHarness.CompanyId.ToString();
    private static readonly string User = InfrastructureHarness.SystemUserId.ToString();
    private static readonly string Branch = InfrastructureHarness.BranchS01.ToString();
    private static readonly string Terminal = InfrastructureHarness.TerminalC01.ToString();

    private static string Session(string id, string number, string status = "OPEN") =>
        $"""
        INSERT INTO cash.cash_sessions (id, company_id, branch_id, pos_terminal_id, cashier_id, number, business_date, opened_at, opening_float,
            status, blind_count, closing_started_at, closed_at, expected_total, counted_total, difference, created_at, created_by)
        VALUES ('{id}', '{Company}', '{Branch}', '{Terminal}', '{User}', '{number}', current_date, now(), 100000, '{status}', true,
            {(status == "OPEN" ? "NULL" : "now()")}, {(status == "CLOSED" ? "now()" : "NULL")}, {(status == "CLOSED" ? "0, 0, 0" : "NULL, NULL, NULL")},
            now(), '{User}')
        """;

    private static async Task<string> CashMethodAsync(InfrastructureHarness harness)
    {
        var method = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(
            $"""
            INSERT INTO cash.payment_methods (id, company_id, code, name, kind, affects_cash_drawer, status, created_at, created_by)
            VALUES ('{method}', '{Company}', 'EFECTIVO', 'Efectivo', 'CASH', true, 'ACTIVE', now(), '{User}')
            """,
            harness.Database.AppConnectionString);
        return method;
    }

    [Fact]
    public async Task Una_jornada_sin_cerrar_por_caja_y_por_cajero()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        await harness.Database.ExecuteAsync(Session(Guid.CreateVersion7().ToString(), "J-1", "CLOSED"), app);
        await harness.Database.ExecuteAsync(Session(Guid.CreateVersion7().ToString(), "J-2"), app);
        var second = await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Session(Guid.CreateVersion7().ToString(), "J-3"), app));
        second.ConstraintName.ShouldBeOneOf("ux_cash_sessions__terminal_open", "ux_cash_sessions__cashier_open");

        // Un cierre exige sus totales; el cajero no revisa su propio cierre.
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                "UPDATE cash.cash_sessions SET status = 'CLOSED', closing_started_at = now(), closed_at = now() WHERE number = 'J-2'", app)))
            .ConstraintName.ShouldBe("ck_cash_sessions__closed");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(
                $"UPDATE cash.cash_sessions SET reviewed_by = '{User}', reviewed_at = now() WHERE number = 'J-1'", app)))
            .ConstraintName.ShouldBe("ck_cash_sessions__reviewer");
    }

    [Fact]
    public async Task Los_movimientos_de_caja_son_de_solo_insercion_y_su_direccion_la_fija_el_tipo()
    {
        await using var harness = await InfrastructureHarness.CreateAsync(postgres);
        var app = harness.Database.AppConnectionString;
        var session = Guid.CreateVersion7().ToString();
        await harness.Database.ExecuteAsync(Session(session, "J-1"), app);
        var method = await CashMethodAsync(harness);
        string Movement(int line, string type, int direction, decimal amount) =>
            $"""
            INSERT INTO cash.cash_movements (id, company_id, session_id, line_no, movement_type, payment_method_id, direction, amount, user_id, occurred_at)
            VALUES (gen_random_uuid(), '{Company}', '{session}', {line}, '{type}', '{method}', {direction}, {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, '{User}', now())
            """;

        await harness.Database.ExecuteAsync(Movement(1, "OPENING_FLOAT", 1, 100_000m), app);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Movement(2, "CASH_OUT_WITHDRAWAL", 1, 5m), app)))
            .ConstraintName.ShouldBe("ck_cash_movements__direction");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Movement(2, "NO_SALE_DRAWER_OPEN", 0, 5m), app)))
            .ConstraintName.ShouldBe("ck_cash_movements__amount");
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync(Movement(1, "CASH_IN", 1, 5m), app)))
            .ConstraintName.ShouldBe("ux_cash_movements__session_line");
        await harness.Database.ExecuteAsync(Movement(2, "CORRECTION", -1, 5m), app);

        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("UPDATE cash.cash_movements SET amount = 1", app)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => harness.Database.ExecuteAsync("DELETE FROM cash.cash_movements")))
            .MessageText.ShouldContain("solo inserción");
    }
}
