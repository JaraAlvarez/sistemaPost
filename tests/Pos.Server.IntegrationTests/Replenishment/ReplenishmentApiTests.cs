using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Server.IntegrationTests.Phase4;
using Pos.Server.IntegrationTests.Phase5;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Replenishment;

/// <summary>Reabastecimiento: mínimos y máximos → pedido sugerido → órdenes de compra en borrador por proveedor; importar y calcular mínimos.</summary>
public class ReplenishmentApiTests
{
    private static readonly string[] GreatTaxpayer = ["O-13"];

    [Fact]
    public async Task Politicas_pedido_sugerido_y_ordenes_en_borrador_por_proveedor()
    {
        await using var factory = new PosServerFactory();
        var shop = await PurchasingScenario.CreateAsync(factory);
        var owner = shop.Owner;

        // Segundo proveedor (arroz) y un producto de otra categoría sin proveedor (panela).
        var party = await shop.CreatePartyAsync(new
        {
            personType = "Legal", identificationType = "NIT", identificationNumber = "830114921", checkDigit = "1", legalName = "Arrocera del Llano SAS",
            taxRegime = "48", fiscalResponsibilities = GreatTaxpayer,
        });
        var arrocera = await shop.SendAsync<SupplierDto>("/api/v1/purchasing/suppliers",
            new { partyId = party.Id, supplier = new { code = "ARROCERA", paymentTermDays = 15, issuesInvoices = true } }, HttpStatusCode.Created);
        var pantry = await shop.Catalog.CategoryAsync("Despensa");
        var panela = (await shop.Catalog.ProductAsync("PANELA-1", "Panela 1 kg", pantry, price: 4_000m)).Id;

        await PutOkAsync(owner, $"/api/v1/purchasing/suppliers/{shop.SupplierId}/products",
            new { productId = shop.Yogurt, packagingId = shop.YogurtBox, supplierCode = "YF1", leadTimeDays = 2, isPreferred = true });
        await PutOkAsync(owner, $"/api/v1/purchasing/suppliers/{arrocera.Id}/products",
            new { productId = shop.Rice, packagingId = (Guid?)null, supplierCode = "A500", leadTimeDays = 5, isPreferred = true });

        // Mínimos y máximos (pestaña Existencias del producto): yogurt 10–50, arroz mínimo 20 sin máximo, panela 5–15.
        await PutOkAsync(owner, "/api/v1/inventory/policies", new { warehouseId = shop.Floor, productId = shop.Yogurt, minQuantity = 10, maxQuantity = 50 });
        await PutOkAsync(owner, "/api/v1/inventory/policies", new { warehouseId = shop.Floor, productId = shop.Rice, minQuantity = 20 });
        await PutOkAsync(owner, "/api/v1/inventory/policies", new { warehouseId = shop.Floor, productId = panela, minQuantity = 5, maxQuantity = 15 });
        await owner.PutAsJsonAsync("/api/v1/inventory/policies", new { warehouseId = shop.Floor, productId = panela, minQuantity = 20, maxQuantity = 15 }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "INVENTORY.INVALID_POLICY");
        var yogurtPolicies = await GetAsync<List<StockPolicyDto>>(owner, $"/api/v1/inventory/policies?productId={shop.Yogurt}");
        yogurtPolicies.ShouldHaveSingleItem().MaxQuantity.ShouldBe(50m);

        // Pedido sugerido: yogurt 50 unidades → 3 cajas x24; arroz hasta el mínimo (20); panela sin proveedor.
        var lines = await GetAsync<List<ReplenishmentLineDto>>(owner, $"/api/v1/purchasing/replenishment?warehouseId={shop.Floor}");
        lines.Count.ShouldBe(3);
        var yogurt = lines.Single(l => l.ProductId == shop.Yogurt);
        yogurt.SupplierId.ShouldBe(shop.SupplierId);
        yogurt.PackagingId.ShouldBe(shop.YogurtBox);
        yogurt.PackagingFactor.ShouldBe(24m);
        yogurt.SuggestedQuantity.ShouldBe(3m);
        yogurt.SuggestedBaseQuantity.ShouldBe(72m);
        var rice = lines.Single(l => l.ProductId == shop.Rice);
        rice.SupplierId.ShouldBe(arrocera.Id);
        rice.SuggestedQuantity.ShouldBe(20m);
        var noSupplier = lines.Single(l => l.ProductId == panela);
        noSupplier.SupplierId.ShouldBeNull();
        noSupplier.SuggestedQuantity.ShouldBe(15m);

        // Filtros: por categoría y por proveedor.
        (await GetAsync<List<ReplenishmentLineDto>>(owner, $"/api/v1/purchasing/replenishment?categoryId={pantry}")).ShouldHaveSingleItem().ProductId.ShouldBe(panela);
        (await GetAsync<List<ReplenishmentLineDto>>(owner, $"/api/v1/purchasing/replenishment?supplierId={arrocera.Id}"))
            .ShouldHaveSingleItem().ProductId.ShouldBe(shop.Rice);

        // Una línea sin proveedor no deja crear nada.
        var request = lines.Select(l => new SuggestedOrderLineRequest(l.WarehouseId, l.SupplierId, l.ProductId, l.PackagingId, l.SuggestedQuantity)).ToList();
        await owner.PostAsJsonAsync("/api/v1/purchasing/replenishment/orders", new { lines = request }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "PURCHASING.SUPPLIER_REQUIRED");
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM purchasing.purchase_orders")).ShouldBe(0);

        // Se asigna la panela a la arrocera y se crean las órdenes: una por proveedor, en borrador.
        request = [.. request.Select(l => l.ProductId == panela ? l with { SupplierId = arrocera.Id } : l)];
        var orders = await shop.SendAsync<List<SuggestedOrderDto>>("/api/v1/purchasing/replenishment/orders", new { lines = request });
        orders.Count.ShouldBe(2);
        orders.Single(o => o.SupplierId == shop.SupplierId).LineCount.ShouldBe(1);
        orders.Single(o => o.SupplierId == arrocera.Id).LineCount.ShouldBe(2);
        foreach (var created in orders)
        {
            var order = await GetAsync<PurchaseOrderDto>(owner, $"/api/v1/purchasing/orders/{created.OrderId}");
            order.Status.ShouldBe("DRAFT");
            order.WarehouseId.ShouldBe(shop.Floor);
            order.Number.ShouldBe(created.Number);
        }

        var dislacOrder = await GetAsync<PurchaseOrderDto>(owner, $"/api/v1/purchasing/orders/{orders.Single(o => o.SupplierId == shop.SupplierId).OrderId}");
        var boxes = dislacOrder.Lines.ShouldHaveSingleItem();
        boxes.PackagingId.ShouldBe(shop.YogurtBox);
        boxes.Quantity.ShouldBe(3m);
        boxes.BaseQuantity.ShouldBe(72m);

        // Lo pedido (aunque esté en borrador) cuenta como pendiente: el sugerido baja a cero y no se duplica el pedido.
        var after = await GetAsync<List<ReplenishmentLineDto>>(owner, $"/api/v1/purchasing/replenishment?warehouseId={shop.Floor}");
        after.Single(l => l.ProductId == shop.Yogurt).PendingQuantity.ShouldBe(72m);
        after.ShouldAllBe(l => l.SuggestedQuantity == 0m);

        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'PURCHASE_ORDERS_SUGGESTED'")).ShouldBe(1);
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'STOCK_POLICY_CREATED'")).ShouldBe(3);
    }

