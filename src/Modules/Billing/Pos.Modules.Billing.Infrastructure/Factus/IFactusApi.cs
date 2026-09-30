namespace Pos.Modules.Billing.Infrastructure.Factus;

/// <summary>
/// Cliente de Factus API v2 (https://developers.factus.com.co/endpoints). Maneja el token OAuth2 en memoria (renovación con
/// refresh token y reintento único ante 401) y traduce las respuestas a resultados tipados; nunca lanza por errores HTTP ni
/// de red (solo por cancelación del llamador). No reintenta errores transitorios: eso lo hace la cola de envío (D11B-09).
/// </summary>
public interface IFactusApi
{
    /// <summary>Pide un token nuevo con usuario y clave (probar credenciales). POST <c>oauth/token</c> grant_type=password.</summary>
    Task<FactusQueryResult<FactusSession>> AuthenticateAsync(CancellationToken cancellationToken);

    /// <summary>POST <c>v2/bills/validate</c>.</summary>
    Task<FactusResult> CreateBillAsync(FactusBillRequest request, CancellationToken cancellationToken);

    /// <summary>POST <c>v2/credit-notes/validate</c>.</summary>
    Task<FactusResult> CreateCreditNoteAsync(FactusCreditNoteRequest request, CancellationToken cancellationToken);

    /// <summary>POST <c>v2/support-documents/validate</c>.</summary>
    Task<FactusResult> CreateSupportDocumentAsync(FactusSupportDocumentRequest request, CancellationToken cancellationToken);

    /// <summary>POST <c>v2/adjustment-notes/validate</c> (nota de ajuste al documento soporte).</summary>
    Task<FactusResult> CreateAdjustmentNoteAsync(FactusAdjustmentNoteRequest request, CancellationToken cancellationToken);

    /// <summary>GET <c>v2/numbering-ranges?filter[is_active]=1&amp;filter[document]=…</c>.</summary>
    Task<FactusQueryResult<IReadOnlyList<FactusNumberingRange>>> GetNumberingRangesAsync(
        bool onlyActive, string? documentCode, CancellationToken cancellationToken);

    /// <summary>GET <c>v2/{bills|credit-notes|support-documents}/:number</c>.</summary>
    Task<FactusQueryResult<FactusDocument>> GetDocumentAsync(FactusDocumentKind kind, string number, CancellationToken cancellationToken);

    /// <summary>GET <c>v2/{…}?filter[reference_code]=…</c>: primer documento con ese código de referencia.</summary>
    Task<FactusQueryResult<FactusDocument>> FindByReferenceAsync(FactusDocumentKind kind, string referenceCode, CancellationToken cancellationToken);

    /// <summary>GET <c>v2/{…}/:number/download-pdf</c> (Base64 → bytes).</summary>
    Task<FactusQueryResult<FactusFile>> DownloadPdfAsync(FactusDocumentKind kind, string number, CancellationToken cancellationToken);

    /// <summary>GET <c>v2/{…}/:number/download-xml</c> (Base64 → bytes).</summary>
    Task<FactusQueryResult<FactusFile>> DownloadXmlAsync(FactusDocumentKind kind, string number, CancellationToken cancellationToken);

    /// <summary>
    /// Elimina un documento NO validado (rechazado) para poder reenviarlo corregido:
    /// DELETE <c>v2/bills/destroy/reference/:reference_code</c>, <c>v2/credit-notes/reference/:reference_code</c> o
    /// <c>v2/support-documents/reference/:reference_code</c> (y, por analogía — SUPUESTO —, <c>v2/adjustment-notes/reference/:reference_code</c>).
    /// </summary>
    Task<FactusQueryResult<bool>> DeleteUnvalidatedAsync(FactusDocumentKind kind, string referenceCode, CancellationToken cancellationToken);
}
