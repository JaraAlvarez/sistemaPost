using Pos.SharedKernel.Results;

namespace Pos.Modules.Billing.Domain;

/// <summary>Renglón guardado del documento de origen (venta o compra) con sus impuestos por tarifa, incluida la bolsa.</summary>
public sealed record FiscalSourceLine(
    Guid LineId,
    int LineNo,
    string Code,
    string Name,
    string UnitCode,
    decimal Quantity,
    decimal UnitPrice,
    bool PriceIncludesTax,
    decimal Gross,
    decimal Discount,
    decimal TaxBase,
    decimal Total,
    IReadOnlyList<FiscalTax> Taxes);

/// <summary>Venta guardada (Fase 7) con el snapshot fiscal del comprador (Fase 8).</summary>
public sealed record FiscalSaleSnapshot(
    Guid SaleId,
    string Number,
    DateOnly BusinessDate,
    DateTimeOffset CompletedAt,
    FiscalParty Customer,
    IReadOnlyList<FiscalSourceLine> Lines,
    IReadOnlyList<FiscalPayment> Payments,
    decimal RoundingAdjustment,
    decimal Total,
    string? VoidReason);

/// <summary>Unidades devueltas de un renglón de la venta (cambio de mercancía o reintegro por garantía) y su valor.</summary>
public sealed record FiscalReturnLine(Guid SaleLineId, decimal Quantity, decimal CreditAmount);

/// <summary>Cambio de mercancía o reintegro por garantía guardado.</summary>
public sealed record FiscalReturnSnapshot(
    Guid ReturnId, string Number, string Kind, string Reason, DateOnly BusinessDate, IReadOnlyList<FiscalReturnLine> Lines, IReadOnlyList<FiscalPayment> Payments);

/// <summary>Compra contabilizada a un proveedor no obligado a facturar.</summary>
public sealed record FiscalPurchaseSnapshot(
    Guid PurchaseId,
    string Number,
    string SupplierInvoiceNumber,
    DateOnly InvoiceDate,
    FiscalParty Supplier,
    IReadOnlyList<FiscalSourceLine> Lines,
    IReadOnlyList<FiscalPayment> Payments,
    string? VoidReason = null);

/// <summary>
/// Arma el modelo fiscal neutro desde el documento GUARDADO (D11B-06, RN-FE-03): la bolsa va como renglón con su valor, el INC y el
/// IVA como impuestos por tarifa, los medios de pago con su código DIAN y el redondeo del efectivo como ajuste. Antes de enviar
/// concilia los totales con lo cobrado; si no cuadran, el documento no sale (se alerta).
/// </summary>
public static class FiscalDraftBuilder
{
    public const string BagTaxKind = "BAG_CONSUMPTION";
    public const string BagTaxName = "Impuesto nacional al consumo de bolsas plásticas";

    public static readonly Error TotalsMismatch = Error.BusinessRule(
        "BILLING.TOTALS_MISMATCH", "El documento fiscal no concilia con lo cobrado en la venta.");

    public static readonly Error RelatedNotAccepted = Error.BusinessRule(
        "BILLING.RELATED_NOT_ACCEPTED", "La factura que corrige la nota crédito aún no ha sido aceptada.");

    public static FiscalHeader Header(
        FiscalDocument document, FiscalNumbering numbering, FiscalParty issuer, FiscalEstablishment establishment, string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new FiscalHeader(
            document.Id, document.ReferenceCode ?? FiscalReference.For(document.Source, document.SourceId), document.SourceNumber, numbering,
            document.BusinessDate, document.IssuedAt, issuer, establishment, notes, document.Attempts > 0);
    }

