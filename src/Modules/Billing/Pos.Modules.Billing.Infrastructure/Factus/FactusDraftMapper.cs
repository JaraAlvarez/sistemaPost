using System.Globalization;
using Pos.Modules.Billing.Domain;

namespace Pos.Modules.Billing.Infrastructure.Factus;

/// <summary>
/// Traduce el modelo fiscal NEUTRO (armado desde el documento guardado, D11B-06) a los cuerpos de Factus API v2. Es puro y
/// determinista: no llama a la red.
/// <para><b>Precio, base y descuento.</b> Factus recibe por ítem <c>price</c> = precio unitario SIN impuestos ni descuentos,
/// <c>quantity</c> y <c>discount_amount</c>, y calcula él mismo bruto = price × quantity, base = bruto − descuento y cada impuesto
/// por separado con redondeo bancario (H6). El POS guarda la BASE GRAVABLE exacta de cada renglón (con precio que incluye IVA la base
/// absorbe el redondeo): el mapeo garantiza que <c>price × quantity − discount_amount</c> dé EXACTAMENTE esa base, así la base que
/// ve la DIAN es la de los libros. El descuento se envía antes de impuestos (el guardado incluye IVA: se lleva a la base en
/// proporción).</para>
/// <para><b>Cantidades (pesables).</b> Factus admite máximo DOS decimales en la cantidad y en el precio, y el POS vende por peso con
/// tres (o cuatro) decimales. Nunca se redondea la cantidad (eso cambiaría la base y el total). Se elige, en este orden, la primera
/// representación EXACTA: (1) la cantidad tal cual si el precio unitario sin impuestos resulta exacto a 2 decimales; (2) la línea en
/// la UNIDAD MENOR (kg → g, L → mL, m → cm) — 1,235 kg = 1235 g — si el precio por gramo resulta exacto (lo normal en fruver y
/// carnes: excluidos, precio por kilo redondo); (3) con cantidad entera (p. ej. 3 unidades con IVA incluido) el precio unitario se
/// redondea HACIA ARRIBA al centavo y la diferencia (menos de un centavo por unidad) se suma al descuento del ítem; (4) si nada de lo
/// anterior es exacto, el ítem va con cantidad 1 (unidad "94") y precio = valor total de la línea, y la cantidad real, la unidad y el
/// precio unitario se informan en la nota del ítem. En todos los casos la base y el total del renglón quedan exactos.</para>
/// <para><b>Totales.</b> Si el IVA que calcula Factus sobre la base difiere en centavos del guardado (redondeo bancario vs. el del
/// POS), la diferencia entre lo pagado y el total de Factus viaja en <c>cash_rounding_amount</c> junto con el redondeo del efectivo
/// (máximo ±500). La nota crédito no admite <c>cash_rounding_amount</c>: sus medios de pago se ajustan al total de Factus.</para>
/// </summary>
public static class FactusDraftMapper
{
    public const string FinalConsumerNames = "Consumidor final";
    public const int CreditDueDays = 30;

    private const int MaxObservation = 250;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Tipos de identificación que Factus admite para el proveedor del documento soporte y la nota de ajuste.</summary>
    private static readonly HashSet<string> SupportIdentificationCodes = ["21", "22", "31", "41", "42", "47", "50"];

