using Pos.Modules.Sales.Domain;
using Pos.SharedKernel.Results;
using static Pos.Modules.Sales.UnitTests.Fx;

namespace Pos.Modules.Sales.UnitTests;

public class CustomerReturnTests
{
    private static readonly DateOnly Later = Today.AddDays(10);

    /// <summary>Venta completada: 3 × 3.500 con $500 de descuento (pagado 10.000) y 1,5 kg de queso × 8.000 (12.000).</summary>
    private static (Sale Sale, SaleLine Arroz, SaleLine Queso) CompletedSale()
    {
        var sale = NewSale();
        var arroz = sale.AddLine(Input(quantity: 3m, price: 3_500m), true, Guid.NewGuid).Value;
        var queso = sale.AddLine(Input(product: Queso, quantity: 1.5m, price: 8_000m, allowsDecimal: true, source: "SCALE_WEIGHT"), true, Guid.NewGuid).Value;
        sale.ApplyDiscount(Guid.NewGuid(), arroz.Id, null, 500m, "Empaque roto", Cashier, Supervisor, Now);
        CompleteInCash(sale, "C1-000100");
        return (sale, arroz, queso);
    }

    private static Result<CustomerReturn> Create(Sale sale, IReadOnlyList<ReturnLineRequest> lines, ReturnKind kind = ReturnKind.Exchange,
        string reason = "Talla equivocada", DateOnly? businessDate = null, int maxDays = 30) =>
        CustomerReturn.Create(Guid.NewGuid(), kind, sale, lines, reason, Terminal, businessDate ?? Later, maxDays, Cashier, Supervisor, Now, Guid.NewGuid);

    [Fact]
    public void Credito_proporcional_y_el_saldo_exacto_al_completar_la_linea()
    {
        var (sale, arroz, _) = CompletedSale();
        arroz.Total.ShouldBe(10_000m);

        CustomerReturn.CreditFor(arroz, 1m).ShouldBe(3_333.33m);
        CustomerReturn.CreditFor(arroz, 3m).ShouldBe(10_000m);
        sale.RegisterReturned(arroz.Id, 1m);
        CustomerReturn.CreditFor(arroz, 1m).ShouldBe(3_333.33m);
        CustomerReturn.CreditFor(arroz, 2m).ShouldBe(6_666.67m);
        Should.Throw<ArgumentNullException>(() => CustomerReturn.CreditFor(null!, 1m));
    }

    [Fact]
    public void Crea_el_cambio_en_borrador_con_el_credito_de_lo_pagado()
    {
        var (sale, arroz, queso) = CompletedSale();
        var lot = Guid.NewGuid();
        sale.SetLineCost(arroz.Id, 2_100m, 6_300m, lot);

        var result = Create(sale, [new(arroz.Id, 2m, ReturnDestination.ReturnToStock), new(queso.Id, 0.5m, ReturnDestination.Discard)]).Value;

        result.Status.ShouldBe(ReturnStatus.Draft);
        result.Kind.ShouldBe(ReturnKind.Exchange);
        (result.CompanyId, result.BranchId, result.PosTerminalId, result.OriginalSaleId, result.OriginalSaleNumber).ShouldBe(
            (Company, Branch, Terminal, sale.Id, "C1-000100"));
        (result.BusinessDate, result.Reason, result.ReceivedBy, result.AuthorizedBy, result.ReceivedAt).ShouldBe(
            (Later, "Talla equivocada", Cashier, (Guid?)Supervisor, Now));
        result.CashSessionId.ShouldBeNull();
        result.Number.ShouldBeNull();
        result.CreditTotal.ShouldBe(6_666.67m + 4_000m);
        result.AuditLabel.ShouldBe($"Cambio {result.Id} de la venta C1-000100");

        var first = result.Lines[0];
        (first.SaleLineId, first.ProductId, first.Sku, first.Name, first.Quantity, first.BaseQuantity).ShouldBe(
            (arroz.Id, Arroz, "SKU-ARROZ", "Arroz 500 g", 2m, 2m));
        (first.UnitCost, first.LotId, first.CreditAmount, first.Destination).ShouldBe((2_100m, (Guid?)lot, 6_666.67m, ReturnDestination.ReturnToStock));
        var second = result.Lines[1];
        (second.UnitCost, second.LotId, second.CreditAmount, second.Destination).ShouldBe((0m, (Guid?)null, 4_000m, ReturnDestination.Discard));
    }

    [Fact]
    public void Solo_ventas_completadas_sin_devolucion_total_y_dentro_del_plazo()
    {
        var open = NewSale();
        open.AddLine(Input(), true, Guid.NewGuid);
        Create(open, [new(open.Lines[0].Id, 1m, ReturnDestination.ReturnToStock)]).Error.ShouldBe(SalesErrors.ExchangeNotAllowed);

        var (sale, arroz, queso) = CompletedSale();
        Create(sale, [new(arroz.Id, 1m, ReturnDestination.ReturnToStock)], businessDate: Today.AddDays(31)).Error.ShouldBe(SalesErrors.ExchangeNotAllowed);
        Create(sale, [new(arroz.Id, 1m, ReturnDestination.ReturnToStock)], businessDate: Today.AddDays(30)).IsSuccess.ShouldBeTrue();

        sale.RegisterReturned(arroz.Id, 3m);
        sale.RegisterReturned(queso.Id, 1.5m);
        Create(sale, [new(arroz.Id, 1m, ReturnDestination.ReturnToStock)]).Error.ShouldBe(SalesErrors.ExchangeNotAllowed);
    }

