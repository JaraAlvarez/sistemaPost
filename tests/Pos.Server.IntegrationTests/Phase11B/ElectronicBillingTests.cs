using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Billing.Domain;
using Pos.Modules.Purchasing.Contracts;
using Pos.Modules.Sales.Application;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase11B.ElectronicBilling;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase11B;

/// <summary>
/// Verificación de la Fase 11-B (docs/fases/fase-11b-propuesta.md §10 y §13) con el proveedor simulado: modo OFF sin cambios, emisión en
/// cada venta, idempotencia, contingencia, rechazo y corrección, notas crédito, documento soporte, rangos, conciliación y permisos.
/// </summary>
public class ElectronicBillingTests
{
    private static readonly string[] GreatTaxpayer = ["O-13"];
    private static readonly string[] NotResponsible = ["R-99-PN"];

    [Fact]
    public async Task En_modo_OFF_todo_sigue_como_hoy_y_encender_exige_credenciales_que_nunca_se_devuelven()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();

        var settings = await GetAsync<BillingSettingsDto>(shop.Owner, "/api/v1/billing/settings");
        (settings.Mode, settings.Environment, settings.HasCredentials, settings.ActiveAdapter).ShouldBe(("OFF", "SANDBOX", false, "FAKE"));

        var receipt = await SellAsync(shop);
        (receipt.DocumentType, receipt.DocumentStatus).ShouldBe(("INTERNAL_RECEIPT", "NOT_REQUIRED"));
        (await ProcessAsync(shop)).StoppedBecause.ShouldBe("NOT_CONFIGURED");
        factory.Fiscal.Submissions.ShouldBeEmpty();
        var document = (await DocumentsAsync(shop)).Single();
        (document.ReferenceCode, document.Status).ShouldBe(((string?)null, "NOT_REQUIRED"));

