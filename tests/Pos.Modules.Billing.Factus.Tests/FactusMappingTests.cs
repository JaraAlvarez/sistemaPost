using Pos.Modules.Billing.Domain;
using Pos.Modules.Billing.Infrastructure.Factus;
using static Pos.Modules.Billing.Factus.Tests.FiscalDrafts;

namespace Pos.Modules.Billing.Factus.Tests;

/// <summary>
/// Mapeo del modelo fiscal neutro a Factus v2 (Fase 11-B, D11B-06): IVA 19/5/0 (exento), excluido, INC, bolsa, descuentos, redondeo del
/// efectivo, varios medios de pago, consumidor final y cliente identificado, notas crédito total y parcial, documento soporte, nota de
/// ajuste y pesables. Invariante en todos: <c>price × quantity − discount_amount</c> = base guardada (exacta, 2 decimales).
/// </summary>
public class FactusMappingTests
{
    /// <summary>Venta de supermercado con todos los impuestos; total cobrado 25.532 redondeado a 25.550 en efectivo.</summary>
    private static (FiscalSaleSnapshot Sale, FiscalInvoiceDraft Draft) Supermarket(FiscalParty? customer = null)
    {
        IReadOnlyList<FiscalSourceLine> lines =
        [
            Included(1, "ARROZ-500", "Arroz 500 g", 3m, 3_000m, 19m),
            Included(2, "ACEITE-250", "Aceite 250 ml", 2m, 1_000m, 5m),
            Included(3, "LECHE-1L", "Leche entera 1 L", 1m, 4_200m, 0m),
            Included(4, "TOMATE", "Tomate chonto", 1.25m, 4_000m, 0m, kind: "EXCLUDED", unit: "KG"),
            Included(5, "GASEOSA", "Gaseosa 1,5 L", 1m, 2_500m, 8m, kind: "CONSUMPTION"),
            Included(6, "CHOCOLATINA", "Chocolatina", 2m, 1_500m, 19m, discount: 300m),
            Included(7, "BOLSA", "Bolsa plástica", 2m, 66m, 0m, kind: "EXCLUDED", extra: Bag(2m, 66m)),
        ];
        var linesTotal = lines.Sum(l => l.Total);
        var charged = 25_550m;
        var sale = new FiscalSaleSnapshot(
            Guid.NewGuid(), "C1-000045", new DateOnly(2026, 9, 30), DateTimeOffset.UtcNow, customer ?? FinalConsumer, lines,
            [Card(10_000m), Nequi(5_000m), Cash(10_550m)], charged - linesTotal, charged, null);
        var draft = FiscalDraftBuilder.Invoice(Header(), customer ?? FinalConsumer, sale);
        draft.IsSuccess.ShouldBeTrue();
        return (sale, draft.Value);
    }

