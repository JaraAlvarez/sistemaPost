using System.Text.Json;
using Pos.Cloud.Licensing.Application;
using Pos.Cloud.Licensing.Domain;
using static Pos.Cloud.Licensing.UnitTests.TestSupport;

namespace Pos.Cloud.Licensing.UnitTests;

/// <summary>Pago en línea (Fase 12-C): firmas de Wompi, precios, referencia y máquina de estados de la orden.</summary>
public class PaymentTests
{
    private const string EventsSecret = "secreto-eventos-de-prueba";
    private const string Reference = "BP-2026-000123";

    // ─────────────────────────────── Firmas de Wompi ───────────────────────────────

    [Fact]
    public void La_firma_de_integridad_es_el_SHA256_de_referencia_monto_moneda_y_secreto()
    {
        // Vector calculado aparte: sha256("BP-2026-0001235990000COPsecreto-integridad-de-prueba").
        WompiProtocol.IntegritySignature(Reference, 5_990_000, "COP", "secreto-integridad-de-prueba")
            .ShouldBe("6a7e89e2b9d6e80c9640150a6f7a33746f5b5c5e5453eafc6cc372a4b6e6dec5");
    }

    [Fact]
    public void La_firma_de_integridad_incluye_la_expiracion_antes_del_secreto()
    {
        var with = WompiProtocol.IntegritySignature(Reference, 100, "COP", "s", "2026-10-18T00:00:00.000Z");
        with.ShouldBe(WompiProtocol.Sha256Hex($"{Reference}100COP2026-10-18T00:00:00.000Zs"));
        with.ShouldNotBe(WompiProtocol.IntegritySignature(Reference, 100, "COP", "s"));
    }

    [Fact]
    public void La_redireccion_al_checkout_lleva_la_llave_publica_el_valor_la_referencia_la_firma_y_el_regreso()
    {
        var url = WompiProtocol.CheckoutUrl("https://checkout.wompi.co/p/", "<llave-publica>", "secreto-integridad-de-prueba",
            new CheckoutRequest(Reference, 5_990_000, "COP", "https://portal.example/pagar/resultado/BP-2026-000123"));

        url.ShouldStartWith("https://checkout.wompi.co/p/?public-key=%3Cllave-publica%3E&currency=COP&amount-in-cents=5990000&reference=BP-2026-000123");
        url.ShouldContain("&signature:integrity=6a7e89e2b9d6e80c9640150a6f7a33746f5b5c5e5453eafc6cc372a4b6e6dec5");
        url.ShouldEndWith("&redirect-url=https%3A%2F%2Fportal.example%2Fpagar%2Fresultado%2FBP-2026-000123");
        url.ShouldNotContain("secreto");
    }

    [Fact]
    public void Un_evento_con_checksum_correcto_es_valido_y_trae_la_transaccion()
    {
        // Vector calculado aparte: sha256("1234-1610641025-49201APPROVED44900001530291411secreto-eventos-de-prueba").
        var body = Event("7a0fa70dad4a5e73d9f9f66a476e08d6d50750f86c3aa70dde51dcdab4fc39cd");

        var parsed = WompiProtocol.ParseEvent(body, null, EventsSecret);

        parsed.SignatureValid.ShouldBeTrue(parsed.RejectionReason);
        parsed.EventType.ShouldBe("transaction.updated");
        parsed.Environment.ShouldBe("test");
        parsed.Transaction.ShouldBe(new GatewayTransaction("1234-1610641025-49201", Reference, 4_490_000, "COP", "APPROVED", "NEQUI"));
    }