    [Fact]
    public async Task Importar_minimos_y_maximos_con_vista_previa_y_calcular_sugeridos()
    {
        await using var factory = new PosServerFactory();
        var shop = await PurchasingScenario.CreateAsync(factory);
        var owner = shop.Owner;
        await PutOkAsync(owner, "/api/v1/inventory/policies", new { warehouseId = shop.Floor, productId = shop.Rice, minQuantity = 4, maxQuantity = 8 });

        var template = await owner.GetAsync("/api/v1/inventory/policies/import/template", Ct);
        template.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await template.Content.ReadAsStringAsync(Ct)).ShouldContain("sku;bodega;minimo;maximo;punto_pedido");

        // Vista previa con errores: no guarda nada.
        var withErrors = CatalogScenario.Csv("sku;minimo;maximo", "ARROZ-500;10;40", "NO-EXISTE;1;2", "YOG-1;30;10", "ARROZ-500;1;2");
        var preview = await ReadAsync<StockPolicyImportDto>(
            await shop.Catalog.UploadAsync($"/api/v1/inventory/policies/import?warehouseId={shop.Floor}&apply=true", withErrors, "minimos.csv"));
        preview.Applied.ShouldBeFalse();
        preview.ErrorRows.ShouldBe(3);
        preview.Rows.Single(r => r.RowNumber == 3).Errors.ShouldHaveSingleItem().ShouldContain("NO-EXISTE");
        preview.Rows.Single(r => r.RowNumber == 4).Errors.ShouldHaveSingleItem().ShouldContain("mínimo");
        preview.Rows.Single(r => r.RowNumber == 5).Errors.ShouldHaveSingleItem().ShouldContain("repetido");
        (await GetAsync<List<StockPolicyDto>>(owner, $"/api/v1/inventory/policies?productId={shop.Rice}")).Single().MinQuantity.ShouldBe(4m);

