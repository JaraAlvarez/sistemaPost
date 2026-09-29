using Pos.Modules.Sales.Domain;

namespace Pos.Modules.Sales.UnitTests;

/// <summary>Datos comunes: impuestos, líneas del motor, productos y ventas de prueba.</summary>
internal static class Fx
{
    public static readonly PricingTax Iva19 = new(K(901), "IVA19", "IVA", 19m, null);
    public static readonly PricingTax Iva5 = new(K(902), "IVA5", "IVA", 5m, null);

    /// <summary>Impuesto al consumo de bolsas plásticas: valor fijo por unidad base.</summary>
    public static readonly PricingTax Bolsa = new(K(903), "INC_BOLSA", "INC_BOLSAS", null, 66m);

    public static readonly Guid Company = K(1001);
    public static readonly Guid Branch = K(1002);
    public static readonly Guid Terminal = K(1003);
    public static readonly Guid Warehouse = K(1004);
    public static readonly Guid Session = K(1005);
    public static readonly Guid Cashier = K(1006);
    public static readonly Guid Supervisor = K(1007);
    public static readonly Guid Arroz = K(1101);
    public static readonly Guid Queso = K(1102);
    public static readonly Guid Granos = K(1201);
    public static readonly Guid EfectivoId = K(1301);
    public static readonly Guid DatafonoId = K(1302);

    public static readonly DateTimeOffset Now = new(2026, 9, 29, 15, 0, 0, TimeSpan.FromHours(-5));
    public static readonly DateOnly Today = new(2026, 9, 29);

    public static readonly CustomerSnapshot ConsumidorFinal = new(null, "Consumidor final", "CC", "222222222222", null);

    /// <summary>Guid determinista (el orden por clave importa en los empates del motor).</summary>
    public static Guid K(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    public static PricingLine Line(
        decimal quantity, decimal unitPrice, Guid? product = null, Guid? category = null, Guid? brand = null, Guid? packaging = null,
        decimal factor = 1m, bool includesTax = true, IReadOnlyList<PricingTax>? taxes = null, ManualDiscount? discount = null,
        bool promotionsAllowed = true, Guid? key = null) =>
        new(key ?? Guid.NewGuid(), product ?? Guid.NewGuid(), packaging, category ?? Guid.NewGuid(), brand, quantity, factor, unitPrice, includesTax,
            taxes ?? [], discount, promotionsAllowed);

    public static Tender Efectivo(decimal amount) => new(EfectivoId, "EFECTIVO", "CASH", GivesChange: true, AffectsCashDrawer: true, amount);

    public static Tender Tarjeta(decimal amount) =>
        new(DatafonoId, "DATAFONO", "DEBIT_CARD", GivesChange: false, AffectsCashDrawer: false, amount, "123456", "VISA", "4242");

    public static SaleLineInput Input(
        decimal quantity = 1m, decimal price = 11_900m, Guid? product = null, bool allowsDecimal = false, bool allowsOpenPrice = false,
        string source = "SCAN", Guid? expiredAuthorizedBy = null, IReadOnlyList<PricingTax>? taxes = null, decimal factor = 1m) =>
        new(product ?? Arroz, "SKU-ARROZ", "Arroz 500 g", "7701234567890", source, "UND", null, null, factor, quantity, price, true, Granos, null,
            IsStockable: true, allowsDecimal, allowsOpenPrice, taxes ?? [Iva19], expiredAuthorizedBy);

    public static Sale NewSale(Guid? exchangeId = null, decimal exchangeCredit = 0m) =>
        Sale.Start(Guid.NewGuid(), Company, Branch, Terminal, Warehouse, Session, Cashier, Today, Now, ConsumidorFinal, exchangeId, exchangeCredit);

    /// <summary>Cobra la venta en efectivo exacto (redondeado a $50) y la deja completada.</summary>
    public static void CompleteInCash(Sale sale, string number = "C1-000001")
    {
        sale.Recalculate([]);
        var allocation = PaymentAllocator.Allocate(sale.Total, [Efectivo(sale.Total + 50m)], 50m).Value;
        sale.Complete(number, allocation, "clave-1", Now, Guid.NewGuid).IsSuccess.ShouldBeTrue();
    }
}
