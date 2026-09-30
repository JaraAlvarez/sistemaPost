using Pos.Modules.Billing.Domain;

namespace Pos.Modules.Billing.Factus.Tests;

/// <summary>
/// Borradores fiscales NEUTROS como los arma el núcleo desde una venta guardada: los renglones se calculan igual que el motor de la
/// venta (<c>SaleCalculator</c>: precio con IVA incluido, la base absorbe el redondeo, redondeo al centavo "lejos de cero").
/// </summary>
internal static class FiscalDrafts
{
    public static readonly Guid BranchId = Guid.Parse("0199a000-0000-7000-8000-000000000001");

    public static FiscalParty Issuer { get; } = new(
        "NIT", "31", "900123456", "7", "Supermercado Demo SAS", "LEGAL", "48", ["O-13"], "Calle 10 # 5-20", "11001", "facturacion@super.test", "6011234567");

    public static FiscalParty FinalConsumer { get; } = new(
        "CF", "13", FiscalParty.FinalConsumerIdentification, null, "Consumidor final", "NATURAL", "49", ["R-99-PN"], null, null, null, null);

    public static FiscalParty Company { get; } = new(
        "NIT", "31", "901.234.567-1", "1", "Tienda La 14 SAS", "LEGAL", "48", ["O-13", "O-15"], "Cra 1 # 2-3", "05001", "compras@la14.co", "6041234567");

    public static FiscalParty Person { get; } = new(
        "CC", "13", "1020304050", null, "Ana María Pérez", "NATURAL", "49", [], "Calle 5 # 6-7", "11001", "ana@correo.co", null);

    public static FiscalEstablishment Establishment { get; } = new(
        BranchId, "CENTRO", "Sede Centro", "Calle 10 # 5-20", "11001", "6011234567", "centro@super.test", null, null);

    public static FiscalHeader Header(string reference = "7f1c2a9e0b1d4c559a513f0e2d8b6a10", string rangeId = "8", string prefix = "SETP", bool resubmission = false) =>
        new(Guid.NewGuid(), reference, "C1-000045", new FiscalNumbering(Guid.NewGuid(), rangeId, prefix, "18760000001"), new DateOnly(2026, 9, 30),
            new DateTimeOffset(2026, 9, 30, 15, 0, 0, TimeSpan.Zero), Issuer, Establishment, null, resubmission);

    public static FiscalTax Vat(decimal rate, decimal taxBase, decimal amount) => new("IVA" + rate, "VAT", rate, null, taxBase, amount, rate == 0m, false);

    public static FiscalTax Excluded(decimal taxBase) => new("IVA-EXC", "VAT", 0m, null, taxBase, 0m, false, true);

    public static FiscalTax Inc(decimal rate, decimal taxBase, decimal amount) => new("INC" + rate, "CONSUMPTION", rate, null, taxBase, amount, false, false);

    public static FiscalTax Bag(decimal quantity, decimal unit) =>
        new("INC-BOLSA", FiscalDraftBuilder.BagTaxKind, null, unit, quantity, decimal.Round(unit * quantity, 2, MidpointRounding.AwayFromZero), false, false);

    /// <summary>Renglón con precio que INCLUYE impuestos (porcentuales) y descuento opcional (también con impuestos incluidos).</summary>
    public static FiscalSourceLine Included(
        int no, string code, string name, decimal quantity, decimal unitPrice, decimal rate, string kind = "VAT", decimal discount = 0m, string unit = "UND",
        FiscalTax? extra = null)
    {
        var gross = Round(unitPrice * quantity);
        var net = gross - discount;
        var fixedTotal = extra?.Amount ?? 0m;
        var taxable = Round((net - fixedTotal) / (1m + rate / 100m));
        var amount = Round(taxable * rate / 100m);
        var taxBase = net - amount - fixedTotal;
        var tax = kind switch
        {
            "EXCLUDED" => Excluded(taxBase),
            "CONSUMPTION" => Inc(rate, taxBase, amount),
            _ => Vat(rate, taxBase, amount),
        };
        IReadOnlyList<FiscalTax> taxes = extra is null ? [tax] : [tax, extra];
        return new FiscalSourceLine(Guid.NewGuid(), no, code, name, unit, quantity, unitPrice, true, gross, discount, taxBase, net, taxes);
    }

    public static FiscalInvoiceDraft Invoice(FiscalParty customer, IReadOnlyList<FiscalSourceLine> source, IReadOnlyList<FiscalPayment> payments, decimal rounding = 0m)
    {
        var lines = FiscalDraftBuilder.Lines(source);
        return new FiscalInvoiceDraft(Header(), customer, lines, payments, FiscalDraftBuilder.Totals(lines, rounding));
    }

    public static FiscalPayment Cash(decimal amount) => new("EFECTIVO", "CASH", "10", amount, null);

    public static FiscalPayment Card(decimal amount) => new("DEBITO", "DEBIT_CARD", "49", amount, "AUT-123456");

    public static FiscalPayment Nequi(decimal amount) => new("NEQUI", "WALLET", null, amount, "NQ-1");

    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
