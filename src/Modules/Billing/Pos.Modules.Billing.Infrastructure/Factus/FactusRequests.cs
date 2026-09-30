using System.Text.Json.Serialization;

namespace Pos.Modules.Billing.Infrastructure.Factus;

// Cuerpos de petición de Factus API v2 (JSON snake_case; ver FactusJson).
//
// Fuentes (consultadas 2026-09-30):
//  - Factura:          https://developers.factus.com.co/facturas/crear-y-validar      (POST v2/bills/validate)
//                      https://developers.factus.com.co/skills/facturas-crear-y-validar.md
//  - Nota crédito:     https://developers.factus.com.co/notas-credito/crear-y-validar (POST v2/credit-notes/validate)
//  - Documento soporte: https://developers.factus.com.co/documentos-soporte/crear-validar (POST v2/support-documents/validate)
//  - Códigos:          https://developers.factus.com.co/tablas-de-referencia/tablas
//
// IMPORTANTE (v1 → v2): la API v1 usaba identificadores internos (identification_document_id, legal_organization_id,
// tribute_id, municipality_id, unit_measure_id, standard_code_id, tax_rate, is_excluded en el ítem). La v2 vigente usa
// CÓDIGOS DIAN (identification_document_code, legal_organization_code, tribute_code, municipality_code,
// unit_measure_code, standard_code) y un arreglo items[].taxes[] con {code, rate, is_excluded}. Aquí se modela la v2.
//
// Todos los valores numéricos viajan como TEXTO con máximo dos decimales ("10000.00"); FactusDecimalConverter los
// escribe así y rechaza (no redondea en silencio) un valor con más de dos decimales: el mapeo debe decidir el redondeo.

/// <summary>Factura electrónica de venta (documento "01", operación "10" estándar).</summary>
public sealed record FactusBillRequest
{
    /// <summary>Clave de idempotencia (D11B-02: el id del documento de origen). Obligatorio y único.</summary>
    public required string ReferenceCode { get; init; }

    /// <summary>Tipo de documento; por defecto "01" factura electrónica de venta.</summary>
    public string? Document { get; init; }

    /// <summary>Obligatorio si hay varios rangos activos (uno por sucursal/caja, D11B-05).</summary>
    public int? NumberingRangeId { get; init; }

    /// <summary>Por defecto "10" (estándar).</summary>
    public string? OperationType { get; init; }

    /// <summary>Hora de creación <c>HH:mm:ss</c> (opcional).</summary>
    public string? CreatedTime { get; init; }

    /// <summary>Por defecto true (solo producción envía correo; en sandbox está deshabilitado).</summary>
    public bool? SendEmail { get; init; }

    /// <summary>Máximo 500 caracteres según la página (la skill dice 250: se recomienda no pasar de 250).</summary>
    public string? Observation { get; init; }

    public required IReadOnlyList<FactusPaymentDetail> PaymentDetails { get; init; }

    /// <summary>Redondeo del efectivo: diferencia entre la suma de <c>payment_details</c> y el total (±500.00).</summary>
    [JsonConverter(typeof(FactusNullableDecimalConverter))]
    public decimal? CashRoundingAmount { get; init; }

    /// <summary>Datos de la sucursal cuando hay varios establecimientos bajo el mismo NIT (H5).</summary>
    public FactusEstablishment? Establishment { get; init; }

    public FactusBillingPeriod? BillingPeriod { get; init; }

    public FactusOrderReference? OrderReference { get; init; }

    public required FactusCustomer Customer { get; init; }

    public required IReadOnlyList<FactusItem> Items { get; init; }

    public IReadOnlyList<FactusAllowanceCharge>? AllowanceCharges { get; init; }
}

/// <summary>Nota crédito (D11B-07). <c>bill_number</c> es el número de la factura aceptada (ej. "SETP990000550").</summary>
public sealed record FactusCreditNoteRequest
{
    public required string ReferenceCode { get; init; }

