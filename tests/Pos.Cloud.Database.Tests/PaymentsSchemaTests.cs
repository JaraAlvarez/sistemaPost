using Pos.Cloud.Licensing.Application;
using static Pos.Cloud.Database.Tests.CloudSeed;

namespace Pos.Cloud.Database.Tests;

/// <summary>
/// Pago en línea (V2026.10.009): precios y eventos de la pasarela de solo inserción, una orden renueva una sola vez, referencia
/// BP-&lt;año&gt;-&lt;n&gt; y periodos válidos garantizados por la BD; las tablas nacen vacías (no se inventan precios).
/// </summary>
public class PaymentsSchemaTests(CloudPostgresFixture postgres)
{
    [Fact]
    public async Task Las_tablas_nacen_vacias_y_el_consecutivo_lo_toma_la_aplicacion()
    {
        var db = await postgres.CreateDatabaseAsync();

        (await db.ScalarAsync<long>("SELECT count(*) FROM licensing.prices")).ShouldBe(0);
        (await db.ScalarAsync<long>("SELECT count(*) FROM licensing.payment_orders")).ShouldBe(0);
        (await db.ScalarAsync<long>("SELECT nextval('licensing.payment_order_number_seq')", db.AppConnectionString)).ShouldBe(1);
        (await db.ScalarAsync<long>("SELECT nextval('licensing.payment_order_number_seq')", db.AppConnectionString)).ShouldBe(2);
    }

    [Fact]
    public async Task Los_precios_y_los_eventos_de_la_pasarela_son_de_solo_insercion_incluso_para_el_dueno()
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.ExecuteAsync(
            $"""
            INSERT INTO licensing.prices (id, edition, billing_period, amount_cop, valid_from, created_at, created_by)
            VALUES (gen_random_uuid(), 'MULTI', 'MONTHLY', 59900, now(), now(), '{SystemUser}');
            INSERT INTO licensing.payment_events (id, received_at, gateway, source, signature_valid, payload)
            VALUES (gen_random_uuid(), now(), 'WOMPI', 'WEBHOOK', false, jsonb_build_object('a', 1));
            """,
            db.AppConnectionString);

        foreach (var (table, column) in new[] { ("licensing.prices", "valid_from"), ("licensing.payment_events", "received_at") })
        {
            (await db.ScalarAsync<bool>($"SELECT has_table_privilege('pos_app', '{table}', 'UPDATE')")).ShouldBeFalse(table);
            (await db.ScalarAsync<bool>($"SELECT has_table_privilege('pos_app', '{table}', 'INSERT')")).ShouldBeTrue(table);
            (await db.FailsAsync($"UPDATE {table} SET {column} = now()", db.AppConnectionString)).SqlState.ShouldBe("42501", table);
            (await db.FailsAsync($"UPDATE {table} SET {column} = now()")).MessageText.ShouldContain("solo inserción");
            (await db.FailsAsync($"DELETE FROM {table}")).MessageText.ShouldContain("solo inserción");
            (await db.FailsAsync($"TRUNCATE {table}")).MessageText.ShouldContain("solo inserción");
        }

