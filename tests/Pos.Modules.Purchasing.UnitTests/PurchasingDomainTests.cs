using Pos.Modules.Purchasing.Application;
using Pos.Modules.Purchasing.Domain;
using Pos.SharedKernel.Domain;

namespace Pos.Modules.Purchasing.UnitTests;

internal static class Build
{
    public static readonly Guid Company = Guid.NewGuid();
    public static readonly Guid Branch = Guid.NewGuid();
    public static readonly Guid Warehouse = Guid.NewGuid();
    public static readonly Guid User = Guid.NewGuid();
    public static readonly DateTimeOffset Now = new(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly Today = new(2026, 9, 28);

    public static readonly CostingTax Vat19 = new(Guid.NewGuid(), "IVA19", true, 19m, null);

    public static Supplier Supplier(int termDays = 30, bool issuesInvoices = true) =>
        Domain.Supplier.Create(Guid.NewGuid(), Company, Guid.NewGuid(), new SupplierData("PRV1", termDays, null, null, issuesInvoices, null)).Value;

    public static PurchaseHeader Header(
        decimal? invoiceTotal = null, PaymentMode mode = PaymentMode.Credit, decimal charges = 0m, ProrationMethod proration = ProrationMethod.Value,
        Guid? method = null, string invoice = "fe-100") =>
        new(invoice, Today, Today, null, mode, method, null, invoiceTotal, proration, charges, charges > 0 ? "Flete" : null, null);

    public static PurchaseLineInput Line(
        decimal quantity, decimal unitCost, decimal factor = 1m, decimal discount = 0m, IReadOnlyList<CostingTax>? taxes = null, decimal? manualCharges = null,
        string? lot = null, Guid? product = null) =>
        new(product ?? Guid.NewGuid(), null, factor, quantity, unitCost, discount, taxes ?? [], manualCharges, lot, null, null);

    public static Purchase Purchase(
        IReadOnlyList<PurchaseLineInput> lines, PurchaseHeader? header = null, IReadOnlyList<WithholdingInput>? withholdings = null, bool vatDeductible = true,
        Supplier? supplier = null) =>
        Domain.Purchase.Create(Guid.NewGuid(), Company, Branch, Warehouse, supplier ?? Supplier(), null, "S01-000001", header ?? Header(), lines,
            withholdings ?? [], vatDeductible, Guid.NewGuid).Value;

    public static Purchase Posted(Purchase purchase)
    {
        purchase.MarkPosted(new Dictionary<Guid, Guid>(), User, Now);
        return purchase;
    }
}

public class PurchaseCostingTests
{
    [Fact]
    public void Ejemplo_de_la_propuesta_10_cajas_de_24_con_descuento_y_flete()
    {
        // 10 cajas × 24 u a $60.000, descuento $30.000, flete $12.000 → (600.000 − 30.000 + 12.000) ÷ 240 = $2.425.
        var result = PurchaseCosting.Calculate([new CostingLine(10m, 24m, 60_000m, 30_000m, [Build.Vat19], null)], 12_000m, ProrationMethod.Value, vatDeductible: true);

        var line = result.Lines.Single();
        line.BaseQuantity.ShouldBe(240m);
        line.NetUnitCost.ShouldBe(2_425m);
        line.TaxAmount.ShouldBe(108_300m);
        line.NonDeductibleTax.ShouldBe(0m);
        line.LineTotal.ShouldBe(678_300m);
        result.Totals.ShouldBe(new CostingTotals(600_000m, 30_000m, 12_000m, 108_300m, 108_300m, 690_300m));
    }

