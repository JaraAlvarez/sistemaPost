using Pos.Modules.Sales.Application;
using Pos.Modules.Sales.Domain;
using static Pos.Modules.Sales.UnitTests.Fx;

namespace Pos.Modules.Sales.UnitTests;

/// <summary>
/// Lo que la venta informa a la caja para cobrar en efectivo (<c>SaleDto.CashAmountDue</c>): la misma regla con la que
/// <see cref="PaymentAllocator"/> valida el cobro (múltiplo más cercano, punto medio hacia arriba).
/// </summary>
public class CashAmountDueTests
{
    [Theory]
    [InlineData(10_890, 10_900)]
    [InlineData(10_874, 10_850)]
    [InlineData(10_875, 10_900)]
    [InlineData(10_900, 10_900)]
    [InlineData(20, 0)]
    public void Efectivo_por_cobrar_es_el_multiplo_de_50_mas_cercano(int remaining, int expected) =>
        PaymentAllocator.CashDue(remaining, 50m).ShouldBe(expected);

    [Fact]
    public void Sin_multiplo_el_efectivo_es_exacto() => PaymentAllocator.CashDue(10_890m, 0m).ShouldBe(10_890m);

    [Theory]
    [InlineData(10_890, 10_900, 10)]
    [InlineData(10_874, 10_850, -24)]
    [InlineData(10_900, 10_900, 0)]
    public void La_venta_expone_el_total_en_efectivo_y_su_ajuste(int price, int cashDue, int adjustment)
    {
        var sale = VentaDe(price);

        var dto = sale.ToDto(50m);

        dto.AmountDue.ShouldBe(price);
        dto.CashAmountDue.ShouldBe(cashDue);
        dto.CashRoundingAdjustment.ShouldBe(adjustment);
        dto.CashRoundingIncrement.ShouldBe(50m);
    }

    [Fact]
    public void Pagar_exactamente_CashAmountDue_en_efectivo_es_aceptado_y_AmountDue_no()
    {
        var sale = VentaDe(10_890m);
        var dto = sale.ToDto(50m);

        var exact = PaymentAllocator.Allocate(sale.Total, [Efectivo(dto.CashAmountDue)], 50m);
        exact.IsSuccess.ShouldBeTrue();
        exact.Value.Change.ShouldBe(0m);
        exact.Value.Total.ShouldBe(dto.CashAmountDue);
        exact.Value.RoundingAdjustment.ShouldBe(dto.CashRoundingAdjustment);

        var unrounded = PaymentAllocator.Allocate(sale.Total, [Efectivo(dto.AmountDue)], 50m);
        unrounded.IsFailure.ShouldBeTrue();
        unrounded.Error.Code.ShouldBe(SalesErrors.InsufficientPayment.Code);
    }

    [Fact]
    public void Mixto_el_redondeo_aplica_solo_a_la_parte_en_efectivo()
    {
        // 10.890 con 4.000 en tarjeta: quedan 6.890 en efectivo → 6.900 (el total cobrado es 10.900).
        var sale = VentaDe(10_890m);

        var cashPart = PaymentAllocator.CashDue(sale.Total - 4_000m, 50m);
        cashPart.ShouldBe(6_900m);
        var allocation = PaymentAllocator.Allocate(sale.Total, [Tarjeta(4_000m), Efectivo(cashPart)], 50m).Value;
        allocation.Total.ShouldBe(10_900m);
        allocation.RoundingAdjustment.ShouldBe(10m);
        allocation.Change.ShouldBe(0m);
        PaymentAllocator.Allocate(sale.Total, [Tarjeta(4_000m), Efectivo(6_890m)], 50m).Error.Code.ShouldBe(SalesErrors.InsufficientPayment.Code);

        // 10.874 con 4.000 en tarjeta: quedan 6.874 → 6.850 (hacia abajo).
        var down = VentaDe(10_874m);
        PaymentAllocator.CashDue(down.Total - 4_000m, 50m).ShouldBe(6_850m);
        PaymentAllocator.Allocate(down.Total, [Tarjeta(4_000m), Efectivo(6_850m)], 50m).Value.Total.ShouldBe(10_850m);
    }

    [Fact]
    public void Sin_multiplo_configurado_CashAmountDue_es_AmountDue()
    {
        var dto = VentaDe(10_890m).ToDto(0m);

        dto.CashAmountDue.ShouldBe(10_890m);
        dto.CashRoundingAdjustment.ShouldBe(0m);
    }

    [Fact]
    public void Con_credito_de_cambio_se_redondea_lo_que_queda_por_pagar()
    {
        var sale = NewSale(Guid.NewGuid(), exchangeCredit: 3_000m);
        sale.AddLine(Input(price: 10_890m), merge: true, Guid.NewGuid).IsSuccess.ShouldBeTrue();
        sale.Recalculate([]);

        var dto = sale.ToDto(50m);

        dto.AmountDue.ShouldBe(7_890m);
        dto.CashAmountDue.ShouldBe(7_900m);
    }

    [Fact]
    public void Una_venta_cobrada_no_tiene_efectivo_pendiente_distinto()
    {
        var sale = VentaDe(10_890m);
        CompleteInCash(sale);

        var dto = sale.ToDto(50m);

        dto.CashAmountDue.ShouldBe(dto.AmountDue);
        dto.CashRoundingAdjustment.ShouldBe(0m);
    }

    private static Sale VentaDe(decimal price)
    {
        var sale = NewSale();
        sale.AddLine(Input(price: price), merge: true, Guid.NewGuid).IsSuccess.ShouldBeTrue();
        sale.Recalculate([]);
        sale.Total.ShouldBe(price);
        return sale;
    }
}
