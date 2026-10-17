using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Licensing.Application;
using Pos.Cloud.Licensing.Domain;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// Endurecimiento del pago en línea (docs/fases/fase-sync-pagos-endurecimiento.md §2): 503 para que Wompi reintente si no se pudo
/// confirmar, conciliación de rechazadas/vencidas, no vencer una orden que Wompi reporta pendiente, página de resultado con token,
/// eventos rechazados con solo metadatos y auditoría limitada por IP, antirrepetición y anulación que devuelve la vigencia.
/// </summary>
public class OnlinePaymentHardeningTests(PaymentsCloudFixture cloud) : IClassFixture<PaymentsCloudFixture>
{
    private const decimal MonthlyPrice = 59_900m;

    private FakePaymentGateway Gateway => cloud.Factory.Gateway;

    [Fact]
    public async Task Si_Wompi_no_responde_al_confirmar_el_webhook_responde_503_y_el_reintento_renueva()
    {
        var (customer, checkout) = await OrderAsync(periods: 1);
        var before = await ValidUntilAsync(customer);
        var transaction = new GatewayTransaction(NewTransactionId(), checkout.Reference, 5_990_000, "COP", "APPROVED", "NEQUI");
        Gateway.Set(transaction);
        var body = FakePaymentGateway.SignedEvent(transaction);

        Gateway.FailNextQueries = 1;
        var failed = await PostEventAsync(body);
        failed.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        failed.Code.ShouldBe("PAYMENTS.GATEWAY_UNAVAILABLE");
        (await ValidUntilAsync(customer)).ShouldBe(before);
        (await ScalarAsync<long>($"SELECT count(*) FROM licensing.payment_events WHERE signature_valid AND reference = '{checkout.Reference}'")).ShouldBe(1);

        // Wompi reintenta: ahora sí se confirma y renueva.
        (await PostEventAsync(body)).Result.ShouldBe("RENEWED");
        (await ValidUntilAsync(customer)).ShouldBe(before.AddMonths(1), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task La_conciliacion_revisa_las_rechazadas_recientes_y_aplica_un_pago_aprobado_despues()
    {
        var (customer, checkout) = await OrderAsync(periods: 1);
        var before = await ValidUntilAsync(customer);
        var declined = new GatewayTransaction(NewTransactionId(), checkout.Reference, 5_990_000, "COP", "DECLINED", "CARD");
        Gateway.Set(declined);
        (await PostEventAsync(FakePaymentGateway.SignedEvent(declined))).Result.ShouldBe("FAILED");
        var order = await OrderRowAsync(customer);
        order.Status.ShouldBe("DECLINED");

        // El cliente reintenta en el checkout y se aprueba, pero el evento se pierde: la conciliación la sigue revisando.
        Gateway.Set(new GatewayTransaction(NewTransactionId(), checkout.Reference, 5_990_000, "COP", "APPROVED", "PSE"));
        await using (var scope = cloud.Factory.Services.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            (await dispatcher.Send(new ListPendingPaymentOrdersQuery(1000), Ct)).Value.ShouldContain(order.Id);
            (await dispatcher.Send(new ReconcilePaymentOrderCommand(order.Id), Ct)).Value.ShouldContain("renovada");
        }

        (await OrderRowAsync(customer)).Status.ShouldBe("APPROVED");
        (await ValidUntilAsync(customer)).ShouldBe(before.AddMonths(1), TimeSpan.FromSeconds(1));

        // Una rechazada de hace más de 7 días ya no se revisa.
        var (_, old) = await OrderAsync(periods: 1);
        var oldDeclined = new GatewayTransaction(NewTransactionId(), old.Reference, 5_990_000, "COP", "DECLINED", "CARD");
        Gateway.Set(oldDeclined);
        (await PostEventAsync(FakePaymentGateway.SignedEvent(oldDeclined))).Result.ShouldBe("FAILED");
        await ExecuteAsync($"UPDATE licensing.payment_orders SET created_at = now() - interval '8 days' WHERE reference = '{old.Reference}'");
        var oldId = await ScalarAsync<Guid>($"SELECT id FROM licensing.payment_orders WHERE reference = '{old.Reference}'");
        await using (var scope = cloud.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ListPendingPaymentOrdersQuery(1000), Ct)).Value.ShouldNotContain(oldId);
        }
    }