    /// <summary>Concepto de corrección: "1" devolución parcial, "2" anulación, "3" rebaja/descuento, "4" ajuste de precio…</summary>
    public required string CorrectionConceptCode { get; init; }

    /// <summary>"20" con referencia a factura electrónica (por defecto) o "22" sin referencia.</summary>
    public string? CustomizationId { get; init; }

    /// <summary>Número de la factura; opcional solo con <c>customization_id</c> "22".</summary>
    public string? BillNumber { get; init; }

    public int? NumberingRangeId { get; init; }

    public string? Observation { get; init; }

    public required IReadOnlyList<FactusPaymentDetail> PaymentDetails { get; init; }

    public FactusEstablishment? Establishment { get; init; }

    /// <summary>Obligatorio con <c>customization_id</c> "22".</summary>
    public FactusBillingPeriod? BillingPeriod { get; init; }

    /// <summary>Si se omite, Factus toma el cliente de la factura referenciada.</summary>
    public FactusCustomer? Customer { get; init; }

    public required IReadOnlyList<FactusItem> Items { get; init; }

    public IReadOnlyList<FactusAllowanceCharge>? AllowanceCharges { get; init; }
}

/// <summary>Documento soporte en adquisiciones a no obligados a facturar (pregunta 5 de la propuesta).</summary>
public sealed record FactusSupportDocumentRequest
{
    public required string ReferenceCode { get; init; }

    public int? NumberingRangeId { get; init; }

    public string? CreatedTime { get; init; }

    public string? Observation { get; init; }

    public required IReadOnlyList<FactusPaymentDetail> PaymentDetails { get; init; }

    [JsonConverter(typeof(FactusNullableDecimalConverter))]
    public decimal? CashRoundingAmount { get; init; }

    public FactusEstablishment? Establishment { get; init; }

    public required FactusProvider Provider { get; init; }

    public required IReadOnlyList<FactusItem> Items { get; init; }
}

/// <summary>
/// Nota de ajuste al documento soporte: POST <c>v2/adjustment-notes/validate</c>
/// (https://developers.factus.com.co/notas-ajuste-documentos-soporte/descripcion-de-campos/). Motivos (tabla de referencia): "1"
/// devolución parcial, "2" anulación del documento soporte, "3" rebaja, "4" ajuste de precio, "5" otros. Rango con documento "25".
/// </summary>
public sealed record FactusAdjustmentNoteRequest
{
    public required string ReferenceCode { get; init; }

    public int? NumberingRangeId { get; init; }

    /// <summary>Número del documento soporte que se ajusta (ej. "SEDS984000004").</summary>
    public required string SupportDocumentNumber { get; init; }

    public required string CorrectionConceptCode { get; init; }

    public string? Observation { get; init; }

    public required IReadOnlyList<FactusPaymentDetail> PaymentDetails { get; init; }

    [JsonConverter(typeof(FactusNullableDecimalConverter))]
    public decimal? CashRoundingAmount { get; init; }

    public required FactusProvider Provider { get; init; }

    public required IReadOnlyList<FactusItem> Items { get; init; }
}

/// <summary>Medio de pago. Un objeto por cada medio usado en la venta.</summary>
public sealed record FactusPaymentDetail
{
    /// <summary>"1" contado, "2" crédito (exige <see cref="DueDate"/>).</summary>
    public required string PaymentForm { get; init; }

    /// <summary>"10" efectivo, "20" cheque, "42" consignación, "47" transferencia (también Nequi/Daviplata), "48" tarjeta crédito,
    /// "49" tarjeta débito, "71" bonos, "72" vales, "ZZZ" otro.</summary>
    public required string PaymentMethodCode { get; init; }

    public string? ReferenceCode { get; init; }

    [JsonConverter(typeof(FactusDecimalConverter))]
    public required decimal Amount { get; init; }

    /// <summary><c>YYYY-MM-DD</c>; solo con forma de pago "2".</summary>
    public string? DueDate { get; init; }
}