    [Fact]
    public void El_checksum_se_compara_sin_distinguir_mayusculas_y_tambien_desde_la_cabecera()
    {
        WompiProtocol.ParseEvent(Event("7A0FA70DAD4A5E73D9F9F66A476E08D6D50750F86C3AA70DDE51DCDAB4FC39CD"), null, EventsSecret).SignatureValid.ShouldBeTrue();

        var withoutBodyChecksum = Event(null);
        WompiProtocol.ParseEvent(withoutBodyChecksum, "7a0fa70dad4a5e73d9f9f66a476e08d6d50750f86c3aa70dde51dcdab4fc39cd", EventsSecret).SignatureValid.ShouldBeTrue();
        WompiProtocol.ParseEvent(withoutBodyChecksum, null, EventsSecret).SignatureValid.ShouldBeFalse();
    }

    [Fact]
    public void Un_checksum_incorrecto_o_un_secreto_distinto_se_rechazan()
    {
        var body = Event("7a0fa70dad4a5e73d9f9f66a476e08d6d50750f86c3aa70dde51dcdab4fc39cd");

        WompiProtocol.ParseEvent(Event(new string('0', 64)), null, EventsSecret).SignatureValid.ShouldBeFalse();
        WompiProtocol.ParseEvent(body, null, "otro-secreto").SignatureValid.ShouldBeFalse();
        WompiProtocol.ParseEvent("no es json", null, EventsSecret).SignatureValid.ShouldBeFalse();
        WompiProtocol.ParseEvent("{}", null, EventsSecret).SignatureValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("\"status\": \"APPROVED\"", "\"status\": \"DECLINED\"")]
    [InlineData("\"amount_in_cents\": 4490000", "\"amount_in_cents\": 100")]
    [InlineData("\"timestamp\": 1530291411", "\"timestamp\": 1530291412")]
    [InlineData("\"id\": \"1234-1610641025-49201\"", "\"id\": \"9999-1610641025-49201\"")]
    public void Un_evento_alterado_se_rechaza(string original, string tampered)
    {
        var body = Event("7a0fa70dad4a5e73d9f9f66a476e08d6d50750f86c3aa70dde51dcdab4fc39cd").Replace(original, tampered, StringComparison.Ordinal);

        WompiProtocol.ParseEvent(body, null, EventsSecret).SignatureValid.ShouldBeFalse();
    }

    // ─────────────────────────────── Reglas ───────────────────────────────

    [Fact]
    public void La_referencia_es_BP_anio_y_consecutivo_de_seis_digitos()
    {
        PaymentRules.Reference(2026, 123).ShouldBe("BP-2026-000123");
        PaymentRules.Reference(2027, 1_234_567).ShouldBe("BP-2027-1234567");
        PaymentRules.IsReference("BP-2026-000123").ShouldBeTrue();
        PaymentRules.IsReference("BP-2026-12").ShouldBeFalse();
        PaymentRules.IsReference("XX-2026-000123").ShouldBeFalse();
        PaymentRules.IsReference("BP-20A6-000123").ShouldBeFalse();
        PaymentRules.RenewalReference("1234-1610641025-49201", "BP-2026-000123").ShouldBe("WOMPI 1234-1610641025-49201 BP-2026-000123");
    }

    [Fact]
    public void El_codigo_del_enlace_tiene_128_bits_en_base64url()
    {
        var codes = Enumerable.Range(0, 50).Select(_ => PaymentRules.NewLinkCode()).ToList();

        codes.ShouldAllBe(c => PaymentRules.IsLinkCode(c));
        codes.Distinct().Count().ShouldBe(50);
        PaymentRules.IsLinkCode("corto").ShouldBeFalse();
        PaymentRules.IsLinkCode("abc+def/ghi=jklmnopqrs").ShouldBeFalse();
    }

