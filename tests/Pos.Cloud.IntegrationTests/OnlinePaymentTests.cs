using System.Net;
using System.Net.Http.Json;
using System.Text;
using Npgsql;
using Pos.Cloud.Licensing.Application;
using Pos.Cloud.Licensing.Domain;
using Pos.Licensing.Contracts;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// Pago en línea de punta a punta (Fase 12-C) con la pasarela simulada: enlace → orden → evento firmado → renovación UNA vez;
/// firma inválida → 401 sin cambios; valor alterado o suscripción suspendida → "Requiere revisión"; "Consultar ahora"; y el
/// check-in devuelve el enlace de pago solo cerca del vencimiento.
/// </summary>
public class OnlinePaymentTests(PaymentsCloudFixture cloud) : IClassFixture<PaymentsCloudFixture>
{
    private const decimal MonthlyPrice = 59_900m;

    private FakePaymentGateway Gateway => cloud.Factory.Gateway;

    [Fact]
    public async Task Orden_desde_el_enlace_y_evento_aprobado_renuevan_la_suscripcion_una_sola_vez()
    {
        await EnsurePriceAsync();
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin, trial: false);
        var before = await ValidUntilAsync(customer);
        var link = await LinkAsync(customer);

        // Página pública (anónima): solo nombre, edición, vencimiento y valores; nunca el NIT.
        using var anonymous = cloud.Factory.CreateClient();
        var page = await PortalApi.GetAsync<PaymentPageDto>(anonymous, $"/v1/payments/links/{link.Code}");
        page.CanPay.ShouldBeTrue(page.Message);
        page.Options.Select(o => o.Periods).ShouldBe([1, 3, 6]);
        page.Options[1].AmountCop.ShouldBe(MonthlyPrice * 3);
        var html = await anonymous.GetAsync(new Uri($"/pagar/{link.Code}", UriKind.Relative), Ct);
        html.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await html.Content.ReadAsStringAsync(Ct);
        text.ShouldContain($"Supermercado {customer.Nit.Split('-')[0]} SAS");
        text.ShouldNotContain(customer.Nit);
        text.ShouldNotContain(customer.License.KeyPrefix);
        html.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("form-action 'self' https://checkout.wompi.co");

        // Orden: valor congelado y redirección firmada al checkout.
        var checkout = await PortalApi.PostAsync<CheckoutDto>(anonymous, $"/v1/payments/links/{link.Code}/orders", new { periods = 3 });
        PaymentRules.IsReference(checkout.Reference).ShouldBeTrue(checkout.Reference);
        checkout.CheckoutUrl.ShouldStartWith("https://checkout.wompi.co/p/?public-key=");
        checkout.CheckoutUrl.ShouldContain("amount-in-cents=17970000");
        checkout.CheckoutUrl.ShouldContain($"reference={checkout.Reference}");
        checkout.CheckoutUrl.ShouldContain(Uri.EscapeDataString($"/pagar/resultado/{checkout.Reference}"));

        // La página de regreso solo lee la orden: sigue pendiente.
        var waiting = await anonymous.GetStringAsync(new Uri($"/pagar/resultado/{checkout.Reference}?id=tx-desconocida", UriKind.Relative), Ct);
        waiting.ShouldContain("Pago en proceso");

        // Wompi aprueba y avisa (dos veces: los reintentos no renuevan de nuevo).
        var transaction = new GatewayTransaction($"it-{Guid.NewGuid():N}"[..20], checkout.Reference, 17_970_000, "COP", "APPROVED", "NEQUI");
        Gateway.Set(transaction);
        var body = FakePaymentGateway.SignedEvent(transaction);
        (await PostEventAsync(body)).ShouldBe((HttpStatusCode.OK, "RENEWED"));
        (await PostEventAsync(body)).ShouldBe((HttpStatusCode.OK, "IGNORED"));

        var detail = await PortalApi.GetAsync<OrganizationDetailDto>(cloud.Superadmin, $"/admin/organizations/{customer.OrganizationId}");
        detail.Events.Where(e => e.Type == "RENEWED" && e.PaymentReference == $"WOMPI {transaction.Id} {checkout.Reference}").ShouldHaveSingleItem();
        detail.Organization.Subscription!.ValidUntil.ShouldBe(before.AddMonths(3), TimeSpan.FromSeconds(1));