    [Fact]
    public void Validaciones_del_motivo_y_de_las_lineas()
    {
        var (sale, arroz, queso) = CompletedSale();
        var ok = new ReturnLineRequest(arroz.Id, 1m, ReturnDestination.ReturnToStock);

        Create(sale, [ok], reason: " ojo ").Error.ShouldBe(SalesErrors.ReasonRequired);
        Create(sale, [ok], reason: null!).Error.ShouldBe(SalesErrors.ReasonRequired);
        Create(sale, [ok], reason: new string('x', 301)).Error.ShouldBe(SalesErrors.ReasonRequired);
        Create(sale, []).Error.ShouldBe(SalesErrors.InvalidExchange);
        Create(sale, [ok, ok]).Error.ShouldBe(SalesErrors.InvalidExchange);
        Create(sale, [ok with { Quantity = 0m }]).Error.ShouldBe(SalesErrors.InvalidExchange);
        Create(sale, [ok with { Destination = (ReturnDestination)99 }]).Error.ShouldBe(SalesErrors.InvalidExchange);
        Create(sale, [ok with { SaleLineId = Guid.NewGuid() }]).Error.ShouldBe(SalesErrors.LineNotFound);
        Create(sale, [ok with { Quantity = 4m }]).Error.ShouldBe(SalesErrors.ExchangeQuantityExceeded);
        Create(sale, [ok with { Quantity = 1.5m }]).Error.ShouldBe(SalesErrors.ExchangeQuantityExceeded);
        Create(sale, [new(queso.Id, 0.25m, ReturnDestination.SendToDamaged)]).IsSuccess.ShouldBeTrue();

        sale.RegisterReturned(arroz.Id, 2m);
        Create(sale, [ok with { Quantity = 2m }]).Error.ShouldBe(SalesErrors.ExchangeQuantityExceeded);

        Should.Throw<ArgumentNullException>(() => CustomerReturn.Create(Guid.NewGuid(), ReturnKind.Exchange, null!, [ok], "Motivo válido", Terminal, Later, 30, Cashier, null, Now, Guid.NewGuid));
        Should.Throw<ArgumentNullException>(() => CustomerReturn.Create(Guid.NewGuid(), ReturnKind.Exchange, sale, null!, "Motivo válido", Terminal, Later, 30, Cashier, null, Now, Guid.NewGuid));
        Should.Throw<ArgumentNullException>(() => CustomerReturn.Create(Guid.NewGuid(), ReturnKind.Exchange, sale, [ok], "Motivo válido", Terminal, Later, 30, Cashier, null, Now, null!));
    }

    [Fact]
    public void Completar_o_cancelar_solo_en_borrador()
    {
        var (sale, arroz, _) = CompletedSale();
        var exchange = Create(sale, [new(arroz.Id, 1m, ReturnDestination.ReturnToStock)]).Value;
        var replacement = Guid.NewGuid();
        exchange.LinkReplacementSale(replacement);
        exchange.ReplacementSaleId.ShouldBe(replacement);

        exchange.Complete("CM-000001", Session, null, Now).IsSuccess.ShouldBeTrue();
        (exchange.Status, exchange.Number, exchange.CashSessionId, exchange.RefundPaymentMethodId, exchange.CompletedAt).ShouldBe(
            (ReturnStatus.Completed, (string?)"CM-000001", (Guid?)Session, (Guid?)null, (DateTimeOffset?)Now));
        exchange.AuditLabel.ShouldBe("Cambio CM-000001 de la venta C1-000100");
        exchange.Complete("CM-000002", Session, null, Now).Error.ShouldBe(SalesErrors.InvalidStatus);
        exchange.Cancel(Now).Error.ShouldBe(SalesErrors.InvalidStatus);

        var warranty = Create(sale, [new(arroz.Id, 1m, ReturnDestination.SendToDamaged)], ReturnKind.WarrantyRefund).Value;
        warranty.AuditLabel.ShouldBe($"Reintegro por garantía {warranty.Id} de la venta C1-000100");
        warranty.Cancel(Now).IsSuccess.ShouldBeTrue();
        (warranty.Status, warranty.CancelledAt).ShouldBe((ReturnStatus.Cancelled, (DateTimeOffset?)Now));
        warranty.Complete("GR-000001", Session, EfectivoId, Now).Error.ShouldBe(SalesErrors.InvalidStatus);
        warranty.Cancel(Now).Error.ShouldBe(SalesErrors.InvalidStatus);

        var refund = Create(sale, [new(arroz.Id, 1m, ReturnDestination.SendToDamaged)], ReturnKind.WarrantyRefund).Value;
        refund.Complete("GR-000002", Session, EfectivoId, Now).IsSuccess.ShouldBeTrue();
        refund.RefundPaymentMethodId.ShouldBe(EfectivoId);
    }
}
