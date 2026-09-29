using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Promotions.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using Pos.Server.IntegrationTests.Phase5;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;
using static Pos.Server.IntegrationTests.Phase7.SalesScenario;

namespace Pos.Server.IntegrationTests.Phase7;

/// <summary>Promociones (propuesta de la Fase 7, bloque 7.4): administración por el encargado, simulador, aplicación en la caja y reporte.</summary>
public class PromotionsApiTests
{
    [Fact]
    public async Task Encargado_crea_simula_y_activa_la_caja_aplica_la_mas_favorable_y_el_reporte_suma_los_descuentos()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.Security.CreateUserAsync("encargado", "PROMOTIONS_MANAGER");
        var manager = await shop.Security.LocalClientAsync("encargado");
        var from = DateTimeOffset.UtcNow.AddHours(-1);

        // Solo el encargado administra promociones: la cajera no crea (403).
        var threeForTwo = Promotion("Arroz 3x2", "MultiBuy", from, new { productId = shop.Rice }, buy: 3, pay: 2);
        (await shop.Cashier.PostAsJsonAsync("/api/v1/promotions", threeForTwo, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var rice3x2 = await PostAsync<PromotionDto>(manager, "/api/v1/promotions", threeForTwo, HttpStatusCode.Created);
        rice3x2.Status.ShouldBe("DRAFT");

        // Simulador: mismo motor de la caja, con la promoción aún en borrador.
        var simulation = await PostAsync<SimulationDto>(manager, $"/api/v1/promotions/{rice3x2.Id}/simulate", new[] { new { productId = shop.Rice, quantity = 3 } });
        simulation.Gross.ShouldBe(9_000m);
        simulation.PromotionTotal.ShouldBe(3_000m);
        simulation.Total.ShouldBe(6_000m);

        // Un borrador no rige en la caja.
        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        sale = await shop.AddAsync(sale.Id, shop.Rice, 3);
        sale.PromotionTotal.ShouldBe(0m);

        (await PostAsync<PromotionDto>(manager, $"/api/v1/promotions/{rice3x2.Id}/activate")).Status.ShouldBe("ACTIVE");
        var groceries10 = await PostAsync<PromotionDto>(manager, "/api/v1/promotions",
            Promotion("Abarrotes 10 %", "PercentOff", from, new { categoryId = shop.Groceries }, percent: 10m), HttpStatusCode.Created);
        await PostAsync<PromotionDto>(manager, $"/api/v1/promotions/{groceries10.Id}/activate");

        // Recalcula: el arroz toma el 3x2 ($3.000); la gaseosa, el 10 % de su categoría. Una promoción por línea.
        sale = await shop.AddAsync(sale.Id, shop.Soda, 2);
        var rice = sale.Lines.Single(l => l.ProductId == shop.Rice);
        rice.PromotionId.ShouldBe(rice3x2.Id);
        rice.PromotionDiscount.ShouldBe(3_000m);
        var soda = sale.Lines.Single(l => l.ProductId == shop.Soda);
        soda.PromotionId.ShouldBe(groceries10.Id);
        soda.PromotionDiscount.ShouldBe(500m);
        sale.PromotionTotal.ShouldBe(3_500m);
        sale.Total.ShouldBe(10_500m);
        var receipt = await shop.CompleteAsync(sale.Id, [Pay(shop.Cash, 10_500m)]);
        receipt.Sale.Status.ShouldBe("COMPLETED");

        // La promoción termina mientras otra venta está abierta: en el siguiente recálculo deja de aplicarse (el arroz es de Lácteos: no toma el 10 %).
        var second = await shop.StartAsync();
        second = await shop.AddAsync(second.Id, shop.Rice, 3);
        second.Lines.Single().PromotionId.ShouldBe(rice3x2.Id);
        (await PostAsync<PromotionDto>(manager, $"/api/v1/promotions/{rice3x2.Id}/end")).Status.ShouldBe("ENDED");
        (await manager.PutAsJsonAsync($"/api/v1/promotions/{rice3x2.Id}", threeForTwo, Json, Ct)).StatusCode.ShouldNotBe(HttpStatusCode.OK);
        second = await shop.AddAsync(second.Id, shop.Soda, 1);
        var riceAfter = second.Lines.Single(l => l.ProductId == shop.Rice);
        riceAfter.PromotionId.ShouldBeNull();
        riceAfter.PromotionDiscount.ShouldBe(0m);
        second.Total.ShouldBe(9_000m + 2_500m - 250m);
        await shop.CompleteAsync(second.Id, [Pay(shop.Cash, second.Total)]);

        // Reporte por promoción (ventas completadas en que se aplicó y descuento total).
        var today = PurchasingScenario.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var report = await GetAsync<List<PromotionReportRowDto>>(manager, $"/api/v1/promotions/report?from={today}&to={today}");
        var row3x2 = report.Single(r => r.PromotionId == rice3x2.Id);
        row3x2.Sales.ShouldBe(1);
        row3x2.Discount.ShouldBe(3_000m);
        var row10 = report.Single(r => r.PromotionId == groceries10.Id);
        row10.Sales.ShouldBe(2);
        row10.Discount.ShouldBe(500m + 250m);

        // La cajera no ve el reporte.
        (await shop.Cashier.GetAsync($"/api/v1/promotions/report?from={today}&to={today}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Combo_precio_especial_y_precio_por_cantidad_se_aplican_en_la_caja()
    {
        await using var factory = new CashServerFactory();
        var shop = await SalesScenario.CreateAsync(factory);
        await shop.Security.CreateUserAsync("encargado", "PROMOTIONS_MANAGER");
        var manager = await shop.Security.LocalClientAsync("encargado");
        var from = DateTimeOffset.UtcNow.AddHours(-1);

        // Combo arroz + yogurt a $5.000 (suelto: $3.000 + $3.500).
        var combo = await PostAsync<PromotionDto>(manager, "/api/v1/promotions", new
        {
            name = "Desayuno", type = "Combo", validFrom = from, price = 5_000m,
            items = new object[] { new { productId = shop.Rice, quantity = 1 }, new { productId = shop.Yogurt, quantity = 1 } },
        }, HttpStatusCode.Created);
        await PostAsync<PromotionDto>(manager, $"/api/v1/promotions/{combo.Id}/activate");

        // Gaseosa a $2.000 (precio especial).
        var special = await PostAsync<PromotionDto>(manager, "/api/v1/promotions",
            Promotion("Gaseosa especial", "SpecialPrice", from, new { productId = shop.Soda }, price: 2_000m), HttpStatusCode.Created);
        await PostAsync<PromotionDto>(manager, $"/api/v1/promotions/{special.Id}/activate");

        await shop.OpenSessionAsync();
        var sale = await shop.StartAsync();
        await shop.AddAsync(sale.Id, shop.Rice, 1);
        await shop.AddAsync(sale.Id, shop.Yogurt, 1);
        sale = await shop.AddAsync(sale.Id, shop.Soda, 2);
        sale.PromotionTotal.ShouldBe(1_500m + 1_000m);
        sale.Lines.Where(l => l.PromotionId == combo.Id).Sum(l => l.PromotionDiscount).ShouldBe(1_500m);
        sale.Lines.Single(l => l.ProductId == shop.Soda).PromotionId.ShouldBe(special.Id);
        sale.Total.ShouldBe(5_000m + 4_000m);

        // Pausada deja de regir.
        await PostAsync<PromotionDto>(manager, $"/api/v1/promotions/{special.Id}/pause");
        sale = await shop.AddAsync(sale.Id, shop.Soda, 1);
        sale.Lines.Single(l => l.ProductId == shop.Soda).PromotionDiscount.ShouldBe(0m);
        sale.Total.ShouldBe(5_000m + 7_500m);

        // Precio por cantidad: desde 5 gaseosas, a $2.200 cada una.
        var quantity = await PostAsync<PromotionDto>(manager, "/api/v1/promotions",
            Promotion("Gaseosa por cantidad", "QuantityPrice", from, new { productId = shop.Soda }, price: 2_200m, minQuantity: 5m), HttpStatusCode.Created);
        await PostAsync<PromotionDto>(manager, $"/api/v1/promotions/{quantity.Id}/activate");
        sale = await shop.AddAsync(sale.Id, shop.Soda, 1);
        sale.Lines.Single(l => l.ProductId == shop.Soda).PromotionDiscount.ShouldBe(0m);
        sale = await shop.AddAsync(sale.Id, shop.Soda, 1);
        var soda = sale.Lines.Single(l => l.ProductId == shop.Soda);
        soda.PromotionId.ShouldBe(quantity.Id);
        soda.Total.ShouldBe(5 * 2_200m);
    }

    private static object Promotion(string name, string type, DateTimeOffset validFrom, object item, int? buy = null, int? pay = null, decimal? price = null,
        decimal? percent = null, decimal? minQuantity = null) =>
        new { name, type, validFrom, buyQuantity = buy, payQuantity = pay, price, percent, minQuantity, items = new[] { item } };
}