/// <summary>Adquiriente. Consumidor final: documento "13" con identificación "222222222222" (regla DIAN; supuesto a
/// confirmar en sandbox), organización "2", tributo "ZZ".</summary>
public sealed record FactusCustomer
{
    /// <summary>"13" cédula, "31" NIT, "22" cédula de extranjería, "41" pasaporte, "47" PEP, "48" PPT…</summary>
    public required string IdentificationDocumentCode { get; init; }

    /// <summary>Sin dígito de verificación ni guion.</summary>
    public required string Identification { get; init; }

    /// <summary>Solo para NIT; si se omite Factus lo calcula.</summary>
    public string? Dv { get; init; }

    /// <summary>Obligatorio en la factura: "1" persona jurídica, "2" persona natural.</summary>
    public string? LegalOrganizationCode { get; init; }

    /// <summary>"01" IVA o "ZZ" no aplica (por defecto).</summary>
    public string? TributeCode { get; init; }

    /// <summary>Por defecto ["R-99-PN"].</summary>
    public IReadOnlyList<string>? Responsibilities { get; init; }

    /// <summary>Obligatorio si es persona jurídica.</summary>
    public string? Company { get; init; }

    public string? TradeName { get; init; }

    /// <summary>Obligatorio si es persona natural.</summary>
    public string? Names { get; init; }

    public string? Address { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }

    /// <summary>Código de país ("CO").</summary>
    public string? CountryCode { get; init; }

    /// <summary>Código DIVIPOLA del municipio (solo Colombia).</summary>
    public string? MunicipalityCode { get; init; }
}

/// <summary>Proveedor del documento soporte.</summary>
public sealed record FactusProvider
{
    public required string IdentificationDocumentCode { get; init; }
    public required string Identification { get; init; }
    public string? Dv { get; init; }

    /// <summary>"1" jurídica, "2" natural. Lo documenta la nota de ajuste (en el documento soporte no aparece: se omite).</summary>
    public string? LegalOrganizationCode { get; init; }

    /// <summary>Razón social; obligatoria en la nota de ajuste si <see cref="LegalOrganizationCode"/> es "1".</summary>
    public string? Company { get; init; }

    public string? TradeName { get; init; }
    public required string Names { get; init; }
    public required string Address { get; init; }
    public required string CountryCode { get; init; }
    public string? MunicipalityCode { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
}

/// <summary>Sucursal. Si se envía, todos los campos son obligatorios.</summary>
public sealed record FactusEstablishment
{
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required string PhoneNumber { get; init; }
    public required string Email { get; init; }
    public required string MunicipalityCode { get; init; }
}

public sealed record FactusBillingPeriod
{
    public required string StartDate { get; init; }
    public string? StartTime { get; init; }
    public required string EndDate { get; init; }
    public string? EndTime { get; init; }
}

public sealed record FactusOrderReference
{
    public required string ReferenceCode { get; init; }
    public string? IssueDate { get; init; }
}

/// <summary>
/// Ítem. <c>price</c> es el precio unitario SIN impuestos ni descuentos. La bolsa plástica (impuesto de valor fijo) va como
/// un ítem propio con su valor (preguntas frecuentes de Factus). Factus calcula cada impuesto por separado con redondeo
/// bancario.
/// </summary>
public sealed record FactusItem
{
    public required string CodeReference { get; init; }
    public required string Name { get; init; }

    /// <summary>Máximo dos decimales (los pesables con 3 decimales deben resolverse en el mapeo).</summary>
    [JsonConverter(typeof(FactusDecimalConverter))]
    public required decimal Quantity { get; init; }

    /// <summary>Porcentaje de descuento; usar este o <see cref="DiscountAmount"/>, no ambos.</summary>
    [JsonConverter(typeof(FactusNullableDecimalConverter))]
    public decimal? DiscountRate { get; init; }

    [JsonConverter(typeof(FactusNullableDecimalConverter))]
    public decimal? DiscountAmount { get; init; }