    [Fact]
    public void El_precio_vigente_es_el_de_mayor_vigente_desde_que_ya_empezo()
    {
        var old = Price(50_000, Start.AddDays(-30));
        var current = Price(59_900, Start.AddDays(-1));
        var future = Price(69_900, Start.AddDays(10));
        var annual = Price(500_000, Start.AddDays(-5), BillingPeriod.Annual);
        var single = Price(39_900, Start.AddDays(-2), edition: LicenseEdition.SingleTerminal);
        var prices = new[] { old, future, current, annual, single };

        PaymentRules.Current(prices, LicenseEdition.MultiTerminal, BillingPeriod.Monthly, Start).ShouldBe(current);
        PaymentRules.Current(prices, LicenseEdition.MultiTerminal, BillingPeriod.Monthly, Start.AddDays(11)).ShouldBe(future);
        PaymentRules.Current(prices, LicenseEdition.MultiTerminal, BillingPeriod.Annual, Start).ShouldBe(annual);
        PaymentRules.Current(prices, LicenseEdition.SingleTerminal, BillingPeriod.Annual, Start).ShouldBeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("100000001")]
    [InlineData("59900.5")]
    public void El_precio_debe_ser_positivo_sin_centavos_y_razonable(string amount) =>
        SubscriptionPrice.Create(Guid.CreateVersion7(), LicenseEdition.MultiTerminal, BillingPeriod.Monthly,
                decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Start)
            .Error.ShouldBe(PaymentErrors.InvalidPrice);

    [Fact]
    public void El_enlace_en_el_checkin_se_ofrece_cerca_del_vencimiento_en_gracia_o_vencida_y_nunca_suspendida()
    {
        var now = Start;
        PaymentRules.ShouldOfferLink(SubscriptionStatus.Active, now.AddDays(15), now, 15).ShouldBeTrue();
        PaymentRules.ShouldOfferLink(SubscriptionStatus.Trial, now.AddDays(3), now, 15).ShouldBeTrue();
        PaymentRules.ShouldOfferLink(SubscriptionStatus.Active, now.AddDays(16), now, 15).ShouldBeFalse();
        PaymentRules.ShouldOfferLink(SubscriptionStatus.PastDue, now.AddDays(-2), now, 15).ShouldBeTrue();
        PaymentRules.ShouldOfferLink(SubscriptionStatus.Expired, now.AddDays(-40), now, 15).ShouldBeTrue();
        PaymentRules.ShouldOfferLink(SubscriptionStatus.Suspended, now.AddDays(1), now, 15).ShouldBeFalse();
        PaymentRules.ShouldOfferLink(SubscriptionStatus.Cancelled, now.AddDays(1), now, 15).ShouldBeFalse();
    }

    // ─────────────────────────────── Orden de pago ───────────────────────────────

    [Fact]
    public void La_orden_congela_el_valor_por_los_periodos_elegidos()
    {
        var subscription = Paid();
        var order = Order(subscription, periods: 3);

        order.Status.ShouldBe(PaymentOrderStatus.Pending);
        order.AmountCop.ShouldBe(179_700m);
        order.AmountInCents.ShouldBe(17_970_000L);
        order.Currency.ShouldBe("COP");
        order.Edition.ShouldBe(LicenseEdition.MultiTerminal);
    }

    [Theory]
    [InlineData(BillingPeriod.Monthly, 2)]
    [InlineData(BillingPeriod.Monthly, 12)]
    [InlineData(BillingPeriod.Annual, 2)]
    public void Solo_se_pagan_1_3_o_6_meses_o_1_anio(BillingPeriod period, int periods)
    {
        var subscription = Paid(period);
        PaymentOrder.Create(Guid.CreateVersion7(), Reference, subscription, Price(59_900, Start.AddDays(-1), period), periods, Start)
            .Error.ShouldBe(PaymentErrors.InvalidPeriods);
    }

    [Fact]
    public void Sin_precio_o_suspendida_no_se_crea_la_orden()
    {
        var subscription = Paid();
        PaymentOrder.Create(Guid.CreateVersion7(), Reference, subscription, null, 1, Start).Error.ShouldBe(PaymentErrors.PriceNotConfigured);

        subscription.Suspend("Fraude en revisión", Now).IsSuccess.ShouldBeTrue();
        PaymentOrder.Create(Guid.CreateVersion7(), Reference, subscription, Price(59_900, Start.AddDays(-1)), 1, Start).Error.ShouldBe(PaymentErrors.SubscriptionNotPayable);
    }

