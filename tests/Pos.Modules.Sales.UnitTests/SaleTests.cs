using Pos.Modules.Sales.Domain;
using static Pos.Modules.Sales.UnitTests.Fx;

namespace Pos.Modules.Sales.UnitTests;

public class SaleTests
{
    [Fact]
    public void Inicia_abierta_con_consumidor_final_y_credito_de_cambio()
    {
        var exchange = Guid.NewGuid();
        var sale = NewSale(exchange, 5_000m);

        sale.Status.ShouldBe(SaleStatus.Open);
        sale.ReturnStatus.ShouldBe(SaleReturnStatus.None);
        (sale.CompanyId, sale.BranchId, sale.PosTerminalId, sale.WarehouseId, sale.CashSessionId, sale.CashierId)
            .ShouldBe((Company, Branch, Terminal, Warehouse, Session, Cashier));
        (sale.BusinessDate, sale.OpenedAt).ShouldBe((Today, Now));
        (sale.ExchangeId, sale.ExchangeCredit).ShouldBe(((Guid?)exchange, 5_000m));
        sale.CustomerId.ShouldBeNull();
        (sale.CustomerName, sale.CustomerIdentificationType, sale.CustomerIdentification).ShouldBe(("Consumidor final", "CC", "222222222222"));
        sale.AuditLabel.ShouldBe($"Venta {sale.Id}");
        Should.Throw<ArgumentNullException>(() => Sale.Start(Guid.NewGuid(), Company, Branch, Terminal, Warehouse, Session, Cashier, Today, Now, null!));
    }

    [Fact]
    public void Agrega_lineas_con_snapshot_y_suma_la_cantidad_a_la_misma()
    {
        var sale = NewSale();
        var first = sale.AddLine(Input(quantity: 2m), merge: true, Guid.NewGuid).Value;

        (first.LineNo, first.ProductId, first.Sku, first.Name, first.ScannedCode, first.Source, first.BaseUnitCode).ShouldBe(
            (1, Arroz, "SKU-ARROZ", "Arroz 500 g", (string?)"7701234567890", "SCAN", "UND"));
        (first.PackagingId, first.PackagingName, first.Factor, first.BaseQuantity).ShouldBe(((Guid?)null, (string?)null, 1m, 2m));
        (first.CategoryId, first.BrandId, first.IsStockable, first.AllowsOpenPrice, first.PriceIncludesTax).ShouldBe((Granos, (Guid?)null, true, false, true));
        first.Taxes.Single().Code.ShouldBe("IVA19");

        sale.AddLine(Input(quantity: 3m), merge: true, Guid.NewGuid).Value.ShouldBeSameAs(first);
        first.Quantity.ShouldBe(5m);

        var separate = sale.AddLine(Input(quantity: 1m), merge: false, Guid.NewGuid).Value;
        separate.LineNo.ShouldBe(2);
        sale.Lines.Count.ShouldBe(2);
    }

    [Fact]
    public void No_suma_lineas_pesadas_con_precio_distinto_o_con_vencido_autorizado()
    {
        var sale = NewSale();
        sale.AddLine(Input(), true, Guid.NewGuid);

        sale.AddLine(Input(source: "SCALE_WEIGHT", allowsDecimal: true, quantity: 0.5m), true, Guid.NewGuid).Value.LineNo.ShouldBe(2);
        sale.AddLine(Input(source: "SCALE_WEIGHT", allowsDecimal: true, quantity: 0.5m), true, Guid.NewGuid).Value.LineNo.ShouldBe(3);
        sale.AddLine(Input(price: 12_000m), true, Guid.NewGuid).Value.LineNo.ShouldBe(4);
        sale.AddLine(Input(expiredAuthorizedBy: Supervisor), true, Guid.NewGuid).Value.LineNo.ShouldBe(5);
        sale.AddLine(Input(product: Queso), true, Guid.NewGuid).Value.LineNo.ShouldBe(6);

        var manual = sale.AddLine(Input(allowsOpenPrice: true, price: 5_000m, product: Queso), false, Guid.NewGuid).Value;
        sale.OverridePrice(manual.Id, 5_000m, Supervisor);
        sale.AddLine(Input(allowsOpenPrice: true, price: 5_000m, product: Queso), true, Guid.NewGuid).Value.LineNo.ShouldBe(8);
    }

