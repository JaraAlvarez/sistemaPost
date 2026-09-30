using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Billing.FactusFake;
using Pos.Modules.Billing.Infrastructure.Factus;
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
/// Servidor POS con el proveedor REAL <c>Pos:Billing:Provider = FACTUS</c> (cliente HTTP de Factus v2) apuntando al Factus SIMULADO en
/// Kestrel (<see cref="FactusFakeServer.StartAsync"/>). La cola la dirigen las pruebas (<c>POST /billing/queue/process</c>).
/// </summary>
public sealed class FactusBillingServerFactory(Uri factusUrl) : PosServerFactory
{
    protected override string Edition => "SINGLE";

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("Pos:Billing:Provider", "FACTUS"),
        new("Pos:Billing:Worker", "false"),
        new("Pos:Billing:Factus:BaseUrl", factusUrl.ToString()),
        new("Pos:Billing:Factus:TimeoutSeconds", "5"),
    ];

    /// <summary>Ajustes de conexión vivos (las pruebas cambian la URL para simular que no hay Internet).</summary>
    public FactusConnectionOptions Connection => Services.GetRequiredService<FactusConnectionOptions>();
}

/// <summary>
/// Pruebas de punta a punta de la Fase 11-B con el adaptador de Factus (docs/fases/fase-11b-propuesta.md §10 y §13): venta → factura
/// aceptada con número, CUFE y QR; anulación → nota crédito; cambio y garantía → nota crédito parcial; sin Internet → contingencia y envío
/// al volver sin duplicados; rechazo DIAN → corrección, borrado y reenvío; rangos; 429 respetado; documento soporte y nota de ajuste.
/// </summary>
public class FactusEndToEndTests
{
    private static readonly string[] GreatTaxpayer = ["O-13"];
    private static readonly string[] NotResponsible = ["R-99-PN"];

    /// <summary>Factus simulado en Kestrel + servidor POS con el proveedor FACTUS + escenario de ventas con la caja abierta.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public required FactusFakeServer Factus { get; init; }
        public required FactusBillingServerFactory Factory { get; init; }
        public required SalesScenario Shop { get; init; }

        public static async Task<Harness> CreateAsync(bool openSession = true)
        {
            var factus = new FactusFakeServer();
            var url = await factus.StartAsync(Ct);
            var factory = new FactusBillingServerFactory(url);
            var shop = await SalesScenario.CreateAsync(factory);
            if (openSession)
            {
                await shop.OpenSessionAsync();
            }

            return new Harness { Factus = factus, Factory = factory, Shop = shop };
        }

        /// <summary>Credenciales de Factus (las del simulado), modo EVERY_SALE en sandbox, rangos sincronizados y asignados a la sucursal.</summary>
        public async Task<List<FiscalRangeDto>> EnableAsync()
        {
            var settings = await SendAsync<BillingSettingsDto>(Shop.Owner, HttpMethod.Put, "/api/v1/billing/settings/credentials", new
            {
                username = FactusFakeServer.DefaultUsername, password = FactusFakeServer.DefaultPassword,
                clientId = FactusFakeServer.DefaultClientId, clientSecret = FactusFakeServer.DefaultClientSecret,
            });
            settings.ActiveAdapter.ShouldBe("FACTUS");
            await SendAsync<BillingSettingsDto>(Shop.Owner, HttpMethod.Put, "/api/v1/billing/settings", new { mode = "EVERY_SALE", environment = "SANDBOX" });
            var ranges = await PostAsync<List<FiscalRangeDto>>(Shop.Owner, "/api/v1/billing/ranges/sync");
            var assigned = new List<FiscalRangeDto>();
            foreach (var range in ranges)
            {
                assigned.Add(await AssignAsync(Shop, range.Id, Shop.Catalog.Setup.BranchId));
            }

            return assigned;
        }

        public JsonElement Request(FakeDocumentKind kind, string reference) =>
            JsonDocument.Parse(Factus.Find(kind, reference).ShouldNotBeNull().RequestJson).RootElement.Clone();