    [Fact]
    public void Aprobada_con_el_valor_correcto_renueva_una_sola_vez_aunque_el_evento_se_repita()
    {
        var subscription = Paid();
        var before = subscription.ValidUntil;
        var order = Order(subscription, periods: 3);
        var approved = Transaction(order, GatewayStatuses.Approved);

        order.Process(approved, subscription, 7, At(Start.AddMinutes(5))).ShouldBe(PaymentOutcome.Renewed);
        order.Status.ShouldBe(PaymentOrderStatus.Approved);
        order.AppliedAt.ShouldBe(Start.AddMinutes(5));
        order.GatewayTransactionId.ShouldBe(approved.Id);
        subscription.ValidUntil.ShouldBe(before.AddMonths(3));
        var renewal = subscription.Events.Last();
        renewal.Type.ShouldBe(SubscriptionEventType.Renewed);
        renewal.PaymentReference.ShouldBe($"WOMPI {approved.Id} {Reference}");
        order.RenewalEventId.ShouldBe(renewal.Id);

        // El mismo evento (o la conciliación) otra vez: no renueva de nuevo.
        order.Process(approved, subscription, 7, At(Start.AddMinutes(6))).ShouldBe(PaymentOutcome.Ignored);
        order.Process(approved with { Status = GatewayStatuses.Pending }, subscription, 7, At(Start.AddMinutes(7))).ShouldBe(PaymentOutcome.Ignored);
        subscription.ValidUntil.ShouldBe(before.AddMonths(3));
        subscription.Events.Count(e => e.Type == SubscriptionEventType.Renewed).ShouldBe(2); // la inicial (StartPaid) y la del pago
    }

