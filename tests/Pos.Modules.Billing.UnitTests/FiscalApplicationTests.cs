using Pos.Modules.Billing.Application;
using Pos.Modules.Billing.Domain;

namespace Pos.Modules.Billing.UnitTests;

/// <summary>Piezas de aplicación sin BD: ritmo, señal de la cola, mapeo y proveedores nulo y simulado.</summary>
public class FiscalApplicationTests
{
    private static readonly FiscalConnection Connection = new(FiscalEnvironment.Sandbox, new FiscalCredentials("u", "p", "c", "s"));

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static FiscalInvoiceDraft Draft(string reference, string identification = "222222222222", string rangeId = "FAKE-FV-1", string prefix = "SETP")
    {
        var party = new FiscalParty("CC", "13", identification, null, "Consumidor final", "NATURAL", "49", [], null, null, null, null);
        var header = new FiscalHeader(
            Guid.NewGuid(), reference, "C1-1", new FiscalNumbering(Guid.NewGuid(), rangeId, prefix, null), new DateOnly(2026, 9, 30), DateTimeOffset.UnixEpoch,
            party, new FiscalEstablishment(Guid.NewGuid(), "S01", "Centro", "Calle 1", "05001", null, null, null, null), null, false);
        return new FiscalInvoiceDraft(header, party, [], [], new FiscalTotals(0m, 0m, 0m, 0m, 0m, 0m, 0m));
    }

    [Fact]
    public void El_ritmo_permite_N_envios_por_minuto_en_ventana_deslizante()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var limiter = new FiscalRateLimiter(time);