        // Archivo correcto: vista previa (sin cambios) y luego aplicar.
        var good = CatalogScenario.Csv("sku;minimo;maximo;punto_pedido", "ARROZ-500;10;40;", "YOG-1;6;24;8");
        var check = await ReadAsync<StockPolicyImportDto>(
            await shop.Catalog.UploadAsync($"/api/v1/inventory/policies/import?warehouseId={shop.Floor}", good, "minimos.csv"));
        check.Applied.ShouldBeFalse();
        (check.CreateRows, check.UpdateRows, check.ErrorRows).ShouldBe((1, 1, 0));
        check.Rows.Single(r => r.Code == "ARROZ-500").CurrentMinQuantity.ShouldBe(4m);
        (await GetAsync<List<StockPolicyDto>>(owner, $"/api/v1/inventory/policies?productId={shop.Yogurt}")).ShouldBeEmpty();

        var applied = await ReadAsync<StockPolicyImportDto>(
            await shop.Catalog.UploadAsync($"/api/v1/inventory/policies/import?warehouseId={shop.Floor}&apply=true", good, "minimos.csv"));
        applied.Applied.ShouldBeTrue();
        var rice = (await GetAsync<List<StockPolicyDto>>(owner, $"/api/v1/inventory/policies?productId={shop.Rice}")).Single();
        (rice.MinQuantity, rice.MaxQuantity).ShouldBe((10m, 40m));
        (await GetAsync<List<StockPolicyDto>>(owner, $"/api/v1/inventory/policies?productId={shop.Yogurt}")).Single().ReorderPoint.ShouldBe(8m);
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'STOCK_POLICIES_IMPORTED'")).ShouldBe(1);

        // Calcular mínimos sugeridos: sin ventas no hay sugerencias; parámetros fuera de rango se rechazan.
        (await GetAsync<List<MinimumSuggestionDto>>(owner, $"/api/v1/inventory/policies/suggestions?warehouseId={shop.Floor}&days=30&leadTimeDays=3&coverageDays=7"))
            .ShouldBeEmpty();
        await owner.GetAsync($"/api/v1/inventory/policies/suggestions?warehouseId={shop.Floor}&days=0", Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "INVENTORY.INVALID_SUGGESTION_PARAMETERS");

        // Aplicar los elegidos: conserva el punto de pedido que ya tenía.
        var result = await shop.SendAsync<StockPolicyBulkResultDto>("/api/v1/inventory/policies/apply-minimums", new
        {
            items = new[] { new MinMaxInput(shop.Floor, shop.Yogurt, 12m, 30m) },
        });
        result.Updated.ShouldBe(1);
        var yogurt = (await GetAsync<List<StockPolicyDto>>(owner, $"/api/v1/inventory/policies?productId={shop.Yogurt}")).Single();
        (yogurt.MinQuantity, yogurt.MaxQuantity, yogurt.ReorderPoint).ShouldBe((12m, 30m, 8m));
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'STOCK_POLICIES_SUGGESTED'")).ShouldBe(1);
    }

    private static async Task PutOkAsync(HttpClient client, string url, object body)
    {
        var response = await client.PutAsJsonAsync(url, body, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }
}