    [Fact]
    public void Un_valor_o_una_moneda_distintos_quedan_en_revision_sin_renovar()
    {
        var subscription = Paid();
        var before = subscription.ValidUntil;
        var order = Order(subscription);

        order.Process(Transaction(order, GatewayStatuses.Approved) with { AmountInCents = 100 }, subscription, 7, Now).ShouldBe(PaymentOutcome.ReviewRequired);

        order.Status.ShouldBe(PaymentOrderStatus.ReviewRequired);
        order.NeedsReview.ShouldBeTrue();
        order.ReviewReason!.ShouldContain("no coincide");
        order.IsApplied.ShouldBeFalse();
        subscription.ValidUntil.ShouldBe(before);

        var usd = Order(subscription);
        usd.Process(Transaction(usd, GatewayStatuses.Approved) with { Currency = "USD" }, subscription, 7, Now).ShouldBe(PaymentOutcome.ReviewRequired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Una_suscripcion_suspendida_o_cancelada_no_se_reactiva_sola_al_pagar(bool cancel)
    {
        var subscription = Paid();
        var order = Order(subscription);
        (cancel ? subscription.Cancel("El cliente cerró el negocio", Now) : subscription.Suspend("Disputa de pago abierta", Now)).IsSuccess.ShouldBeTrue();

        order.Process(Transaction(order, GatewayStatuses.Approved), subscription, 7, Now).ShouldBe(PaymentOutcome.ReviewRequired);

        order.Status.ShouldBe(PaymentOrderStatus.ReviewRequired);
        subscription.Status.ShouldBe(cancel ? SubscriptionStatus.Cancelled : SubscriptionStatus.Suspended);
        subscription.Events.ShouldNotContain(e => e.Type == SubscriptionEventType.Renewed && e.PaymentReference!.StartsWith("WOMPI", StringComparison.Ordinal));
    }

    [Fact]
    public void Una_vencida_que_paga_empieza_un_periodo_nuevo_desde_hoy()
    {
        var subscription = Paid();
        var later = Start.AddMonths(3);
        var order = PaymentOrder.Create(Guid.CreateVersion7(), Reference, subscription, Price(59_900, Start.AddDays(-1)), 1, later).Value;

        order.Process(Transaction(order, GatewayStatuses.Approved), subscription, 7, At(later)).ShouldBe(PaymentOutcome.Renewed);

        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        subscription.ValidUntil.ShouldBe(later.AddMonths(1));
    }

    [Fact]
    public void Pendiente_rechazada_y_luego_aprobada_en_un_reintento()
    {
        var subscription = Paid();
        var order = Order(subscription);
        var first = Transaction(order, GatewayStatuses.Pending);

        order.Process(first, subscription, 7, Now).ShouldBe(PaymentOutcome.StillPending);
        order.Status.ShouldBe(PaymentOrderStatus.Pending);
        order.GatewayTransactionId.ShouldBe(first.Id);

        order.Process(first with { Status = GatewayStatuses.Declined }, subscription, 7, Now).ShouldBe(PaymentOutcome.Failed);
        order.Status.ShouldBe(PaymentOrderStatus.Declined);
        order.Process(first with { Status = GatewayStatuses.Declined }, subscription, 7, Now).ShouldBe(PaymentOutcome.Ignored);

        var retry = first with { Id = "5678-1610641025-00001", Status = GatewayStatuses.Approved };
        order.Process(retry, subscription, 7, Now).ShouldBe(PaymentOutcome.Renewed);
        order.Status.ShouldBe(PaymentOrderStatus.Approved);
    }

    [Theory]
    [InlineData(GatewayStatuses.Voided, PaymentOrderStatus.Voided)]
    [InlineData(GatewayStatuses.Error, PaymentOrderStatus.Error)]
    public void Anulada_o_con_error_cierra_la_orden_sin_renovar(string status, PaymentOrderStatus expected)
    {
        var subscription = Paid();
        var order = Order(subscription);

        order.Process(Transaction(order, status), subscription, 7, Now).ShouldBe(PaymentOutcome.Failed);

        order.Status.ShouldBe(expected);
        order.IsApplied.ShouldBeFalse();
    }

    [Fact]
    public void Una_anulacion_despues_de_renovar_devuelve_la_vigencia_una_sola_vez()
    {
        var subscription = Paid();
        var before = subscription.ValidUntil;
        var order = Order(subscription);
        var approved = Transaction(order, GatewayStatuses.Approved);
        order.Process(approved, subscription, 7, Now).ShouldBe(PaymentOutcome.Renewed);
        subscription.ValidUntil.ShouldBe(before.AddMonths(1));
        order.GrantedPeriodEnd.ShouldBe(before.AddMonths(1));
        order.PreviousPeriodEnd.ShouldBe(before);

        order.Process(approved with { Status = GatewayStatuses.Voided }, subscription, 7, At(Start.AddMinutes(10))).ShouldBe(PaymentOutcome.Reverted);

        order.Status.ShouldBe(PaymentOrderStatus.Voided);
        order.RevertedAt.ShouldBe(Start.AddMinutes(10));
        order.IsApplied.ShouldBeTrue();
        subscription.ValidUntil.ShouldBe(before);
        subscription.Events.Last().Type.ShouldBe(SubscriptionEventType.RenewalReverted);

        // Repetir el evento de anulación no vuelve a descontar.
        order.Process(approved with { Status = GatewayStatuses.Voided }, subscription, 7, At(Start.AddMinutes(11))).ShouldBe(PaymentOutcome.Ignored);
        subscription.ValidUntil.ShouldBe(before);
    }

    [Fact]
    public void Una_anulacion_de_una_suscripcion_cancelada_queda_en_revision()
    {
        var subscription = Paid();
        var order = Order(subscription);
        var approved = Transaction(order, GatewayStatuses.Approved);
        order.Process(approved, subscription, 7, Now).ShouldBe(PaymentOutcome.Renewed);
        subscription.Cancel("El cliente cerró el negocio", Now).IsSuccess.ShouldBeTrue();

        order.Process(approved with { Status = GatewayStatuses.Voided }, subscription, 7, Now).ShouldBe(PaymentOutcome.ReviewRequired);
        order.Status.ShouldBe(PaymentOrderStatus.ReviewRequired);
    }

    [Fact]
    public void Una_orden_en_gracia_que_paga_empieza_hoy_y_una_vigente_continua()
    {
        // W3: vigente → desde el fin del periodo actual.
        var active = Paid();
        var end = active.ValidUntil;
        var order = Order(active);
        order.Process(Transaction(order, GatewayStatuses.Approved), active, 7, At(Start.AddDays(3))).ShouldBe(PaymentOutcome.Renewed);
        active.ValidUntil.ShouldBe(end.AddMonths(1));

        // En gracia → desde hoy.
        var late = Paid();
        var today = late.ValidUntil.AddDays(3);
        var lateOrder = PaymentOrder.Create(Guid.CreateVersion7(), Reference, late, Price(59_900, Start.AddDays(-1)), 1, today).Value;
        lateOrder.Process(Transaction(lateOrder, GatewayStatuses.Approved), late, 7, At(today)).ShouldBe(PaymentOutcome.Renewed);
        late.CurrentPeriodStart.ShouldBe(today);
        late.ValidUntil.ShouldBe(today.AddMonths(1));
    }

    [Fact]
    public void La_orden_tiene_un_token_aleatorio_para_su_pagina_de_resultado()
    {
        var subscription = Paid();
        var first = Order(subscription);
        PaymentRules.IsResultToken(first.ResultToken).ShouldBeTrue();
        first.ResultToken.ShouldNotBe(Order(subscription).ResultToken);
    }

    [Fact]
    public void El_checkout_con_vencimiento_lo_envia_y_lo_firma()
    {
        var expires = new DateTimeOffset(2026, 10, 18, 15, 30, 0, TimeSpan.FromHours(-5));
        var url = WompiProtocol.CheckoutUrl("https://checkout.wompi.co/p/", "llave", "s",
            new CheckoutRequest(Reference, 100, "COP", "https://portal.example/pagar/resultado/x", expires));

        url.ShouldContain("&expiration-time=2026-10-18T20%3A30%3A00.000Z");
        url.ShouldContain("&signature:integrity=" + WompiProtocol.IntegritySignature(Reference, 100, "COP", "s", "2026-10-18T20:30:00.000Z"));
    }

    [Fact]
    public void El_evento_trae_su_timestamp_y_fuera_de_la_ventana_no_es_fresco()
    {
        var body = Event("7a0fa70dad4a5e73d9f9f66a476e08d6d50750f86c3aa70dde51dcdab4fc39cd");
        var parsed = WompiProtocol.ParseEvent(body, null, EventsSecret);
        parsed.Timestamp.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1530291411));

        var at = parsed.Timestamp!.Value;
        WompiProtocol.IsFresh(at, at.AddHours(71), TimeSpan.FromHours(72), TimeSpan.FromMinutes(5)).ShouldBeTrue();
        WompiProtocol.IsFresh(at, at.AddHours(73), TimeSpan.FromHours(72), TimeSpan.FromMinutes(5)).ShouldBeFalse();
        WompiProtocol.IsFresh(at, at.AddMinutes(-6), TimeSpan.FromHours(72), TimeSpan.FromMinutes(5)).ShouldBeFalse();
        WompiProtocol.IsFresh(null, at, TimeSpan.FromHours(72), TimeSpan.FromMinutes(5)).ShouldBeFalse();
    }

