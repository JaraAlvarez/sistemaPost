using Pos.Modules.Sales.Domain;
using static Pos.Modules.Sales.UnitTests.Fx;

namespace Pos.Modules.Sales.UnitTests;

public class PaymentAllocatorTests
{
    [Fact]
    public void Efectivo_se_redondea_a_50_hacia_abajo_y_da_cambio()
    {
        var allocation = PaymentAllocator.Allocate(4_820m, [Efectivo(5_000m)], 50m).Value;

        allocation.RoundingAdjustment.ShouldBe(-20m);
        allocation.Total.ShouldBe(4_800m);
        allocation.Paid.ShouldBe(5_000m);
        allocation.Change.ShouldBe(200m);
        var payment = allocation.Payments.Single();
        (payment.Applied, payment.Change).ShouldBe((4_800m, 200m));
    }

    [Fact]
    public void Efectivo_se_redondea_a_50_hacia_arriba_en_el_punto_medio()
    {
        var allocation = PaymentAllocator.Allocate(4_825m, [Efectivo(10_000m)], 50m).Value;

        allocation.RoundingAdjustment.ShouldBe(25m);
        allocation.Total.ShouldBe(4_850m);
        allocation.Change.ShouldBe(5_150m);
    }

    [Fact]
    public void Sin_multiplo_de_efectivo_no_redondea()
    {
        var allocation = PaymentAllocator.Allocate(4_820m, [Efectivo(5_000m)], 0m).Value;

        allocation.RoundingAdjustment.ShouldBe(0m);
        allocation.Total.ShouldBe(4_820m);
        allocation.Change.ShouldBe(180m);
    }

    [Fact]
    public void Primero_los_medios_sin_cambio_y_el_efectivo_cubre_el_resto()
    {
        var allocation = PaymentAllocator.Allocate(30_000m, [Efectivo(20_000m), Tarjeta(20_000m)], 50m).Value;

        allocation.Payments.Count.ShouldBe(2);
        allocation.Payments[0].Tender.Kind.ShouldBe("DEBIT_CARD");
        (allocation.Payments[0].Applied, allocation.Payments[0].Change).ShouldBe((20_000m, 0m));
        allocation.Payments[1].Tender.Kind.ShouldBe("CASH");
        (allocation.Payments[1].Applied, allocation.Payments[1].Change).ShouldBe((10_000m, 10_000m));
        allocation.Paid.ShouldBe(40_000m);
        (allocation.Paid - allocation.Change).ShouldBe(allocation.Total);
        allocation.Payments.Sum(p => p.Applied).ShouldBe(allocation.Total);
    }

    [Fact]
    public void Tarjeta_exacta_sin_efectivo_no_redondea()
    {
        var allocation = PaymentAllocator.Allocate(25_010m, [Tarjeta(25_010m)], 50m).Value;

        allocation.RoundingAdjustment.ShouldBe(0m);
        allocation.Total.ShouldBe(25_010m);
        allocation.Change.ShouldBe(0m);
        allocation.Payments.Single().Tender.CardLast4.ShouldBe("4242");
    }

    [Fact]
    public void Residuo_menor_que_media_moneda_se_absorbe_con_el_efectivo_en_cero()
    {
        var allocation = PaymentAllocator.Allocate(4_820m, [Tarjeta(4_800m), Efectivo(0m)], 50m).Value;

        allocation.RoundingAdjustment.ShouldBe(-20m);
        allocation.Total.ShouldBe(4_800m);
        allocation.Payments[1].Applied.ShouldBe(0m);
        allocation.Change.ShouldBe(0m);
    }

    [Fact]
    public void Pagos_invalidos()
    {
        PaymentAllocator.Allocate(1_000m, [], 50m).Error.ShouldBe(SalesErrors.InvalidPayment);
        PaymentAllocator.Allocate(1_000m, [Efectivo(-1m)], 50m).Error.ShouldBe(SalesErrors.InvalidPayment);
        PaymentAllocator.Allocate(1_000m, [Efectivo(1_000.005m)], 50m).Error.ShouldBe(SalesErrors.InvalidPayment);
        PaymentAllocator.Allocate(1_000m, [Tarjeta(0m), Efectivo(1_000m)], 50m).Error.ShouldBe(SalesErrors.InvalidPayment);
        PaymentAllocator.Allocate(1_000m, [Efectivo(500m), Efectivo(500m)], 50m).Error.ShouldBe(SalesErrors.SingleCashTender);
        PaymentAllocator.Allocate(1_000m, [Tarjeta(1_500m)], 50m).Error.ShouldBe(SalesErrors.NonCashOverpayment);
        PaymentAllocator.Allocate(1_000m, [Tarjeta(1_000m), Efectivo(500m)], 50m).Error.ShouldBe(SalesErrors.CashNotNeeded);
        Should.Throw<ArgumentNullException>(() => PaymentAllocator.Allocate(1_000m, null!, 50m));
    }

    [Fact]
    public void Pagos_insuficientes_informan_cuanto_falta()
    {
        var cash = PaymentAllocator.Allocate(10_000m, [Tarjeta(4_000m), Efectivo(5_000m)], 50m);
        cash.Error.Code.ShouldBe(SalesErrors.InsufficientPayment.Code);
        cash.Error.Message.ShouldContain("Faltan");

        var card = PaymentAllocator.Allocate(10_000m, [Tarjeta(4_000m)], 50m);
        card.Error.Code.ShouldBe(SalesErrors.InsufficientPayment.Code);
        card.Error.Message.ShouldContain("Faltan");
    }
}