    [Fact]
    public void Cantidades_invalidas()
    {
        var sale = NewSale();

        sale.AddLine(Input(quantity: 0m), true, Guid.NewGuid).Error.ShouldBe(SalesErrors.InvalidQuantity);
        sale.AddLine(Input(quantity: 1.5m), true, Guid.NewGuid).Error.ShouldBe(SalesErrors.InvalidQuantity);
        sale.AddLine(Input(quantity: 1.23456m, allowsDecimal: true), true, Guid.NewGuid).Error.ShouldBe(SalesErrors.InvalidQuantity);
        sale.AddLine(Input(quantity: 100_001m), true, Guid.NewGuid).Error.ShouldBe(SalesErrors.InvalidQuantity);
        sale.AddLine(Input(factor: 0m), true, Guid.NewGuid).Error.ShouldBe(SalesErrors.InvalidQuantity);
        sale.AddLine(Input(price: -1m), true, Guid.NewGuid).Error.ShouldBe(SalesErrors.InvalidQuantity);
        sale.AddLine(Input(quantity: 1.2345m, allowsDecimal: true, factor: 1m), true, Guid.NewGuid).IsSuccess.ShouldBeTrue();
        Should.Throw<ArgumentNullException>(() => sale.AddLine(null!, true, Guid.NewGuid));
        Should.Throw<ArgumentNullException>(() => sale.AddLine(Input(), true, null!));
    }

    [Fact]
    public void Cambia_la_cantidad_de_una_linea_activa()
    {
        var sale = NewSale();
        var line = sale.AddLine(Input(factor: 6m), true, Guid.NewGuid).Value;

        sale.ChangeQuantity(line.Id, 4m).Value.BaseQuantity.ShouldBe(24m);
        sale.ChangeQuantity(line.Id, 0.5m).Error.ShouldBe(SalesErrors.InvalidQuantity);
        sale.ChangeQuantity(Guid.NewGuid(), 1m).Error.ShouldBe(SalesErrors.LineNotFound);
    }

    [Fact]
    public void Eliminar_una_linea_la_anula_y_quita_su_descuento()
    {
        var sale = NewSale();
        var line = sale.AddLine(Input(), true, Guid.NewGuid).Value;
        var discount = sale.ApplyDiscount(Guid.NewGuid(), line.Id, 10m, null, "Producto averiado", Cashier, Supervisor, Now).Value;

        sale.VoidLine(line.Id, Cashier, Now).IsSuccess.ShouldBeTrue();

        (line.Status, line.IsActive, line.VoidedBy, line.VoidedAt).ShouldBe((SaleLineStatus.Voided, false, (Guid?)Cashier, (DateTimeOffset?)Now));
        discount.Status.ShouldBe(DiscountStatus.Removed);
        sale.ActiveLines.ShouldBeEmpty();
        sale.VoidLine(line.Id, Cashier, Now).Error.ShouldBe(SalesErrors.LineNotFound);
        sale.ChangeQuantity(line.Id, 2m).Error.ShouldBe(SalesErrors.LineNotFound);
    }