    [Fact]
    public async Task Una_orden_vieja_no_vence_mientras_Wompi_la_reporta_pendiente()
    {
        var (_, pendingCheckout) = await OrderAsync(periods: 1);
        Gateway.Set(new GatewayTransaction(NewTransactionId(), pendingCheckout.Reference, 5_990_000, "COP", "PENDING", "PSE"));
        var (_, abandoned) = await OrderAsync(periods: 1);
        await ExecuteAsync(
            $"UPDATE licensing.payment_orders SET created_at = now() - interval '25 hours' WHERE reference IN ('{pendingCheckout.Reference}', '{abandoned.Reference}')");

        await ReconcileAsync(pendingCheckout.Reference);
        await ReconcileAsync(abandoned.Reference);

        (await ScalarAsync<string>($"SELECT status FROM licensing.payment_orders WHERE reference = '{pendingCheckout.Reference}'")).ShouldBe("PENDING");
        (await ScalarAsync<string>($"SELECT status FROM licensing.payment_orders WHERE reference = '{abandoned.Reference}'")).ShouldBe("EXPIRED");
    }

    [Fact]
    public async Task La_pagina_de_resultado_exige_el_token_de_la_orden()
    {
        var (_, checkout) = await OrderAsync(periods: 1);
        var path = FakePaymentGateway.ResultPath(checkout.CheckoutUrl);
        var token = path.Split('/')[^1];
        PaymentRules.IsResultToken(token).ShouldBeTrue(path);
        using var anonymous = cloud.Factory.CreateClient();

        var withToken = await anonymous.GetStringAsync(new Uri(path, UriKind.Relative), Ct);
        withToken.ShouldContain("Pago en proceso");
        withToken.ShouldContain(checkout.Reference);

        // Sin token, con otro token o solo con el id de la transacción: ni estado, ni valor, ni referencia.
        foreach (var guess in new[]
                 {
                     $"/pagar/resultado/{checkout.Reference}", $"/pagar/resultado/{checkout.Reference}/{PaymentRules.NewLinkCode()}", "/pagar/resultado?id=tx-1",
                 })
        {
            var html = await anonymous.GetStringAsync(new Uri(guess, UriKind.Relative), Ct);
            html.ShouldNotContain("Pago en proceso");
            html.ShouldNotContain(checkout.Reference);
            html.ShouldNotContain("59.900");
            html.ShouldContain("la pasarela lo confirmar"); // «confirmará» (la tilde llega codificada en el HTML)
        }
    }

