using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase7;

/// <summary>
/// La venta informa lo que se cobra en efectivo (<c>CashAmountDue</c>, redondeado a $50 con la misma regla del cobro, D7-09): la caja
/// lo usa como total en efectivo. Pagar ese valor exacto se acepta; pagar <c>AmountDue</c> sin redondear, cuando el redondeo sube, no.
/// </summary>
public class CashAmountDueTests
{
    [Fact]
    public async Task Cobrar_exactamente_CashAmountDue_en_efectivo_es_aceptado_y_AmountDue_sin_redondear_no()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        var up = (await shop.Catalog.ProductAsync("RED-ARRIBA", "Aceite 1 L", shop.Groceries, price: 10_890m)).Id;
        var down = (await shop.Catalog.ProductAsync("RED-ABAJO", "Café 500 g", shop.Groceries, price: 10_874m)).Id;
        var exact = (await shop.Catalog.ProductAsync("RED-EXACTO", "Panela 1 kg", shop.Groceries, price: 10_900m)).Id;
        await shop.Purchasing.BuyAsync(
            "FE-RED-1",
            new { productId = up, quantity = 10, unitCost = 5_000 },
            new { productId = down, quantity = 10, unitCost = 5_000 },
            new { productId = exact, quantity = 10, unitCost = 5_000 });
        await shop.OpenSessionAsync();

        // 10.890 → en efectivo 10.900 (+10). El valor sin redondear se rechaza con lo que falta.
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, up, 1m);
        sale.AmountDue.ShouldBe(10_890m);
        sale.CashAmountDue.ShouldBe(10_900m);
        sale.CashRoundingAdjustment.ShouldBe(10m);
        sale.CashRoundingIncrement.ShouldBe(50m);
        (await GetAsync<SaleDto>(shop.Cashier, "/api/v1/sales/current")).CashAmountDue.ShouldBe(10_900m);
        await shop.Cashier.PostAsJsonAsync($"/api/v1/sales/{sale.Id}/complete", new { payments = new[] { Pay(shop.Cash, sale.AmountDue) } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.INSUFFICIENT_PAYMENT");
        var done = (await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, sale.CashAmountDue)])).Sale;
        done.Total.ShouldBe(10_900m);
        done.RoundingAdjustment.ShouldBe(10m);
        done.ChangeTotal.ShouldBe(0m);
        done.CashAmountDue.ShouldBe(done.AmountDue);

        // 10.874 → 10.850 (hacia abajo): se acepta el valor redondeado.
        sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, down, 1m);
        sale.CashAmountDue.ShouldBe(10_850m);
        sale.CashRoundingAdjustment.ShouldBe(-24m);
        (await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, sale.CashAmountDue)])).Sale.Total.ShouldBe(10_850m);

        // Ya múltiplo de 50: sin ajuste.
        sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, exact, 1m);
        (sale.CashAmountDue, sale.CashRoundingAdjustment).ShouldBe((10_900m, 0m));
        (await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, sale.CashAmountDue)])).Sale.Total.ShouldBe(10_900m);

        // Mixto: el redondeo aplica solo a la parte en efectivo (10.890 − 4.000 en débito = 6.890 → 6.900).
        sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, up, 1m);
        await shop.Cashier.PostAsJsonAsync(
                $"/api/v1/sales/{sale.Id}/complete", new { payments = new[] { Pay(shop.Debit, 4_000m, "APR-1"), Pay(shop.Cash, 6_890m) } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "SALES.INSUFFICIENT_PAYMENT");
        var mixed = (await shop.CompleteAsync(sale.Id, [Pay(shop.Debit, 4_000m, "APR-1"), Pay(shop.Cash, 6_900m)])).Sale;
        mixed.Total.ShouldBe(10_900m);
        mixed.RoundingAdjustment.ShouldBe(10m);
        mixed.ChangeTotal.ShouldBe(0m);
    }
}