    [Fact]
    public void Precio_abierto_solo_si_el_producto_lo_admite_y_quita_las_promociones()
    {
        var sale = NewSale();
        var fixedPrice = sale.AddLine(Input(), true, Guid.NewGuid).Value;
        var open = sale.AddLine(Input(product: Queso, price: 0m, allowsOpenPrice: true), true, Guid.NewGuid).Value;

        sale.OverridePrice(fixedPrice.Id, 5_000m, null).Error.ShouldBe(SalesErrors.OpenPriceNotAllowed);
        sale.OverridePrice(open.Id, 0m, null).Error.ShouldBe(SalesErrors.InvalidPrice);
        sale.OverridePrice(open.Id, 10.005m, null).Error.ShouldBe(SalesErrors.InvalidPrice);
        sale.OverridePrice(Guid.NewGuid(), 10m, null).Error.ShouldBe(SalesErrors.LineNotFound);

        sale.OverridePrice(open.Id, 8_000m, Supervisor).IsSuccess.ShouldBeTrue();
        sale.OverridePrice(open.Id, 9_000m, Supervisor).IsSuccess.ShouldBeTrue();
        (open.UnitPrice, open.OriginalUnitPrice, open.PriceOverridden, open.PriceAuthorizedBy).ShouldBe((9_000m, (decimal?)0m, true, (Guid?)Supervisor));

        var promo = new PromotionRule(Guid.NewGuid(), "Quesos", PromotionKind.PercentOff, [new PromotionTarget(Queso)], Percent: 50m);
        sale.Recalculate([promo]);
        open.PromotionId.ShouldBeNull();
        open.Total.ShouldBe(9_000m);
    }

    [Fact]
    public void Descuentos_manuales_validaciones_y_reemplazo()
    {
        var sale = NewSale();
        var line = sale.AddLine(Input(), true, Guid.NewGuid).Value;

        sale.ApplyDiscount(Guid.NewGuid(), Guid.NewGuid(), 10m, null, "Motivo válido", Cashier, null, Now).Error.ShouldBe(SalesErrors.LineNotFound);
        sale.ApplyDiscount(Guid.NewGuid(), line.Id, 10m, null, " no ", Cashier, null, Now).Error.ShouldBe(SalesErrors.ReasonRequired);
        sale.ApplyDiscount(Guid.NewGuid(), line.Id, 10m, null, null!, Cashier, null, Now).Error.ShouldBe(SalesErrors.ReasonRequired);
        sale.ApplyDiscount(Guid.NewGuid(), line.Id, 10m, null, new string('x', 301), Cashier, null, Now).Error.ShouldBe(SalesErrors.ReasonRequired);
        sale.ApplyDiscount(Guid.NewGuid(), line.Id, 10m, 100m, "Motivo válido", Cashier, null, Now).Error.ShouldBe(SalesErrors.InvalidDiscount);
        sale.ApplyDiscount(Guid.NewGuid(), line.Id, null, null, "Motivo válido", Cashier, null, Now).Error.ShouldBe(SalesErrors.InvalidDiscount);
        sale.ApplyDiscount(Guid.NewGuid(), line.Id, 0m, null, "Motivo válido", Cashier, null, Now).Error.ShouldBe(SalesErrors.InvalidDiscount);
        sale.ApplyDiscount(Guid.NewGuid(), line.Id, 100.01m, null, "Motivo válido", Cashier, null, Now).Error.ShouldBe(SalesErrors.InvalidDiscount);
        sale.ApplyDiscount(Guid.NewGuid(), line.Id, 10.001m, null, "Motivo válido", Cashier, null, Now).Error.ShouldBe(SalesErrors.InvalidDiscount);
        sale.ApplyDiscount(Guid.NewGuid(), line.Id, null, 0m, "Motivo válido", Cashier, null, Now).Error.ShouldBe(SalesErrors.InvalidDiscount);
        sale.ApplyDiscount(Guid.NewGuid(), line.Id, null, 10.001m, "Motivo válido", Cashier, null, Now).Error.ShouldBe(SalesErrors.InvalidDiscount);

        var first = sale.ApplyDiscount(Guid.NewGuid(), line.Id, 10m, null, "  Cliente frecuente  ", Cashier, Supervisor, Now).Value;
        (first.Scope, first.SaleLineId, first.Percent, first.Amount, first.Reason).ShouldBe(
            (DiscountScope.Line, (Guid?)line.Id, (decimal?)10m, (decimal?)null, "Cliente frecuente"));
        (first.AppliedBy, first.AuthorizedBy, first.AppliedAt, first.Status).ShouldBe((Cashier, (Guid?)Supervisor, Now, DiscountStatus.Active));

        var second = sale.ApplyDiscount(Guid.NewGuid(), line.Id, null, 1_000m, "Cliente frecuente", Cashier, Supervisor, Now).Value;
        first.Status.ShouldBe(DiscountStatus.Removed);
        var global = sale.ApplyDiscount(Guid.NewGuid(), null, 5m, null, "Descuento de gerencia", Cashier, Supervisor, Now).Value;
        global.Scope.ShouldBe(DiscountScope.Global);
        second.Status.ShouldBe(DiscountStatus.Active);

        // 11.900 − 1.000 = 10.900; − 5 % global = 10.355.
        sale.Recalculate([]);
        (sale.Gross, sale.DiscountTotal, sale.Total).ShouldBe((11_900m, 1_545m, 10_355m));

        sale.RemoveDiscount(global.Id).IsSuccess.ShouldBeTrue();
        sale.RemoveDiscount(global.Id).Error.ShouldBe(SalesErrors.InvalidDiscount);
        sale.Recalculate([]).Total.ShouldBe(10_900m);
        sale.Discounts.Count.ShouldBe(3);
    }