    [Fact]
    public void Factura_de_supermercado_con_IVA_19_5_0_excluido_INC_bolsa_descuento_y_redondeo_del_efectivo()
    {
        var (_, draft) = Supermarket();
        draft.Totals.LinesTotal.ShouldBe(25_532m);

        var bill = FactusDraftMapper.ToBill(draft);

        (bill.ReferenceCode, bill.NumberingRangeId, bill.Document, bill.OperationType).ShouldBe(("7f1c2a9e0b1d4c559a513f0e2d8b6a10", (int?)8, "01", "10"));
        bill.Items.Select(i => i.CodeReference).ShouldBe(["ARROZ-500", "ACEITE-250", "LECHE-1L", "TOMATE", "GASEOSA", "CHOCOLATINA", "INC-BOLSA"]);

        // La base que ve la DIAN es la guardada, exacta.
        var products = draft.Lines.Where(l => l.Kind == FiscalLineKind.Product && l.Total > 0m).ToList();
        foreach (var (item, line) in bill.Items.Take(6).Zip(products))
        {
            (item.Price * item.Quantity - (item.DiscountAmount ?? 0m)).ShouldBe(line.TaxBase, item.CodeReference);
        }

        // IVA 19 % incluido, 3 unidades: 7.563,02 / 3 no es exacto → precio al centavo superior y 0,01 al descuento.
        var rice = bill.Items[0];
        (rice.Quantity, rice.Price, rice.DiscountAmount, rice.UnitMeasureCode).ShouldBe((3m, 2_521.01m, (decimal?)0.01m, "94"));
        rice.Taxes.ShouldHaveSingleItem().ShouldBe(new FactusItemTax { Code = "01", Rate = 19m });

        // IVA 5 % exacto: sin descuento (discount_rate 0).
        var oil = bill.Items[1];
        (oil.Quantity, oil.Price, oil.DiscountAmount, oil.DiscountRate).ShouldBe((2m, 952.38m, (decimal?)null, (decimal?)0m));
        oil.Taxes.Single().Rate.ShouldBe(5m);

        // Exento (tarifa 0, no excluido) vs. excluido (is_excluded).
        bill.Items[2].Taxes.ShouldHaveSingleItem().ShouldBe(new FactusItemTax { Code = "01", Rate = 0m });
        bill.Items[3].Taxes.ShouldHaveSingleItem().ShouldBe(new FactusItemTax { Code = "01", Rate = 0m, IsExcluded = true });
        (bill.Items[3].Quantity, bill.Items[3].Price, bill.Items[3].UnitMeasureCode).ShouldBe((1.25m, 4_000m, "KGM"));

        // INC 8 %.
        bill.Items[4].Taxes.ShouldHaveSingleItem().ShouldBe(new FactusItemTax { Code = "04", Rate = 8m });

        // Descuento con IVA incluido (300) llevado a la base: 252,10 + 0,01 de ajuste del precio unitario.
        var chocolate = bill.Items[5];
        (chocolate.Price * chocolate.Quantity - chocolate.DiscountAmount!.Value).ShouldBe(2_268.91m);
        chocolate.DiscountAmount.Value.ShouldBe(252.11m);

        // Bolsa: ítem con su valor (2 × 66), sin IVA; el producto "bolsa" en cero no se envía.
        var bag = bill.Items[6];
        (bag.Name, bag.Quantity, bag.Price, bag.Taxes.Single().IsExcluded).ShouldBe((FiscalDraftBuilder.BagTaxName, 2m, 66m, (bool?)true));

        // Total de Factus (IVA con redondeo bancario): 25.532; pagado 25.550 → cash_rounding_amount 18.
        FactusDraftMapper.EstimatedTotal(bill.Items).ShouldBe(25_532m);
        bill.CashRoundingAmount.ShouldBe(18m);
        bill.PaymentDetails.Sum(p => p.Amount).ShouldBe(25_550m);

        // Todo viaja con 2 decimales (el serializador rechaza más).
        Should.NotThrow(() => FactusJson.Serialize(bill));
    }