    [Fact]
    public void Sin_IVA_descontable_el_IVA_es_costo_y_los_demas_impuestos_siempre()
    {
        var bag = new CostingTax(null, "INC_BOLSA", false, null, 50m);
        var nonDeductible = PurchaseCosting.Calculate([new CostingLine(100m, 1m, 1_000m, 0m, [Build.Vat19, bag], null)], 0m, ProrationMethod.Value, false);
        nonDeductible.Lines[0].NonDeductibleTax.ShouldBe(19_000m + 5_000m);
        nonDeductible.Lines[0].NetUnitCost.ShouldBe(1_240m);
        nonDeductible.Totals.DeductibleTaxTotal.ShouldBe(0m);

        var deductible = PurchaseCosting.Calculate([new CostingLine(100m, 1m, 1_000m, 0m, [Build.Vat19, bag], null)], 0m, ProrationMethod.Value, true);
        deductible.Lines[0].NetUnitCost.ShouldBe(1_050m);
        deductible.Lines[0].Taxes.Single(t => t.Code == "IVA19").IsDeductible.ShouldBeTrue();
        deductible.Lines[0].Taxes.Single(t => t.Code == "INC_BOLSA").FixedAmount.ShouldBe(50m);
    }

    [Fact]
    public void El_prorrateo_por_valor_cuadra_exacto_y_el_residuo_va_a_la_linea_mayor()
    {
        CostingLine[] lines =
        [
            new(1m, 1m, 100m, 0m, [], null),
            new(1m, 1m, 100m, 0m, [], null),
            new(1m, 1m, 100m, 0m, [], null),
        ];
        var charges = PurchaseCosting.Prorate(lines, 100m, ProrationMethod.Value);
        charges.Sum().ShouldBe(100m);
        charges.ShouldBe([33.34m, 33.33m, 33.33m]);
    }