    /// <summary>Unidad del POS → código UN/ECE (R__ref__units_of_measure) y su unidad menor exacta con el factor.</summary>
    private static readonly Dictionary<string, UnitInfo> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UND"] = new("94", null, 1),
        ["KG"] = new("KGM", "GRM", 1000),
        ["G"] = new("GRM", null, 1),
        ["LB"] = new("LBR", null, 1),
        ["L"] = new("LTR", "MLT", 1000),
        ["ML"] = new("MLT", null, 1),
        ["GAL"] = new("GLL", null, 1),
        ["M"] = new("MTR", "CMT", 100),
        ["CM"] = new("CMT", null, 1),
    };

    // ─────────────────────────────── Documentos ───────────────────────────────

    public static FactusBillRequest ToBill(FiscalInvoiceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var items = Items(draft.Lines, supportDocument: false);
        var total = EstimatedTotal(items);
        var payments = Payments(draft.Payments, draft.Header.IssueDate, total);
        return new FactusBillRequest
        {
            ReferenceCode = draft.Header.ReferenceCode,
            Document = FactusCodes.InvoiceDocument,
            NumberingRangeId = RangeId(draft.Header.Numbering),
            OperationType = FactusCodes.StandardOperation,
            SendEmail = !draft.Customer.IsFinalConsumer && !string.IsNullOrWhiteSpace(draft.Customer.Email),
            Observation = Observation($"Venta {draft.Header.SourceNumber}", draft.Header.Notes),
            PaymentDetails = payments,
            CashRoundingAmount = CashRounding(payments, total),
            Establishment = Establishment(draft.Header.Establishment),
            Customer = Customer(draft.Customer),
            Items = items,
        };
    }

    /// <summary>
    /// Nota crédito que referencia la factura aceptada (<c>bill_number</c>, <c>customization_id</c> "20"). El adquirente se omite: Factus
    /// lo toma de la factura (así coincide aunque el supervisor lo haya corregido tras un rechazo).
    /// </summary>
    public static FactusCreditNoteRequest ToCreditNote(FiscalCreditNoteDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var items = Items(draft.Lines, supportDocument: false);
        var total = EstimatedTotal(items);
        return new FactusCreditNoteRequest
        {
            ReferenceCode = draft.Header.ReferenceCode,
            CorrectionConceptCode = draft.Concept == FiscalCorrectionConcept.Void ? FactusCodes.CorrectionCancellation : FactusCodes.CorrectionPartialReturn,
            CustomizationId = FactusCodes.CreditNoteWithInvoice,
            BillNumber = draft.Invoice.FiscalNumber,
            NumberingRangeId = RangeId(draft.Header.Numbering),
            Observation = Observation(draft.Reason, $"Corrige la factura {draft.Invoice.FiscalNumber}", draft.Header.SourceNumber),
            PaymentDetails = Balance(Payments(draft.Payments, draft.Header.IssueDate, total), total),
            Establishment = Establishment(draft.Header.Establishment),
            Items = items,
        };
    }

    /// <summary>Documento soporte de la compra: el proveedor no obligado a facturar y los renglones al costo (solo IVA, código "01").</summary>
    public static FactusSupportDocumentRequest ToSupportDocument(FiscalSupportDocumentDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var items = Items(draft.Lines, supportDocument: true);
        var total = EstimatedTotal(items);
        var payments = Payments(draft.Payments, draft.Header.IssueDate, total);
        return new FactusSupportDocumentRequest
        {
            ReferenceCode = draft.Header.ReferenceCode,
            NumberingRangeId = RangeId(draft.Header.Numbering),
            Observation = Observation($"Compra {draft.Header.SourceNumber}", $"Cuenta del proveedor {draft.SupplierInvoiceNumber}"),
            PaymentDetails = payments,
            CashRoundingAmount = CashRounding(payments, total),
            Establishment = Establishment(draft.Header.Establishment),
            Provider = Provider(draft.Supplier, withOrganization: false),
            Items = items,
        };
    }

    /// <summary>Nota de ajuste al documento soporte (anulación de la compra: motivo "2").</summary>
    public static FactusAdjustmentNoteRequest ToAdjustmentNote(FiscalAdjustmentNoteDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var items = Items(draft.Lines, supportDocument: true);
        var total = EstimatedTotal(items);
        var payments = Payments(draft.Payments, draft.Header.IssueDate, total);
        return new FactusAdjustmentNoteRequest
        {
            ReferenceCode = draft.Header.ReferenceCode,
            NumberingRangeId = RangeId(draft.Header.Numbering),
            SupportDocumentNumber = draft.SupportDocument.FiscalNumber,
            CorrectionConceptCode = draft.Concept == FiscalCorrectionConcept.Void ? FactusCodes.AdjustmentCancellation : FactusCodes.CorrectionPartialReturn,
            Observation = Observation(draft.Reason, $"Ajusta el documento soporte {draft.SupportDocument.FiscalNumber}", draft.Header.SourceNumber),
            PaymentDetails = payments,
            CashRoundingAmount = CashRounding(payments, total),
            Provider = Provider(draft.Supplier, withOrganization: true),
            Items = items,
        };
    }

    // ─────────────────────────────── Terceros ───────────────────────────────

    /// <summary>
    /// Adquirente. Consumidor final: cédula "13" con 222222222222, persona natural, tributo "ZZ" y responsabilidad R-99-PN (la DIAN lo
    /// admite así; confirmar en el sandbox). Cliente identificado: código DIAN de su tipo de identificación, NIT sin DV (el DV aparte),
    /// jurídica → <c>company</c>, natural → <c>names</c>, responsable de IVA (régimen 48) → tributo "01".
    /// </summary>
    public static FactusCustomer Customer(FiscalParty party)
    {
        ArgumentNullException.ThrowIfNull(party);
        if (party.IsFinalConsumer)
        {
            return new FactusCustomer
            {
                IdentificationDocumentCode = FactusCodes.IdCitizenship,
                Identification = FiscalParty.FinalConsumerIdentification,
                LegalOrganizationCode = FactusCodes.NaturalPerson,
                TributeCode = FactusCodes.TributeNotApplicable,
                Responsibilities = ["R-99-PN"],
                Names = string.IsNullOrWhiteSpace(party.Name) ? FinalConsumerNames : party.Name.Trim(),
                Email = Text(party.Email),
                CountryCode = FactusCodes.CountryColombia,
            };
        }

        var legal = IsLegal(party.PersonType);
        var code = Text(party.IdentificationFiscalCode) ?? FactusCodes.IdCitizenship;
        return new FactusCustomer
        {
            IdentificationDocumentCode = code,
            Identification = Identification(party.IdentificationNumber),
            Dv = code == FactusCodes.IdNit ? Text(party.CheckDigit) : null,
            LegalOrganizationCode = legal ? FactusCodes.LegalEntity : FactusCodes.NaturalPerson,
            TributeCode = party.TaxRegime == "48" ? FactusCodes.TributeIva : FactusCodes.TributeNotApplicable,
            Responsibilities = party.Responsibilities.Count > 0 ? party.Responsibilities : ["R-99-PN"],
            Company = legal ? party.Name.Trim() : null,
            Names = legal ? null : party.Name.Trim(),
            Address = Text(party.Address),
            Email = Text(party.Email),
            Phone = Text(party.Phone),
            CountryCode = FactusCodes.CountryColombia,
            MunicipalityCode = Text(party.MunicipalityCode),
        };
    }

    /// <summary>
    /// Proveedor del documento soporte. Factus solo admite NIT y documentos de extranjeros para el proveedor: una cédula (13) se envía
    /// como NIT (31) con el mismo número (el NIT de una persona natural es su cédula; Factus calcula el DV si no viene). Sin dirección
    /// registrada se envía "No informada" (el campo es obligatorio).
    /// </summary>
    public static FactusProvider Provider(FiscalParty party, bool withOrganization)
    {
        ArgumentNullException.ThrowIfNull(party);
        var code = SupportIdentificationCodes.Contains(party.IdentificationFiscalCode) ? party.IdentificationFiscalCode : FactusCodes.IdNit;
        var legal = IsLegal(party.PersonType);
        return new FactusProvider
        {
            IdentificationDocumentCode = code,
            Identification = Identification(party.IdentificationNumber),
            Dv = code == FactusCodes.IdNit ? Text(party.CheckDigit) : null,
            LegalOrganizationCode = withOrganization ? (legal ? FactusCodes.LegalEntity : FactusCodes.NaturalPerson) : null,
            Company = withOrganization && legal ? party.Name.Trim() : null,
            Names = party.Name.Trim(),
            Address = Text(party.Address) ?? "No informada",
            CountryCode = FactusCodes.CountryColombia,
            MunicipalityCode = Text(party.MunicipalityCode),
            Email = Text(party.Email),
            Phone = Text(party.Phone),
        };
    }

    /// <summary>Establecimiento (H5): se envía solo si la sucursal tiene todos los datos (si se envía, Factus los exige todos).</summary>
    public static FactusEstablishment? Establishment(FiscalEstablishment establishment)
    {
        ArgumentNullException.ThrowIfNull(establishment);
        return Text(establishment.Name) is { } name && Text(establishment.Address) is { } address && Text(establishment.Phone) is { } phone
               && Text(establishment.Email) is { } email && Text(establishment.MunicipalityCode) is { } municipality
            ? new FactusEstablishment { Name = name, Address = address, PhoneNumber = phone, Email = email, MunicipalityCode = municipality }
            : null;
    }

    // ─────────────────────────────── Pagos ───────────────────────────────

    /// <summary>
    /// Un objeto por medio de pago con su código DIAN (el del medio configurado en caja; si no tiene, por su clase). Crédito (fiado o
    /// compra a crédito): forma "2" con vencimiento a ⚙️ 30 días de la emisión. Sin medios (o todos en cero): uno "otro" por el total.
    /// </summary>
    public static IReadOnlyList<FactusPaymentDetail> Payments(IEnumerable<FiscalPayment> payments, DateOnly issueDate, decimal fallbackTotal)
    {
        ArgumentNullException.ThrowIfNull(payments);
        var result = payments.Where(p => p.Amount > 0m).Select(p => Payment(p, issueDate)).ToList();
        if (result.Count == 0)
        {
            result.Add(new FactusPaymentDetail
            {
                PaymentForm = FactusCodes.PaymentFormCash, PaymentMethodCode = FactusCodes.MethodOther, Amount = Money(Math.Max(0m, fallbackTotal)),
            });
        }

        return result;
    }

    public static FactusPaymentDetail Payment(FiscalPayment payment, DateOnly issueDate)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var credit = payment.MethodKind is "CREDIT" or "CUSTOMER_CREDIT";
        var method = Text(payment.DianCode) ?? payment.MethodKind switch
        {
            "CASH" => FactusCodes.MethodCash,
            "DEBIT_CARD" => FactusCodes.MethodDebitCard,
            "CREDIT_CARD" => FactusCodes.MethodCreditCard,
            "TRANSFER" or "WALLET" => FactusCodes.MethodTransfer,
            "VOUCHER" => FactusCodes.MethodVouchers,
            "CUSTOMER_CREDIT" or "CREDIT" => FactusCodes.MethodUndefined,
            _ => FactusCodes.MethodOther,
        };
        return new FactusPaymentDetail
        {
            PaymentForm = credit ? FactusCodes.PaymentFormCredit : FactusCodes.PaymentFormCash,
            PaymentMethodCode = method,
            ReferenceCode = Text(payment.Reference) is { } reference ? Truncate(reference, 60) : null,
            Amount = Money(payment.Amount),
            DueDate = credit ? issueDate.AddDays(CreditDueDays).ToString("yyyy-MM-dd", Invariant) : null,
        };
    }

    /// <summary>Lo pagado menos el total que calculará Factus (redondeo del efectivo + centavos del IVA); nulo si cuadra.</summary>
    public static decimal? CashRounding(IReadOnlyList<FactusPaymentDetail> payments, decimal factusTotal)
    {
        ArgumentNullException.ThrowIfNull(payments);
        var difference = Money(payments.Sum(p => p.Amount) - factusTotal);
        return difference == 0m ? null : difference;
    }

    /// <summary>Ajusta los medios de pago para que sumen el total (la nota crédito no admite <c>cash_rounding_amount</c>).</summary>
    public static IReadOnlyList<FactusPaymentDetail> Balance(IReadOnlyList<FactusPaymentDetail> payments, decimal total)
    {
        ArgumentNullException.ThrowIfNull(payments);
        var difference = Money(total - payments.Sum(p => p.Amount));
        if (difference == 0m || payments.Count == 0)
        {
            return payments;
        }

        // La diferencia va al efectivo (de donde sale el redondeo) o, si no hay, al medio de mayor valor.
        var target = payments.Select((p, i) => (p, i)).OrderByDescending(x => x.p.PaymentMethodCode == FactusCodes.MethodCash).ThenByDescending(x => x.p.Amount).First().i;
        return [.. payments.Select((p, i) => i == target ? p with { Amount = Math.Max(0m, p.Amount + difference) } : p)];
    }

    // ─────────────────────────────── Ítems ───────────────────────────────

    /// <summary>
    /// Ítems del documento. El producto "bolsa plástica" cuyo único valor es el impuesto (base y total en cero) no se envía: la bolsa ya
    /// va como ítem propio con su valor (renglón siguiente); un ítem en cero no aporta y Factus podría rechazarlo.
    /// </summary>
    public static IReadOnlyList<FactusItem> Items(IEnumerable<FiscalLine> lines, bool supportDocument)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var list = lines.ToList();
        return
        [
            .. list.Where((l, i) => !(l.Kind == FiscalLineKind.Product && l.Total == 0m && l.TaxBase == 0m && i + 1 < list.Count
                                      && list[i + 1].Kind == FiscalLineKind.BagTax))
                .Select(l => Item(l, supportDocument)),
        ];
    }

    /// <summary>Un ítem con base exacta (ver el resumen de la clase). La bolsa (renglón <see cref="FiscalLineKind.BagTax"/>) va como ítem con su valor.</summary>
    public static FactusItem Item(FiscalLine line, bool supportDocument)
    {
        ArgumentNullException.ThrowIfNull(line);
        var (taxes, folded) = Taxes(line, supportDocument);
        var bag = line.Kind == FiscalLineKind.BagTax;
        var taxBase = Money(bag ? line.Total : line.TaxBase + folded);
        var discount = bag ? 0m : PreTaxDiscount(line);
        var shape = Shape(line, taxBase + discount);
        var appliedDiscount = shape.Price * shape.Quantity - taxBase;
        return new FactusItem
        {
            CodeReference = Truncate(line.Code, 100),
            Name = Truncate(line.Name, 250),
            Quantity = shape.Quantity,
            Price = shape.Price,
            DiscountRate = appliedDiscount == 0m ? 0m : null,
            DiscountAmount = appliedDiscount == 0m ? null : appliedDiscount,
            UnitMeasureCode = shape.UnitCode,
            StandardCode = FactusCodes.StandardContributor,
            Note = shape.Note,
            Taxes = taxes,
        };
    }

    /// <summary>
    /// Impuestos del ítem por tarifa: IVA "01" (exento = tarifa 0; excluido = <c>is_excluded</c>), INC "04" y ultraprocesados "35". Los
    /// que Factus no recibe como tarifa (valor fijo por unidad como el de bebidas azucaradas, u otros códigos; y en el documento soporte
    /// todo lo que no sea IVA) se SUMAN a la base del ítem para que el total no cambie (<paramref name="supportDocument"/>). Sin impuestos
    /// (emisor no responsable de IVA): IVA excluido.
    /// </summary>
    public static (IReadOnlyList<FactusItemTax> Taxes, decimal Folded) Taxes(FiscalLine line, bool supportDocument)
    {
        ArgumentNullException.ThrowIfNull(line);
        var excluded = new FactusItemTax { Code = FactusCodes.TaxIva, Rate = 0m, IsExcluded = true };
        if (line.Kind == FiscalLineKind.BagTax)
        {
            return ([excluded], 0m);
        }

        var taxes = new List<FactusItemTax>();
        var folded = 0m;
        foreach (var tax in line.Taxes)
        {
            if (tax.IsExcluded)
            {
                if (!taxes.Contains(excluded))
                {
                    taxes.Add(excluded);
                }

                continue;
            }

            var code = TaxCode(tax);
            if (code is null || tax.Rate is null || (supportDocument && code != FactusCodes.TaxIva))
            {
                folded += tax.Amount;
                continue;
            }

            taxes.Add(new FactusItemTax { Code = code, Rate = Money(tax.Rate.Value) });
        }

        if (taxes.Count == 0)
        {
            taxes.Add(excluded);
        }

        return (taxes, folded);
    }

    /// <summary>Código de tributo de Factus para un impuesto del POS; <c>null</c> si Factus no lo recibe como tarifa.</summary>
    public static string? TaxCode(FiscalTax tax)
    {
        ArgumentNullException.ThrowIfNull(tax);
        return tax.Kind.ToUpperInvariant() switch
        {
            "VAT" or "IVA" => FactusCodes.TaxIva,
            "CONSUMPTION" or "INC" => FactusCodes.TaxInc,
            "ULTRA_PROCESSED_FOOD" => FactusCodes.TaxUltraProcessed,
            "OTHER" when tax.Code is FactusCodes.TaxIva or FactusCodes.TaxInc or FactusCodes.TaxUltraProcessed => tax.Code,
            _ => null,
        };
    }

    /// <summary>
    /// Descuento antes de impuestos. Precio sin impuestos: el guardado. Precio con impuestos: el guardado llevado a la base en
    /// proporción (base / neto); si el neto es cero (descuento del 100 %), todo el bruto sin impuestos.
    /// </summary>
    public static decimal PreTaxDiscount(FiscalLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Discount <= 0m)
        {
            return 0m;
        }

        if (!line.PriceIncludesTax)
        {
            return Money(line.Discount);
        }

        var net = line.Gross - line.Discount;
        if (net > 0m)
        {
            return Money(line.Discount * line.TaxBase / net);
        }

        var rates = line.Taxes.Where(t => t.Rate is not null && !t.IsExcluded).Sum(t => t.Rate!.Value);
        return Money(line.Gross / (1m + rates / 100m));
    }

    /// <summary>Cantidad, unidad y precio exactos para Factus (máximo 2 decimales) de un renglón de bruto sin impuestos <paramref name="gross"/>.</summary>
    public static ItemShape Shape(FiscalLine line, decimal gross)
    {
        ArgumentNullException.ThrowIfNull(line);
        var unit = Units.TryGetValue(line.UnitCode ?? string.Empty, out var known) ? known : new UnitInfo("94", null, 1);
        var quantity = line.Quantity;
        if (quantity > 0m)
        {
            // (1) Tal cual.
            if (HasTwoDecimals(quantity) && ExactPrice(gross, quantity) is { } price)
            {
                return new ItemShape(quantity, unit.DianCode, price, null);
            }

            // (2) En la unidad menor (1,235 kg = 1235 g).
            if (quantity != decimal.Truncate(quantity) && unit.SmallerDianCode is { } smaller)
            {
                var small = quantity * unit.Factor;
                if (HasTwoDecimals(small) && ExactPrice(gross, small) is { } smallPrice)
                {
                    return new ItemShape(small, smaller, smallPrice, null);
                }
            }

            // (3) Cantidad entera: precio al centavo superior; la diferencia (< 1 centavo por unidad) va al descuento.
            if (quantity == decimal.Truncate(quantity))
            {
                return new ItemShape(quantity, unit.DianCode, Math.Ceiling(gross / quantity * 100m) / 100m, null);
            }
        }

        // (4) Por su valor: cantidad 1 y la cantidad real en la nota.
        var note = string.Create(
            Invariant,
            $"Cantidad {quantity:0.####} {line.UnitCode} a {line.UnitPrice:0.##} por {line.UnitCode}{(line.PriceIncludesTax ? " (IVA incluido)" : string.Empty)}; se factura por el valor de la línea porque Factus admite cantidades con 2 decimales.");
        return new ItemShape(1m, "94", Money(gross), note);
    }

    /// <summary>
    /// Total que calculará Factus con estos ítems: bruto = precio × cantidad, base = bruto − descuento y cada impuesto con redondeo
    /// bancario (preguntas frecuentes de Factus; SUPUESTO a conciliar en el sandbox).
    /// </summary>
    public static decimal EstimatedTotal(IEnumerable<FactusItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var total = 0m;
        foreach (var item in items)
        {
            var gross = decimal.Round(item.Price * item.Quantity, 2, MidpointRounding.ToEven);
            var discount = item.DiscountAmount ?? decimal.Round(gross * (item.DiscountRate ?? 0m) / 100m, 2, MidpointRounding.ToEven);
            var taxBase = gross - discount;
            total += taxBase + item.Taxes.Where(t => t.IsExcluded != true).Sum(t => decimal.Round(taxBase * t.Rate / 100m, 2, MidpointRounding.ToEven));
        }

        return total;
    }

    // ─────────────────────────────── Rangos ───────────────────────────────

    /// <summary>
    /// Rango de Factus → rango neutro. <c>document</c> "21" factura, "22" nota crédito, "24" documento soporte, "25" nota de ajuste (o
    /// su nombre). El <c>current</c> de Factus es el SIGUIENTE número (SUPUESTO); el POS guarda el último usado. Un rango sin desde/hasta
    /// (las notas no exigen resolución) se toma como 1 a 999.999.999. <c>null</c> si el documento no es uno que emita el POS.
    /// </summary>
    public static FiscalProviderRange? ToRange(FactusNumberingRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (DocumentTypeOf(range.Document) is not { } type)
        {
            return null;
        }

        var from = Math.Max(1, range.From);
        var to = range.To >= from ? range.To : 999_999_999L;
        var current = Math.Clamp(range.Current - 1, from - 1, to);
        return new FiscalProviderRange(
            range.Id.ToString(Invariant), type, (range.Prefix ?? string.Empty).Trim(), from, to, current, Text(range.ResolutionNumber), range.StartDate,
            range.EndDate is { } end && range.StartDate is { } start && end < start ? start : range.EndDate, range.IsActive && !range.IsExpired);
    }

    public static FiscalDocumentType? DocumentTypeOf(string? document)
    {
        var text = (document ?? string.Empty).Trim();
        return text switch
        {
            FactusCodes.RangeInvoice => FiscalDocumentType.InvoiceElectronic,
            FactusCodes.RangeCreditNote => FiscalDocumentType.CreditNote,
            FactusCodes.RangeSupportDocument => FiscalDocumentType.SupportDocument,
            FactusCodes.RangeAdjustmentNote => FiscalDocumentType.AdjustmentNote,
            _ when text.Contains("ajuste", StringComparison.OrdinalIgnoreCase) => FiscalDocumentType.AdjustmentNote,
            _ when text.Contains("soporte", StringComparison.OrdinalIgnoreCase) => FiscalDocumentType.SupportDocument,
            _ when text.Contains("crédito", StringComparison.OrdinalIgnoreCase) || text.Contains("credito", StringComparison.OrdinalIgnoreCase) =>
                FiscalDocumentType.CreditNote,
            _ when text.Contains("factura", StringComparison.OrdinalIgnoreCase) => FiscalDocumentType.InvoiceElectronic,
            _ => null,
        };
    }

    /// <summary>Id numérico del rango de Factus (<c>numbering_range_id</c>); nulo si el rango no es de Factus.</summary>
    public static int? RangeId(FiscalNumbering numbering)
    {
        ArgumentNullException.ThrowIfNull(numbering);
        return int.TryParse(numbering.ProviderRangeId, NumberStyles.Integer, Invariant, out var id) ? id : null;
    }

    // ─────────────────────────────── Utilidades ───────────────────────────────

    /// <summary>Número de identificación sin DV, puntos ni espacios ("900.123.456-7" → "900123456").</summary>
    public static string Identification(string number)
    {
        ArgumentNullException.ThrowIfNull(number);
        var withoutDv = number.Split('-')[0];
        return new string([.. withoutDv.Where(c => !char.IsWhiteSpace(c) && c != '.')]);
    }

    private static bool IsLegal(string? personType) =>
        personType is not null && (personType.Equals("LEGAL", StringComparison.OrdinalIgnoreCase) || personType.Equals("JURIDICA", StringComparison.OrdinalIgnoreCase));

    private static decimal? ExactPrice(decimal gross, decimal quantity)
    {
        var price = gross / quantity;
        return decimal.Round(price, 2) == price && price * quantity == gross ? price : null;
    }

    private static bool HasTwoDecimals(decimal value) => decimal.Round(value, 2) == value;

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;

    private static string? Observation(params string?[] parts)
    {
        var text = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return text.Length == 0 ? null : Truncate(text, MaxObservation);
    }

    private sealed record UnitInfo(string DianCode, string? SmallerDianCode, int Factor);

    /// <summary>Forma del ítem en Factus: cantidad, unidad UN/ECE, precio unitario sin impuestos y nota (si se factura por valor).</summary>
    public sealed record ItemShape(decimal Quantity, string UnitCode, decimal Price, string? Note);
}
