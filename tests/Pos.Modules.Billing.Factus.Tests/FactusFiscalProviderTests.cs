using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pos.Modules.Billing.Domain;
using Pos.Modules.Billing.FactusFake;
using Pos.Modules.Billing.Infrastructure.Factus;
using static Pos.Modules.Billing.Factus.Tests.FiscalDrafts;

namespace Pos.Modules.Billing.Factus.Tests;

/// <summary>
/// Adaptador <see cref="FactusFiscalProvider"/> contra el Factus simulado en memoria: resultados neutros (aceptado, duplicado, rechazo,
/// sin red, 429, 5xx, credenciales), borrar y reenviar tras un rechazo, consulta de estado, rangos y token separado por empresa.
/// </summary>
public sealed class FactusFiscalProviderTests : IAsyncDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FactusFakeServer _server;
    private readonly ServiceProvider _services;

    public FactusFiscalProviderTests()
    {
        _server = new FactusFakeServer(_time);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton(new FactusConnectionOptions { BaseUrl = FactusFakeServer.InMemoryBaseUrl, RequestTimeout = TimeSpan.FromSeconds(10) });
        services.AddFactusApi().ConfigurePrimaryHttpMessageHandler(_server.CreateHandler);
        services.AddSingleton<FactusFiscalProvider>();
        _services = services.BuildServiceProvider();
        Provider = _services.GetRequiredService<FactusFiscalProvider>();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FactusFiscalProvider Provider { get; }

    private static FiscalConnection Connection(string password = FactusFakeServer.DefaultPassword, FiscalEnvironment environment = FiscalEnvironment.Sandbox) =>
        new(environment, new FiscalCredentials(FactusFakeServer.DefaultUsername, password, FactusFakeServer.DefaultClientId, FactusFakeServer.DefaultClientSecret));

    private static FiscalInvoiceDraft Invoice(string reference = "venta-1", FiscalParty? customer = null, bool resubmission = false)
    {
        var line = Included(1, "ARROZ-500", "Arroz 500 g", 2m, 3_000m, 19m);
        var lines = FiscalDraftBuilder.Lines([line]);
        return new FiscalInvoiceDraft(
            Header(reference, resubmission: resubmission), customer ?? FinalConsumer, lines, [Cash(6_000m)], FiscalDraftBuilder.Totals(lines, 0m));
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Factura_aceptada_con_numero_CUFE_QR_y_consecutivo_y_el_duplicado_devuelve_los_mismos_datos()
    {
        var result = await Provider.SubmitInvoiceAsync(Connection(), Invoice(), Ct);

        (result.Outcome, result.FiscalNumber, result.Consecutive, result.ProviderStatus).ShouldBe((FiscalOutcome.Accepted, (string?)"SETP990000001", (long?)990_000_001, (string?)"VALIDATED"));
        result.Cufe.ShouldNotBeNullOrWhiteSpace();
        result.QrData!.ShouldContain(result.Cufe!);
        result.ValidatedAt.ShouldNotBeNull();
        result.Message!.ShouldContain("FAJ44b"); // notificación DIAN (no rechaza)

        // Lo que viajó: reference_code, rango, consumidor final y el ítem con base exacta.
        using var body = JsonDocument.Parse(_server.Find(FakeDocumentKind.Bill, "venta-1")!.RequestJson);
        var root = body.RootElement;
        (root.GetProperty("reference_code").GetString(), root.GetProperty("numbering_range_id").GetInt32()).ShouldBe(("venta-1", 8));
        root.GetProperty("customer").GetProperty("identification").GetString().ShouldBe("222222222222");
        root.GetProperty("items")[0].GetProperty("price").GetString().ShouldBe("2521.01");

        // Reenviar el mismo reference_code (respuesta perdida) no crea otro documento: aceptado con los datos existentes.
        var again = await Provider.SubmitInvoiceAsync(Connection(), Invoice(), Ct);
        (again.Outcome, again.FiscalNumber, again.Cufe).ShouldBe((FiscalOutcome.Accepted, result.FiscalNumber, result.Cufe));
        _server.Documents.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sin_red_es_contingencia_429_y_5xx_son_transitorios_y_las_credenciales_malas_fallan_sin_lanzar()
    {
        _server.EnqueueFault(FakeFault.ConnectionDrop());
        (await Provider.SubmitInvoiceAsync(Connection(), Invoice("v-red"), Ct)).Outcome.ShouldBe(FiscalOutcome.Unavailable);

        _server.EnqueueFault(FakeFault.TooManyRequests(TimeSpan.FromSeconds(30)));
        var throttled = await Provider.SubmitInvoiceAsync(Connection(), Invoice("v-429"), Ct);
        (throttled.Outcome, throttled.Code, throttled.RetryAfter).ShouldBe((FiscalOutcome.Failed, (string?)"429", (TimeSpan?)TimeSpan.FromSeconds(30)));

        _server.EnqueueFault(FakeFault.ServerError(503));
        (await Provider.SubmitInvoiceAsync(Connection(), Invoice("v-503"), Ct)).ShouldSatisfyAllConditions(
            r => r.Outcome.ShouldBe(FiscalOutcome.Failed), r => r.Code.ShouldBe("503"));

        var credentials = await Provider.SubmitInvoiceAsync(Connection(password: "otra"), Invoice("v-cred"), Ct);
        (credentials.Outcome, credentials.Code).ShouldBe((FiscalOutcome.Failed, (string?)"CREDENTIALS"));
        credentials.Message!.ShouldNotContain("otra");

        // Sin red también al consultar rangos o estado → contingencia.
        _server.EnqueueFault(FakeFault.ConnectionDrop());
        (await Provider.GetNumberingRangesAsync(Connection(), Ct)).Outcome.ShouldBe(FiscalOutcome.Unavailable);
        _server.EnqueueFault(FakeFault.ConnectionDrop());
        (await Provider.GetStatusAsync(Connection(), FiscalDocumentType.InvoiceElectronic, "v-red", null, Ct)).Outcome.ShouldBe(FiscalOutcome.Unavailable);

        _server.Documents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rechazo_DIAN_se_corrige_se_borra_el_no_validado_y_se_reenvia()
    {
        _server.RejectNext("FAK24", "No está informado el DV del NIT");
        var rejected = await Provider.SubmitInvoiceAsync(Connection(), Invoice("v-rech", Company), Ct);
        (rejected.Outcome, rejected.Code).ShouldBe((FiscalOutcome.Rejected, (string?)"FAK24"));
        rejected.Message!.ShouldContain("Rechazo: No está informado el DV del NIT");

        // Mientras el rechazado siga en Factus bloquea a los demás: otro documento falla (transitorio), no queda rechazado.
        var blocked = await Provider.SubmitInvoiceAsync(Connection(), Invoice("v-otra"), Ct);
        (blocked.Outcome, blocked.Code).ShouldBe((FiscalOutcome.Failed, (string?)"BLOCKED"));

        // La consulta de estado no lo da por aceptado (se puede reenviar).
        (await Provider.GetStatusAsync(Connection(), FiscalDocumentType.InvoiceElectronic, "v-rech", null, Ct)).Outcome.ShouldBe(FiscalOutcome.NotFound);

        // Reenvío corregido: se elimina el no validado con el mismo reference_code y se envía de nuevo.
        var resent = await Provider.SubmitInvoiceAsync(Connection(), Invoice("v-rech", Company, resubmission: true), Ct);
        resent.Outcome.ShouldBe(FiscalOutcome.Accepted);
        _server.Requests.ShouldContain(r => r.Method == "DELETE" && r.PathAndQuery == "v2/bills/destroy/reference/v-rech");
        _server.Find(FakeDocumentKind.Bill, "v-rech")!.State.ShouldBe(FakeDocumentState.Validated);

        // Ya no bloquea: el otro documento sale.
        (await Provider.SubmitInvoiceAsync(Connection(), Invoice("v-otra"), Ct)).Outcome.ShouldBe(FiscalOutcome.Accepted);
    }

    [Fact]
    public async Task Un_primer_envio_rechazado_no_se_borra_solo_y_una_validacion_422_es_rechazo_con_mensajes()
    {
        _server.RejectNext();
        (await Provider.SubmitInvoiceAsync(Connection(), Invoice("v-1"), Ct)).Outcome.ShouldBe(FiscalOutcome.Rejected);
        _server.Requests.ShouldNotContain(r => r.Method == "DELETE");

        // Datos inválidos (persona jurídica sin razón social): 422 de Factus → rechazo con el campo.
        await using var other = new FactusFiscalProviderTests();
        var invalid = await other.Provider.SubmitInvoiceAsync(Connection(), Invoice("v-2", Company with { Name = " " }), Ct);
        invalid.Outcome.ShouldBe(FiscalOutcome.Rejected);
        invalid.Message!.ShouldContain("company");
    }

    [Fact]
    public async Task Pendiente_en_la_DIAN_es_transitorio_y_al_reenviar_se_valida()
    {
        _server.DelayValidationNext();
        var pending = await Provider.SubmitInvoiceAsync(Connection(), Invoice("v-p"), Ct);
        (pending.Outcome, pending.Code).ShouldBe((FiscalOutcome.Failed, (string?)"PENDING_DIAN"));
        (await Provider.GetStatusAsync(Connection(), FiscalDocumentType.InvoiceElectronic, "v-p", null, Ct)).Outcome.ShouldBe(FiscalOutcome.NotFound);

        (await Provider.SubmitInvoiceAsync(Connection(), Invoice("v-p", resubmission: true), Ct)).Outcome.ShouldBe(FiscalOutcome.Accepted);
        var status = await Provider.GetStatusAsync(Connection(), FiscalDocumentType.InvoiceElectronic, "v-p", null, Ct);
        (status.Outcome, status.FiscalNumber).ShouldBe((FiscalOutcome.Accepted, (string?)"SETP990000001"));
        _server.Documents.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Notas_credito_documento_soporte_y_nota_de_ajuste_referencian_su_documento()
    {
        var invoice = await Provider.SubmitInvoiceAsync(Connection(), Invoice("v-nc"), Ct);
        var draft = Invoice("v-nc");
        var reference = new FiscalDocumentReference(Guid.NewGuid(), "v-nc", invoice.ProviderDocumentId, invoice.FiscalNumber!, invoice.Cufe!, draft.Header.IssueDate);
        var note = new FiscalCreditNoteDraft(
            Header("NCA-v-nc", "9", "NC"), FinalConsumer, reference, FiscalCorrectionConcept.Void, "Anulación", draft.Lines, draft.Payments, draft.Totals);
        var credit = await Provider.SubmitCreditNoteAsync(Connection(), note, Ct);
        (credit.Outcome, credit.FiscalNumber, credit.Consecutive).ShouldBe((FiscalOutcome.Accepted, (string?)"NC1", (long?)1));

        var purchaseLine = new FiscalSourceLine(Guid.NewGuid(), 1, "ARROZ-500", "Arroz", "UND", 5m, 1_000m, false, 5_000m, 0m, 5_000m, 5_000m, []);
        var purchase = new FiscalPurchaseSnapshot(
            Guid.NewGuid(), "CO-1", "SF-1", new DateOnly(2026, 9, 30), Person, [purchaseLine], [new FiscalPayment("CREDITO", "CREDIT", "ZZZ", 5_000m, null)], "Error");
        var support = await Provider.SubmitSupportDocumentAsync(Connection(), FiscalDraftBuilder.SupportDocument(Header("DS-1", "10", "DS"), purchase), Ct);
        (support.Outcome, support.FiscalNumber).ShouldBe((FiscalOutcome.Accepted, (string?)"DS1"));

        var supportReference = new FiscalDocumentReference(Guid.NewGuid(), "DS-1", support.ProviderDocumentId, support.FiscalNumber!, support.Cufe!, purchase.InvoiceDate);
        var adjustment = await Provider.SubmitAdjustmentNoteAsync(
            Connection(), FiscalDraftBuilder.AdjustmentNote(Header("NAS-1", "13", "NA"), purchase, supportReference), Ct);
        (adjustment.Outcome, adjustment.FiscalNumber).ShouldBe((FiscalOutcome.Accepted, (string?)"NA1"));
        _server.Requests.ShouldContain(r => r.PathAndQuery == "v2/adjustment-notes/validate");
    }

    [Fact]
    public async Task Rangos_de_Factus_y_token_separado_por_empresa_y_ambiente()
    {
        var ranges = await Provider.GetNumberingRangesAsync(Connection(), Ct);
        ranges.Outcome.ShouldBe(FiscalOutcome.Accepted);
        ranges.Ranges.Select(r => (r.ProviderRangeId, r.DocumentType, r.Prefix)).ShouldBe(
        [
            ("8", FiscalDocumentType.InvoiceElectronic, "SETP"), ("9", FiscalDocumentType.CreditNote, "NC"),
            ("10", FiscalDocumentType.SupportDocument, "DS"), ("13", FiscalDocumentType.AdjustmentNote, "NA"),
        ]);
        ranges.Ranges[0].Current.ShouldBe(990_000_000L);

        // Otra empresa (credenciales inválidas) no toca el token de la primera; producción y sandbox tampoco lo comparten.
        (await Provider.GetNumberingRangesAsync(Connection(password: "de-otra-empresa"), Ct)).Outcome.ShouldBe(FiscalOutcome.Failed);
        (await Provider.GetNumberingRangesAsync(Connection(), Ct)).Outcome.ShouldBe(FiscalOutcome.Accepted);
        _server.PasswordGrants.ShouldBe(1);
        Provider.Options(Connection(environment: FiscalEnvironment.Production)).ShouldSatisfyAllConditions(
            o => o.Environment.ShouldBe(FactusEnvironment.Production), o => o.ToString().ShouldNotContain(FactusFakeServer.DefaultPassword));
    }
}
