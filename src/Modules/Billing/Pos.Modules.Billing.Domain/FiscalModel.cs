namespace Pos.Modules.Billing.Domain;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
// MODELO FISCAL NEUTRO (Fase 11-B, D11B-06). Lo que un adaptador (Factus u otro) necesita para emitir, construido
// DESDE EL DOCUMENTO GUARDADO (venta, anulación, cambio, garantía o compra), nunca desde el catálogo actual: la factura
// refleja exactamente lo cobrado. Los códigos son los internos del POS (tipo de identificación, régimen, medio de pago,
// tipo de impuesto); cada adaptador los traduce a los de su API, apoyándose en los códigos DIAN que ya trae el modelo.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Modo de emisión (D11B-01). <c>OFF</c>: comprobante interno como hasta hoy.</summary>
public enum BillingMode
{
    Off,

    /// <summary>Factura solo si el cliente la pide (transición: no cumple la obligación del POS electrónico).</summary>
    OnRequest,

    /// <summary>Factura electrónica en cada venta; consumidor final si no hay cliente (recomendado al encender).</summary>
    EverySale,
}

/// <summary>Ambiente del proveedor.</summary>
public enum FiscalEnvironment
{
    Sandbox,
    Production,
}

/// <summary>Resultado de una llamada al proveedor.</summary>
public enum FiscalOutcome
{
    /// <summary>Validado por la DIAN: trae número, CUFE/CUDE y QR.</summary>
    Accepted,

    /// <summary>Rechazo de la DIAN o del proveedor por los datos (no se arregla reintentando igual).</summary>
    Rejected,

    /// <summary>Sin Internet o proveedor caído: el documento queda en contingencia y se envía al volver la conexión.</summary>
    Unavailable,

    /// <summary>Error transitorio (límite de ritmo, error 5xx, token vencido…): se reintenta con espera creciente.</summary>
    Failed,

    /// <summary>No hay proveedor o credenciales configuradas: el documento sigue pendiente.</summary>
    NotConfigured,

    /// <summary>Consulta de estado: el proveedor no conoce ese código de referencia (se puede enviar).</summary>
    NotFound,
}

/// <summary>Concepto de corrección de una nota crédito (tabla DIAN 13.2.4).</summary>
public enum FiscalCorrectionConcept
{
    /// <summary>1 · Devolución parcial de los bienes (cambio de mercancía, reintegro por garantía).</summary>
    PartialReturn,

    /// <summary>2 · Anulación de la factura electrónica (venta anulada).</summary>
    Void,
}

/// <summary>Clase de renglón del documento.</summary>
public enum FiscalLineKind
{
    Product,

    /// <summary>Impuesto nacional al consumo de bolsas plásticas: va como ítem con su valor (H6).</summary>
    BagTax,
}

/// <summary>Credenciales del proveedor. Nunca se registran: <see cref="ToString"/> las oculta.</summary>
public sealed record FiscalCredentials(string Username, string Password, string ClientId, string ClientSecret)
{
    public override string ToString() => $"FiscalCredentials {{ Username = {Username}, Password = ***, ClientId = ***, ClientSecret = *** }}";
}

/// <summary>Ambiente y credenciales con que el adaptador se conecta (se descifran solo para la llamada).</summary>
public sealed record FiscalConnection(FiscalEnvironment Environment, FiscalCredentials Credentials);

/// <summary>
/// Tercero del documento: el emisor (la empresa), el adquirente (cliente o consumidor final) o el proveedor del documento soporte.
/// <c>IdentificationType</c> es el código interno (CC, NIT, CE…) y <c>IdentificationFiscalCode</c> el de la DIAN (13, 31, 22…).
/// </summary>
public sealed record FiscalParty(
    string IdentificationType,
    string IdentificationFiscalCode,
    string IdentificationNumber,
    string? CheckDigit,
    string Name,
    string PersonType,
    string TaxRegime,
    IReadOnlyList<string> Responsibilities,
    string? Address,
    string? MunicipalityCode,
    string? Email,
    string? Phone)
{
    /// <summary>Identificación genérica del consumidor final ante la DIAN.</summary>
    public const string FinalConsumerIdentification = "222222222222";

    public bool IsFinalConsumer => IdentificationNumber == FinalConsumerIdentification;
}

/// <summary>Sucursal (establecimiento) y caja que emiten.</summary>
public sealed record FiscalEstablishment(
    Guid BranchId, string Code, string Name, string Address, string MunicipalityCode, string? Phone, string? Email, Guid? PosTerminalId, string? TerminalCode);

/// <summary>Rango de numeración con que se emite (el número lo asigna el proveedor, ADR-0013).</summary>
public sealed record FiscalNumbering(Guid RangeId, string ProviderRangeId, string Prefix, string? ResolutionNumber);

/// <summary>
/// Impuesto de un renglón por tarifa. <c>Kind</c>: VAT, CONSUMPTION (INC), SUGARY_DRINKS, ULTRA_PROCESSED_FOOD u OTHER (la bolsa va como
/// renglón <see cref="FiscalLineKind.BagTax"/>). IVA exento (tarifa 0 con derecho a devolución) ≠ excluido (no causa IVA).
/// </summary>
public sealed record FiscalTax(string Code, string Kind, decimal? Rate, decimal? FixedAmount, decimal TaxBase, decimal Amount, bool IsExempt, bool IsExcluded);