    [Fact]
    public void Una_transaccion_de_otra_referencia_se_ignora()
    {
        var subscription = Paid();
        var order = Order(subscription);

        order.Process(Transaction(order, GatewayStatuses.Approved) with { Reference = "BP-2026-999999" }, subscription, 7, Now).ShouldBe(PaymentOutcome.Ignored);
        order.Status.ShouldBe(PaymentOrderStatus.Pending);
    }

    [Fact]
    public void La_orden_pendiente_vence_a_las_24_horas_y_un_pago_tardio_igual_se_aplica()
    {
        var subscription = Paid();
        var order = Order(subscription);

        order.Expire(Start.AddHours(23), TimeSpan.FromHours(24)).ShouldBeFalse();
        order.Expire(Start.AddHours(25), TimeSpan.FromHours(24)).ShouldBeTrue();
        order.Status.ShouldBe(PaymentOrderStatus.Expired);

        order.Process(Transaction(order, GatewayStatuses.Approved), subscription, 7, At(Start.AddHours(26))).ShouldBe(PaymentOutcome.Renewed);
    }

    [Fact]
    public void Marcar_revisado_exige_nota_y_solo_aplica_a_pagos_en_revision()
    {
        var subscription = Paid();
        var order = Order(subscription);
        order.MarkReviewed(Actor, "Renové a mano", Start).Error.ShouldBe(PaymentErrors.NotUnderReview);

        order.Process(Transaction(order, GatewayStatuses.Approved) with { AmountInCents = 1 }, subscription, 7, Now);
        order.MarkReviewed(Actor, "ok", Start).Error.ShouldBe(PaymentErrors.NoteRequired);
        order.MarkReviewed(Actor, "  Devuelto en el panel de Wompi  ", Start).IsSuccess.ShouldBeTrue();

        order.ReviewNote.ShouldBe("Devuelto en el panel de Wompi");
        order.ReviewedBy.ShouldBe(Actor);
        order.NeedsReview.ShouldBeFalse();
        order.MarkReviewed(Actor, "Otra vez", Start).Error.ShouldBe(PaymentErrors.NotUnderReview);
    }