    /// <summary>
    /// Adquirente del documento: el snapshot de la venta con el nombre, la identificación y el correo del documento, y los datos fiscales
    /// corregidos si el supervisor los corrigió. <paramref name="fiscalCodeOf"/> traduce el tipo de identificación al código DIAN.
    /// </summary>
    public static FiscalParty Buyer(FiscalDocument document, FiscalParty original, Func<string, string?> fiscalCodeOf)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(fiscalCodeOf);
        var party = original with
        {
            Name = document.BuyerName,
            IdentificationType = document.BuyerIdentificationType,
            IdentificationFiscalCode = document.BuyerIdentificationType == original.IdentificationType
                ? original.IdentificationFiscalCode
                : fiscalCodeOf(document.BuyerIdentificationType) ?? original.IdentificationFiscalCode,
            IdentificationNumber = document.BuyerIdentification,
            Email = document.BuyerEmail ?? original.Email,
        };
        return document.BuyerFiscal is { } fiscal
            ? party with
            {
                PersonType = fiscal.PersonType, CheckDigit = fiscal.CheckDigit, TaxRegime = fiscal.TaxRegime, Responsibilities = fiscal.Responsibilities,
                Address = fiscal.Address ?? party.Address, MunicipalityCode = fiscal.MunicipalityCode ?? party.MunicipalityCode, Phone = fiscal.Phone ?? party.Phone,
            }
            : party;
    }

    /// <summary>Factura electrónica de la venta. Falla si los renglones más el redondeo no dan el total cobrado.</summary>
    public static Result<FiscalInvoiceDraft> Invoice(FiscalHeader header, FiscalParty customer, FiscalSaleSnapshot sale)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(sale);
        var lines = Lines(sale.Lines);
        var totals = Totals(lines, sale.RoundingAdjustment);
        return totals.Total != sale.Total
            ? Mismatch(totals.Total, sale.Total)
            : new FiscalInvoiceDraft(header, customer, lines, [.. sale.Payments.Where(p => p.Amount > 0m)], totals);
    }

    /// <summary>Nota crédito por anulación de la venta: todos sus renglones y medios de pago, concepto "anulación".</summary>
    public static Result<FiscalCreditNoteDraft> VoidCreditNote(
        FiscalHeader header, FiscalParty customer, FiscalDocumentReference invoice, FiscalSaleSnapshot sale)
    {
        ArgumentNullException.ThrowIfNull(sale);
        var draft = Invoice(header, customer, sale);
        return draft.IsFailure
            ? draft.Error
            : new FiscalCreditNoteDraft(
                header, customer, invoice, FiscalCorrectionConcept.Void, string.IsNullOrWhiteSpace(sale.VoidReason) ? "Anulación de la venta" : sale.VoidReason,
                draft.Value.Lines, draft.Value.Payments, draft.Value.Totals);
    }

    /// <summary>
    /// Nota crédito por cambio de mercancía o reintegro por garantía: las unidades devueltas al precio que el cliente pagó, con los
    /// impuestos proporcionales de cada renglón original (devolución parcial).
    /// </summary>
    public static Result<FiscalCreditNoteDraft> ReturnCreditNote(
        FiscalHeader header, FiscalParty customer, FiscalDocumentReference invoice, FiscalSaleSnapshot sale, FiscalReturnSnapshot customerReturn)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(customerReturn);
        var byId = sale.Lines.ToDictionary(l => l.LineId);
        var returned = new List<FiscalSourceLine>();
        foreach (var line in customerReturn.Lines)
        {
            if (!byId.TryGetValue(line.SaleLineId, out var original) || original.Quantity <= 0m || line.Quantity > original.Quantity)
            {
                return Error.BusinessRule("BILLING.RETURN_LINE_MISMATCH", "Un renglón devuelto no corresponde a la venta original.");
            }

            returned.Add(Prorate(original, line.Quantity, line.CreditAmount));
        }

        var lines = Lines(returned);
        var totals = Totals(lines, 0m);
        var expected = customerReturn.Lines.Sum(l => l.CreditAmount);
        return totals.Total != expected
            ? Mismatch(totals.Total, expected)
            : new FiscalCreditNoteDraft(
                header, customer, invoice, FiscalCorrectionConcept.PartialReturn, customerReturn.Reason, lines,
                [.. customerReturn.Payments.Where(p => p.Amount > 0m)], totals);
    }

    /// <summary>Documento soporte de la compra: renglones al costo con sus impuestos (el total es la suma de los renglones).</summary>
    public static FiscalSupportDocumentDraft SupportDocument(FiscalHeader header, FiscalPurchaseSnapshot purchase)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(purchase);
        var lines = Lines(purchase.Lines);
        return new FiscalSupportDocumentDraft(header, purchase.Supplier, purchase.SupplierInvoiceNumber, lines, purchase.Payments, Totals(lines, 0m));
    }

    /// <summary>
    /// Nota de ajuste por anulación de la compra: los mismos renglones y medios de pago del documento soporte aceptado, concepto
    /// "anulación" (tabla de motivos de las notas de ajuste, código 2).
    /// </summary>
    public static FiscalAdjustmentNoteDraft AdjustmentNote(FiscalHeader header, FiscalPurchaseSnapshot purchase, FiscalDocumentReference supportDocument)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        var support = SupportDocument(header, purchase);
        return new FiscalAdjustmentNoteDraft(
            header, support.Supplier, supportDocument, FiscalCorrectionConcept.Void,
            string.IsNullOrWhiteSpace(purchase.VoidReason) ? "Anulación de la compra" : purchase.VoidReason, support.Lines, support.Payments, support.Totals);
    }

    /// <summary>Referencia a la factura aceptada que corrige una nota crédito.</summary>
    public static Result<FiscalDocumentReference> Reference(FiscalDocument? invoice)
    {
        if (invoice is not { Status: FiscalStatus.Accepted, FiscalNumber: not null, Cufe: not null })
        {
            return RelatedNotAccepted;
        }

        return new FiscalDocumentReference(
            invoice.Id, invoice.ReferenceCode ?? FiscalReference.For(invoice.Source, invoice.SourceId), invoice.ProviderDocumentId, invoice.FiscalNumber,
            invoice.Cufe, invoice.BusinessDate);
    }

    /// <summary>Renglones fiscales: el impuesto a la bolsa sale del renglón y se vuelve un renglón propio con su valor.</summary>
    public static IReadOnlyList<FiscalLine> Lines(IEnumerable<FiscalSourceLine> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lines = new List<FiscalLine>();
        foreach (var line in source.OrderBy(l => l.LineNo))
        {
            var bag = line.Taxes.Where(t => t.Kind == BagTaxKind).ToList();
            var taxes = line.Taxes.Where(t => t.Kind != BagTaxKind).ToList();
            var bagAmount = bag.Sum(t => t.Amount);
            lines.Add(new FiscalLine(
                lines.Count + 1, FiscalLineKind.Product, line.Code, line.Name, line.UnitCode, line.Quantity, line.UnitPrice, line.PriceIncludesTax, line.Gross,
                line.Discount, line.TaxBase, taxes, line.Total - bagAmount));
            foreach (var tax in bag.Where(t => t.Amount > 0m))
            {
                var unit = tax.FixedAmount ?? (line.Quantity == 0m ? tax.Amount : Math.Round(tax.Amount / line.Quantity, 2));
                lines.Add(new FiscalLine(
                    lines.Count + 1, FiscalLineKind.BagTax, tax.Code, BagTaxName, line.UnitCode, line.Quantity, unit, true, tax.Amount, 0m, 0m, [], tax.Amount));
            }
        }

        return lines;
    }

    public static FiscalTotals Totals(IReadOnlyList<FiscalLine> lines, decimal rounding)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var products = lines.Where(l => l.Kind == FiscalLineKind.Product).ToList();
        var linesTotal = lines.Sum(l => l.Total);
        return new FiscalTotals(
            lines.Sum(l => l.Gross), lines.Sum(l => l.Discount), products.Sum(l => l.TaxBase), products.Sum(l => l.TaxTotal), linesTotal, rounding,
            linesTotal + rounding);
    }

    private static FiscalSourceLine Prorate(FiscalSourceLine line, decimal quantity, decimal creditAmount)
    {
        var ratio = quantity / line.Quantity;
        var taxes = line.Taxes
            .Select(t => t with { TaxBase = Math.Round(t.TaxBase * ratio, 2), Amount = Math.Round(t.Amount * ratio, 2) })
            .ToList();
        var nonBagTaxes = taxes.Where(t => t.Kind != BagTaxKind).Sum(t => t.Amount);
        var bag = taxes.Where(t => t.Kind == BagTaxKind).Sum(t => t.Amount);
        return line with
        {
            Quantity = quantity,
            Gross = Math.Round(line.Gross * ratio, 2),
            Discount = Math.Round(line.Discount * ratio, 2),
            TaxBase = creditAmount - bag - nonBagTaxes,
            Total = creditAmount,
            Taxes = taxes,
        };
    }

    private static Error Mismatch(decimal built, decimal charged) =>
        Error.BusinessRule(TotalsMismatch.Code, $"{TotalsMismatch.Message} Documento {built:N2}, cobrado {charged:N2}.");
}