/// <summary>
/// Renglón con lo cobrado: <c>UnitPrice</c> como se vendió (<c>PriceIncludesTax</c>), bruto, descuentos (promoción + manual + parte del
/// descuento global), base gravable, impuestos y total del renglón.
/// </summary>
public sealed record FiscalLine(
    int LineNo,
    FiscalLineKind Kind,
    string Code,
    string Name,
    string UnitCode,
    decimal Quantity,
    decimal UnitPrice,
    bool PriceIncludesTax,
    decimal Gross,
    decimal Discount,
    decimal TaxBase,
    IReadOnlyList<FiscalTax> Taxes,
    decimal Total)
{
    public decimal TaxTotal => Taxes.Sum(t => t.Amount);
}

/// <summary>Medio de pago con su código interno, su clase y su código DIAN (tabla 13.3.4.2) si lo tiene.</summary>
public sealed record FiscalPayment(string MethodCode, string MethodKind, string? DianCode, decimal Amount, string? Reference);

/// <summary>
/// Totales. <c>LinesTotal</c> = suma de los renglones; <c>RoundingAdjustment</c> = redondeo del efectivo (ajuste documentado, puede ser
/// negativo); <c>Total</c> = lo cobrado (<c>LinesTotal + RoundingAdjustment</c>).
/// </summary>
public sealed record FiscalTotals(decimal Gross, decimal Discount, decimal TaxBase, decimal TaxTotal, decimal LinesTotal, decimal RoundingAdjustment, decimal Total);

/// <summary>
/// Encabezado común. <c>ReferenceCode</c> es la llave de idempotencia ante el proveedor (id del origen). <c>Resubmission</c> indica que el
/// documento ya se había enviado antes (p. ej. corregido tras un rechazo): el adaptador decide si debe descartar el intento anterior.
/// </summary>
public sealed record FiscalHeader(
    Guid DocumentId,
    string ReferenceCode,
    string SourceNumber,
    FiscalNumbering Numbering,
    DateOnly IssueDate,
    DateTimeOffset IssuedAt,
    FiscalParty Issuer,
    FiscalEstablishment Establishment,
    string? Notes,
    bool Resubmission);

/// <summary>Factura electrónica de venta (cliente identificado o consumidor final).</summary>
public sealed record FiscalInvoiceDraft(
    FiscalHeader Header, FiscalParty Customer, IReadOnlyList<FiscalLine> Lines, IReadOnlyList<FiscalPayment> Payments, FiscalTotals Totals);

/// <summary>Factura a la que se refiere una nota crédito.</summary>
public sealed record FiscalDocumentReference(Guid DocumentId, string ReferenceCode, string? ProviderDocumentId, string FiscalNumber, string Cufe, DateOnly IssueDate);

/// <summary>Nota crédito: anulación (total) o devolución parcial (cambio de mercancía, garantía).</summary>
public sealed record FiscalCreditNoteDraft(
    FiscalHeader Header,
    FiscalParty Customer,
    FiscalDocumentReference Invoice,
    FiscalCorrectionConcept Concept,
    string Reason,
    IReadOnlyList<FiscalLine> Lines,
    IReadOnlyList<FiscalPayment> Payments,
    FiscalTotals Totals);

/// <summary>Documento soporte de una compra a un proveedor no obligado a facturar (el emisor es la empresa compradora).</summary>
public sealed record FiscalSupportDocumentDraft(
    FiscalHeader Header, FiscalParty Supplier, string SupplierInvoiceNumber, IReadOnlyList<FiscalLine> Lines, IReadOnlyList<FiscalPayment> Payments, FiscalTotals Totals);

/// <summary>
/// Nota de ajuste al documento soporte (anulación de una compra cuyo documento soporte ya fue aceptado): referencia el documento
/// soporte y repite sus renglones.
/// </summary>
public sealed record FiscalAdjustmentNoteDraft(
    FiscalHeader Header,
    FiscalParty Supplier,
    FiscalDocumentReference SupportDocument,
    FiscalCorrectionConcept Concept,
    string Reason,
    IReadOnlyList<FiscalLine> Lines,
    IReadOnlyList<FiscalPayment> Payments,
    FiscalTotals Totals);

/// <summary>
/// Respuesta del proveedor a un envío o a una consulta de estado. <c>RetryAfter</c>: espera que pidió el proveedor (HTTP 429 con
/// <c>Retry-After</c>): la cola no envía nada más a ese proveedor antes de ese tiempo.
/// </summary>
public sealed record FiscalProviderResult(
    FiscalOutcome Outcome,
    string? ProviderDocumentId = null,
    string? ProviderStatus = null,
    string? FiscalNumber = null,
    long? Consecutive = null,
    string? Cufe = null,
    string? QrData = null,
    string? PdfUrl = null,
    string? Code = null,
    string? Message = null,
    DateTimeOffset? ValidatedAt = null,
    TimeSpan? RetryAfter = null)
{
    public static FiscalProviderResult NotConfigured(string message) => new(FiscalOutcome.NotConfigured, Code: "NOT_CONFIGURED", Message: message);
}

/// <summary>Rango de numeración tal como lo informa el proveedor.</summary>
public sealed record FiscalProviderRange(
    string ProviderRangeId,
    FiscalDocumentType DocumentType,
    string Prefix,
    long From,
    long To,
    long Current,
    string? ResolutionNumber,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsActive);

/// <summary>Resultado de consultar los rangos del proveedor.</summary>
public sealed record FiscalRangeSyncResult(FiscalOutcome Outcome, IReadOnlyList<FiscalProviderRange> Ranges, string? Message = null);
