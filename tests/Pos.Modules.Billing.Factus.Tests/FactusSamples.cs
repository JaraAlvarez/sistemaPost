using Pos.Modules.Billing.Infrastructure.Factus;

namespace Pos.Modules.Billing.Factus.Tests;

/// <summary>Documentos de ejemplo de un supermercado (consumidor final, varios impuestos, bolsa como ítem).</summary>
internal static class FactusSamples
{
    public const string SaleReference = "7f1c2a9e-0b1d-4c55-9a51-3f0e2d8b6a10";

    /// <summary>
    /// Factura con IVA 19 %, IVA 5 %, IVA 0 % (exento) con descuento, excluido, INC 8 % y la bolsa plástica como ítem.
    /// Total esperado (redondeo bancario por impuesto): 61.590,00; pagado 61.600,00 → redondeo del efectivo +10,00.
    /// </summary>
    public static FactusBillRequest Bill(string reference = SaleReference, FactusCustomer? customer = null) => new()
    {
        ReferenceCode = reference,
        Document = FactusCodes.InvoiceDocument,
        NumberingRangeId = 8,
        OperationType = FactusCodes.StandardOperation,
        SendEmail = false,
        Observation = "Venta C1-000045",
        PaymentDetails =
        [
            new FactusPaymentDetail { PaymentForm = FactusCodes.PaymentFormCash, PaymentMethodCode = FactusCodes.MethodCash, Amount = 41_600m },
            new FactusPaymentDetail
            {
                PaymentForm = FactusCodes.PaymentFormCash, PaymentMethodCode = FactusCodes.MethodDebitCard, ReferenceCode = "AUT-123456", Amount = 20_000m,
            },
        ],
        CashRoundingAmount = 10m,
        Establishment = new FactusEstablishment
        {
            Name = "Supermercado Demo Sede Centro",
            Address = "Calle 10 # 5-20",
            PhoneNumber = "6011234567",
            Email = "centro@super.test",
            MunicipalityCode = "11001",
        },
        Customer = customer ?? FinalConsumer(),
        Items =
        [
            Item("7702001", "Gaseosa 1,5 L", 2m, 4_201.68m, Tax(FactusCodes.TaxIva, 19m)),
            Item("7702002", "Café molido 250 g", 1m, 9_523.81m, Tax(FactusCodes.TaxIva, 5m)),
            Item("7702003", "Leche entera 1 L", 3m, 3_500m, Tax(FactusCodes.TaxIva, 0m)) with { DiscountRate = 10m },
            Item("7702004", "Huevos AA x 30", 1m, 12_000m, new FactusItemTax { Code = FactusCodes.TaxIva, Rate = 0m, IsExcluded = true }),
            Item("7702005", "Hamburguesa preparada", 1m, 18_518.52m, Tax(FactusCodes.TaxInc, 8m)),
            // Impuesto nacional al consumo de bolsas plásticas: valor fijo, se envía como ítem (preguntas frecuentes de Factus).
            Item("BOLSA", "Bolsa plástica (impuesto al consumo)", 2m, 70m, new FactusItemTax { Code = FactusCodes.TaxIva, Rate = 0m, IsExcluded = true }),
        ],
    };

    public static FactusCustomer FinalConsumer() => new()
    {
        IdentificationDocumentCode = FactusCodes.IdCitizenship,
        Identification = "222222222222",
        LegalOrganizationCode = FactusCodes.NaturalPerson,
        TributeCode = FactusCodes.TributeNotApplicable,
        Names = "Consumidor final",
        CountryCode = "CO",
    };

    public static FactusCreditNoteRequest CreditNote(string billNumber, string reference = "b2d4a6c8-1111-4222-8333-944455556666") => new()
    {
        ReferenceCode = reference,
        CorrectionConceptCode = FactusCodes.CorrectionCancellation,
        CustomizationId = FactusCodes.CreditNoteWithInvoice,
        BillNumber = billNumber,
        NumberingRangeId = 9,
        Observation = "Anulación de la venta C1-000045",
        PaymentDetails = [new FactusPaymentDetail { PaymentForm = FactusCodes.PaymentFormCash, PaymentMethodCode = FactusCodes.MethodCash, Amount = 10_000m }],
        Items = [Item("7702001", "Gaseosa 1,5 L", 2m, 4_201.68m, Tax(FactusCodes.TaxIva, 19m))],
    };

    public static FactusSupportDocumentRequest SupportDocument(string reference = "c3e5b7d9-2222-4333-8444-a55566667777") => new()
    {
        ReferenceCode = reference,
        NumberingRangeId = 10,
        Observation = "Compra de papa a productor campesino",
        PaymentDetails = [new FactusPaymentDetail { PaymentForm = FactusCodes.PaymentFormCash, PaymentMethodCode = FactusCodes.MethodCash, Amount = 150_000m }],
        Provider = new FactusProvider
        {
            IdentificationDocumentCode = FactusCodes.IdCitizenship,
            Identification = "1098765432",
            Names = "Pedro Campesino",
            Address = "Vereda El Rosal",
            CountryCode = "CO",
            MunicipalityCode = "68679",
        },
        Items = [Item("PAPA-50", "Papa pastusa bulto 50 kg", 3m, 50_000m, new FactusItemTax { Code = FactusCodes.TaxIva, Rate = 0m, IsExcluded = true })],
    };

    private static FactusItem Item(string code, string name, decimal quantity, decimal price, params FactusItemTax[] taxes) => new()
    {
        CodeReference = code,
        Name = name,
        Quantity = quantity,
        DiscountRate = 0m,
        Price = price,
        UnitMeasureCode = FactusCodes.UnitMeasureUnit,
        StandardCode = FactusCodes.StandardContributor,
        Taxes = taxes,
    };

    private static FactusItemTax Tax(string code, decimal rate) => new() { Code = code, Rate = rate };
}