    [JsonConverter(typeof(FactusDecimalConverter))]
    public required decimal Price { get; init; }

    /// <summary>"94" unidad (ejemplo oficial). Otros códigos: tabla de unidades de medida.</summary>
    public required string UnitMeasureCode { get; init; }

    /// <summary>"999" estándar de adopción del contribuyente.</summary>
    public required string StandardCode { get; init; }

    public string? Note { get; init; }

    public required IReadOnlyList<FactusItemTax> Taxes { get; init; }

    public IReadOnlyList<FactusWithholdingTax>? WithholdingTaxes { get; init; }
}

/// <summary>
/// Impuesto del ítem: <c>code</c> "01" IVA, "04" INC, "35" ultraprocesados. Exento: IVA "01" con tarifa 0. Excluido:
/// <c>is_excluded</c> = true (y, si el emisor no es responsable de IVA, todos los productos van excluidos).
/// </summary>
public sealed record FactusItemTax
{
    public required string Code { get; init; }

    [JsonConverter(typeof(FactusDecimalConverter))]
    public required decimal Rate { get; init; }

    public bool? IsExcluded { get; init; }
}

/// <summary>Autorretención del emisor (no retenciones que le hacen terceros).</summary>
public sealed record FactusWithholdingTax
{
    public required string Code { get; init; }

    [JsonConverter(typeof(FactusDecimalConverter))]
    public required decimal Rate { get; init; }
}

/// <summary>Descuento o recargo global de la factura.</summary>
public sealed record FactusAllowanceCharge
{
    public required string ConceptType { get; init; }
    public required bool IsSurcharge { get; init; }
    public required string Reason { get; init; }

    [JsonConverter(typeof(FactusDecimalConverter))]
    public required decimal BaseAmount { get; init; }

    [JsonConverter(typeof(FactusDecimalConverter))]
    public required decimal Amount { get; init; }
}

/// <summary>Códigos de uso frecuente de las tablas de referencia de Factus.</summary>
public static class FactusCodes
{
    public const string InvoiceDocument = "01";
    public const string StandardOperation = "10";

    public const string TaxIva = "01";
    public const string TaxInc = "04";
    public const string TaxUltraProcessed = "35";

    public const string IdCitizenship = "13";
    public const string IdNit = "31";
    public const string IdForeignerCard = "22";
    public const string IdPassport = "41";

    public const string LegalEntity = "1";
    public const string NaturalPerson = "2";

    public const string TributeNotApplicable = "ZZ";
    public const string TributeIva = "01";

    public const string PaymentFormCash = "1";
    public const string PaymentFormCredit = "2";

    public const string MethodCash = "10";
    public const string MethodCheck = "20";
    public const string MethodDeposit = "42";
    public const string MethodTransfer = "47";
    public const string MethodCreditCard = "48";
    public const string MethodDebitCard = "49";
    public const string MethodBonds = "71";
    public const string MethodVouchers = "72";
    public const string MethodOther = "ZZZ";

    /// <summary>"1" medio de pago no definido (compras y ventas a crédito sin medio).</summary>
    public const string MethodUndefined = "1";

    public const string UnitMeasureUnit = "94";
    public const string StandardContributor = "999";

    public const string CorrectionPartialReturn = "1";
    public const string CorrectionCancellation = "2";
    public const string CorrectionDiscount = "3";
    public const string CorrectionPriceAdjustment = "4";

    public const string CreditNoteWithInvoice = "20";
    public const string CreditNoteWithoutInvoice = "22";

    /// <summary>Códigos de documento de los rangos de numeración.</summary>
    public const string RangeInvoice = "21";
    public const string RangeCreditNote = "22";
    public const string RangeSupportDocument = "24";
    public const string RangeAdjustmentNote = "25";

    /// <summary>Motivo "2" de la nota de ajuste: anulación del documento soporte.</summary>
    public const string AdjustmentCancellation = "2";

    public const string CountryColombia = "CO";
}
