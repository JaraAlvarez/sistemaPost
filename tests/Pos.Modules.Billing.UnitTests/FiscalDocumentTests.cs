using Pos.Modules.Billing.Domain;

namespace Pos.Modules.Billing.UnitTests;

public class FiscalDocumentTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly Guid Terminal = Guid.NewGuid();
    private static readonly Guid Cashier = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 15, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateOnly Today = new(2026, 9, 29);

    private static FiscalIssue Issue(FiscalSource source = FiscalSource.Sale, Guid? related = null) =>
        new(source, Guid.NewGuid(), "C1-000045", Branch, Terminal, Today, "Consumidor final", "CC", "222222222222", null, 10_000m, 1_900m, 11_900m, related);

    private static FiscalDocument Internal() => FiscalDocument.Issue(Guid.NewGuid(), Company, Issue(), electronic: false, Now, Cashier, Guid.NewGuid);

    private static FiscalDocument Electronic(FiscalSource source = FiscalSource.Sale) =>
        FiscalDocument.Issue(Guid.NewGuid(), Company, Issue(source), electronic: true, Now, Cashier, Guid.NewGuid);

    [Fact]
    public void Comprobante_interno_no_se_envia_a_la_DIAN()
    {
        var issue = Issue();
        var document = FiscalDocument.Issue(Guid.NewGuid(), Company, issue, electronic: false, Now, Cashier, Guid.NewGuid);

        document.DocumentType.ShouldBe(FiscalDocumentType.InternalReceipt);
        document.Status.ShouldBe(FiscalStatus.NotRequired);
        document.IsElectronic.ShouldBeFalse();
        document.NextAttemptAt.ShouldBeNull();
        (document.CompanyId, document.BranchId, document.PosTerminalId, document.Source, document.SourceId, document.SourceNumber).ShouldBe(
            (Company, Branch, (Guid?)Terminal, FiscalSource.Sale, issue.SourceId, "C1-000045"));
        (document.BusinessDate, document.BuyerName, document.BuyerIdentificationType, document.BuyerIdentification, document.BuyerEmail).ShouldBe(
            (Today, "Consumidor final", "CC", "222222222222", (string?)null));
        (document.Subtotal, document.TaxTotal, document.Total, document.IssuedAt, document.RelatedDocumentId).ShouldBe((10_000m, 1_900m, 11_900m, Now, (Guid?)null));
        (document.Provider, document.FiscalNumber, document.Cufe, document.QrData, document.Attempts).ShouldBe(((string?)null, (string?)null, (string?)null, (string?)null, 0));
        document.AuditLabel.ShouldBe("Documento InternalReceipt de C1-000045");

        var issued = document.Events.Single();
        (issued.EventType, issued.Detail, issued.ProviderCode, issued.OccurredAt, issued.UserId).ShouldBe(
            ("ISSUED", (string?)"Comprobante interno.", (string?)null, Now, (Guid?)Cashier));
    }

    [Fact]
    public void Electronico_nace_pendiente_y_la_anulacion_o_el_cambio_emiten_nota_credito()
    {
        var sale = Electronic();
        (sale.DocumentType, sale.Status, sale.NextAttemptAt, sale.IsElectronic).ShouldBe((FiscalDocumentType.InvoiceElectronic, FiscalStatus.Pending, (DateTimeOffset?)Now, true));
        sale.ReferenceCode.ShouldBe(sale.SourceId.ToString("N"));
        sale.Events.Single().Detail.ShouldBe("Pendiente de envío al proveedor.");

        Electronic(FiscalSource.SaleVoid).DocumentType.ShouldBe(FiscalDocumentType.CreditNote);
        Electronic(FiscalSource.CustomerReturn).DocumentType.ShouldBe(FiscalDocumentType.CreditNote);

        var related = Guid.NewGuid();
        FiscalDocument.Issue(Guid.NewGuid(), Company, Issue(FiscalSource.SaleVoid, related), true, Now, null, Guid.NewGuid).RelatedDocumentId.ShouldBe(related);
        Should.Throw<ArgumentNullException>(() => FiscalDocument.Issue(Guid.NewGuid(), Company, null!, false, Now, null, Guid.NewGuid));
        Should.Throw<ArgumentNullException>(() => FiscalDocument.Issue(Guid.NewGuid(), Company, Issue(), false, Now, null, null!));
    }

    [Fact]
    public void Anular_el_comprobante_interno_o_el_electronico_aun_no_enviado()
    {
        var receipt = Internal();
        receipt.Void("Venta anulada: error de digitación", Now.AddMinutes(5), Cashier, Guid.NewGuid).ShouldBeTrue();
        receipt.Status.ShouldBe(FiscalStatus.Voided);
        receipt.NextAttemptAt.ShouldBeNull();
        var voided = receipt.Events[^1];
        (voided.EventType, voided.Detail, voided.OccurredAt, voided.UserId).ShouldBe(("VOIDED", (string?)"Venta anulada: error de digitación", Now.AddMinutes(5), (Guid?)Cashier));

        var pending = Electronic();
        pending.Void("Venta anulada", Now, Cashier, Guid.NewGuid).ShouldBeTrue();
        pending.NextAttemptAt.ShouldBeNull();
        (pending.Status, pending.Events[^1].EventType).ShouldBe((FiscalStatus.Cancelled, "CANCELLED"));
        pending.Void("Otra vez", Now, Cashier, Guid.NewGuid).ShouldBeTrue();
        pending.Events.Count(e => e.EventType == "CANCELLED").ShouldBe(1);
        Should.Throw<ArgumentNullException>(() => pending.Void("x", Now, null, null!));
    }

    [Theory]
    [InlineData(FiscalStatus.Accepted)]
    [InlineData(FiscalStatus.Submitting)]
    public void Un_electronico_aceptado_o_en_curso_no_se_anula_requiere_nota_credito(FiscalStatus status)
    {
        var document = Electronic();
        document.RecordAttempt(status, "SETP990000001", "cufe-123", "qr", "00", "Procesado", "FACTUS", Now, null, Guid.NewGuid);
        var events = document.Events.Count;

        document.Void("Venta anulada", Now, Cashier, Guid.NewGuid).ShouldBeFalse();

        document.Status.ShouldBe(status);
        document.Events.Count.ShouldBe(events);
    }

    [Fact]
    public void Registra_los_intentos_de_envio_al_proveedor()
    {
        var document = Electronic();
        var retryAt = Now.AddMinutes(2);

        document.RecordAttempt(FiscalStatus.Error, null, null, null, "503", "Proveedor no disponible", "FACTUS", Now, retryAt, Guid.NewGuid);
        (document.Attempts, document.Provider, document.Status, document.NextAttemptAt).ShouldBe((1, (string?)"FACTUS", FiscalStatus.Error, (DateTimeOffset?)retryAt));
        var attempt = document.Events[^1];
        (attempt.EventType, attempt.Detail, attempt.ProviderCode, attempt.UserId).ShouldBe(("ATTEMPT", (string?)"Proveedor no disponible", (string?)"503", (Guid?)null));

        document.RecordAttempt(FiscalStatus.Pending, null, null, null, null, null, "FACTUS", Now, retryAt, Guid.NewGuid);
        document.NextAttemptAt.ShouldBe(retryAt);

        document.RecordAttempt(FiscalStatus.Accepted, "SETP990000001", "cufe-123", "https://catalogo-vpfe.dian.gov.co/qr", "00", "Aceptado", "FACTUS", Now, retryAt, Guid.NewGuid);
        (document.Attempts, document.Status, document.NextAttemptAt).ShouldBe((3, FiscalStatus.Accepted, (DateTimeOffset?)null));
        (document.FiscalNumber, document.Cufe, document.QrData).ShouldBe(((string?)"SETP990000001", (string?)"cufe-123", (string?)"https://catalogo-vpfe.dian.gov.co/qr"));

        // Un intento posterior sin datos conserva los que ya tenía.
        document.RecordAttempt(FiscalStatus.Accepted, null, null, null, null, new string('x', 1_500), "FACTUS", Now, null, Guid.NewGuid);
        (document.FiscalNumber, document.Cufe, document.QrData).ShouldBe(((string?)"SETP990000001", (string?)"cufe-123", (string?)"https://catalogo-vpfe.dian.gov.co/qr"));
        document.Events[^1].Detail!.Length.ShouldBe(1_000);
        Should.Throw<ArgumentNullException>(() => document.RecordAttempt(FiscalStatus.Error, null, null, null, null, null, "FACTUS", Now, null, null!));
    }

    [Theory]
    [InlineData(FiscalStatus.Pending)]
    [InlineData(FiscalStatus.Error)]
    [InlineData(FiscalStatus.Rejected)]
    [InlineData(FiscalStatus.Contingency)]
    public void Reintento_manual_de_un_electronico_pendiente_con_error_o_rechazado(FiscalStatus status)
    {
        var document = Electronic();
        document.RecordAttempt(status, null, null, null, "99", "Falla", "FACTUS", Now, null, Guid.NewGuid);
        var retryAt = Now.AddHours(1);

        document.Retry(retryAt, Cashier, Guid.NewGuid).IsSuccess.ShouldBeTrue();

        (document.Status, document.NextAttemptAt).ShouldBe((FiscalStatus.Pending, (DateTimeOffset?)retryAt));
        var retry = document.Events[^1];
        (retry.EventType, retry.Detail, retry.UserId, retry.OccurredAt).ShouldBe(("RETRY_REQUESTED", (string?)null, (Guid?)Cashier, retryAt));
    }

    [Fact]
    public void No_se_reintenta_un_comprobante_interno_ni_uno_aceptado_o_anulado()
    {
        Internal().Retry(Now, Cashier, Guid.NewGuid).Error.ShouldBe(BillingErrors.NotRetryable);

        var accepted = Electronic();
        accepted.RecordAttempt(FiscalStatus.Accepted, "SETP1", "cufe", "qr", "00", null, "FACTUS", Now, null, Guid.NewGuid);
        accepted.Retry(Now, Cashier, Guid.NewGuid).Error.ShouldBe(BillingErrors.NotRetryable);

        var voided = Electronic();
        voided.Void("Venta anulada", Now, Cashier, Guid.NewGuid);
        voided.Retry(Now, Cashier, Guid.NewGuid).Error.ShouldBe(BillingErrors.NotRetryable);
        Should.Throw<ArgumentNullException>(() => voided.Retry(Now, Cashier, null!));
    }

    [Fact]
    public void Errores_con_codigo_estable()
    {
        BillingErrors.NotFound.Code.ShouldBe("BILLING.DOCUMENT_NOT_FOUND");
        BillingErrors.NotRetryable.Code.ShouldBe("BILLING.NOT_RETRYABLE");
        BillingErrors.InvalidStatus.Code.ShouldBe("BILLING.INVALID_STATUS");
    }
}