        // Las órdenes sí cambian de estado (UPDATE), pero nunca se borran.
        (await db.ScalarAsync<bool>("SELECT has_table_privilege('pos_app', 'licensing.payment_orders', 'UPDATE')")).ShouldBeTrue();
        (await db.ScalarAsync<bool>("SELECT has_table_privilege('pos_app', 'licensing.payment_orders', 'DELETE')")).ShouldBeFalse();
    }

    [Fact]
    public async Task La_BD_exige_referencia_periodos_valor_en_centavos_y_una_sola_renovacion_por_orden()
    {
        var (sql, ids) = FullChain();
        var db = await postgres.CreateDatabaseAsync();
        await db.ExecuteAsync(sql, db.AppConnectionString);

        (await db.FailsAsync(Order(ids, "BP-26-1"), db.AppConnectionString)).ConstraintName.ShouldBe("ck_payment_orders__reference");
        (await db.FailsAsync(Order(ids, "BP-2026-000001", periods: 2), db.AppConnectionString)).ConstraintName.ShouldBe("ck_payment_orders__periods");
        (await db.FailsAsync(Order(ids, "BP-2026-000001", cents: 1), db.AppConnectionString)).ConstraintName.ShouldBe("ck_payment_orders__amount");

        // Dos renovaciones con la misma transacción de Wompi: la BD rechaza la segunda.
        var renewal = Guid.CreateVersion7();
        await db.ExecuteAsync(Renewal(renewal, ids.Subscription, "WOMPI 1234-1-1 BP-2026-000001") + Order(ids, "BP-2026-000001", renewal: renewal), db.AppConnectionString);
        (await db.FailsAsync(Renewal(Guid.CreateVersion7(), ids.Subscription, "WOMPI 1234-1-1 BP-2026-000001"), db.AppConnectionString))
            .ConstraintName.ShouldBe("ux_subscription_events__online_payment");

        // Un mismo evento de renovación no puede pertenecer a dos órdenes.
        (await db.FailsAsync(Order(ids, "BP-2026-000002", renewal: renewal), db.AppConnectionString)).ConstraintName.ShouldBe("ux_payment_orders__renewal_event");
        (await db.FailsAsync(Order(ids, "BP-2026-000001"), db.AppConnectionString)).ConstraintName.ShouldBe("ux_payment_orders__reference");
    }

    [Fact]
    public async Task Precios_y_enlace_de_pago_por_casos_de_uso()
    {
        await using var harness = await CloudHarness.CreateAsync(postgres);
        var db = harness.Database;
        var (sql, ids) = FullChain();
        await db.ExecuteAsync(sql, db.AppConnectionString);

        (await harness.SendAsync(new SetPriceCommand("MULTI", "MONTHLY", 59_900m))).IsSuccess.ShouldBeTrue();
        (await harness.SendAsync(new SetPriceCommand("MULTI", "MONTHLY", 64_900m, DateTimeOffset.UtcNow.AddDays(30)))).IsSuccess.ShouldBeTrue();
        (await harness.SendAsync(new SetPriceCommand("MULTI", "MONTHLY", 0m))).Error.Code.ShouldBe("PAYMENTS.INVALID_PRICE");

        var prices = (await harness.SendAsync(new ListPricesQuery())).Value;
        prices.PaymentsConfigured.ShouldBeFalse();
        prices.History.Count.ShouldBe(2);
        prices.Current.ShouldHaveSingleItem().AmountCop.ShouldBe(59_900m);
        (await db.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'PAYMENT_PRICE_SET'")).ShouldBe(2);

        // El enlace se crea una vez y se reutiliza; regenerarlo revoca el anterior.
        var link = (await harness.SendAsync(new EnsurePaymentLinkCommand(ids.Subscription))).Value;
        (await harness.SendAsync(new EnsurePaymentLinkCommand(ids.Subscription))).Value.Code.ShouldBe(link.Code);
        var regenerated = (await harness.SendAsync(new RegeneratePaymentLinkCommand(ids.Subscription))).Value;
        regenerated.Code.ShouldNotBe(link.Code);
        (await db.ScalarAsync<long>($"SELECT count(*) FROM licensing.payment_links WHERE subscription_id = '{ids.Subscription}' AND revoked_at IS NULL")).ShouldBe(1);

        // Sin llaves de Wompi la página pública lo dice y no se crean órdenes.
        var page = (await harness.SendAsync(new GetPaymentPageQuery(regenerated.Code))).Value;
        page.Configured.ShouldBeFalse();
        page.CanPay.ShouldBeFalse();
        (await harness.SendAsync(new GetPaymentPageQuery(link.Code))).Error.Code.ShouldBe("PAYMENTS.LINK_NOT_FOUND");
        (await harness.SendAsync(new CreatePaymentOrderCommand(regenerated.Code, 1, "https://portal.example"))).Error.Code.ShouldBe("PAYMENTS.NOT_CONFIGURED");
    }

    private static string Order(SeededLicense ids, string reference, int periods = 1, long? cents = null, Guid? renewal = null) => $"""
        INSERT INTO licensing.payment_orders (id, reference, subscription_id, organization_id, edition, billing_period, periods, amount_cop,
            amount_in_cents, currency, status, gateway, gateway_transaction_id, created_at, updated_at, applied_at, renewal_event_id)
        VALUES (gen_random_uuid(), '{reference}', '{ids.Subscription}', '{ids.Organization}', 'MULTI', 'MONTHLY', {periods}, {59900 * periods},
            {cents ?? 5_990_000L * periods}, 'COP', '{(renewal is null ? "PENDING" : "APPROVED")}', 'WOMPI', {(renewal is null ? "NULL" : "'1234-1-1'")},
            now(), now(), {(renewal is null ? "NULL" : "now()")}, {(renewal is null ? "NULL" : $"'{renewal}'")});
        """;

    private static string Renewal(Guid id, Guid subscription, string reference) => $"""
        INSERT INTO licensing.subscription_events (id, subscription_id, type, occurred_at, actor_id, payment_reference, period_start, period_end)
        VALUES ('{id}', '{subscription}', 'RENEWED', now(), '{SystemUser}', '{reference}', now(), now() + interval '1 month');
        """;
}
