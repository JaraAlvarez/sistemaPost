using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Pos.Modules.Billing.Domain;

namespace Pos.Modules.Billing.Application;

/// <summary>
/// Proveedor de facturación electrónica (D7-12, Fase 11-B), NEUTRO: recibe el modelo fiscal armado desde el documento guardado y
/// devuelve un resultado neutro. Cambiar de proveedor es escribir otro adaptador (Factus es el primero). Reglas para un adaptador:
/// <list type="bullet">
/// <item>Idempotente por <c>Header.ReferenceCode</c>: reenviar el mismo código nunca crea otro documento.</item>
/// <item>Nunca lanza por fallas de red o del proveedor: devuelve <see cref="FiscalOutcome.Unavailable"/> (sin conexión, caído,
/// tiempo agotado) o <see cref="FiscalOutcome.Failed"/> (transitorio: 429, 5xx, token). <see cref="FiscalOutcome.Rejected"/> solo
/// para rechazos por los datos (reglas DIAN), con el mensaje legible en <c>Message</c>.</item>
/// <item>Nunca escribe las credenciales en registros ni en los mensajes.</item>
/// </list>
/// </summary>
public interface IFiscalProvider
{
    /// <summary>Nombre corto del adaptador (se guarda en cada documento): FACTUS, NONE, FAKE.</summary>
    string Name { get; }

    Task<FiscalProviderResult> SubmitInvoiceAsync(FiscalConnection connection, FiscalInvoiceDraft draft, CancellationToken cancellationToken);

    Task<FiscalProviderResult> SubmitCreditNoteAsync(FiscalConnection connection, FiscalCreditNoteDraft draft, CancellationToken cancellationToken);

    Task<FiscalProviderResult> SubmitSupportDocumentAsync(FiscalConnection connection, FiscalSupportDocumentDraft draft, CancellationToken cancellationToken);

    /// <summary>
    /// Estado de un documento ya enviado por su código de referencia (y el id del proveedor si se conoce). <see cref="FiscalOutcome.NotFound"/>
    /// si el proveedor no lo tiene (se puede enviar).
    /// </summary>
    Task<FiscalProviderResult> GetStatusAsync(
        FiscalConnection connection, FiscalDocumentType documentType, string referenceCode, string? providerDocumentId, CancellationToken cancellationToken);

    /// <summary>Rangos de numeración autorizados de la empresa en el proveedor.</summary>
    Task<FiscalRangeSyncResult> GetNumberingRangesAsync(FiscalConnection connection, CancellationToken cancellationToken);
}

/// <summary>Sin proveedor configurado: el documento sigue pendiente y se registra el intento.</summary>
public sealed class NullFiscalProvider : IFiscalProvider
{
    public const string NotConfiguredMessage = "No hay proveedor de facturación electrónica configurado (Fase 11-B).";

    public string Name => "NONE";

    public Task<FiscalProviderResult> SubmitInvoiceAsync(FiscalConnection connection, FiscalInvoiceDraft draft, CancellationToken cancellationToken) =>
        NotConfigured();

    public Task<FiscalProviderResult> SubmitCreditNoteAsync(FiscalConnection connection, FiscalCreditNoteDraft draft, CancellationToken cancellationToken) =>
        NotConfigured();

    public Task<FiscalProviderResult> SubmitSupportDocumentAsync(FiscalConnection connection, FiscalSupportDocumentDraft draft, CancellationToken cancellationToken) =>
        NotConfigured();

    public Task<FiscalProviderResult> GetStatusAsync(
        FiscalConnection connection, FiscalDocumentType documentType, string referenceCode, string? providerDocumentId, CancellationToken cancellationToken) =>
        NotConfigured();

    public Task<FiscalRangeSyncResult> GetNumberingRangesAsync(FiscalConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult(new FiscalRangeSyncResult(FiscalOutcome.NotConfigured, [], NotConfiguredMessage));

    private static Task<FiscalProviderResult> NotConfigured() => Task.FromResult(FiscalProviderResult.NotConfigured(NotConfiguredMessage));
}

/// <summary>
/// Proveedor SIMULADO para pruebas y desarrollo (<c>Pos:Billing:Provider = FAKE</c>, nunca en producción): numera por rango, genera
/// CUFE y QR, es idempotente por código de referencia como Factus y permite simular caídas (<see cref="Offline"/>), rechazos por
/// identificación (<see cref="RejectedIdentifications"/>) y rangos (<see cref="Ranges"/>).
/// </summary>
public sealed class FakeFiscalProvider : IFiscalProvider
{
    private readonly ConcurrentDictionary<string, FiscalProviderResult> _accepted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _consecutives = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<object> _submissions = new();
    private readonly Lock _gate = new();

    public string Name => "FAKE";

    /// <summary>Simula que no hay Internet o que el proveedor está caído.</summary>
    public bool Offline { get; set; }

    /// <summary>Identificaciones del adquirente que la "DIAN" rechaza (regla FAK24 simulada).</summary>
    public ConcurrentBag<string> RejectedIdentifications { get; } = [];