    [Fact]
    public void El_registro_del_evento_guarda_el_JSON_y_recorta_lo_demasiado_grande()
    {
        PaymentText.Payload("{\"a\":1}").ShouldBe("{\"a\":1}");

        var huge = PaymentText.Payload(new string('x', PaymentEvent.MaxPayloadChars + 10));
        using var document = JsonDocument.Parse(huge);
        document.RootElement.GetProperty("recortado").GetBoolean().ShouldBeTrue();
        huge.Length.ShouldBeLessThan(3000);

        using var invalid = JsonDocument.Parse(PaymentText.Payload("no es json"));
        invalid.RootElement.GetProperty("inicio").GetString().ShouldBe("no es json");
    }

    private static SubscriptionPrice Price(
        decimal amount, DateTimeOffset validFrom, BillingPeriod period = BillingPeriod.Monthly, LicenseEdition edition = LicenseEdition.MultiTerminal) =>
        SubscriptionPrice.Create(Guid.CreateVersion7(), edition, period, amount, validFrom).Value;

    private static PaymentOrder Order(Subscription subscription, int periods = 1) =>
        PaymentOrder.Create(Guid.CreateVersion7(), Reference, subscription, Price(59_900, Start.AddDays(-1), subscription.BillingPeriod), periods, Start).Value;

    private static GatewayTransaction Transaction(PaymentOrder order, string status) =>
        new("1234-1610641025-49201", order.Reference, order.AmountInCents, "COP", status, "NEQUI");

    private static string Event(string? checksum) => $$"""
        {
          "event": "transaction.updated",
          "data": {
            "transaction": {
              "id": "1234-1610641025-49201",
              "amount_in_cents": 4490000,
              "reference": "{{Reference}}",
              "currency": "COP",
              "payment_method_type": "NEQUI",
              "status": "APPROVED"
            }
          },
          "environment": "test",
          "signature": {
            "properties": ["transaction.id", "transaction.status", "transaction.amount_in_cents"]{{(checksum is null ? string.Empty : $",\n    \"checksum\": \"{checksum}\"")}}
          },
          "timestamp": 1530291411,
          "sent_at": "2026-10-17T16:45:05.000Z"
        }
        """;
}