        var order = (await PortalApi.GetAsync<List<PaymentOrderRowDto>>(cloud.Superadmin, $"/admin/payments/orders?organizationId={customer.OrganizationId}")).ShouldHaveSingleItem();
        order.Status.ShouldBe("APPROVED");
        order.AppliedAt.ShouldNotBeNull();
        var orderDetail = await PortalApi.GetAsync<PaymentOrderDetailDto>(cloud.Superadmin, $"/admin/payments/orders/{order.Id}");
        orderDetail.Events.Count(e => e.Source == "WEBHOOK" && e.SignatureValid == true).ShouldBe(2);

        (await anonymous.GetStringAsync(new Uri($"/pagar/resultado?id={transaction.Id}", UriKind.Relative), Ct)).ShouldContain("Pago aprobado");
        (await AuditCountAsync("PAYMENT_ORDER_CREATED", checkout.Reference)).ShouldBe(1);
        (await AuditCountAsync("PAYMENT_APPROVED", checkout.Reference)).ShouldBe(1);
    }

    [Fact]
    public async Task Un_evento_con_firma_invalida_responde_401_queda_auditado_y_no_cambia_nada()
    {
        await EnsurePriceAsync();
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin, trial: false);
        var before = await ValidUntilAsync(customer);
        var link = await LinkAsync(customer);
        using var anonymous = cloud.Factory.CreateClient();
        var checkout = await PortalApi.PostAsync<CheckoutDto>(anonymous, $"/v1/payments/links/{link.Code}/orders", new { periods = 1 });
        var transaction = new GatewayTransaction($"it-{Guid.NewGuid():N}"[..20], checkout.Reference, 5_990_000, "COP", "APPROVED", "CARD");
        Gateway.Set(transaction);

        (await PostEventAsync(FakePaymentGateway.SignedEvent(transaction, secret: "secreto-de-un-atacante"))).Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await PostEventAsync("{\"event\":\"transaction.updated\"}")).Status.ShouldBe(HttpStatusCode.Unauthorized);

        (await ValidUntilAsync(customer)).ShouldBe(before);
        (await PortalApi.GetAsync<List<PaymentOrderRowDto>>(cloud.Superadmin, $"/admin/payments/orders?organizationId={customer.OrganizationId}"))
            .ShouldHaveSingleItem().Status.ShouldBe("PENDING");
        (await ScalarAsync<long>($"SELECT count(*) FROM licensing.payment_events WHERE signature_valid = false AND reference = '{checkout.Reference}'")).ShouldBe(1);
        (await AuditCountAsync("PAYMENT_EVENT_REJECTED", checkout.Reference)).ShouldBe(1);
    }

    [Fact]
    public async Task Valor_alterado_o_suscripcion_suspendida_quedan_en_revision_con_alerta_en_el_tablero()
    {
        await EnsurePriceAsync();
        using var anonymous = cloud.Factory.CreateClient();

        // Valor distinto al de la orden (p. ej. alguien alteró el checkout): no renueva.
        var altered = await LicensedCustomer.CreateAsync(cloud.Superadmin, trial: false);
        var alteredBefore = await ValidUntilAsync(altered);
        var alteredCheckout = await PortalApi.PostAsync<CheckoutDto>(anonymous, $"/v1/payments/links/{(await LinkAsync(altered)).Code}/orders", new { periods = 1 });
        var cheap = new GatewayTransaction($"it-{Guid.NewGuid():N}"[..20], alteredCheckout.Reference, 100_00, "COP", "APPROVED", "CARD");
        Gateway.Set(cheap);
        (await PostEventAsync(FakePaymentGateway.SignedEvent(cheap))).ShouldBe((HttpStatusCode.OK, "REVIEW_REQUIRED"));
        (await ValidUntilAsync(altered)).ShouldBe(alteredBefore);

        // Suspendida por el dueño: el pago aprobado no la reactiva sola.
        var suspended = await LicensedCustomer.CreateAsync(cloud.Superadmin, trial: false);
        var checkout = await PortalApi.PostAsync<CheckoutDto>(anonymous, $"/v1/payments/links/{(await LinkAsync(suspended)).Code}/orders", new { periods = 1 });
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/subscriptions/{suspended.SubscriptionId}/suspend", new { reason = "Disputa con el banco" }))
            .Status.ShouldBe(HttpStatusCode.NoContent);
        var paid = new GatewayTransaction($"it-{Guid.NewGuid():N}"[..20], checkout.Reference, 5_990_000, "COP", "APPROVED", "NEQUI");
        Gateway.Set(paid);
        (await PostEventAsync(FakePaymentGateway.SignedEvent(paid))).ShouldBe((HttpStatusCode.OK, "REVIEW_REQUIRED"));

        var detail = await PortalApi.GetAsync<OrganizationDetailDto>(cloud.Superadmin, $"/admin/organizations/{suspended.OrganizationId}");
        detail.Organization.Subscription!.Status.ShouldBe("SUSPENDED");
        var alerts = await PortalApi.GetAsync<PaymentAlertsDto>(cloud.Superadmin, "/admin/payments/alerts");
        alerts.ToReview.Select(o => o.Reference).ShouldContain(checkout.Reference);
        alerts.ToReview.Select(o => o.Reference).ShouldContain(alteredCheckout.Reference);

        // Una suspendida ya no crea órdenes nuevas.
        (await PortalApi.SendAsync(anonymous, HttpMethod.Post, $"/v1/payments/links/{(await LinkAsync(suspended)).Code}/orders", new { periods = 1 }))
            .Code.ShouldBe("PAYMENTS.SUBSCRIPTION_NOT_PAYABLE");

        // "Marcar revisado" con su nota; la alerta desaparece.
        var order = alerts.ToReview.Single(o => o.Reference == checkout.Reference);
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/payments/orders/{order.Id}/review", new { note = "Se devolvió en el panel de Wompi" }))
            .Status.ShouldBe(HttpStatusCode.NoContent);
        (await PortalApi.GetAsync<PaymentAlertsDto>(cloud.Superadmin, "/admin/payments/alerts")).ToReview.Select(o => o.Reference).ShouldNotContain(checkout.Reference);
    }

    [Fact]
    public async Task Consultar_ahora_aplica_un_pago_cuyo_evento_se_perdio()
    {
        await EnsurePriceAsync();
        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin, trial: false);
        var before = await ValidUntilAsync(customer);
        using var anonymous = cloud.Factory.CreateClient();
        var checkout = await PortalApi.PostAsync<CheckoutDto>(anonymous, $"/v1/payments/links/{(await LinkAsync(customer)).Code}/orders", new { periods = 6 });
        var order = (await PortalApi.GetAsync<List<PaymentOrderRowDto>>(cloud.Superadmin, $"/admin/payments/orders?organizationId={customer.OrganizationId}")).Single();

        Gateway.Set(new GatewayTransaction($"it-{Guid.NewGuid():N}"[..20], checkout.Reference, 35_940_000, "COP", "APPROVED", "PSE"));
        var response = await cloud.Superadmin.PostAsync(new Uri($"/admin/payments/orders/{order.Id}/check", UriKind.Relative), null, Ct);
        await PortalApi.EnsureAsync(response, HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("renovada");

        (await ValidUntilAsync(customer)).ShouldBe(before.AddMonths(6), TimeSpan.FromSeconds(1));
        var again = await cloud.Superadmin.PostAsync(new Uri($"/admin/payments/orders/{order.Id}/check", UriKind.Relative), null, Ct);
        (await again.Content.ReadAsStringAsync(Ct)).ShouldContain("cerrada");
        (await ValidUntilAsync(customer)).ShouldBe(before.AddMonths(6), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task El_checkin_devuelve_el_enlace_de_pago_cerca_del_vencimiento_y_no_cuando_falta_mucho()
    {
        var far = await ActivatedPosAsync(await LicensedCustomer.CreateAsync(cloud.Superadmin, trialDays: 30));
        (await CheckinAsync(far)).PaymentUrl.ShouldBeNull();

        var customer = await LicensedCustomer.CreateAsync(cloud.Superadmin, trialDays: 10);
        var near = await ActivatedPosAsync(customer);
        var response = await CheckinAsync(near);
        response.PaymentUrl.ShouldNotBeNull();
        response.PaymentUrl.ShouldStartWith("http://localhost/pagar/");
        var code = response.PaymentUrl.Split('/')[^1];
        PaymentRules.IsLinkCode(code).ShouldBeTrue(code);
        (await CheckinAsync(near)).PaymentUrl.ShouldBe(response.PaymentUrl); // mismo enlace vigente

        // Suspendida: nunca se ofrece el pago en línea.
        (await PortalApi.SendAsync(cloud.Superadmin, HttpMethod.Post, $"/admin/subscriptions/{customer.SubscriptionId}/suspend", new { reason = "Revisión de fraude" }))
            .Status.ShouldBe(HttpStatusCode.NoContent);
        (await CheckinAsync(near)).PaymentUrl.ShouldBeNull();
    }

    private async Task EnsurePriceAsync()
    {
        var prices = await PortalApi.GetAsync<PricesDto>(cloud.Superadmin, "/admin/payments/prices");
        if (!prices.Current.Any(p => p.Edition == "MULTI" && p.BillingPeriod == "MONTHLY"))
        {
            await PortalApi.PostAsync<Guid>(cloud.Superadmin, "/admin/payments/prices", new { edition = "MULTI", billingPeriod = "MONTHLY", amountCop = MonthlyPrice });
        }
    }

    private Task<PaymentLinkDto> LinkAsync(LicensedCustomer customer) =>
        PortalApi.PostAsync<PaymentLinkDto>(cloud.Superadmin, $"/admin/subscriptions/{customer.SubscriptionId}/payment-link", new { });

    private async Task<DateTimeOffset> ValidUntilAsync(LicensedCustomer customer) =>
        (await PortalApi.GetAsync<OrganizationDetailDto>(cloud.Superadmin, $"/admin/organizations/{customer.OrganizationId}")).Organization.Subscription!.ValidUntil;

    private async Task<(HttpStatusCode Status, string? Result)> PostEventAsync(string body)
    {
        using var client = cloud.Factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/v1/payments/wompi/events", UriKind.Relative), content, Ct);
        return response.IsSuccessStatusCode
            ? (response.StatusCode, (await response.Content.ReadFromJsonAsync<PaymentEventReceipt>(PortalApi.Json, Ct))!.Result)
            : (response.StatusCode, null);
    }

    private async Task<Pos.License.Simulator.SimulatedPos> ActivatedPosAsync(LicensedCustomer customer)
    {
        var pos = NewPos(customer);
        await pos.RefreshPublicKeysAsync(Ct);
        (await pos.ActivateAsync(customer.License.Key, Ct)).Succeeded.ShouldBeTrue();
        return pos;
    }

    private async Task<LicenseTokenResponse> CheckinAsync(Pos.License.Simulator.SimulatedPos pos)
    {
        using var client = cloud.Factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/v1/checkins", new CheckinRequest(pos.Token!, pos.Identity.Fingerprint, pos.Identity.AppVersion, 1, DateTimeOffset.UtcNow), PortalApi.Json, Ct);
        await PortalApi.EnsureAsync(response, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<LicenseTokenResponse>(PortalApi.Json, Ct))!;
    }

    private Pos.License.Simulator.SimulatedPos NewPos(LicensedCustomer customer) =>
        new(cloud.Factory.CreateClient(), Pos.License.Simulator.SimulatorIdentity.Create(
            customer.Nit, DeviceRoles.StoreServer, board: $"PLACA-{Guid.NewGuid():N}", disk: $"DISCO-{Guid.NewGuid():N}", machine: $"MAQ-{Guid.NewGuid():N}"));

    private async Task<long> AuditCountAsync(string action, string reference) =>
        await ScalarAsync<long>($"SELECT count(*) FROM audit.audit_log WHERE action = '{action}' AND summary LIKE '%{reference}%'");

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(cloud.Factory.Database.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