        // Encender sin credenciales no se permite; el modo o ambiente desconocido tampoco.
        await shop.Owner.PutAsJsonAsync("/api/v1/billing/settings", new { mode = "EVERY_SALE", environment = "SANDBOX" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.CREDENTIALS_REQUIRED");
        await shop.Owner.PutAsJsonAsync("/api/v1/billing/settings", new { mode = "SIEMPRE", environment = "SANDBOX" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "BILLING.INVALID_SETTINGS");
        await shop.Owner.PutAsJsonAsync("/api/v1/billing/settings/credentials", new { username = "x", password = "", clientId = "c", clientSecret = "s" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "BILLING.INVALID_CREDENTIALS");

        // Credenciales: cifradas con DPAPI en la BD, nunca en la respuesta ni en la auditoría.
        var response = await shop.Owner.PutAsJsonAsync("/api/v1/billing/settings/credentials", new
        {
            username = "tienda@correo.co", password = Password, clientId = "cliente-9", clientSecret = ClientSecret,
        }, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain(Password);
        body.ShouldNotContain(ClientSecret);
        (await response.Content.ReadFromJsonAsync<BillingSettingsDto>(Json, Ct))!.HasCredentials.ShouldBeTrue();
        var stored = await shop.Catalog.ScalarAsync<string>("SELECT encode(credentials, 'escape') FROM billing.provider_settings");
        stored!.ShouldNotContain(Password);
        stored!.ShouldNotContain("tienda@correo.co");
        (await CountAsync(shop, $"SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_CREDENTIALS_CHANGED' AND severity = 'CRITICAL'")).ShouldBe(1);
        (await CountAsync(shop, $"SELECT count(*) FROM audit.audit_log WHERE summary LIKE '%{Password}%' OR new_values::text LIKE '%{Password}%'")).ShouldBe(0);

        // Con credenciales pero en OFF: la venta sigue con comprobante interno y nada se envía.
        (await SellAsync(shop)).DocumentType.ShouldBe("INTERNAL_RECEIPT");
        (await ProcessAsync(shop)).Sent.ShouldBe(0);
        factory.Fiscal.Submissions.ShouldBeEmpty();
        (await GetAsync<FiscalAlertsDto>(shop.Owner, "/api/v1/billing/alerts")).ShouldSatisfyAllConditions(
            a => a.Mode.ShouldBe("OFF"), a => a.Count.ShouldBe(0));
    }

    [Fact]
    public async Task En_cada_venta_nace_PENDING_la_cola_la_acepta_y_reenviar_nunca_duplica()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        var ranges = await EnableAsync(shop);
        ranges.Count.ShouldBe(3);
        ranges.ShouldAllBe(r => r.BranchId == shop.Catalog.Setup.BranchId && r.IsUsable);
        (await CountAsync(shop, "SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_RANGES_SYNCED'")).ShouldBe(1);
        (await CountAsync(shop, "SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_SETTINGS_CHANGED'")).ShouldBe(1);

        // La venta no espera: el documento nace PENDING en su transacción con reference_code = id de la venta.
        var sale = await shop.StartAsync();
        await shop.AddAsync(sale.Id, shop.Rice, 2);
        await shop.AddAsync(sale.Id, shop.Tomato, 1.25m);
        var receipt = await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 20_000m)], key: "caja1-venta-fe");
        (receipt.DocumentType, receipt.DocumentStatus).ShouldBe(("INVOICE_ELECTRONIC", "PENDING"));
        var pending = await ForSourceAsync(shop, sale.Id);
        (pending.ReferenceCode, pending.Status, pending.Attempts).ShouldBe((sale.Id.ToString("N"), "PENDING", 0));

        var run = await ProcessAsync(shop);
        (run.Processed, run.Sent, run.Accepted, run.StoppedBecause).ShouldBe((1, 1, 1, (string?)null));
        var accepted = await DocumentAsync(shop, pending.Id);
        (accepted.Status, accepted.FiscalNumber, accepted.Attempts, accepted.Provider).ShouldBe(("ACCEPTED", (string?)"SETP990000001", 1, (string?)"FAKE"));
        accepted.Cufe.ShouldNotBeNullOrWhiteSpace();
        accepted.QrData!.ShouldContain(accepted.Cufe!);
        accepted.ValidatedAt.ShouldNotBeNull();
        accepted.NumberingRangeId.ShouldBe(ranges.Single(r => r.DocumentType == "INVOICE_ELECTRONIC").Id);
        (await GetAsync<List<FiscalRangeDto>>(shop.Owner, "/api/v1/billing/ranges")).Single(r => r.DocumentType == "INVOICE_ELECTRONIC").Current
            .ShouldBe(990_000_001);

        // Modelo neutro armado desde la venta guardada: consumidor final, renglones con impuestos por tarifa, pago y total cobrado.
        var draft = factory.Fiscal.Invoices.Single();
        (draft.Header.ReferenceCode, draft.Header.SourceNumber, draft.Header.Numbering.Prefix, draft.Header.Issuer.IdentificationNumber).ShouldBe(
            (sale.Id.ToString("N"), receipt.Sale.Number!, "SETP", "900123456"));
        draft.Customer.IsFinalConsumer.ShouldBeTrue();
        draft.Lines.Select(l => l.Code).ShouldBe(["ARROZ-500", "TOMATE"]);
        draft.Lines.Single(l => l.Code == "TOMATE").Taxes.ShouldAllBe(t => t.IsExcluded);
        draft.Totals.Total.ShouldBe(receipt.Sale.Total);
        draft.Totals.RoundingAdjustment.ShouldBe(receipt.Sale.RoundingAdjustment);
        draft.Payments.Single().MethodCode.ShouldBe("EFECTIVO");
        factory.Fiscal.LastConnection!.Credentials.Password.ShouldBe(Password);
        factory.Fiscal.LastConnection.Environment.ShouldBe(FiscalEnvironment.Sandbox);

        // Repetir el cobro con la misma llave no emite otro documento; una pasada más no envía nada.
        (await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 20_000m)], key: "caja1-venta-fe")).Sale.Number.ShouldBe(receipt.Sale.Number);
        (await DocumentsAsync(shop)).Count.ShouldBe(1);
        (await ProcessAsync(shop)).Sent.ShouldBe(0);

        // Un envío que quedó a medias (el proceso se cayó con el documento reclamado) se retoma CONSULTANDO el estado: sin duplicar.
        await shop.Catalog.ExecuteAsync(
            $"UPDATE billing.fiscal_documents SET status = 'SUBMITTING', next_attempt_at = now() - interval '1 minute' WHERE id = '{pending.Id}'");
        var submissions = factory.Fiscal.Submissions.Count;
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        (factory.Fiscal.Submissions.Count, factory.Fiscal.DistinctDocuments).ShouldBe((submissions, 1));
        (await DocumentAsync(shop, pending.Id)).ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe("ACCEPTED"), d => d.FiscalNumber.ShouldBe("SETP990000001"), d => d.Attempts.ShouldBe(2));

        // Un documento aceptado no se reintenta.
        await shop.Owner.PostAsJsonAsync($"/api/v1/billing/documents/{pending.Id}/retry", new { }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.NOT_RETRYABLE");

        // Conciliación del día: 1 venta, 1 aceptado, nada pendiente ni sin documento.
        var day = (await GetAsync<List<FiscalReconciliationDayDto>>(shop.Owner, "/api/v1/billing/reconciliation")).Single(d => d.SalesCompleted > 0);
        (day.SalesCompleted, day.Accepted, day.Pending, day.Rejected, day.SalesWithoutDocument, day.AcceptedTotal).ShouldBe(
            (1, 1, 0, 0, 0, receipt.Sale.Total));
        await shop.Owner.GetAsync("/api/v1/billing/reconciliation?from=2026-01-01&to=2026-12-31", Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "BILLING.INVALID_PERIOD");
    }

    [Fact]
    public async Task Sin_Internet_la_venta_sigue_y_los_documentos_salen_en_orden_al_volver_la_conexion()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await EnableAsync(shop);
        factory.Fiscal.Offline = true;

        var first = await SellAsync(shop);
        var second = await SellAsync(shop);
        first.Sale.Status.ShouldBe("COMPLETED");

        // Contingencia: el primero queda en CONTINGENCY y la pasada se detiene (no martilla al proveedor).
        var run = await ProcessAsync(shop);
        (run.Processed, run.Accepted, run.StoppedBecause).ShouldBe((1, 0, (string?)"CONTINGENCY"));
        var contingency = await ForSourceAsync(shop, first.Sale.Id);
        (contingency.Status, contingency.Attempts).ShouldBe(("CONTINGENCY", 1));
        contingency.NextAttemptAt.ShouldNotBeNull();
        (await ForSourceAsync(shop, second.Sale.Id)).Status.ShouldBe("PENDING");

        // Aún sin conexión, antes de su espera no se reintenta; vencida, sigue en contingencia.
        (await ProcessAsync(shop)).Processed.ShouldBe(1); // el segundo (el primero espera)
        (await ForSourceAsync(shop, second.Sale.Id)).Status.ShouldBe("CONTINGENCY");

        // Vuelve Internet: salen en orden de llegada, sin duplicados.
        factory.Fiscal.Offline = false;
        await DueNowAsync(shop);
        (await ProcessAsync(shop)).Accepted.ShouldBe(2);
        factory.Fiscal.Invoices.Select(i => i.Header.SourceNumber).ShouldBe([first.Sale.Number!, second.Sale.Number!]);
        factory.Fiscal.DistinctDocuments.ShouldBe(2);
        (await DocumentsAsync(shop, "?status=ACCEPTED")).Count.ShouldBe(2);

        // Pendientes antiguos: alerta si pasan de ⚙️ 24 h.
        await shop.Catalog.ExecuteAsync("UPDATE billing.fiscal_documents SET status = 'PENDING', fiscal_number = NULL, cufe = NULL, validated_at = NULL, issued_at = now() - interval '25 hours' WHERE source_id = '" + first.Sale.Id + "'");
        var alerts = await GetAsync<FiscalAlertsDto>(shop.Owner, "/api/v1/billing/alerts");
        (alerts.PendingOverdue, alerts.Mode).ShouldBe((1, "EVERY_SALE"));
        alerts.OldestPendingAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Un_rechazo_queda_REJECTED_con_alerta_critica_y_se_corrige_o_se_reintenta()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await EnableAsync(shop);
        factory.Fiscal.RejectedIdentifications.Add(FiscalParty.FinalConsumerIdentification);

        var sale = await SellAsync(shop);
        (await ProcessAsync(shop)).Accepted.ShouldBe(0);
        var rejected = await ForSourceAsync(shop, sale.Sale.Id);
        (rejected.Status, rejected.NextAttemptAt).ShouldBe(("REJECTED", (DateTimeOffset?)null));
        rejected.RejectionMessage!.ShouldContain("FAK24");
        (await shop.Catalog.ScalarAsync<string>("SELECT severity FROM audit.audit_log WHERE action = 'FISCAL_DOCUMENT_REJECTED'")).ShouldBe("CRITICAL");
        (await GetAsync<FiscalAlertsDto>(shop.Owner, "/api/v1/billing/alerts")).Rejected.ShouldBe(1);
        (await DocumentsAsync(shop, "?status=REJECTED&search=" + sale.Sale.Number)).Single().Id.ShouldBe(rejected.Id);

        // El supervisor corrige los datos del adquirente (la venta no cambia) y el documento se reenvía con ellos.
        var corrected = await SendAsync<FiscalDocumentDto>(shop.Supervisor, HttpMethod.Put, $"/api/v1/billing/documents/{rejected.Id}/buyer", new
        {
            name = "Tienda La 14 SAS", identificationType = "NIT", identificationNumber = "901234567", checkDigit = "1", email = "compras@la14.co",
            personType = "LEGAL", taxRegime = "48", responsibilities = GreatTaxpayer, municipalityCode = "05001",
        });
        (corrected.Status, corrected.BuyerIdentificationType, corrected.BuyerIdentification).ShouldBe(("PENDING", "NIT", "901234567"));
        corrected.Events[^1].EventType.ShouldBe("BUYER_CORRECTED");
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        var resent = factory.Fiscal.Invoices[^1];
        (resent.Customer.IdentificationFiscalCode, resent.Customer.PersonType, resent.Customer.CheckDigit, resent.Header.Resubmission).ShouldBe(
            ("31", "LEGAL", (string?)"1", true));
        (await GetAsync<SaleDto>(shop.Owner, $"/api/v1/sales/{sale.Sale.Id}")).Customer.Name.ShouldBe("Consumidor final");
        (await CountAsync(shop, "SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_BUYER_CORRECTED'")).ShouldBe(1);

        // Un aceptado ya no se corrige.
        await shop.Supervisor.PutAsJsonAsync($"/api/v1/billing/documents/{rejected.Id}/buyer", new
        {
            name = "Otro", identificationType = "CC", identificationNumber = "1", personType = "NATURAL", taxRegime = "49",
        }, Json, Ct).ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.NOT_CORRECTABLE");

        // Reintento manual tras un rechazo transitorio del lado del proveedor.
        var other = await SellAsync(shop);
        (await ProcessAsync(shop)).Accepted.ShouldBe(0);
        factory.Fiscal.RejectedIdentifications.Clear();
        var retry = await ForSourceAsync(shop, other.Sale.Id);
        (await PostAsync<FiscalDocumentDto>(shop.Supervisor, $"/api/v1/billing/documents/{retry.Id}/retry")).Status.ShouldBe("PENDING");
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        (await DocumentAsync(shop, retry.Id)).Status.ShouldBe("ACCEPTED");
        (await CountAsync(shop, "SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_DOCUMENT_RETRIED'")).ShouldBe(1);
        (await GetAsync<FiscalAlertsDto>(shop.Owner, "/api/v1/billing/alerts")).Rejected.ShouldBe(0);
    }

    [Fact]
    public async Task Anulacion_cambio_y_garantia_emiten_nota_credito_y_la_factura_no_enviada_se_cancela()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await EnableAsync(shop);

        // Anular una venta cuya factura aún no se envió: la factura se cancela y no hay nota crédito.
        var notSent = await SellAsync(shop);
        var voided = await shop.AuthorizedAsync<SaleReceiptDto>(HttpMethod.Post, $"/api/v1/sales/{notSent.Sale.Id}/void", new { reason = "Error de digitación" });
        voided.DocumentStatus.ShouldBe("CANCELLED");
        (await ForSourceAsync(shop, notSent.Sale.Id)).Events[^1].EventType.ShouldBe("CANCELLED");
        (await ProcessAsync(shop)).Sent.ShouldBe(0);
        (await DocumentsAsync(shop, "?type=CREDIT_NOTE")).ShouldBeEmpty();

        // Anular una venta con factura aceptada: nota crédito de anulación que referencia la factura.
        var accepted = await SellAsync(shop);
        await ProcessAsync(shop);
        var invoice = await ForSourceAsync(shop, accepted.Sale.Id);
        invoice.Status.ShouldBe("ACCEPTED");
        await shop.AuthorizedAsync<SaleReceiptDto>(HttpMethod.Post, $"/api/v1/sales/{accepted.Sale.Id}/void", new { reason = "Cliente desistió" });
        var voidNote = await ForSourceAsync(shop, accepted.Sale.Id, "SALE_VOID");
        (voidNote.DocumentType, voidNote.Status, voidNote.RelatedDocumentId, voidNote.ReferenceCode).ShouldBe(
            ("CREDIT_NOTE", "PENDING", (Guid?)invoice.Id, (string?)("NCA" + accepted.Sale.Id.ToString("N"))));
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        var voidDraft = factory.Fiscal.CreditNotes.Single();
        (voidDraft.Concept, voidDraft.Reason, voidDraft.Invoice.Cufe, voidDraft.Invoice.FiscalNumber, voidDraft.Totals.Total).ShouldBe(
            (FiscalCorrectionConcept.Void, "Cliente desistió", invoice.Cufe!, invoice.FiscalNumber!, accepted.Sale.Total));
        (await ForSourceAsync(shop, accepted.Sale.Id, "SALE_VOID")).FiscalNumber.ShouldBe("NC1");

        // Cambio de mercancía de una venta aceptada: nota crédito de devolución parcial + factura de la venta nueva.
        var original = await SellAsync(shop);
        await ProcessAsync(shop);
        var riceLine = original.Sale.Lines.Single(l => l.ProductId == shop.Rice);
        var body = new
        {
            originalSaleId = original.Sale.Id, reason = "Arroz de más",
            lines = new object[] { new { saleLineId = riceLine.Id, quantity = 1m, destination = "ReturnToStock" } },
        };
        var grant = await shop.GrantAsync(await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/exchanges", body));
        var started = await SendAsync<ExchangeStartedDto>(shop.Cashier, HttpMethod.Post, "/api/v1/exchanges", body, HttpStatusCode.Created, grant: grant);
        await shop.AddAsync(started.Sale.Id, shop.Soda, 2);
        var replacement = await shop.CompleteAsync(started.Sale.Id, [Pay(shop.Cash, 2_000m)]);
        replacement.DocumentType.ShouldBe("INVOICE_ELECTRONIC");
        var exchangeNote = await ForSourceAsync(shop, started.Exchange.Id, "CUSTOMER_RETURN");
        (exchangeNote.DocumentType, exchangeNote.RelatedDocumentId).ShouldBe(("CREDIT_NOTE", (Guid?)(await ForSourceAsync(shop, original.Sale.Id)).Id));
        (await ProcessAsync(shop)).Accepted.ShouldBe(2);
        var exchangeDraft = factory.Fiscal.CreditNotes[^1];
        (exchangeDraft.Concept, exchangeDraft.Totals.Total, exchangeDraft.Lines.Single().Code, exchangeDraft.Payments.Single().MethodKind).ShouldBe(
            (FiscalCorrectionConcept.PartialReturn, 3_000m, "ARROZ-500", "EXCHANGE_CREDIT"));
        factory.Fiscal.Invoices[^1].Payments.ShouldContain(p => p.MethodKind == "EXCHANGE_CREDIT" && p.Amount == 3_000m);

        // Reintegro por garantía (solo el propietario): nota crédito con el medio con que salió el dinero.
        var owner = factory.CreateClient();
        await SecurityScenario.PosLoginAsync(owner, "100", "4826");
        var sodaLine = original.Sale.Lines.Single(l => l.ProductId == shop.Soda);
        var refund = await PostAsync<RefundReceiptDto>(owner, "/api/v1/exchanges/warranty-refund", new
        {
            originalSaleId = original.Sale.Id, reason = "Gaseosa sin gas",
            lines = new object[] { new { saleLineId = sodaLine.Id, quantity = 1m, destination = "Discard" } },
        });
        var warrantyNote = await ForSourceAsync(shop, refund.Refund.Id, "CUSTOMER_RETURN");
        warrantyNote.Status.ShouldBe("PENDING");
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        var warrantyDraft = factory.Fiscal.CreditNotes[^1];
        (warrantyDraft.Reason, warrantyDraft.Totals.Total, warrantyDraft.Payments.Single().MethodCode).ShouldBe(("Gaseosa sin gas", 2_500m, "EFECTIVO"));
        factory.Fiscal.CreditNotes.ShouldAllBe(n => n.Header.Numbering.Prefix == "NC");

        var day = (await GetAsync<List<FiscalReconciliationDayDto>>(shop.Owner, "/api/v1/billing/reconciliation")).Single(d => d.SalesCompleted > 0);
        (day.SalesVoided, day.Cancelled, day.CreditNotes).ShouldBe((2, 1, 3));
    }

    [Fact]
    public async Task Una_nota_credito_espera_a_que_su_factura_sea_aceptada()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await EnableAsync(shop);
        factory.Fiscal.Offline = true;

        // La factura queda en contingencia; el cambio de mercancía emite su nota crédito, que no sale antes que la factura.
        var original = await SellAsync(shop);
        await ProcessAsync(shop);
        var riceLine = original.Sale.Lines.Single(l => l.ProductId == shop.Rice);
        var body = new
        {
            originalSaleId = original.Sale.Id, reason = "Cambio",
            lines = new object[] { new { saleLineId = riceLine.Id, quantity = 1m, destination = "ReturnToStock" } },
        };
        var grant = await shop.GrantAsync(await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/exchanges", body));
        var started = await SendAsync<ExchangeStartedDto>(shop.Cashier, HttpMethod.Post, "/api/v1/exchanges", body, HttpStatusCode.Created, grant: grant);
        await shop.AddAsync(started.Sale.Id, shop.Rice, 1);
        await shop.CompleteAsync(started.Sale.Id, []);
        var note = await ForSourceAsync(shop, started.Exchange.Id, "CUSTOMER_RETURN");
        note.Status.ShouldBe("PENDING");

        factory.Fiscal.Offline = false;
        await shop.Catalog.ExecuteAsync(
            $"UPDATE billing.fiscal_documents SET next_attempt_at = now() + interval '1 hour' WHERE source_id = '{original.Sale.Id}' AND source = 'SALE'");
        await ProcessAsync(shop);
        var waiting = await DocumentAsync(shop, note.Id);
        (waiting.Status, waiting.Events[^1].EventType).ShouldBe(("PENDING", "WAITING_INVOICE"));
        factory.Fiscal.CreditNotes.ShouldBeEmpty();

        await DueNowAsync(shop);
        await ProcessAsync(shop);
        await DueNowAsync(shop);
        await ProcessAsync(shop);
        (await DocumentAsync(shop, note.Id)).Status.ShouldBe("ACCEPTED");
        factory.Fiscal.CreditNotes.Single().Invoice.FiscalNumber.ShouldBe((await ForSourceAsync(shop, original.Sale.Id)).FiscalNumber!);
    }

    [Fact]
    public async Task Sin_rango_vigente_la_venta_sigue_y_el_documento_queda_pendiente_con_alerta_critica()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var session = await shop.OpenSessionAsync();

        // El rango de facturas del proveedor está agotado.
        factory.Fiscal.Ranges[0] = factory.Fiscal.Ranges[0] with { Current = factory.Fiscal.Ranges[0].To };
        var ranges = await EnableAsync(shop);
        var exhausted = ranges.Single(r => r.DocumentType == "INVOICE_ELECTRONIC");
        (exhausted.IsUsable, exhausted.NeedsAlert, exhausted.Remaining).ShouldBe((false, true, 0L));

        var sale = await SellAsync(shop);
        sale.Sale.Status.ShouldBe("COMPLETED");
        (await ProcessAsync(shop)).Sent.ShouldBe(0);
        var pending = await ForSourceAsync(shop, sale.Sale.Id);
        (pending.Status, pending.Events[^1].EventType, pending.NumberingRangeId).ShouldBe(("PENDING", "NO_RANGE", (Guid?)null));
        (await shop.Catalog.ScalarAsync<string>("SELECT severity FROM audit.audit_log WHERE action = 'FISCAL_RANGE_MISSING'")).ShouldBe("CRITICAL");
        var alerts = await GetAsync<FiscalAlertsDto>(shop.Owner, "/api/v1/billing/alerts");
        (alerts.WithoutRange, alerts.Ranges.Single().RangeId).ShouldBe((1, exhausted.Id));

        // Se vuelve a intentar sin repetir la alerta.
        await DueNowAsync(shop);
        await ProcessAsync(shop);
        (await CountAsync(shop, "SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_RANGE_MISSING'")).ShouldBe(1);

        // Llega un rango nuevo: se sincroniza, se asigna a la caja (reemplaza al agotado en ese lugar) y el documento sale.
        factory.Fiscal.Ranges.Add(new FiscalProviderRange(
            "FAKE-FV-2", FiscalDocumentType.InvoiceElectronic, "FE", 1, 5_000, 0, "18760000009", null, PurchasingDate().AddYears(1), true));
        var synced = await PostAsync<List<FiscalRangeDto>>(shop.Owner, "/api/v1/billing/ranges/sync");
        var fresh = synced.Single(r => r.ProviderRangeId == "FAKE-FV-2");
        fresh.BranchId.ShouldBeNull();
        var terminalId = session.PosTerminalId;
        (await AssignAsync(shop, fresh.Id, shop.Catalog.Setup.BranchId, terminalId)).PosTerminalId.ShouldBe(terminalId);
        await shop.Owner.PutAsJsonAsync($"/api/v1/billing/ranges/{fresh.Id}/assignment", new { posTerminalId = terminalId }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "BILLING.INVALID_ASSIGNMENT");
        await shop.Owner.PutAsJsonAsync($"/api/v1/billing/ranges/{Guid.NewGuid()}/assignment", new { branchId = shop.Catalog.Setup.BranchId }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.NotFound, "BILLING.RANGE_NOT_FOUND");
        (await CountAsync(shop, "SELECT count(*) FROM audit.audit_log WHERE action = 'FISCAL_RANGE_ASSIGNED'")).ShouldBe(4);

        await DueNowAsync(shop);
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        (await DocumentAsync(shop, pending.Id)).ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe("ACCEPTED"), d => d.FiscalNumber.ShouldBe("FE1"), d => d.NumberingRangeId.ShouldBe(fresh.Id));
        (await GetAsync<FiscalAlertsDto>(shop.Owner, "/api/v1/billing/alerts")).WithoutRange.ShouldBe(0);

        // El proveedor caído al sincronizar deja el error registrado.
        factory.Fiscal.Offline = true;
        await shop.Owner.PostAsJsonAsync("/api/v1/billing/ranges/sync", new { }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "BILLING.RANGE_SYNC_FAILED");
        (await GetAsync<BillingSettingsDto>(shop.Owner, "/api/v1/billing/settings")).LastSyncError.ShouldNotBeNull();
    }

    [Fact]
    public async Task La_compra_a_un_proveedor_no_obligado_genera_documento_soporte_si_la_facturacion_esta_encendida()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var purchasing = shop.Purchasing;
        var farmer = await purchasing.CreatePartyAsync(new
        {
            personType = "Natural", identificationType = "CC", identificationNumber = "71234567", firstNames = "Pedro", lastNames = "Campesino",
            taxRegime = "49", fiscalResponsibilities = NotResponsible,
        });
        var created = await shop.Owner.PostAsJsonAsync("/api/v1/purchasing/suppliers",
            new { partyId = farmer.Id, supplier = new { code = "CAMPO", paymentTermDays = 0, issuesInvoices = false } }, Json, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var supplierId = (await created.Content.ReadFromJsonAsync<SupplierDto>(Json, Ct))!.Id;

        // Apagada: la compra se contabiliza y no genera documento.
        var off = await BuyFromAsync(shop, supplierId, "SF-1");
        off.RequiresSupportDocument.ShouldBeTrue();
        (await DocumentsAsync(shop, "?source=PURCHASE")).ShouldBeEmpty();

        await EnableAsync(shop);
        var on = await BuyFromAsync(shop, supplierId, "SF-2");
        var support = await ForSourceAsync(shop, on.Id, "PURCHASE");
        (support.DocumentType, support.Status, support.BuyerName, support.BuyerIdentification, support.ReferenceCode).ShouldBe(
            ("SUPPORT_DOCUMENT", "PENDING", "Pedro Campesino", "71234567", (string?)("DS" + on.Id.ToString("N"))));
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        var draft = factory.Fiscal.SupportDocuments.Single();
        (draft.Supplier.IdentificationFiscalCode, draft.Supplier.Name, draft.SupplierInvoiceNumber, draft.Header.Numbering.Prefix, draft.Lines.Single().Code)
            .ShouldBe(("13", "Pedro Campesino", "SF-2", "DS", "ARROZ-500"));
        draft.Totals.Total.ShouldBe(on.Lines.Sum(l => l.LineTotal));
        (await DocumentAsync(shop, support.Id)).FiscalNumber.ShouldBe("DS1");

        // Un proveedor que factura no genera documento soporte.
        await purchasing.BuyAsync("FE-900", new { productId = shop.Rice, quantity = 1, unitCost = 1_000 });
        (await DocumentsAsync(shop, "?source=PURCHASE")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Permisos_la_configuracion_es_del_propietario_y_el_administrador()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.Security.CreateUserAsync("admin.fe", "ADMIN");
        await shop.Security.CreateUserAsync("contadora", "ACCOUNTANT");
        var admin = await shop.Security.LocalClientAsync("admin.fe");
        var accountant = await shop.Security.LocalClientAsync("contadora");

        (await GetAsync<BillingSettingsDto>(admin, "/api/v1/billing/settings")).Mode.ShouldBe("OFF");
        await CredentialsAsync(admin);

        foreach (var client in new[] { shop.Supervisor, shop.Cashier, accountant })
        {
            (await StatusAsync(client, HttpMethod.Get, "/api/v1/billing/settings")).ShouldBe(HttpStatusCode.Forbidden);
            (await StatusAsync(client, HttpMethod.Put, "/api/v1/billing/settings/credentials", new { username = "a", password = "b", clientId = "c", clientSecret = "d" }))
                .ShouldBe(HttpStatusCode.Forbidden);
            (await StatusAsync(client, HttpMethod.Post, "/api/v1/billing/ranges/sync")).ShouldBe(HttpStatusCode.Forbidden);
        }

        // Ver: supervisor y contador; administrar documentos: supervisor (no el contador ni el cajero).
        (await StatusAsync(shop.Supervisor, HttpMethod.Get, "/api/v1/billing/alerts")).ShouldBe(HttpStatusCode.OK);
        (await StatusAsync(accountant, HttpMethod.Get, "/api/v1/billing/reconciliation")).ShouldBe(HttpStatusCode.OK);
        (await StatusAsync(accountant, HttpMethod.Post, "/api/v1/billing/queue/process")).ShouldBe(HttpStatusCode.Forbidden);
        (await StatusAsync(shop.Cashier, HttpMethod.Get, "/api/v1/billing/documents")).ShouldBe(HttpStatusCode.Forbidden);
        (await StatusAsync(shop.Supervisor, HttpMethod.Post, "/api/v1/billing/queue/process")).ShouldBe(HttpStatusCode.OK);

        (await CountAsync(shop,
                """
                SELECT count(*) FROM identity.role_permissions rp JOIN identity.roles r ON r.id = rp.role_id
                WHERE rp.permission_code = 'billing.settings.manage' AND r.code IN ('OWNER', 'ADMIN')
                """))
            .ShouldBe(2);
    }

    [Fact]
    public async Task El_tiquete_espera_hasta_3_segundos_los_datos_fiscales()
    {
        await using var factory = new WorkerBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await EnableAsync(shop);

        var receipt = await SellAsync(shop);
        var documentId = (await ForSourceAsync(shop, receipt.Sale.Id)).Id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
            var info = await billing.WaitForFiscalDataAsync(documentId, cancellationToken: Ct);
            (info!.Status, info.DocumentType).ShouldBe(("ACCEPTED", "INVOICE_ELECTRONIC"));
            info.FiscalNumber.ShouldNotBeNull();
            info.QrData.ShouldNotBeNull();
            (await billing.WaitForFiscalDataAsync(Guid.NewGuid(), TimeSpan.FromMilliseconds(200), Ct)).ShouldBeNull();
        }

        // Sin Internet la espera termina a los ⚙️ 3 s con el documento aún sin número (el tiquete dice "en proceso").
        factory.Fiscal.Offline = true;
        var offline = await SellAsync(shop);
        var offlineId = (await ForSourceAsync(shop, offline.Sale.Id)).Id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var started = DateTimeOffset.UtcNow;
            var info = await scope.ServiceProvider.GetRequiredService<IBillingService>().WaitForFiscalDataAsync(offlineId, TimeSpan.FromSeconds(1), Ct);
            info!.FiscalNumber.ShouldBeNull();
            info.Status.ShouldBeOneOf("PENDING", "SUBMITTING", "CONTINGENCY");
            (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(3));
        }
    }

    private static DateOnly PurchasingDate() => Phase5.PurchasingScenario.Today;

    private static async Task<PurchaseDto> BuyFromAsync(SalesScenario shop, Guid supplierId, string invoice)
    {
        var owner = shop.Owner;
        var today = Phase5.PurchasingScenario.Today;
        var floor = shop.Purchasing.Floor;
        var line = new { productId = shop.Rice, quantity = 5, unitCost = 1_000 };
        var response = await owner.PostAsJsonAsync("/api/v1/purchasing/purchases", new
        {
            supplierId, warehouseId = floor, supplierInvoiceNumber = invoice, invoiceDate = today, paymentMode = "Credit", proration = "Value", lines = new[] { line },
        }, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var draft = (await response.Content.ReadFromJsonAsync<PurchaseDto>(Json, Ct))!;
        var updated = await owner.PutAsJsonAsync($"/api/v1/purchasing/purchases/{draft.Id}", new
        {
            supplierId, warehouseId = floor, supplierInvoiceNumber = invoice, invoiceDate = today, paymentMode = "Credit", invoiceTotal = draft.Total,
            proration = "Value", lines = new[] { line },
        }, Json, Ct);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));
        return await shop.Purchasing.PostAsync(draft.Id);
    }
}