    [Fact]
    public async Task Los_eventos_rechazados_guardan_solo_metadatos_y_se_auditan_una_vez_por_IP_y_ventana()
    {
        var (_, checkout) = await OrderAsync(periods: 1);
        var forged = new GatewayTransaction(NewTransactionId(), checkout.Reference, 5_990_000, "COP", "APPROVED", "CARD");
        var auditsBefore = await ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'PAYMENT_EVENT_REJECTED'");

        for (var i = 0; i < 3; i++)
        {
            (await PostEventAsync(FakePaymentGateway.SignedEvent(forged, secret: "secreto-de-un-atacante"))).Status.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Antirrepetición: un evento bien firmado pero de hace 4 días también se rechaza.
        var replayed = new GatewayTransaction(NewTransactionId(), checkout.Reference, 5_990_000, "COP", "APPROVED", "CARD");
        Gateway.Set(replayed);
        (await PostEventAsync(FakePaymentGateway.SignedEvent(replayed, at: DateTimeOffset.UtcNow.AddDays(-4)))).Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await ScalarAsync<string>($"SELECT status FROM licensing.payment_orders WHERE reference = '{checkout.Reference}'")).ShouldBe("PENDING");

        (await ScalarAsync<long>(
                $"SELECT count(*) FROM licensing.payment_events WHERE signature_valid = false AND reference = '{checkout.Reference}' AND payload->>'rechazado' = 'true'"))
            .ShouldBe(4);
        (await ScalarAsync<long>(
                $"SELECT count(*) FROM licensing.payment_events WHERE signature_valid = false AND reference = '{checkout.Reference}' AND payload::text LIKE '%checksum%'"))
            .ShouldBe(0);
        (await ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'PAYMENT_EVENT_REJECTED'")).ShouldBeLessThanOrEqualTo(auditsBefore + 1);
    }

    [Fact]
    public async Task Una_anulacion_despues_de_renovar_devuelve_la_vigencia()
    {
        var (customer, checkout) = await OrderAsync(periods: 1);
        var before = await ValidUntilAsync(customer);
        var approved = new GatewayTransaction(NewTransactionId(), checkout.Reference, 5_990_000, "COP", "APPROVED", "CARD");
        Gateway.Set(approved);
        (await PostEventAsync(FakePaymentGateway.SignedEvent(approved))).Result.ShouldBe("RENEWED");
        (await ValidUntilAsync(customer)).ShouldBe(before.AddMonths(1), TimeSpan.FromSeconds(1));

        var voided = approved with { Status = "VOIDED" };
        Gateway.Set(voided);
        (await PostEventAsync(FakePaymentGateway.SignedEvent(voided))).Result.ShouldBe("REVERTED");
        (await PostEventAsync(FakePaymentGateway.SignedEvent(voided))).Result.ShouldBe("IGNORED");

        (await ValidUntilAsync(customer)).ShouldBe(before, TimeSpan.FromSeconds(1));
        (await OrderRowAsync(customer)).Status.ShouldBe("VOIDED");
        var detail = await PortalApi.GetAsync<OrganizationDetailDto>(cloud.Superadmin, $"/admin/organizations/{customer.OrganizationId}");
        detail.Events.ShouldContain(e => e.Type == "RENEWAL_REVERTED");
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.audit_log WHERE action = 'PAYMENT_VOIDED_REVERTED' AND summary LIKE '%{checkout.Reference}%'"))
            .ShouldBe(1);
    }

    private static string NewTransactionId() => $"it-{Guid.NewGuid():N}"[..20];

    private async Task<(LicensedCustomer Customer, CheckoutDto Checkout)> OrderAsync(int periods)
    {
        await EnsurePriceAsync();
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin, trial: false);
        var link = await PortalApi.PostAsync<PaymentLinkDto>(cloud.Superadmin, $"/admin/subscriptions/{customer.SubscriptionId}/payment-link", new { });
        using var anonymous = cloud.Factory.CreateClient();
        return (customer, await PortalApi.PostAsync<CheckoutDto>(anonymous, $"/v1/payments/links/{link.Code}/orders", new { periods }));
    }

    private async Task EnsurePriceAsync()
    {
        var prices = await PortalApi.GetAsync<PricesDto>(cloud.Superadmin, "/admin/payments/prices");
        if (!prices.Current.Any(p => p.Edition == "MULTI" && p.BillingPeriod == "MONTHLY"))
        {
            await PortalApi.PostAsync<Guid>(cloud.Superadmin, "/admin/payments/prices", new { edition = "MULTI", billingPeriod = "MONTHLY", amountCop = MonthlyPrice });
        }
    }

    private async Task ReconcileAsync(string reference)
    {
        var id = await ScalarAsync<Guid>($"SELECT id FROM licensing.payment_orders WHERE reference = '{reference}'");
        await using var scope = cloud.Factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ReconcilePaymentOrderCommand(id), Ct)).IsSuccess.ShouldBeTrue();
    }

    private async Task<PaymentOrderRowDto> OrderRowAsync(LicensedCustomer customer) =>
        (await PortalApi.GetAsync<List<PaymentOrderRowDto>>(cloud.Superadmin, $"/admin/payments/orders?organizationId={customer.OrganizationId}")).Single();

    private async Task<DateTimeOffset> ValidUntilAsync(LicensedCustomer customer) =>
        (await PortalApi.GetAsync<OrganizationDetailDto>(cloud.Superadmin, $"/admin/organizations/{customer.OrganizationId}")).Organization.Subscription!.ValidUntil;

    private async Task<(HttpStatusCode Status, string? Result, string? Code)> PostEventAsync(string body)
    {
        using var client = cloud.Factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/v1/payments/wompi/events", UriKind.Relative), content, Ct);
        if (response.IsSuccessStatusCode)
        {
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<PaymentEventReceipt>(PortalApi.Json, Ct))!.Result, null);
        }

        var problem = await response.Content.ReadAsStringAsync(Ct);
        using var document = System.Text.Json.JsonDocument.Parse(problem);
        return (response.StatusCode, null, document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(cloud.Factory.Database.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(cloud.Factory.Database.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
