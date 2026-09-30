using System.Net;
using Pos.Modules.Sales.Application;
using Pos.Modules.Sales.Contracts;
using Pos.Printing;
using Pos.Server.IntegrationTests.Phase7;
using static Pos.Server.IntegrationTests.Phase11B.ElectronicBilling;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase11B;

/// <summary>
/// Tiquete con datos fiscales (D11B-03, criterio de la §13): la caja espera ⚙️ <c>billing.ticket_wait_seconds</c> DESPUÉS de confirmar la
/// venta; si llegan número, CUFE y QR los imprime, si no imprime "en proceso" con el número interno; la reimpresión los trae cuando
/// llegan; la anulación y el reintegro imprimen su nota crédito; en modo OFF el tiquete sigue como hoy. Con el proveedor simulado.
/// </summary>
public class FiscalTicketTests
{
    private static IEnumerable<string> Texts(TicketDocument ticket) => ticket.Elements.OfType<TextLine>().Select(t => t.Text);

    [Fact]
    public async Task En_modo_OFF_el_tiquete_queda_igual_que_hoy()
    {
        await using var factory = new WorkerBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();

        var receipt = await SellAsync(shop);
        (receipt.DocumentType, receipt.DocumentStatus, receipt.FiscalNumber, receipt.Cufe).ShouldBe(("INTERNAL_RECEIPT", "NOT_REQUIRED", (string?)null, (string?)null));
        Texts(receipt.Ticket).ShouldContain("Comprobante de venta. No es factura electrónica.");
        receipt.TicketText.ShouldNotContain("Factura electrónica");
        receipt.TicketText.ShouldNotContain("CUFE");
        receipt.Ticket.Elements.OfType<QrElement>().ShouldBeEmpty();
        receipt.Ticket.Elements.OfType<BarcodeElement>().Single().Data.ShouldBe(receipt.Sale.Number);
        factory.Fiscal.Submissions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Si_la_DIAN_valida_a_tiempo_el_tiquete_trae_numero_CUFE_y_QR_y_la_anulacion_su_nota_credito()
    {
        await using var factory = new WorkerBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await EnableAsync(shop, ticketWaitSeconds: 5);

        var receipt = await SellAsync(shop);
        var document = await ForSourceAsync(shop, receipt.Sale.Id);
        (receipt.DocumentType, receipt.DocumentStatus, receipt.FiscalDocumentId).ShouldBe(("INVOICE_ELECTRONIC", "ACCEPTED", (Guid?)document.Id));
        (receipt.FiscalNumber, receipt.Cufe).ShouldBe((document.FiscalNumber, document.Cufe));
        receipt.FiscalNumber.ShouldNotBeNull();
        Texts(receipt.Ticket).ShouldContain(SaleTicketBuilder.InvoiceTitle);
        Texts(receipt.Ticket).ShouldContain(document.Cufe!);
        Texts(receipt.Ticket).ShouldNotContain(SaleTicketBuilder.InProcessLegend);
        receipt.Ticket.Elements.OfType<ColumnsLine>().ShouldContain(c => c.Left == "No. factura" && c.Right == document.FiscalNumber);
        receipt.Ticket.Elements.OfType<QrElement>().Single().Data.ShouldBe(document.QrData);
        receipt.TicketText.ShouldNotContain("No es factura");
        receipt.TicketText.Split('\n').ShouldAllBe(line => line.Length <= 42);

        // La anulación de una factura aceptada imprime su nota crédito validada.
        var voided = await shop.AuthorizedAsync<SaleReceiptDto>(HttpMethod.Post, $"/api/v1/sales/{receipt.Sale.Id}/void", new { reason = "Cliente desistió" });
        var note = await ForSourceAsync(shop, receipt.Sale.Id, "SALE_VOID");
        (voided.DocumentType, voided.DocumentStatus, voided.FiscalDocumentId, voided.FiscalNumber).ShouldBe(("CREDIT_NOTE", "ACCEPTED", (Guid?)note.Id, note.FiscalNumber));
        voided.TicketText.ShouldContain("VENTA ANULADA");
        Texts(voided.Ticket).ShouldContain(SaleTicketBuilder.CreditNoteTitle);
        voided.Ticket.Elements.OfType<ColumnsLine>().ShouldContain(c => c.Left == "No. nota crédito" && c.Right == note.FiscalNumber);
        voided.Ticket.Elements.OfType<QrElement>().Select(q => q.Data).ShouldBe([document.QrData, note.QrData]);
    }

    [Fact]
    public async Task Sin_respuesta_a_tiempo_imprime_en_proceso_y_la_reimpresion_trae_los_datos_fiscales()
    {
        await using var factory = new WorkerBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await EnableAsync(shop, ticketWaitSeconds: 1);
        factory.Fiscal.Offline = true;

        // La venta nunca falla ni espera más de ⚙️ 1 s: sale "en proceso" con el número interno.
        var started = DateTimeOffset.UtcNow;
        var receipt = await SellAsync(shop);
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(6));
        receipt.Sale.Status.ShouldBe("COMPLETED");
        (receipt.DocumentType, receipt.FiscalNumber, receipt.Cufe).ShouldBe(("INVOICE_ELECTRONIC", (string?)null, (string?)null));
        receipt.DocumentStatus.ShouldBeOneOf("PENDING", "SUBMITTING", "CONTINGENCY");
        Texts(receipt.Ticket).ShouldContain(SaleTicketBuilder.InvoiceTitle);
        Texts(receipt.Ticket).ShouldContain(SaleTicketBuilder.InProcessLegend);
        receipt.Ticket.Elements.OfType<ColumnsLine>().ShouldContain(c => c.Left == "No. interno" && c.Right == receipt.Sale.Number);
        receipt.Ticket.Elements.OfType<QrElement>().ShouldBeEmpty();
        receipt.TicketText.Split('\n').ShouldAllBe(line => line.Length <= 42);

        // Aún sin conexión, la reimpresión sigue "en proceso".
        var copy = await PostAsync<SaleReceiptDto>(shop.Cashier, $"/api/v1/sales/{receipt.Sale.Id}/reprint");
        (copy.FiscalNumber, copy.TicketText.Contains("COPIA", StringComparison.Ordinal)).ShouldBe(((string?)null, true));

        // Vuelve Internet: el documento sale y la reimpresión trae número, CUFE y QR.
        factory.Fiscal.Offline = false;
        await DueNowAsync(shop);
        await ProcessAsync(shop);
        var document = await ForSourceAsync(shop, receipt.Sale.Id);
        document.Status.ShouldBe("ACCEPTED");
        var reprinted = await PostAsync<SaleReceiptDto>(shop.Cashier, $"/api/v1/sales/{receipt.Sale.Id}/reprint");
        (reprinted.DocumentStatus, reprinted.FiscalNumber, reprinted.Cufe).ShouldBe(("ACCEPTED", document.FiscalNumber, document.Cufe));
        reprinted.TicketText.ShouldContain("COPIA");
        Texts(reprinted.Ticket).ShouldNotContain(SaleTicketBuilder.InProcessLegend);
        reprinted.Ticket.Elements.OfType<QrElement>().Single().Data.ShouldBe(document.QrData);
    }