    [Fact]
    public void Cambia_el_cliente_mientras_esta_abierta()
    {
        var sale = NewSale();
        var party = Guid.NewGuid();

        sale.SetCustomer(new CustomerSnapshot(party, "Ana Pérez", "CC", "52123456", "ana@example.com"), SalePricing.General, false).IsSuccess.ShouldBeTrue();

        (sale.CustomerId, sale.CustomerName, sale.CustomerIdentification, sale.CustomerEmail).ShouldBe(((Guid?)party, "Ana Pérez", "52123456", (string?)"ana@example.com"));
        Should.Throw<ArgumentNullException>(() => sale.SetCustomer(null!, SalePricing.General, false));
    }

    [Fact]
    public void Suspender_y_recuperar()
    {
        var sale = NewSale();
        sale.Hold("Cliente fue por la billetera", Now).Error.ShouldBe(SalesErrors.EmptySale);
        sale.Resume().Error.ShouldBe(SalesErrors.InvalidStatus);
        var line = sale.AddLine(Input(), true, Guid.NewGuid).Value;

        sale.Hold("  " + new string('a', 70) + "  ", Now).IsSuccess.ShouldBeTrue();
        sale.Status.ShouldBe(SaleStatus.OnHold);
        sale.HoldLabel!.Length.ShouldBe(60);
        sale.HeldAt.ShouldBe(Now);

        // Suspendida no se edita.
        sale.AddLine(Input(), true, Guid.NewGuid).Error.ShouldBe(SalesErrors.NotOpen);
        sale.ChangeQuantity(line.Id, 2m).Error.ShouldBe(SalesErrors.NotOpen);
        sale.VoidLine(line.Id, Cashier, Now).Error.ShouldBe(SalesErrors.NotOpen);
        sale.OverridePrice(line.Id, 1m, null).Error.ShouldBe(SalesErrors.NotOpen);
        sale.ApplyDiscount(Guid.NewGuid(), null, 5m, null, "Motivo válido", Cashier, null, Now).Error.ShouldBe(SalesErrors.NotOpen);
        sale.RemoveDiscount(Guid.NewGuid()).Error.ShouldBe(SalesErrors.NotOpen);
        sale.SetCustomer(ConsumidorFinal, SalePricing.General, false).Error.ShouldBe(SalesErrors.NotOpen);
        sale.Hold(null, Now).Error.ShouldBe(SalesErrors.NotOpen);

        sale.Resume().IsSuccess.ShouldBeTrue();
        sale.Status.ShouldBe(SaleStatus.Open);
        sale.Hold("   ", Now).IsSuccess.ShouldBeTrue();
        sale.HoldLabel.ShouldBeNull();
        sale.Resume();
        sale.Hold(" Mesa 2 ", Now).IsSuccess.ShouldBeTrue();
        sale.HoldLabel.ShouldBe("Mesa 2");
    }