        Enumerable.Range(0, 3).Select(_ => limiter.TryAcquire(3)).ShouldAllBe(ok => ok);
        limiter.TryAcquire(3).ShouldBeFalse();
        time.Now += TimeSpan.FromSeconds(59);
        limiter.TryAcquire(3).ShouldBeFalse();
        time.Now += TimeSpan.FromSeconds(1);
        limiter.TryAcquire(3).ShouldBeTrue();
        limiter.TryAcquire(0).ShouldBeFalse();
    }

    [Fact]
    public async Task La_senal_despierta_la_cola_una_vez_por_varios_avisos()
    {
        using var signal = new FiscalQueueSignal();
        signal.Notify();
        signal.Notify();

        (await signal.WaitAsync(TimeSpan.Zero, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await signal.WaitAsync(TimeSpan.Zero, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public void El_mapeo_lee_los_valores_de_la_BD_y_de_la_API()
    {
        BillingMapping.Parse<BillingMode>("EVERY_SALE").ShouldBe(BillingMode.EverySale);
        BillingMapping.Parse<BillingMode>("onrequest").ShouldBe(BillingMode.OnRequest);
        BillingMapping.Parse<FiscalStatus>(" not_required ").ShouldBe(FiscalStatus.NotRequired);
        BillingMapping.Parse<BillingMode>("SIEMPRE").ShouldBeNull();
        BillingMapping.Parse<BillingMode>(null).ShouldBeNull();
        FiscalDocumentType.SupportDocument.Db().ShouldBe("SUPPORT_DOCUMENT");
        BillingMapping.Source("PURCHASE").ShouldBe(FiscalSource.Purchase);
        Should.Throw<ArgumentOutOfRangeException>(() => BillingMapping.Source("OTRO"));
    }

    [Fact]
    public async Task El_proveedor_nulo_deja_todo_pendiente()
    {
        var provider = new NullFiscalProvider();
        var ct = TestContext.Current.CancellationToken;

        provider.Name.ShouldBe("NONE");
        (await provider.SubmitInvoiceAsync(Connection, Draft("r"), ct)).Outcome.ShouldBe(FiscalOutcome.NotConfigured);
        (await provider.SubmitCreditNoteAsync(Connection, null!, ct)).Outcome.ShouldBe(FiscalOutcome.NotConfigured);
        (await provider.SubmitSupportDocumentAsync(Connection, null!, ct)).Outcome.ShouldBe(FiscalOutcome.NotConfigured);
        (await provider.GetStatusAsync(Connection, FiscalDocumentType.InvoiceElectronic, "r", null, ct)).Outcome.ShouldBe(FiscalOutcome.NotConfigured);
        (await provider.GetNumberingRangesAsync(Connection, ct)).Outcome.ShouldBe(FiscalOutcome.NotConfigured);
    }

    [Fact]
    public async Task El_proveedor_simulado_numera_es_idempotente_rechaza_y_se_cae_a_voluntad()
    {
        var provider = new FakeFiscalProvider();
        var ct = TestContext.Current.CancellationToken;

        var first = await provider.SubmitInvoiceAsync(Connection, Draft("A"), ct);
        var second = await provider.SubmitInvoiceAsync(Connection, Draft("B"), ct);
        (first.Outcome, first.FiscalNumber, second.FiscalNumber, first.Consecutive).ShouldBe(
            (FiscalOutcome.Accepted, (string?)"SETP990000001", (string?)"SETP990000002", (long?)990_000_001));
        (await provider.SubmitInvoiceAsync(Connection, Draft("A"), ct)).ShouldBe(first);
        (provider.DistinctDocuments, provider.Invoices.Count, provider.LastConnection).ShouldBe((2, 3, Connection));
        (await provider.GetStatusAsync(Connection, FiscalDocumentType.InvoiceElectronic, "A", null, ct)).ShouldBe(first);
        (await provider.GetStatusAsync(Connection, FiscalDocumentType.InvoiceElectronic, "Z", null, ct)).Outcome.ShouldBe(FiscalOutcome.NotFound);
        (await provider.GetNumberingRangesAsync(Connection, ct)).Ranges.Single(r => r.ProviderRangeId == "FAKE-FV-1").Current.ShouldBe(990_000_002);

        provider.RejectedIdentifications.Add("123");
        var rejected = await provider.SubmitInvoiceAsync(Connection, Draft("C", "123"), ct);
        (rejected.Outcome, rejected.Code).ShouldBe((FiscalOutcome.Rejected, (string?)"FAK24"));

        provider.Offline = true;
        (await provider.SubmitInvoiceAsync(Connection, Draft("D"), ct)).Outcome.ShouldBe(FiscalOutcome.Unavailable);
        (await provider.GetStatusAsync(Connection, FiscalDocumentType.InvoiceElectronic, "A", null, ct)).Outcome.ShouldBe(FiscalOutcome.Unavailable);
        (await provider.GetNumberingRangesAsync(Connection, ct)).Outcome.ShouldBe(FiscalOutcome.Unavailable);
        provider.Offline = false;

        var invoice = Draft("E");
        var credit = new FiscalCreditNoteDraft(
            invoice.Header with { ReferenceCode = "NC-E", Numbering = new FiscalNumbering(Guid.NewGuid(), "FAKE-NC-1", "NC", null) }, invoice.Customer,
            new FiscalDocumentReference(Guid.NewGuid(), "E", null, "SETP1", "cufe", new DateOnly(2026, 9, 30)), FiscalCorrectionConcept.Void, "x", [], [],
            invoice.Totals);
        (await provider.SubmitCreditNoteAsync(Connection, credit, ct)).FiscalNumber.ShouldBe("NC1");
        var support = new FiscalSupportDocumentDraft(
            invoice.Header with { ReferenceCode = "DS-E", Numbering = new FiscalNumbering(Guid.NewGuid(), "FAKE-DS-1", "DS", null) }, invoice.Customer, "SF-1", [], [],
            invoice.Totals);
        (await provider.SubmitSupportDocumentAsync(Connection, support, ct)).FiscalNumber.ShouldBe("DS1");
        (provider.CreditNotes.Count, provider.SupportDocuments.Count, provider.Submissions.Count).ShouldBe((1, 1, 6));
        Should.Throw<ArgumentNullException>(() => provider.SubmitInvoiceAsync(Connection, null!, ct));
        Should.Throw<ArgumentNullException>(() => provider.SubmitCreditNoteAsync(Connection, null!, ct));
        Should.Throw<ArgumentNullException>(() => provider.SubmitSupportDocumentAsync(Connection, null!, ct));
    }
}