    [Fact]
    public async Task El_cambio_y_el_reintegro_por_garantia_imprimen_su_nota_credito()
    {
        await using var factory = new WorkerBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await EnableAsync(shop, ticketWaitSeconds: 5);

        var receipt = await SellAsync(shop);
        receipt.DocumentStatus.ShouldBe("ACCEPTED");

        // Cambio de mercancía: el tiquete de la venta nueva trae su factura y la nota crédito de lo devuelto.
        var riceLine = receipt.Sale.Lines.Single(l => l.ProductId == shop.Rice);
        var body = new
        {
            originalSaleId = receipt.Sale.Id, reason = "Arroz de más",
            lines = new object[] { new { saleLineId = riceLine.Id, quantity = 1m, destination = "ReturnToStock" } },
        };
        var grant = await shop.GrantAsync(await RawAsync(shop.Cashier, HttpMethod.Post, "/api/v1/exchanges", body));
        var started = await SendAsync<ExchangeStartedDto>(shop.Cashier, HttpMethod.Post, "/api/v1/exchanges", body, HttpStatusCode.Created, grant: grant);
        await shop.AddAsync(started.Sale.Id, shop.Soda, 2);
        var replacement = await shop.CompleteAsync(started.Sale.Id, [Pay(shop.Cash, 2_000m)]);
        var exchangeNote = await ForSourceAsync(shop, started.Exchange.Id, "CUSTOMER_RETURN");
        (replacement.DocumentType, replacement.DocumentStatus).ShouldBe(("INVOICE_ELECTRONIC", "ACCEPTED"));
        exchangeNote.Status.ShouldBe("ACCEPTED");
        Texts(replacement.Ticket).ShouldContain(SaleTicketBuilder.InvoiceTitle);
        Texts(replacement.Ticket).ShouldContain(SaleTicketBuilder.CreditNoteTitle);
        replacement.Ticket.Elements.OfType<QrElement>().Select(q => q.Data).ShouldBe([(await ForSourceAsync(shop, started.Sale.Id)).QrData, exchangeNote.QrData]);

        // Reintegro por garantía (solo el propietario, desde la caja): su tiquete trae la nota crédito validada.
        var owner = factory.CreateClient();
        await Phase3.SecurityScenario.PosLoginAsync(owner, "100", "4826");
        var soda = receipt.Sale.Lines.Single(l => l.ProductId == shop.Soda);
        var refund = await PostAsync<RefundReceiptDto>(owner, "/api/v1/exchanges/warranty-refund", new
        {
            originalSaleId = receipt.Sale.Id, reason = "Gaseosa sin gas",
            lines = new object[] { new { saleLineId = soda.Id, quantity = 1m, destination = "Discard" } },
        });
        var note = await ForSourceAsync(shop, refund.Refund.Id, "CUSTOMER_RETURN");
        (refund.DocumentType, refund.DocumentStatus, refund.FiscalNumber, refund.Cufe).ShouldBe(("CREDIT_NOTE", "ACCEPTED", note.FiscalNumber, note.Cufe));
        Texts(refund.Ticket).ShouldContain(SaleTicketBuilder.CreditNoteTitle);
        refund.Ticket.Elements.OfType<QrElement>().Single().Data.ShouldBe(note.QrData);
        refund.TicketText.ShouldContain("REINTEGRO POR GARANTÍA");
    }

    [Fact]
    public async Task Sin_el_proceso_en_segundo_plano_la_venta_no_espera_mas_de_lo_configurado()
    {
        await using var factory = new ElectronicBillingServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.OpenSessionAsync();
        await EnableAsync(shop);

        var receipt = await SellAsync(shop);
        (receipt.DocumentStatus, receipt.FiscalNumber).ShouldBe(("PENDING", (string?)null));
        Texts(receipt.Ticket).ShouldContain(SaleTicketBuilder.InProcessLegend);
        (await RawAsync(shop.Cashier, HttpMethod.Post, $"/api/v1/sales/{receipt.Sale.Id}/reprint")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