    [Fact]
    public void Prorrateo_por_cantidad_manual_y_lineas_sin_valor()
    {
        CostingLine[] lines = [new(10m, 1m, 500m, 0m, [], 7m), new(30m, 1m, 1m, 0m, [], 3m)];
        PurchaseCosting.Prorate(lines, 40m, ProrationMethod.Quantity).ShouldBe([10m, 30m]);
        PurchaseCosting.Prorate(lines, 10m, ProrationMethod.Manual).ShouldBe([7m, 3m]);
        PurchaseCosting.Prorate(lines, 0m, ProrationMethod.Value).ShouldBe([0m, 0m]);

        // Todo con costo cero (bonificación): se reparte por cantidad.
        CostingLine[] free = [new(1m, 1m, 0m, 0m, [], null), new(3m, 1m, 0m, 0m, [], null)];
        PurchaseCosting.Prorate(free, 8m, ProrationMethod.Value).ShouldBe([2m, 6m]);
    }
}

public class PurchaseTests
{
    [Fact]
    public void Calcula_totales_retenciones_vencimiento_y_documento_soporte()
    {
        var supplier = Build.Supplier(termDays: 45, issuesInvoices: false);
        var purchase = Build.Purchase(
            [Build.Line(10m, 60_000m, factor: 24m, discount: 30_000m, taxes: [Build.Vat19], lot: " l-1 ")],
            Build.Header(invoiceTotal: 690_300m, charges: 12_000m),
            [new WithholdingInput(WithholdingKind.Retefuente, 570_000m, 2.5m, 14_250m)],
            supplier: supplier);

        purchase.Total.ShouldBe(690_300m);
        purchase.WithholdingTotal.ShouldBe(14_250m);
        purchase.PayableTotal.ShouldBe(676_050m);
        purchase.DueDate.ShouldBe(Build.Today.AddDays(45));
        purchase.RequiresSupportDocument.ShouldBeTrue();
        purchase.SupplierInvoiceNumber.ShouldBe("FE-100");
        purchase.Lines[0].LotNumber.ShouldBe("L-1");
        purchase.Lines[0].NetUnitCost.ShouldBe(2_425m);
        purchase.Lines[0].Taxes.Count.ShouldBe(1);
        purchase.CanPost(0m, false).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void No_se_contabiliza_si_el_total_no_cuadra_o_falta_el_medio_de_contado()
    {
        var noTotal = Build.Purchase([Build.Line(1m, 1_000m)]);
        noTotal.CanPost(0m, false).Error.ShouldBe(PurchasingErrors.InvoiceTotalRequired);

        var mismatch = Build.Purchase([Build.Line(1m, 1_000m)], Build.Header(invoiceTotal: 1_001m));
        mismatch.CanPost(0m, false).Error.Code.ShouldBe("PURCHASING.TOTALS_MISMATCH");
        mismatch.CanPost(1m, false).IsSuccess.ShouldBeTrue();

        var cash = Build.Purchase([Build.Line(1m, 1_000m)], Build.Header(invoiceTotal: 1_000m, mode: PaymentMode.Cash));
        cash.DueDate.ShouldBe(Build.Today);
        cash.CanPost(0m, false).Error.ShouldBe(PurchasingErrors.PaymentMethodRequired);
        var withMethod = Build.Purchase([Build.Line(1m, 1_000m)], Build.Header(invoiceTotal: 1_000m, mode: PaymentMode.Cash, method: Guid.NewGuid()));
        withMethod.CanPost(0m, paymentMethodRequiresReference: true).Error.ShouldBe(PurchasingErrors.PaymentMethodRequired);
        withMethod.CanPost(0m, paymentMethodRequiresReference: false).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Validaciones_de_edicion()
    {
        var supplier = Build.Supplier();
        Result(supplier, [], Build.Header()).ShouldBe(PurchasingErrors.NoLines);
        Result(supplier, [Build.Line(0m, 1m)], Build.Header()).ShouldBe(PurchasingErrors.InvalidLine);
        Result(supplier, [Build.Line(1m, 100m, discount: 101m)], Build.Header()).ShouldBe(PurchasingErrors.InvalidLine);
        Result(supplier, [Build.Line(1.00001m, 100m)], Build.Header()).ShouldBe(PurchasingErrors.InvalidLine);
        Result(supplier, [Build.Line(1m, 100m) with { ExpiryDate = Build.Today }], Build.Header()).ShouldBe(PurchasingErrors.InvalidLine);
        Result(supplier, [Build.Line(1m, 100m)], Build.Header(invoice: " ")).ShouldBe(PurchasingErrors.InvalidHeader);
        Result(supplier, [Build.Line(1m, 100m)], Build.Header() with { DueDate = Build.Today.AddDays(-1) }).ShouldBe(PurchasingErrors.InvalidHeader);
        Result(supplier, [Build.Line(1m, 100m, manualCharges: 5m)], Build.Header(charges: 10m, proration: ProrationMethod.Manual))
            .ShouldBe(PurchasingErrors.ChargesMismatch);
        Result(supplier, [Build.Line(1m, 100m)], Build.Header(), [new(WithholdingKind.Reteica, 100m, null, 1m), new(WithholdingKind.Reteica, 100m, null, 1m)])
            .ShouldBe(PurchasingErrors.InvalidWithholding);
        Result(supplier, [Build.Line(1m, 100m)], Build.Header(), [new(WithholdingKind.Reteiva, 100m, null, 101m)])
            .ShouldBe(PurchasingErrors.WithholdingsExceedTotal);
    }

    [Fact]
    public void Contabilizada_no_se_edita_y_se_anula_con_motivo_si_no_tiene_devoluciones()
    {
        var supplier = Build.Supplier();
        var purchase = Build.Posted(Build.Purchase([Build.Line(10m, 100m)], supplier: supplier));
        purchase.Status.ShouldBe(PurchaseStatus.Posted);
        purchase.Edit(supplier, Build.Header(), [Build.Line(1m, 1m)], [], true, Guid.NewGuid).Error.ShouldBe(PurchasingErrors.InvalidStatus);
        Should.Throw<DomainException>(() => purchase.MarkPosted(new Dictionary<Guid, Guid>(), Build.User, Build.Now));
        purchase.CanPost(0m, false).Error.ShouldBe(PurchasingErrors.InvalidStatus);

        purchase.Void("x", Build.User, Build.Now).Error.ShouldBe(PurchasingErrors.VoidReasonRequired);
        purchase.Void("Factura mal digitada", Build.User, Build.Now).IsSuccess.ShouldBeTrue();
        purchase.Status.ShouldBe(PurchaseStatus.Voided);
        purchase.Void("Otra vez anulada", Build.User, Build.Now).Error.ShouldBe(PurchasingErrors.InvalidStatus);

        var returned = Build.Posted(Build.Purchase([Build.Line(10m, 100m)], supplier: supplier));
        returned.RegisterReturn(returned.Lines[0].Id, 3m).IsSuccess.ShouldBeTrue();
        returned.Lines[0].ReturnableBaseQuantity.ShouldBe(7m);
        returned.RegisterReturn(returned.Lines[0].Id, 8m).Error.ShouldBe(PurchasingErrors.ReturnExceedsPurchase);
        returned.RegisterReturn(Guid.NewGuid(), 1m).Error.ShouldBe(PurchasingErrors.ReturnExceedsPurchase);
        returned.Void("Factura mal digitada", Build.User, Build.Now).Error.ShouldBe(PurchasingErrors.PurchaseHasReturns);
    }

    [Fact]
    public void Asigna_los_lotes_al_contabilizar()
    {
        var purchase = Build.Purchase([Build.Line(1m, 100m, lot: "A"), Build.Line(1m, 100m)]);
        var lot = Guid.NewGuid();
        purchase.MarkPosted(new Dictionary<Guid, Guid> { [purchase.Lines[0].Id] = lot }, Build.User, Build.Now);
        purchase.Lines[0].LotId.ShouldBe(lot);
        purchase.Lines[1].LotId.ShouldBeNull();
        Build.Purchase([Build.Line(1m, 100m)]).RegisterReturn(Guid.NewGuid(), 1m).Error.ShouldBe(PurchasingErrors.InvalidStatus);
    }

    private static Pos.SharedKernel.Results.Error Result(
        Supplier supplier, IReadOnlyList<PurchaseLineInput> lines, PurchaseHeader header, IReadOnlyList<WithholdingInput>? withholdings = null) =>
        Purchase.Create(Guid.NewGuid(), Build.Company, Build.Branch, Build.Warehouse, supplier, null, "N", header, lines, withholdings ?? [], true, Guid.NewGuid).Error;
}

public class PurchaseOrderTests
{
    private static PurchaseOrder Order(params OrderLineInput[] lines) =>
        PurchaseOrder.Create(Guid.NewGuid(), Build.Company, Build.Branch, Build.Warehouse, Guid.NewGuid(), "S01-1", Build.Today, Build.Today.AddDays(3), null,
            lines, Guid.NewGuid).Value;

    [Fact]
    public void Flujo_completo_con_recepcion_parcial_total_tolerancia_y_reversion()
    {
        var order = Order(new OrderLineInput(Guid.NewGuid(), null, 24m, 10m, 60_000m), new OrderLineInput(Guid.NewGuid(), null, 1m, 5m, 1_000m));
        order.Total.ShouldBe(605_000m);
        order.Lines[0].BaseQuantity.ShouldBe(240m);
        order.RegisterReceipt(order.Lines[0].Id, 1m, 0m).Error.ShouldBe(PurchasingErrors.OrderNotReceivable);

        order.Approve(Build.User, Build.Now).IsSuccess.ShouldBeTrue();
        order.MarkSent(Build.Now).IsSuccess.ShouldBeTrue();
        order.RegisterReceipt(order.Lines[0].Id, 120m, 0m).IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(PurchaseOrderStatus.PartiallyReceived);
        order.Lines[0].PendingBaseQuantity.ShouldBe(120m);
        order.RegisterReceipt(order.Lines[0].Id, 121m, 0m).Error.ShouldBe(PurchasingErrors.ReceiptExceedsOrder);
        order.RegisterReceipt(order.Lines[0].Id, 121m, 1m).IsSuccess.ShouldBeTrue();
        order.RegisterReceipt(Guid.NewGuid(), 1m, 0m).Error.ShouldBe(PurchasingErrors.OrderLineMismatch);
        order.RegisterReceipt(order.Lines[1].Id, 5m, 0m).IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(PurchaseOrderStatus.Received);

        order.RevertReceipt(order.Lines[1].Id, 5m);
        order.Status.ShouldBe(PurchaseOrderStatus.PartiallyReceived);
        order.RevertReceipt(order.Lines[0].Id, 241m);
        order.Status.ShouldBe(PurchaseOrderStatus.Sent);
        Should.Throw<DomainException>(() => order.RevertReceipt(Guid.NewGuid(), 1m));

        order.Cancel(Build.Now).IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(PurchaseOrderStatus.Cancelled);
        order.Close(Build.Now).Error.ShouldBe(PurchasingErrors.InvalidStatus);
    }

    [Fact]
    public void Estados_y_validaciones()
    {
        var order = Order(new OrderLineInput(Guid.NewGuid(), null, 1m, 1m, 1m));
        order.MarkSent(Build.Now).Error.ShouldBe(PurchasingErrors.InvalidStatus);
        order.Edit(Build.Today, null, null, [], Guid.NewGuid).Error.ShouldBe(PurchasingErrors.NoLines);
        var product = Guid.NewGuid();
        order.Edit(Build.Today, null, null, [new(product, null, 1m, 1m, 1m), new(product, null, 1m, 2m, 1m)], Guid.NewGuid).Error.ShouldBe(PurchasingErrors.DuplicatedLine);
        order.Edit(Build.Today, null, null, [new(product, null, 1m, -1m, 1m)], Guid.NewGuid).Error.ShouldBe(PurchasingErrors.InvalidLine);
        order.Edit(Build.Today, Build.Today.AddDays(-1), null, [new(product, null, 1m, 1m, 1m)], Guid.NewGuid).Error.ShouldBe(PurchasingErrors.InvalidHeader);
        order.Approve(Build.User, Build.Now).IsSuccess.ShouldBeTrue();
        order.Approve(Build.User, Build.Now).Error.ShouldBe(PurchasingErrors.InvalidStatus);
        order.Edit(Build.Today, null, null, [new(product, null, 1m, 1m, 1m)], Guid.NewGuid).Error.ShouldBe(PurchasingErrors.InvalidStatus);
        order.RegisterReceipt(order.Lines[0].Id, 1m, 0m).IsSuccess.ShouldBeTrue();
        order.Cancel(Build.Now).Error.ShouldBe(PurchasingErrors.OrderHasReceipts);
        order.Close(Build.Now).IsSuccess.ShouldBeTrue();
        order.RevertReceipt(order.Lines[0].Id, 1m);
        order.Status.ShouldBe(PurchaseOrderStatus.Closed);

        var received = Order(new OrderLineInput(Guid.NewGuid(), null, 1m, 2m, 1m));
        received.Approve(Build.User, Build.Now);
        received.RegisterReceipt(received.Lines[0].Id, 1m, 0m);
        received.Cancel(Build.Now).Error.ShouldBe(PurchasingErrors.OrderHasReceipts);
    }
}

public class PayableTests
{
    [Fact]
    public void Libro_con_cargo_pago_devolucion_reintegro_y_anulacion_de_pago()
    {
        var purchase = Build.Posted(Build.Purchase([Build.Line(10m, 1_000m)]));
        var account = AccountPayable.Open(Guid.NewGuid(), purchase, Build.User, Build.Now, Guid.NewGuid());
        account.Balance.ShouldBe(10_000m);
        account.Status.ShouldBe(PayableStatus.Open);

        var payment = Guid.NewGuid();
        account.ApplyPayment(Guid.NewGuid(), 10_001m, payment, "P1", Build.User, Build.Now).Error.ShouldBe(PurchasingErrors.Overpayment);
        account.ApplyPayment(Guid.NewGuid(), 10_000m, payment, "P1", Build.User, Build.Now).IsSuccess.ShouldBeTrue();
        account.Status.ShouldBe(PayableStatus.Settled);
        account.ApplyPayment(Guid.NewGuid(), 1m, payment, "P1", Build.User, Build.Now).Error.ShouldBe(PurchasingErrors.PayableNotOpen);

        account.Adjust(Guid.NewGuid(), PayableEntryType.Return, 2_000m, "SUPPLIER_RETURN", Guid.NewGuid(), "D1", Build.User, Build.Now).IsSuccess.ShouldBeTrue();
        account.Balance.ShouldBe(-2_000m);
        account.Adjust(Guid.NewGuid(), PayableEntryType.Refund, 2_000m, "SUPPLIER_RETURN_SETTLEMENT", Guid.NewGuid(), "D1", Build.User, Build.Now);
        account.Balance.ShouldBe(0m);
        account.Void(Guid.NewGuid(), purchase.Id, purchase.Number, Build.User, Build.Now).Error.ShouldBe(PurchasingErrors.PurchaseHasPayments);

        account.Adjust(Guid.NewGuid(), PayableEntryType.PaymentVoid, 10_000m, "PAYABLE_PAYMENT_VOID", payment, "P1", Build.User, Build.Now);
        account.Balance.ShouldBe(10_000m);
        account.PaidAmount.ShouldBe(0m);
        account.Entries.Select(e => e.Amount).ShouldBe([10_000m, -10_000m, -2_000m, 2_000m, 10_000m]);
        account.Entries[^1].BalanceAfter.ShouldBe(10_000m);
        Should.Throw<ArgumentOutOfRangeException>(() =>
            account.Adjust(Guid.NewGuid(), PayableEntryType.Charge, 1m, "X", Guid.NewGuid(), null, Build.User, Build.Now));

        account.Void(Guid.NewGuid(), purchase.Id, purchase.Number, Build.User, Build.Now).IsSuccess.ShouldBeTrue();
        account.Status.ShouldBe(PayableStatus.Voided);
        account.Balance.ShouldBe(0m);
        account.Void(Guid.NewGuid(), purchase.Id, purchase.Number, Build.User, Build.Now).Error.ShouldBe(PurchasingErrors.InvalidStatus);
        account.Adjust(Guid.NewGuid(), PayableEntryType.Refund, 1m, "X", Guid.NewGuid(), null, Build.User, Build.Now).Error.ShouldBe(PurchasingErrors.InvalidStatus);
    }

    [Fact]
    public void Compra_sin_saldo_nace_saldada_y_signos_de_los_asientos()
    {
        var purchase = Build.Posted(Build.Purchase([Build.Line(1m, 0m)]));
        var account = AccountPayable.Open(Guid.NewGuid(), purchase, Build.User, Build.Now, Guid.NewGuid());
        account.Status.ShouldBe(PayableStatus.Settled);
        account.Entries.ShouldBeEmpty();
        PayableEntry.IsIncrease(PayableEntryType.Charge).ShouldBeTrue();
        PayableEntry.IsIncrease(PayableEntryType.Replacement).ShouldBeTrue();
        PayableEntry.IsIncrease(PayableEntryType.Payment).ShouldBeFalse();
        PayableEntry.IsIncrease(PayableEntryType.Return).ShouldBeFalse();
    }

    [Fact]
    public void Pago_a_varias_facturas_y_su_anulacion()
    {
        AllocationInput[] allocations = [new(Guid.NewGuid(), 100m), new(Guid.NewGuid(), 50.5m)];
        var payment = PayablePayment.Create(Guid.NewGuid(), Build.Company, Build.Branch, Guid.NewGuid(), "P-1", Build.Today, Guid.NewGuid(), " TRF-1 ", null,
            allocations, Guid.NewGuid).Value;
        payment.Amount.ShouldBe(150.5m);
        payment.Reference.ShouldBe("TRF-1");
        payment.Allocations.Count.ShouldBe(2);
        payment.Void("abc", Build.User, Build.Now).Error.ShouldBe(PurchasingErrors.VoidReasonRequired);
        payment.Void("Error de digitación", Build.User, Build.Now).IsSuccess.ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.Voided);
        payment.Void("Error de digitación", Build.User, Build.Now).Error.ShouldBe(PurchasingErrors.InvalidStatus);

        var account = Guid.NewGuid();
        Create([]).ShouldBe(PurchasingErrors.InvalidPayment);
        Create([new(account, 0m)]).ShouldBe(PurchasingErrors.InvalidPayment);
        Create([new(account, 1.001m)]).ShouldBe(PurchasingErrors.InvalidPayment);
        Create([new(account, 1m), new(account, 2m)]).ShouldBe(PurchasingErrors.InvalidPayment);
    }

    [Theory]
    [InlineData(0, Aging.Current)]
    [InlineData(-5, Aging.Current)]
    [InlineData(1, Aging.Days1To30)]
    [InlineData(30, Aging.Days1To30)]
    [InlineData(31, Aging.Days31To60)]
    [InlineData(61, Aging.Days61To90)]
    [InlineData(91, Aging.Over90)]
    public void Edades_de_la_cartera(int overdueDays, string bucket) =>
        Aging.Bucket(Build.Today, Build.Today.AddDays(overdueDays)).ShouldBe(bucket);

    private static Pos.SharedKernel.Results.Error Create(IReadOnlyList<AllocationInput> allocations) =>
        PayablePayment.Create(Guid.NewGuid(), Build.Company, Build.Branch, Guid.NewGuid(), "P", Build.Today, Guid.NewGuid(), null, null, allocations, Guid.NewGuid).Error;
}

public class SupplierReturnTests
{
    [Fact]
    public void Devolucion_al_costo_de_la_compra_con_credito_proporcional()
    {
        var purchase = Build.Posted(Build.Purchase(
            [Build.Line(10m, 60_000m, factor: 24m, discount: 30_000m, taxes: [Build.Vat19])], Build.Header(charges: 12_000m)));
        var line = purchase.Lines[0];
        var created = SupplierReturn.Create(Guid.NewGuid(), purchase, "D-1", Build.Today, "Empaque dañado",
            [new ReturnLineInput(line.Id, line.ProductId, 10m, line.NetUnitCost, null)], Guid.NewGuid).Value;
        created.Total.ShouldBe(24_250m);
        created.CreditTotal.ShouldBe(28_262.5m);
        created.Status.ShouldBe(SupplierReturnStatus.Draft);

        created.Settle(ReturnSettlement.CreditNote, "NC-1", Build.User, Build.Now).Error.ShouldBe(PurchasingErrors.InvalidStatus);
        created.MarkPosted(Build.User, Build.Now);
        Should.Throw<DomainException>(() => created.MarkPosted(Build.User, Build.Now));
        created.Cancel(Build.Now).Error.ShouldBe(PurchasingErrors.InvalidStatus);
        created.Settle(ReturnSettlement.Refund, " ", Build.User, Build.Now).Error.ShouldBe(PurchasingErrors.InvalidSettlement);
        created.Settle(ReturnSettlement.Refund, "RC-9", Build.User, Build.Now).IsSuccess.ShouldBeTrue();
        created.Status.ShouldBe(SupplierReturnStatus.Settled);
        created.Settlement.ShouldBe(ReturnSettlement.Refund);

        var all = SupplierReturn.Create(Guid.NewGuid(), purchase, "D-2", Build.Today, "Devolución total",
            [new ReturnLineInput(line.Id, line.ProductId, 240m, line.NetUnitCost, null)], Guid.NewGuid).Value;
        all.CreditTotal.ShouldBe(line.LineTotal);
        all.Cancel(Build.Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Validaciones_de_la_devolucion()
    {
        var draft = Build.Purchase([Build.Line(5m, 100m)]);
        var line = draft.Lines[0];
        Create(draft, [new(line.Id, line.ProductId, 1m, 100m, null)]).ShouldBe(PurchasingErrors.InvalidStatus);

        var purchase = Build.Posted(Build.Purchase([Build.Line(5m, 100m)]));
        line = purchase.Lines[0];
        Create(purchase, [new(line.Id, line.ProductId, 1m, 100m, null)], "no").ShouldBe(PurchasingErrors.VoidReasonRequired);
        Create(purchase, []).ShouldBe(PurchasingErrors.NoLines);
        Create(purchase, [new(line.Id, line.ProductId, 1m, 100m, null), new(line.Id, line.ProductId, 1m, 100m, null)]).ShouldBe(PurchasingErrors.DuplicatedLine);
        Create(purchase, [new(line.Id, Guid.NewGuid(), 1m, 100m, null)]).ShouldBe(PurchasingErrors.InvalidLine);
        Create(purchase, [new(line.Id, line.ProductId, 6m, 100m, null)]).ShouldBe(PurchasingErrors.ReturnExceedsPurchase);
    }

    private static Pos.SharedKernel.Results.Error Create(Purchase purchase, IReadOnlyList<ReturnLineInput> lines, string reason = "Producto vencido") =>
        SupplierReturn.Create(Guid.NewGuid(), purchase, "D", Build.Today, reason, lines, Guid.NewGuid).Error;
}

public class SupplierTests
{
    [Fact]
    public void Proveedor_codigo_por_defecto_estado_y_validaciones()
    {
        Supplier.DefaultCode("900.123.456").ShouldBe("900123456");
        Supplier.DefaultCode("ab-1234567890123456789012").ShouldBe("AB-12345678901234567");

        var supplier = Build.Supplier();
        supplier.AcceptsNewDocuments.ShouldBeTrue();
        supplier.SetStatus(SupplierStatus.Blocked);
        supplier.AcceptsNewDocuments.ShouldBeFalse();
        supplier.Update(new SupplierData("mi prov", 30, null, null, true, null)).Error.ShouldBe(PurchasingErrors.InvalidSupplier);
        supplier.Update(new SupplierData("P1", 400, null, null, true, null)).Error.ShouldBe(PurchasingErrors.InvalidSupplier);
        supplier.Update(new SupplierData("P1", 30, null, -1m, true, null)).Error.ShouldBe(PurchasingErrors.InvalidSupplier);
        supplier.Update(new SupplierData("p-1", 15, Guid.NewGuid(), 1_000_000m, false, "  Solo lunes ")).IsSuccess.ShouldBeTrue();
        supplier.Code.ShouldBe("P-1");
        supplier.Notes.ShouldBe("Solo lunes");
        supplier.AuditLabel.ShouldBe("Proveedor P-1");
    }

    [Fact]
    public void Producto_del_proveedor_y_su_ultimo_costo()
    {
        var item = SupplierProduct.Create(Guid.NewGuid(), Build.Company, Guid.NewGuid(), Guid.NewGuid(), null, " ref-9 ", 3, true).Value;
        item.SupplierCode.ShouldBe("REF-9");
        item.RecordPurchase(2_425m, Build.Now);
        item.LastCost.ShouldBe(2_425m);
        item.LastPurchaseAt.ShouldBe(Build.Now);
        item.Update(null, new string('x', 41), null, false).Error.ShouldBe(PurchasingErrors.InvalidSupplierProduct);
        item.Update(null, null, 500, false).Error.ShouldBe(PurchasingErrors.InvalidSupplierProduct);
        item.AuditLabel.ShouldContain("REF-9");
    }

    [Fact]
    public void Configuraciones_publicadas()
    {
        PurchasingSettings.VatDeductible.DefaultValue.ShouldBeTrue();
        PurchasingSettings.InvoiceTotalTolerance.DefaultValue.ShouldBe(0m);
        PurchasingSettings.ReceiptTolerancePercent.DefaultValue.ShouldBe(0m);
        PurchasingSettings.CostVariationAlertPercent.DefaultValue.ShouldBe(20m);
        new PurchasingPermissionCatalog().GetPermissions().Count().ShouldBe(11); // Fase 8: purchasing.supplier.bank_manage
    }
}