        public int Validations(string resource) => Factus.Requests.Count(r => r.Method == "POST" && r.PathAndQuery == $"v2/{resource}/validate");

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await Factus.DisposeAsync();
        }
    }

    [Fact]
    public async Task Venta_factura_aceptada_anulacion_cambio_y_garantia_con_notas_credito_en_Factus()
    {
        await using var h = await Harness.CreateAsync();
        var shop = h.Shop;
        var ranges = await h.EnableAsync();
        ranges.Select(r => (r.DocumentType, r.Prefix, r.Provider)).OrderBy(r => r.DocumentType, StringComparer.Ordinal).ShouldBe(
        [
            ("ADJUSTMENT_NOTE", "NA", "FACTUS"), ("CREDIT_NOTE", "NC", "FACTUS"), ("INVOICE_ELECTRONIC", "SETP", "FACTUS"), ("SUPPORT_DOCUMENT", "DS", "FACTUS"),
        ]);

        // Venta → factura electrónica aceptada con número, CUFE y QR (consumidor final).
        var sale = await SellAsync(shop);
        (sale.DocumentType, sale.DocumentStatus).ShouldBe(("INVOICE_ELECTRONIC", "PENDING"));
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        var invoice = await ForSourceAsync(shop, sale.Sale.Id);
        (invoice.Status, invoice.FiscalNumber, invoice.Provider, invoice.ProviderStatus).ShouldBe(("ACCEPTED", (string?)"SETP990000001", (string?)"FACTUS", (string?)"VALIDATED"));
        invoice.Cufe.ShouldNotBeNullOrWhiteSpace();
        invoice.QrData!.ShouldContain(invoice.Cufe!);
        invoice.ValidatedAt.ShouldNotBeNull();
        var bill = h.Request(FakeDocumentKind.Bill, sale.Sale.Id.ToString("N"));
        bill.GetProperty("customer").GetProperty("identification").GetString().ShouldBe("222222222222");
        bill.GetProperty("payment_details")[0].GetProperty("payment_method_code").GetString().ShouldBe("10");
        bill.GetProperty("numbering_range_id").GetInt32().ShouldBe(8);
        h.Factus.Find(FakeDocumentKind.Bill, sale.Sale.Id.ToString("N"))!.Totals.Total.ShouldBe(sale.Sale.Total - sale.Sale.RoundingAdjustment, 0.01m);
        (await GetAsync<List<FiscalRangeDto>>(shop.Owner, "/api/v1/billing/ranges")).Single(r => r.DocumentType == "INVOICE_ELECTRONIC").Current.ShouldBe(990_000_001);

        // Anulación de la venta aceptada → nota crédito de anulación (concepto 2) que referencia la factura.
        await shop.AuthorizedAsync<SaleReceiptDto>(HttpMethod.Post, $"/api/v1/sales/{sale.Sale.Id}/void", new { reason = "Cliente desistió" });
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        var voidNote = await ForSourceAsync(shop, sale.Sale.Id, "SALE_VOID");
        (voidNote.Status, voidNote.FiscalNumber).ShouldBe(("ACCEPTED", (string?)"NC1"));
        var voidRequest = h.Request(FakeDocumentKind.CreditNote, "NCA" + sale.Sale.Id.ToString("N"));
        (voidRequest.GetProperty("bill_number").GetString(), voidRequest.GetProperty("correction_concept_code").GetString()).ShouldBe(("SETP990000001", "2"));

        // Cambio de mercancía → nota crédito parcial (concepto 1) + factura de la venta nueva.
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
        await shop.CompleteAsync(started.Sale.Id, [Pay(shop.Cash, 2_000m)]);
        (await ProcessAsync(shop)).Accepted.ShouldBe(2);
        var exchangeNote = await ForSourceAsync(shop, started.Exchange.Id, "CUSTOMER_RETURN");
        exchangeNote.Status.ShouldBe("ACCEPTED");
        var exchangeRequest = h.Request(FakeDocumentKind.CreditNote, exchangeNote.ReferenceCode!);
        exchangeRequest.GetProperty("correction_concept_code").GetString().ShouldBe("1");
        exchangeRequest.GetProperty("bill_number").GetString().ShouldBe((await ForSourceAsync(shop, original.Sale.Id)).FiscalNumber);
        exchangeRequest.GetProperty("items").GetArrayLength().ShouldBe(1);
        (await ForSourceAsync(shop, started.Sale.Id)).Status.ShouldBe("ACCEPTED");

        // Reintegro por garantía → nota crédito parcial con el medio con que salió el dinero.
        var owner = h.Factory.CreateClient();
        await SecurityScenario.PosLoginAsync(owner, "100", "4826");
        var sodaLine = original.Sale.Lines.Single(l => l.ProductId == shop.Soda);
        var refund = await PostAsync<RefundReceiptDto>(owner, "/api/v1/exchanges/warranty-refund", new
        {
            originalSaleId = original.Sale.Id, reason = "Gaseosa sin gas",
            lines = new object[] { new { saleLineId = sodaLine.Id, quantity = 1m, destination = "Discard" } },
        });
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        var warranty = await ForSourceAsync(shop, refund.Refund.Id, "CUSTOMER_RETURN");
        (warranty.Status, warranty.FiscalNumber).ShouldBe(("ACCEPTED", (string?)"NC3"));
        h.Request(FakeDocumentKind.CreditNote, warranty.ReferenceCode!).GetProperty("payment_details")[0].GetProperty("payment_method_code").GetString()
            .ShouldBe("10");

        // Nada se duplicó en Factus.
        h.Factus.Documents.Count(d => d.Kind == FakeDocumentKind.Bill).ShouldBe(3);
        h.Factus.Documents.Count(d => d.Kind == FakeDocumentKind.CreditNote).ShouldBe(3);
        h.Factus.Documents.ShouldAllBe(d => d.State == FakeDocumentState.Validated);
    }

    [Fact]
    public async Task Sin_Internet_queda_en_contingencia_y_se_envia_al_volver_sin_duplicados()
    {
        await using var h = await Harness.CreateAsync();
        var shop = h.Shop;
        await h.EnableAsync();
        var factusUrl = h.Factory.Connection.BaseUrl;

        // Sin Internet (nadie escucha en ese puerto): la venta sigue y el documento queda en contingencia.
        h.Factory.Connection.BaseUrl = new Uri("http://127.0.0.1:1/");
        var first = await SellAsync(shop);
        var second = await SellAsync(shop);
        first.Sale.Status.ShouldBe("COMPLETED");
        var run = await ProcessAsync(shop);
        (run.Processed, run.Accepted, run.StoppedBecause).ShouldBe((1, 0, (string?)"CONTINGENCY"));
        (await ForSourceAsync(shop, first.Sale.Id)).Status.ShouldBe("CONTINGENCY");
        (await ForSourceAsync(shop, second.Sale.Id)).Status.ShouldBe("PENDING");
        h.Factus.Documents.ShouldBeEmpty();

        // Vuelve la conexión: salen en orden y sin duplicados.
        h.Factory.Connection.BaseUrl = factusUrl;
        await DueNowAsync(shop);
        (await ProcessAsync(shop)).Accepted.ShouldBe(2);
        (await ForSourceAsync(shop, first.Sale.Id)).FiscalNumber.ShouldBe("SETP990000001");
        (await ForSourceAsync(shop, second.Sale.Id)).FiscalNumber.ShouldBe("SETP990000002");

        // La respuesta se perdió (el envío quedó "en curso"): al retomarlo se CONSULTA Factus por reference_code y no se reenvía.
        var document = await ForSourceAsync(shop, first.Sale.Id);
        await shop.Catalog.ExecuteAsync(
            $"UPDATE billing.fiscal_documents SET status = 'SUBMITTING', next_attempt_at = now() - interval '1 minute' WHERE id = '{document.Id}'");
        var validations = h.Validations("bills");
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        h.Validations("bills").ShouldBe(validations);
        h.Factus.Requests.ShouldContain(r => r.PathAndQuery == $"v2/bills?filter[reference_code]={first.Sale.Id:N}");
        (await DocumentAsync(shop, document.Id)).ShouldSatisfyAllConditions(d => d.Status.ShouldBe("ACCEPTED"), d => d.FiscalNumber.ShouldBe("SETP990000001"));
        h.Factus.Documents.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Rechazo_DIAN_queda_REJECTED_se_corrige_el_comprador_se_borra_en_Factus_y_se_reenvia()
    {
        await using var h = await Harness.CreateAsync();
        var shop = h.Shop;
        await h.EnableAsync();

        h.Factus.RejectNext("FAK24", "No está informado el DV del NIT");
        var sale = await SellAsync(shop);
        (await ProcessAsync(shop)).Accepted.ShouldBe(0);
        var rejected = await ForSourceAsync(shop, sale.Sale.Id);
        (rejected.Status, rejected.NextAttemptAt, rejected.ProviderStatus).ShouldBe(("REJECTED", (DateTimeOffset?)null, (string?)"REJECTED"));
        rejected.RejectionMessage!.ShouldContain("FAK24");
        rejected.RejectionMessage!.ShouldContain("No está informado el DV del NIT");
        (await shop.Catalog.ScalarAsync<string>("SELECT severity FROM audit.audit_log WHERE action = 'FISCAL_DOCUMENT_REJECTED'")).ShouldBe("CRITICAL");

        // El supervisor corrige el comprador; al reenviar se elimina en Factus el no validado y se envía corregido.
        await SendAsync<FiscalDocumentDto>(shop.Supervisor, HttpMethod.Put, $"/api/v1/billing/documents/{rejected.Id}/buyer", new
        {
            name = "Tienda La 14 SAS", identificationType = "NIT", identificationNumber = "901234567", checkDigit = "1", email = "compras@la14.co",
            personType = "LEGAL", taxRegime = "48", responsibilities = GreatTaxpayer, municipalityCode = "05001",
        });
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        var accepted = await DocumentAsync(shop, rejected.Id);
        (accepted.Status, accepted.FiscalNumber).ShouldBe(("ACCEPTED", (string?)"SETP990000002"));
        h.Factus.Requests.ShouldContain(r => r.Method == "DELETE" && r.PathAndQuery == $"v2/bills/destroy/reference/{sale.Sale.Id:N}");
        var customer = h.Request(FakeDocumentKind.Bill, sale.Sale.Id.ToString("N")).GetProperty("customer");
        (customer.GetProperty("identification_document_code").GetString(), customer.GetProperty("identification").GetString(), customer.GetProperty("dv").GetString(),
            customer.GetProperty("company").GetString(), customer.GetProperty("tribute_code").GetString()).ShouldBe(("31", "901234567", "1", "Tienda La 14 SAS", "01"));
        h.Factus.Documents.ShouldHaveSingleItem().State.ShouldBe(FakeDocumentState.Validated);

        // Ya no bloquea: la siguiente venta sale sin más.
        await SellAsync(shop);
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
    }

    [Fact]
    public async Task Rangos_sincronizados_desde_Factus_y_el_429_se_respeta()
    {
        await using var h = await Harness.CreateAsync();
        var shop = h.Shop;
        var ranges = await h.EnableAsync();
        var invoiceRange = ranges.Single(r => r.DocumentType == "INVOICE_ELECTRONIC");
        (invoiceRange.ProviderRangeId, invoiceRange.From, invoiceRange.To, invoiceRange.Current, invoiceRange.ResolutionNumber, invoiceRange.IsUsable)
            .ShouldBe(("8", 990_000_000L, 995_000_000L, 990_000_000L, (string?)"18760000001", true));
        (invoiceRange.ValidFrom, invoiceRange.ValidTo).ShouldBe(((DateOnly?)new DateOnly(2026, 1, 1), (DateOnly?)new DateOnly(2027, 12, 31)));

        // Un rango nuevo en Factus y uno que se desactiva: la resincronización lo refleja.
        h.Factus.NumberingRanges.Add(new FakeNumberingRange { Id = 21, Document = "21", Prefix = "FE2", From = 1, To = 1_000 });
        h.Factus.NumberingRanges.Single(r => r.Id == 10).IsActive = false;
        var synced = await PostAsync<List<FiscalRangeDto>>(shop.Owner, "/api/v1/billing/ranges/sync");
        synced.Single(r => r.ProviderRangeId == "21").ShouldSatisfyAllConditions(r => r.Prefix.ShouldBe("FE2"), r => r.BranchId.ShouldBeNull());
        synced.Single(r => r.ProviderRangeId == "10").IsActive.ShouldBeFalse();

        // 429 con Retry-After: el documento queda con error y la cola NO envía nada más hasta que pase la espera.
        h.Factus.EnqueueFault(FakeFault.TooManyRequests(TimeSpan.FromSeconds(2)));
        var first = await SellAsync(shop);
        var second = await SellAsync(shop);
        var run = await ProcessAsync(shop);
        (run.Processed, run.Accepted, run.StoppedBecause).ShouldBe((1, 0, (string?)"RATE_LIMIT"));
        var throttled = await ForSourceAsync(shop, first.Sale.Id);
        (throttled.Status, throttled.Events[^1].ProviderCode).ShouldBe(("ERROR", (string?)"429"));
        (await ForSourceAsync(shop, second.Sale.Id)).Status.ShouldBe("PENDING");
        var requests = h.Factus.Requests.Count;
        (await ProcessAsync(shop)).ShouldSatisfyAllConditions(r => r.Processed.ShouldBe(0), r => r.StoppedBecause.ShouldBe("RATE_LIMIT"));
        h.Factus.Requests.Count.ShouldBe(requests);

        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
        await DueNowAsync(shop);
        (await ProcessAsync(shop)).Accepted.ShouldBe(2);
        h.Factus.Documents.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Documento_soporte_de_una_compra_y_nota_de_ajuste_al_anularla()
    {
        await using var h = await Harness.CreateAsync(openSession: false);
        var shop = h.Shop;
        var farmer = await shop.Purchasing.CreatePartyAsync(new
        {
            personType = "Natural", identificationType = "CC", identificationNumber = "71234567", firstNames = "Pedro", lastNames = "Campesino",
            taxRegime = "49", fiscalResponsibilities = NotResponsible,
        });
        var created = await shop.Owner.PostAsJsonAsync("/api/v1/purchasing/suppliers",
            new { partyId = farmer.Id, supplier = new { code = "CAMPO", paymentTermDays = 0, issuesInvoices = false } }, Json, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var supplierId = (await created.Content.ReadFromJsonAsync<SupplierDto>(Json, Ct))!.Id;
        await h.EnableAsync();

        // Compra a un proveedor no obligado a facturar → documento soporte aceptado.
        var purchase = await BuyFromAsync(shop, supplierId, "SF-2");
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        var support = await ForSourceAsync(shop, purchase.Id, "PURCHASE");
        (support.DocumentType, support.Status, support.FiscalNumber).ShouldBe(("SUPPORT_DOCUMENT", "ACCEPTED", (string?)"DS1"));
        var provider = h.Request(FakeDocumentKind.SupportDocument, "DS" + purchase.Id.ToString("N")).GetProperty("provider");
        (provider.GetProperty("identification_document_code").GetString(), provider.GetProperty("identification").GetString(), provider.GetProperty("names").GetString())
            .ShouldBe(("31", "71234567", "Pedro Campesino"));

        // Anular la compra con el documento soporte aceptado → nota de ajuste (anulación) que lo referencia.
        await PostAsync<PurchaseDto>(shop.Owner, $"/api/v1/purchasing/purchases/{purchase.Id}/void", new { reason = "Mercancía devuelta al campesino" });
        var adjustment = await ForSourceAsync(shop, purchase.Id, "PURCHASE_VOID");
        (adjustment.DocumentType, adjustment.Status, adjustment.RelatedDocumentId, adjustment.ReferenceCode).ShouldBe(
            ("ADJUSTMENT_NOTE", "PENDING", (Guid?)support.Id, (string?)("NAS" + purchase.Id.ToString("N"))));
        (await ProcessAsync(shop)).Accepted.ShouldBe(1);
        (await DocumentAsync(shop, adjustment.Id)).FiscalNumber.ShouldBe("NA1");
        var note = h.Request(FakeDocumentKind.AdjustmentNote, adjustment.ReferenceCode!);
        (note.GetProperty("support_document_number").GetString(), note.GetProperty("correction_concept_code").GetString()).ShouldBe(("DS1", "2"));
        note.GetProperty("observation").GetString()!.ShouldStartWith("Mercancía devuelta al campesino");

        // Anular una compra cuyo documento soporte aún no se envió: se cancela y no hay nota de ajuste.
        var notSent = await BuyFromAsync(shop, supplierId, "SF-3");
        await PostAsync<PurchaseDto>(shop.Owner, $"/api/v1/purchasing/purchases/{notSent.Id}/void", new { reason = "Error de digitación" });
        (await ForSourceAsync(shop, notSent.Id, "PURCHASE")).Status.ShouldBe("CANCELLED");
        (await DocumentsAsync(shop, "?source=PURCHASE_VOID")).Count.ShouldBe(1);
        (await ProcessAsync(shop)).Sent.ShouldBe(0);
        h.Factus.Find(FakeDocumentKind.SupportDocument, "DS" + notSent.Id.ToString("N")).ShouldBeNull();
    }

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