    [Fact]
    public void Cancelar_abierta_o_suspendida_con_motivo()
    {
        var sale = NewSale();
        sale.Cancel("no", Cashier, null, Now).Error.ShouldBe(SalesErrors.ReasonRequired);
        sale.Cancel(null!, Cashier, null, Now).Error.ShouldBe(SalesErrors.ReasonRequired);

        sale.Cancel(" Cliente desistió ", Cashier, Supervisor, Now).IsSuccess.ShouldBeTrue();
        (sale.Status, sale.CancelReason, sale.CancelledBy, sale.CancelAuthorizedBy, sale.CancelledAt).ShouldBe(
            (SaleStatus.Cancelled, (string?)"Cliente desistió", (Guid?)Cashier, (Guid?)Supervisor, (DateTimeOffset?)Now));
        sale.Number.ShouldBeNull();
        sale.Cancel("Cliente desistió", Cashier, null, Now).Error.ShouldBe(SalesErrors.InvalidStatus);

        var held = NewSale();
        held.AddLine(Input(), true, Guid.NewGuid);
        held.Hold(null, Now);
        held.Cancel("Venta olvidada", Cashier, null, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Recalcular_deja_en_cero_las_lineas_eliminadas_y_sus_impuestos()
    {
        var sale = NewSale();
        var kept = sale.AddLine(Input(quantity: 2m), true, Guid.NewGuid).Value;
        var removed = sale.AddLine(Input(product: Queso, taxes: [Iva19, Bolsa, new PricingTax(K(999), "EXCL", "EXCLUIDO", null, null)]), true, Guid.NewGuid).Value;
        sale.Recalculate([]);
        removed.Taxes[1].Amount.ShouldBe(66m);
        removed.Taxes[2].Amount.ShouldBe(0m);

        sale.VoidLine(removed.Id, Cashier, Now);
        var priced = sale.Recalculate([]);

        priced.Lines.Count.ShouldBe(1);
        (kept.Gross, kept.TaxBase, kept.TaxTotal, kept.Total).ShouldBe((23_800m, 20_000m, 3_800m, 23_800m));
        (kept.Taxes.Single().TaxBase, kept.Taxes.Single().Amount).ShouldBe((20_000m, 3_800m));
        (kept.Taxes.Single().TaxId, kept.Taxes.Single().Kind, kept.Taxes.Single().Rate, kept.Taxes.Single().FixedAmount).ShouldBe((Iva19.TaxId, "IVA", (decimal?)19m, (decimal?)null));
        (removed.Gross, removed.Total, removed.PromotionId, removed.PromotionName).ShouldBe((0m, 0m, (Guid?)null, (string?)null));
        (removed.PromotionDiscount, removed.LineDiscount, removed.GlobalDiscountShare, removed.TaxBase, removed.TaxTotal).ShouldBe((0m, 0m, 0m, 0m, 0m));
        removed.Taxes.ShouldAllBe(t => t.Amount == 0m && t.TaxBase == 0m);
        (sale.Gross, sale.PromotionTotal, sale.Subtotal, sale.TaxTotal, sale.Total, sale.RoundingAdjustment).ShouldBe((23_800m, 0m, 20_000m, 3_800m, 23_800m, 0m));
    }

    [Fact]
    public void Recalcular_aplica_promociones_a_las_lineas()
    {
        var sale = NewSale();
        var line = sale.AddLine(Input(quantity: 3m, price: 1_000m), true, Guid.NewGuid).Value;
        var rule = new PromotionRule(Guid.NewGuid(), "3x2 arroz", PromotionKind.MultiBuy, [new PromotionTarget(Arroz)], 3, 2, TicketText: "3x2");

        sale.Recalculate([rule]);

        (line.PromotionId, line.PromotionName, line.PromotionDiscount, line.Total).ShouldBe(((Guid?)rule.Id, (string?)"3x2", 1_000m, 2_000m));
        sale.PromotionTotal.ShouldBe(1_000m);
    }

    [Fact]
    public void Completar_asigna_numero_pagos_y_redondeo()
    {
        var sale = NewSale();
        sale.AddLine(Input(quantity: 1m, price: 4_820m), true, Guid.NewGuid);
        sale.Recalculate([]);
        var allocation = PaymentAllocator.Allocate(sale.Total, [Tarjeta(2_000m), Efectivo(5_000m)], 50m).Value;

        sale.Complete("C1-000045", allocation, "clave-45", Now, Guid.NewGuid).IsSuccess.ShouldBeTrue();

        (sale.Status, sale.Number, sale.CompletionKey, sale.CompletedAt).ShouldBe((SaleStatus.Completed, (string?)"C1-000045", (string?)"clave-45", (DateTimeOffset?)Now));
        (sale.RoundingAdjustment, sale.Total, sale.PaidTotal, sale.ChangeTotal).ShouldBe((-20m, 4_800m, 7_000m, 2_200m));
        sale.AuditLabel.ShouldBe("Venta C1-000045");
        sale.Payments.Count.ShouldBe(2);
        var card = sale.Payments[0];
        (card.LineNo, card.PaymentMethodId, card.MethodCode, card.MethodKind, card.AffectsCashDrawer).ShouldBe((1, DatafonoId, "DATAFONO", "DEBIT_CARD", false));
        (card.Tendered, card.Applied, card.Change, card.Reference, card.CardFranchise, card.CardLast4).ShouldBe(
            (2_000m, 2_000m, 0m, (string?)"123456", (string?)"VISA", (string?)"4242"));
        var cash = sale.Payments[1];
        (cash.LineNo, cash.Tendered, cash.Applied, cash.Change, cash.AffectsCashDrawer).ShouldBe((2, 5_000m, 2_800m, 2_200m, true));

        // Completada no se edita ni se vuelve a completar.
        sale.Complete("C1-000046", allocation, "clave-46", Now, Guid.NewGuid).Error.ShouldBe(SalesErrors.NotOpen);
        sale.AddLine(Input(), true, Guid.NewGuid).Error.ShouldBe(SalesErrors.NotOpen);
        sale.Cancel("Cliente desistió", Cashier, null, Now).Error.ShouldBe(SalesErrors.InvalidStatus);
        sale.Resume().Error.ShouldBe(SalesErrors.InvalidStatus);
    }

    [Fact]
    public void Completar_exige_productos_con_precio_y_pagos_que_cuadren()
    {
        var sale = NewSale();
        var allocation = PaymentAllocator.Allocate(11_900m, [Efectivo(11_900m)], 50m).Value;
        sale.Complete("C1-1", allocation, "k", Now, Guid.NewGuid).Error.ShouldBe(SalesErrors.EmptySale);

        var open = sale.AddLine(Input(product: Queso, price: 0m, allowsOpenPrice: true), true, Guid.NewGuid).Value;
        sale.Complete("C1-1", allocation, "k", Now, Guid.NewGuid).Error.ShouldBe(SalesErrors.PriceRequired);
        sale.VoidLine(open.Id, Cashier, Now);

        sale.AddLine(Input(), true, Guid.NewGuid);
        sale.Recalculate([]);
        var other = PaymentAllocator.Allocate(10_000m, [Efectivo(10_000m)], 50m).Value;
        sale.Complete("C1-1", other, "k", Now, Guid.NewGuid).Error.ShouldBe(SalesErrors.InsufficientPayment);
        sale.Complete("C1-1", allocation with { Paid = 11_000m }, "k", Now, Guid.NewGuid).Error.ShouldBe(SalesErrors.InsufficientPayment);
        Should.Throw<ArgumentNullException>(() => sale.Complete("C1-1", null!, "k", Now, Guid.NewGuid));
        Should.Throw<ArgumentNullException>(() => sale.Complete("C1-1", allocation, "k", Now, null!));
        sale.Complete("C1-1", allocation, "k", Now, Guid.NewGuid).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Costo_de_linea_fijado_por_el_kardex()
    {
        var sale = NewSale();
        var line = sale.AddLine(Input(quantity: 2m), true, Guid.NewGuid).Value;
        CompleteInCash(sale);
        var lot = Guid.NewGuid();

        sale.SetLineCost(line.Id, 7_500.1234m, 15_000.25m, lot);

        (line.UnitCost, line.CostTotal, line.LotId).ShouldBe(((decimal?)7_500.1234m, (decimal?)15_000.25m, (Guid?)lot));
    }

    [Fact]
    public void Anular_solo_completada_sin_cambios_y_con_motivo()
    {
        var open = NewSale();
        open.Void("Error de digitación", Cashier, Supervisor, Now).Error.ShouldBe(SalesErrors.VoidNotAllowed);

        var sale = NewSale();
        sale.AddLine(Input(), true, Guid.NewGuid);
        CompleteInCash(sale);
        sale.Void("nop", Cashier, Supervisor, Now).Error.ShouldBe(SalesErrors.ReasonRequired);
        sale.Void(null!, Cashier, Supervisor, Now).Error.ShouldBe(SalesErrors.ReasonRequired);

        sale.Void(" Error de digitación ", Cashier, Supervisor, Now).IsSuccess.ShouldBeTrue();
        (sale.Status, sale.VoidReason, sale.VoidedBy, sale.VoidAuthorizedBy, sale.VoidedAt).ShouldBe(
            (SaleStatus.Voided, (string?)"Error de digitación", (Guid?)Cashier, (Guid?)Supervisor, (DateTimeOffset?)Now));
        sale.Void("Error de digitación", Cashier, Supervisor, Now).Error.ShouldBe(SalesErrors.VoidNotAllowed);

        var returned = NewSale();
        var line = returned.AddLine(Input(quantity: 2m), true, Guid.NewGuid).Value;
        CompleteInCash(returned);
        returned.RegisterReturned(line.Id, 1m);
        returned.Void("Error de digitación", Cashier, Supervisor, Now).Error.ShouldBe(SalesErrors.VoidNotAllowed);
    }

    [Fact]
    public void Registrar_lo_cambiado_actualiza_el_estado_de_devolucion()
    {
        var sale = NewSale();
        var arroz = sale.AddLine(Input(quantity: 2m), true, Guid.NewGuid).Value;
        var queso = sale.AddLine(Input(product: Queso), true, Guid.NewGuid).Value;
        var anulada = sale.AddLine(Input(product: K(1199)), true, Guid.NewGuid).Value;
        sale.VoidLine(anulada.Id, Cashier, Now);
        sale.RegisterReturned(arroz.Id, 1m).Error.ShouldBe(SalesErrors.ExchangeNotAllowed);
        CompleteInCash(sale);

        sale.RegisterReturned(Guid.NewGuid(), 1m).Error.ShouldBe(SalesErrors.LineNotFound);
        sale.RegisterReturned(anulada.Id, 1m).Error.ShouldBe(SalesErrors.LineNotFound);
        sale.RegisterReturned(arroz.Id, 0m).Error.ShouldBe(SalesErrors.ExchangeQuantityExceeded);
        sale.RegisterReturned(arroz.Id, 3m).Error.ShouldBe(SalesErrors.ExchangeQuantityExceeded);

        sale.RegisterReturned(arroz.Id, 2m).IsSuccess.ShouldBeTrue();
        sale.ReturnStatus.ShouldBe(SaleReturnStatus.Partial);
        arroz.ReturnedQuantity.ShouldBe(2m);
        sale.RegisterReturned(arroz.Id, 1m).Error.ShouldBe(SalesErrors.ExchangeQuantityExceeded);
        sale.RegisterReturned(queso.Id, 1m).IsSuccess.ShouldBeTrue();
        sale.ReturnStatus.ShouldBe(SaleReturnStatus.Full);
    }
}