    /// <summary>Rangos que informa el proveedor.</summary>
    public List<FiscalProviderRange> Ranges { get; } =
    [
        new("FAKE-FV-1", FiscalDocumentType.InvoiceElectronic, "SETP", 990_000_001, 995_000_000, 990_000_000, "18760000001", null, null, true),
        new("FAKE-NC-1", FiscalDocumentType.CreditNote, "NC", 1, 100_000, 0, null, null, null, true),
        new("FAKE-DS-1", FiscalDocumentType.SupportDocument, "DS", 1, 100_000, 0, "18760000002", null, null, true),
    ];

    /// <summary>Última conexión recibida (para verificar ambiente y credenciales descifradas en las pruebas).</summary>
    public FiscalConnection? LastConnection { get; private set; }

    /// <summary>Todo lo enviado (incluidos los reenvíos idempotentes), en orden.</summary>
    public IReadOnlyList<object> Submissions => [.. _submissions];

    public IReadOnlyList<FiscalInvoiceDraft> Invoices => [.. _submissions.OfType<FiscalInvoiceDraft>()];

    public IReadOnlyList<FiscalCreditNoteDraft> CreditNotes => [.. _submissions.OfType<FiscalCreditNoteDraft>()];

    public IReadOnlyList<FiscalSupportDocumentDraft> SupportDocuments => [.. _submissions.OfType<FiscalSupportDocumentDraft>()];

    /// <summary>Documentos distintos creados en el "proveedor" (la idempotencia evita duplicados).</summary>
    public int DistinctDocuments => _accepted.Count;

    public Task<FiscalProviderResult> SubmitInvoiceAsync(FiscalConnection connection, FiscalInvoiceDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return Submit(connection, draft, draft.Header, draft.Customer.IdentificationNumber);
    }

    public Task<FiscalProviderResult> SubmitCreditNoteAsync(FiscalConnection connection, FiscalCreditNoteDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return Submit(connection, draft, draft.Header, draft.Customer.IdentificationNumber);
    }

    public Task<FiscalProviderResult> SubmitSupportDocumentAsync(FiscalConnection connection, FiscalSupportDocumentDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return Submit(connection, draft, draft.Header, draft.Supplier.IdentificationNumber);
    }

    public Task<FiscalProviderResult> GetStatusAsync(
        FiscalConnection connection, FiscalDocumentType documentType, string referenceCode, string? providerDocumentId, CancellationToken cancellationToken)
    {
        LastConnection = connection;
        return Task.FromResult(
            Offline ? Unavailable()
            : _accepted.TryGetValue(referenceCode, out var existing) ? existing
            : new FiscalProviderResult(FiscalOutcome.NotFound, Code: "404", Message: "No existe."));
    }

    public Task<FiscalRangeSyncResult> GetNumberingRangesAsync(FiscalConnection connection, CancellationToken cancellationToken)
    {
        LastConnection = connection;
        lock (_gate)
        {
            return Task.FromResult(Offline
                ? new FiscalRangeSyncResult(FiscalOutcome.Unavailable, [], "Sin conexión con el proveedor (simulado).")
                : new FiscalRangeSyncResult(FiscalOutcome.Accepted, [.. Ranges.Select(r => r with { Current = Math.Max(r.Current, _consecutives.GetValueOrDefault(r.ProviderRangeId)) })]));
        }
    }

    private Task<FiscalProviderResult> Submit(FiscalConnection connection, object draft, FiscalHeader header, string buyerIdentification)
    {
        LastConnection = connection;
        if (Offline)
        {
            return Task.FromResult(Unavailable());
        }

        _submissions.Enqueue(draft);
        if (_accepted.TryGetValue(header.ReferenceCode, out var existing))
        {
            return Task.FromResult(existing);
        }

        if (RejectedIdentifications.Contains(buyerIdentification))
        {
            return Task.FromResult(new FiscalProviderResult(
                FiscalOutcome.Rejected, ProviderStatus: "REJECTED", Code: "FAK24",
                Message: $"Regla FAK24: el NIT o documento {buyerIdentification} del adquirente no es válido."));
        }

        lock (_gate)
        {
            var consecutive = _consecutives.AddOrUpdate(
                header.Numbering.ProviderRangeId,
                _ => (Ranges.FirstOrDefault(r => r.ProviderRangeId == header.Numbering.ProviderRangeId)?.Current ?? 0) + 1,
                (_, current) => current + 1);
            var cufe = Convert.ToHexStringLower(SHA384.HashData(Encoding.UTF8.GetBytes(header.ReferenceCode)));
            var result = new FiscalProviderResult(
                FiscalOutcome.Accepted, ProviderDocumentId: $"fake-{consecutive}", ProviderStatus: "VALIDATED",
                FiscalNumber: $"{header.Numbering.Prefix}{consecutive}", Consecutive: consecutive, Cufe: cufe,
                QrData: $"https://catalogo-vpfe-hab.dian.gov.co/document/searchqr?documentkey={cufe}", PdfUrl: $"https://fake.test/pdf/{header.ReferenceCode}",
                Code: "200", Message: "Documento validado por la DIAN (simulado).", ValidatedAt: header.IssuedAt);
            _accepted[header.ReferenceCode] = result;
            return Task.FromResult(result);
        }
    }

    private static FiscalProviderResult Unavailable() =>
        new(FiscalOutcome.Unavailable, Code: "NETWORK", Message: "Sin conexión con el proveedor (simulado).");
}