    [Fact]
    public void Varios_medios_de_pago_con_su_codigo_DIAN()
    {
        var (_, draft) = Supermarket();
        var payments = FactusDraftMapper.ToBill(draft).PaymentDetails;

        payments.Select(p => (p.PaymentForm, p.PaymentMethodCode, p.Amount, p.ReferenceCode)).ShouldBe(
        [
            ("1", "49", 10_000m, "AUT-123456"),
            ("1", "47", 5_000m, "NQ-1"), // billetera sin código configurado → transferencia
            ("1", "10", 10_550m, null),
        ]);
        payments.ShouldAllBe(p => p.DueDate == null);

        // Crédito (fiado): forma 2 con vencimiento; sin medios: uno "otro" por el total.
        var credit = FactusDraftMapper.Payment(new FiscalPayment("FIADO", "CUSTOMER_CREDIT", null, 5_000m, null), new DateOnly(2026, 9, 30));
        (credit.PaymentForm, credit.PaymentMethodCode, credit.DueDate).ShouldBe(("2", "1", "2026-10-30"));
        FactusDraftMapper.Payments([], new DateOnly(2026, 9, 30), 1_000m).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            p => p.PaymentMethodCode.ShouldBe("ZZZ"), p => p.Amount.ShouldBe(1_000m));
    }

    [Fact]
    public void Consumidor_final_y_cliente_identificado()
    {
        var finalConsumer = FactusDraftMapper.ToBill(Supermarket().Draft);
        finalConsumer.Customer.ShouldSatisfyAllConditions(
            c => c.IdentificationDocumentCode.ShouldBe("13"),
            c => c.Identification.ShouldBe("222222222222"),
            c => c.LegalOrganizationCode.ShouldBe("2"),
            c => c.TributeCode.ShouldBe("ZZ"),
            c => c.Responsibilities.ShouldBe(["R-99-PN"]),
            c => c.Names.ShouldBe("Consumidor final"),
            c => c.Company.ShouldBeNull());
        finalConsumer.SendEmail.ShouldBe(false);
        finalConsumer.Establishment!.ShouldSatisfyAllConditions(
            e => e.Name.ShouldBe("Sede Centro"), e => e.MunicipalityCode.ShouldBe("11001"), e => e.PhoneNumber.ShouldBe("6011234567"));

        // Persona jurídica con NIT: sin DV ni puntos en la identificación, DV aparte, responsable de IVA.
        var company = FactusDraftMapper.ToBill(Supermarket(Company).Draft);
        company.Customer.ShouldSatisfyAllConditions(
            c => c.IdentificationDocumentCode.ShouldBe("31"),
            c => c.Identification.ShouldBe("901234567"),
            c => c.Dv.ShouldBe("1"),
            c => c.LegalOrganizationCode.ShouldBe("1"),
            c => c.TributeCode.ShouldBe("01"),
            c => c.Company.ShouldBe("Tienda La 14 SAS"),
            c => c.Names.ShouldBeNull(),
            c => c.Responsibilities.ShouldBe(["O-13", "O-15"]),
            c => c.MunicipalityCode.ShouldBe("05001"),
            c => c.Email.ShouldBe("compras@la14.co"));
        company.SendEmail.ShouldBe(true);

        // Persona natural con cédula.
        FactusDraftMapper.Customer(Person).ShouldSatisfyAllConditions(
            c => c.IdentificationDocumentCode.ShouldBe("13"),
            c => c.Dv.ShouldBeNull(),
            c => c.LegalOrganizationCode.ShouldBe("2"),
            c => c.Names.ShouldBe("Ana María Pérez"),
            c => c.TributeCode.ShouldBe("ZZ"),
            c => c.Responsibilities.ShouldBe(["R-99-PN"]));

        // Una sucursal sin todos los datos no envía el establecimiento (Factus los exige todos).
        FactusDraftMapper.Establishment(Establishment with { Email = null }).ShouldBeNull();
    }

    [Fact]
    public void Nota_credito_total_por_anulacion_y_parcial_por_devolucion()
    {
        var (sale, _) = Supermarket();
        var invoice = new FiscalDocumentReference(Guid.NewGuid(), "7f1c2a9e0b1d4c559a513f0e2d8b6a10", "SETP990000001", "SETP990000001", "cufe-1", sale.BusinessDate);

        // Anulación: concepto "2", referencia la factura, sin adquirente (Factus lo toma de la factura) y medios de pago = total de Factus.
        var voided = FiscalDraftBuilder.VoidCreditNote(Header("NCA" + sale.SaleId.ToString("N"), "9", "NC"), FinalConsumer, invoice, sale with { VoidReason = "Cliente desistió" });
        var note = FactusDraftMapper.ToCreditNote(voided.Value);
        (note.CorrectionConceptCode, note.CustomizationId, note.BillNumber, note.NumberingRangeId).ShouldBe(("2", "20", (string?)"SETP990000001", (int?)9));
        note.Customer.ShouldBeNull();
        note.Observation!.ShouldStartWith("Cliente desistió");
        note.Items.Count.ShouldBe(7);
        note.PaymentDetails.Sum(p => p.Amount).ShouldBe(FactusDraftMapper.EstimatedTotal(note.Items));
        note.PaymentDetails.Single(p => p.PaymentMethodCode == "10").Amount.ShouldBe(10_532m); // el redondeo del efectivo sale del efectivo

        // Devolución parcial (cambio o garantía): una unidad de arroz de 3 000 (base 2.521,01 + IVA 478,99).
        var riceLine = sale.Lines[0];
        var returned = FiscalDraftBuilder.ReturnCreditNote(
            Header("NCD1", "9", "NC"), FinalConsumer, invoice, sale,
            new FiscalReturnSnapshot(Guid.NewGuid(), "D-1", "WARRANTY", "Arroz dañado", sale.BusinessDate, [new FiscalReturnLine(riceLine.LineId, 1m, 3_000m)], [Cash(3_000m)]));
        var partial = FactusDraftMapper.ToCreditNote(returned.Value);
        partial.CorrectionConceptCode.ShouldBe("1");
        var item = partial.Items.ShouldHaveSingleItem();
        (item.Quantity, item.Price, item.DiscountAmount).ShouldBe((1m, 2_521.01m, (decimal?)null));
        FactusDraftMapper.EstimatedTotal(partial.Items).ShouldBe(3_000m);
        partial.PaymentDetails.ShouldHaveSingleItem().Amount.ShouldBe(3_000m);
    }

    private static FiscalPurchaseSnapshot Purchase(string? voidReason = null)
    {
        var farmer = new FiscalParty("CC", "13", "71234567", null, "Pedro Campesino", "NATURAL", "49", ["R-99-PN"], null, "68679", null, "3001234567");
        IReadOnlyList<FiscalSourceLine> lines =
        [
            new(Guid.NewGuid(), 1, "ARROZ-500", "Arroz 500 g", "UND", 5m, 1_000m, false, 5_000m, 0m, 5_000m, 5_000m, []),
            new(Guid.NewGuid(), 2, "HUEVOS", "Huevo AA", "UND", 30m, 350m, false, 10_500m, 500m, 10_000m, 10_500m, [Vat(5m, 10_000m, 500m)]),
            new(Guid.NewGuid(), 3, "PANELA", "Panela", "KG", 2.5m, 400m, false, 1_000m, 0m, 1_000m, 1_080m, [Inc(8m, 1_000m, 80m)]),
        ];
        return new FiscalPurchaseSnapshot(
            Guid.NewGuid(), "CO-000010", "SF-2", new DateOnly(2026, 9, 30), farmer, lines, [new FiscalPayment("CREDITO", "CREDIT", "ZZZ", 16_580m, null)], voidReason);
    }

    [Fact]
    public void Documento_soporte_de_una_compra_a_un_proveedor_no_obligado()
    {
        var purchase = Purchase();
        var draft = FiscalDraftBuilder.SupportDocument(Header("DS1", "10", "DS"), purchase);

        var request = FactusDraftMapper.ToSupportDocument(draft);

        // Proveedor con cédula → NIT de persona natural (Factus no admite "13" para el proveedor); sin dirección → "No informada".
        request.Provider.ShouldSatisfyAllConditions(
            p => p.IdentificationDocumentCode.ShouldBe("31"),
            p => p.Identification.ShouldBe("71234567"),
            p => p.Names.ShouldBe("Pedro Campesino"),
            p => p.Address.ShouldBe("No informada"),
            p => p.CountryCode.ShouldBe("CO"),
            p => p.MunicipalityCode.ShouldBe("68679"),
            p => p.LegalOrganizationCode.ShouldBeNull());
        request.Observation.ShouldBe("Compra C1-000045 · Cuenta del proveedor SF-2");

        // Compra a crédito: forma 2 con vencimiento.
        request.PaymentDetails.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            p => p.PaymentForm.ShouldBe("2"), p => p.PaymentMethodCode.ShouldBe("ZZZ"), p => p.DueDate.ShouldBe("2026-10-30"));

        // Solo IVA en el documento soporte: sin IVA → excluido; el INC se suma a la base (el total no cambia).
        request.Items[0].Taxes.Single().IsExcluded.ShouldBe(true);
        (request.Items[1].Price, request.Items[1].Quantity, request.Items[1].DiscountAmount).ShouldBe((350m, 30m, (decimal?)500m));
        request.Items[1].Taxes.Single().ShouldBe(new FactusItemTax { Code = "01", Rate = 5m });
        var panela = request.Items[2];
        (panela.Quantity, panela.Price, panela.UnitMeasureCode, panela.Taxes.Single().IsExcluded).ShouldBe((2.5m, 432m, "KGM", (bool?)true));
        FactusDraftMapper.EstimatedTotal(request.Items).ShouldBe(16_580m);
        request.CashRoundingAmount.ShouldBeNull();
    }

    [Fact]
    public void Nota_de_ajuste_al_documento_soporte_por_anulacion_de_la_compra()
    {
        var purchase = Purchase("Mercancía devuelta al proveedor");
        var support = new FiscalDocumentReference(Guid.NewGuid(), "DS1", "DS1", "DS1", "cuds-1", purchase.InvoiceDate);

        var request = FactusDraftMapper.ToAdjustmentNote(FiscalDraftBuilder.AdjustmentNote(Header("NAS1", "13", "NA"), purchase, support));

        (request.SupportDocumentNumber, request.CorrectionConceptCode, request.NumberingRangeId).ShouldBe(("DS1", "2", (int?)13));
        request.Observation!.ShouldStartWith("Mercancía devuelta al proveedor · Ajusta el documento soporte DS1");
        (request.Provider.IdentificationDocumentCode, request.Provider.LegalOrganizationCode, request.Provider.Company).ShouldBe(("31", "2", (string?)null));
        request.Items.Count.ShouldBe(3);
        FactusDraftMapper.EstimatedTotal(request.Items).ShouldBe(16_580m);
        Should.NotThrow(() => FactusJson.Serialize(request));
    }

    [Theory]
    // (1) Tal cual: 2,5 kg a 2.000 (excluido).
    [InlineData(2.5, 2_000, 0, 2.5, "KGM", 2_000, false)]
    // (2) En gramos: 1,235 kg a 3.980/kg = 4.915,30 = 1235 g × 3,98.
    [InlineData(1.235, 3_980, 0, 1235, "GRM", 3.98, false)]
    // (2) En gramos: 0,755 kg a 12.990/kg = 9.807,45 = 755 g × 12,99.
    [InlineData(0.755, 12_990, 0, 755, "GRM", 12.99, false)]
    // (4) Con IVA 5 % incluido la base por gramo no es exacta: cantidad 1 por el valor de la línea y la cantidad real en la nota.
    [InlineData(1.237, 18_450, 5, 1, "94", 21_735.86, true)]
    public void Pesables_con_3_decimales_mantienen_la_base_y_el_total_exactos(
        double quantity, double unitPrice, double rate, double expectedQuantity, string expectedUnit, double expectedPrice, bool byValue)
    {
        var line = Included(1, "PESABLE", "Producto pesable", (decimal)quantity, (decimal)unitPrice, (decimal)rate, kind: rate == 0 ? "EXCLUDED" : "VAT", unit: "KG");
        var fiscal = FiscalDraftBuilder.Lines([line]).Single();

        var item = FactusDraftMapper.Item(fiscal, supportDocument: false);

        (item.Quantity, item.UnitMeasureCode, item.Price, item.DiscountAmount).ShouldBe(((decimal)expectedQuantity, expectedUnit, (decimal)expectedPrice, (decimal?)null));
        (item.Price * item.Quantity).ShouldBe(fiscal.TaxBase);
        decimal.Round(item.Quantity, 2).ShouldBe(item.Quantity);
        FactusDraftMapper.EstimatedTotal([item]).ShouldBe(fiscal.Total, 0.01m);
        if (byValue)
        {
            item.Note!.ShouldContain("1.237 KG");
            item.Note!.ShouldContain("18450");
        }
        else
        {
            item.Note.ShouldBeNull();
        }

        Should.NotThrow(() => FactusJson.Serialize(item));
    }

    [Fact]
    public void Rangos_de_Factus_a_rangos_neutros()
    {
        var invoice = FactusDraftMapper.ToRange(new FactusNumberingRange
        {
            Id = 8, Document = "21", Prefix = "SETP", From = 990_000_000, To = 995_000_000, Current = 990_000_001, ResolutionNumber = "18760000001",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2027, 12, 31), IsActive = true,
        })!;
        (invoice.ProviderRangeId, invoice.DocumentType, invoice.Prefix, invoice.From, invoice.To, invoice.Current, invoice.IsActive).ShouldBe(
            ("8", FiscalDocumentType.InvoiceElectronic, "SETP", 990_000_000L, 995_000_000L, 990_000_000L, true));

        // Nota crédito sin desde/hasta: 1 a 999.999.999; vencido → inactivo; nombre en vez de código.
        var credit = FactusDraftMapper.ToRange(new FactusNumberingRange { Id = 9, Document = "22", Prefix = "NC", IsActive = true, IsExpired = true })!;
        (credit.DocumentType, credit.From, credit.To, credit.Current, credit.IsActive).ShouldBe((FiscalDocumentType.CreditNote, 1L, 999_999_999L, 0L, false));
        FactusDraftMapper.ToRange(new FactusNumberingRange { Id = 13, Document = "Nota de Ajuste Documento Soporte", Prefix = "NA", IsActive = true })!
            .DocumentType.ShouldBe(FiscalDocumentType.AdjustmentNote);
        FactusDraftMapper.ToRange(new FactusNumberingRange { Id = 10, Document = "24", Prefix = "DS", From = 1, To = 5_000, Current = 1, IsActive = true })!
            .DocumentType.ShouldBe(FiscalDocumentType.SupportDocument);
        FactusDraftMapper.ToRange(new FactusNumberingRange { Id = 99, Document = "30", Prefix = "X" }).ShouldBeNull();

        FactusDraftMapper.RangeId(new FiscalNumbering(Guid.NewGuid(), "FAKE-FV-1", "SETP", null)).ShouldBeNull();
    }
}
