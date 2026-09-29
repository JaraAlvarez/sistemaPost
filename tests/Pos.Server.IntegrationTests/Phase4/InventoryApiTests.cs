using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Server.IntegrationTests.Phase3;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase4;

/// <summary>Criterios de aceptación del inventario (propuesta §14): kardex, costo promedio, ajustes, conteos y traslados.</summary>
public class InventoryApiTests
{
    private sealed record Store(CatalogScenario Scenario, Guid Floor, Guid Storage, Guid Transit, Guid Damaged, Guid Rice, Guid Milk)
    {
        public HttpClient Owner => Scenario.Owner;
    }

    private static async Task<Store> CreateStoreAsync(PosServerFactory factory)
    {
        var scenario = await CatalogScenario.CreateAsync(factory);
        var branch = await GetAsync<BranchDetailDto>(scenario.Owner, $"/api/v1/organization/branches/{scenario.Setup.BranchId}");
        var storageResponse = await scenario.Owner.PostAsJsonAsync($"/api/v1/organization/branches/{scenario.Setup.BranchId}/warehouses",
            new { code = "BODEGA", name = "Bodega trasera", kind = "Storage", allowsSales = false }, Json, Ct);
        storageResponse.StatusCode.ShouldBe(HttpStatusCode.Created, await storageResponse.Content.ReadAsStringAsync(Ct));
        var storage = (await GetAsync<BranchDetailDto>(scenario.Owner, $"/api/v1/organization/branches/{scenario.Setup.BranchId}"))
            .Warehouses.Single(w => w.Code == "BODEGA").Id;
        var category = await scenario.CategoryAsync("Despensa");
        var rice = await scenario.ProductAsync("ARROZ-500", "Arroz Diana 500 g", category, barcode: "7702177000014", price: 3_000m);
        var milk = await scenario.ProductAsync("LECHE-1", "Leche entera 1100 ml", category, price: 5_000m);
        return new Store(scenario, branch.Warehouses.Single(w => w.Kind == "SalesFloor").Id, storage, branch.Warehouses.Single(w => w.Kind == "InTransit").Id,
            branch.Warehouses.Single(w => w.Kind == "Damaged").Id, rice.Id, milk.Id);
    }

    private static async Task<Guid> ReasonAsync(HttpClient client, string code) =>
        (await GetAsync<List<AdjustmentReasonDto>>(client, "/api/v1/inventory/reasons")).Single(r => r.Code == code).Id;

    private static async Task<AdjustmentDto> AdjustAsync(HttpClient client, Guid warehouse, Guid reason, params object[] lines)
    {
        var created = await client.PostAsJsonAsync("/api/v1/inventory/adjustments", new { warehouseId = warehouse, reasonId = reason, notes = "prueba", lines }, Json, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var adjustment = (await created.Content.ReadFromJsonAsync<AdjustmentDto>(Json, Ct))!;
        var posted = await client.PostAsync($"/api/v1/inventory/adjustments/{adjustment.Id}/post", null, Ct);
        posted.StatusCode.ShouldBe(HttpStatusCode.OK, await posted.Content.ReadAsStringAsync(Ct));
        return (await posted.Content.ReadFromJsonAsync<AdjustmentDto>(Json, Ct))!;
    }

    private static async Task ThresholdAsync(HttpClient client, decimal value) =>
        (await client.PutAsJsonAsync("/api/v1/settings/inventory.adjustment_approval_threshold", new { scope = "Company", value }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

    private static async Task<StockDto?> StockAsync(HttpClient client, Guid warehouse, Guid product) =>
        (await GetAsync<List<StockDto>>(client, $"/api/v1/inventory/stock?warehouseId={warehouse}&productId={product}")).SingleOrDefault();

    private static async Task InitialBalanceAsync(Store store)
    {
        var file = CatalogScenario.Csv("producto;cantidad;costo", "ARROZ-500;100;2.500", "LECHE-1;40;4.100,50");
        var imported = await store.Scenario.UploadAsync($"/api/v1/inventory/adjustments/initial-balance/import?warehouseId={store.Storage}", file, "saldo.csv");
        imported.StatusCode.ShouldBe(HttpStatusCode.Created, await imported.Content.ReadAsStringAsync(Ct));
        var draft = (await imported.Content.ReadFromJsonAsync<AdjustmentDto>(Json, Ct))!;
        draft.Status.ShouldBe("DRAFT");
        draft.ReasonCode.ShouldBe("INITIAL_BALANCE");
        var posted = await store.Owner.PostAsync($"/api/v1/inventory/adjustments/{draft.Id}/post", null, Ct);
        posted.StatusCode.ShouldBe(HttpStatusCode.OK, await posted.Content.ReadAsStringAsync(Ct));
        (await posted.Content.ReadFromJsonAsync<AdjustmentDto>(Json, Ct))!.Status.ShouldBe("POSTED");
    }

    [Fact]
    public async Task Saldo_inicial_ajustes_con_aprobacion_y_stock_insuficiente()
    {
        await using var factory = new PosServerFactory();
        var store = await CreateStoreAsync(factory);
        var owner = store.Owner;

        // Saldo inicial desde archivo con errores → nada se crea.
        var bad = CatalogScenario.Csv("producto;cantidad;costo", "NO-EXISTE;1;1", "ARROZ-500;-3;1");
        await store.Scenario.UploadAsync($"/api/v1/inventory/adjustments/initial-balance/import?warehouseId={store.Storage}", bad, "saldo.csv")
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "INVENTORY.IMPORT_HAS_ERRORS");

        await InitialBalanceAsync(store);
        var rice = (await StockAsync(owner, store.Storage, store.Rice))!;
        (rice.Quantity, rice.AverageCost, rice.TotalValue).ShouldBe((100m, 2_500m, 250_000m));
        (await StockAsync(owner, store.Storage, store.Milk))!.AverageCost.ShouldBe(4_100.50m);

        // No se repite el saldo inicial de un producto con movimientos.
        var initial = await ReasonAsync(owner, "INITIAL_BALANCE");
        await owner.PostAsJsonAsync("/api/v1/inventory/adjustments",
                new { warehouseId = store.Storage, reasonId = initial, lines = new[] { new { productId = store.Rice, quantity = 1, unitCost = 1 } } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "INVENTORY.INITIAL_BALANCE_NOT_ALLOWED");

        // Avería pequeña: se publica directo (la cantidad se toma como salida).
        var damage = await AdjustAsync(owner, store.Storage, await ReasonAsync(owner, "DAMAGE"), new { productId = store.Rice, quantity = 2 });
        damage.Status.ShouldBe("POSTED");
        (await StockAsync(owner, store.Storage, store.Rice))!.Quantity.ShouldBe(98m);

        // Más de lo que hay: bloqueado por defecto (RN-INV-03).
        var correction = await ReasonAsync(owner, "CORRECTION");
        var tooMuch = (await (await owner.PostAsJsonAsync("/api/v1/inventory/adjustments",
            new { warehouseId = store.Storage, reasonId = correction, notes = "conteo", lines = new[] { new { productId = store.Milk, quantity = -50 } } }, Json, Ct))
            .Content.ReadFromJsonAsync<AdjustmentDto>(Json, Ct))!;
        await owner.PostAsync($"/api/v1/inventory/adjustments/{tooMuch.Id}/post", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "INVENTORY.INSUFFICIENT_STOCK");
        (await StockAsync(owner, store.Storage, store.Milk))!.Quantity.ShouldBe(40m);

        // Ajuste grande (sobre el umbral, aquí $100.000): queda pendiente y el creador no puede aprobarlo.
        await ThresholdAsync(owner, 100_000m);
        var big = (await (await owner.PostAsJsonAsync("/api/v1/inventory/adjustments",
            new { warehouseId = store.Storage, reasonId = correction, notes = "robo", lines = new[] { new { productId = store.Rice, quantity = -90 } } }, Json, Ct))
            .Content.ReadFromJsonAsync<AdjustmentDto>(Json, Ct))!;
        var pending = (await (await owner.PostAsync($"/api/v1/inventory/adjustments/{big.Id}/post", null, Ct)).Content.ReadFromJsonAsync<AdjustmentDto>(Json, Ct))!;
        pending.Status.ShouldBe("PENDING_APPROVAL");
        pending.TotalValue.ShouldBe(225_000m, "90 × 2.500");
        await owner.PostAsync($"/api/v1/inventory/adjustments/{big.Id}/approve", null, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "INVENTORY.SELF_APPROVAL");
    }

    [Fact]
    public async Task Otro_administrador_aprueba_el_ajuste_grande()
    {
        await using var factory = new PosServerFactory();
        var store = await CreateStoreAsync(factory);
        await InitialBalanceAsync(store);
        var security = SecurityScenario.ForExisting(factory, store.Owner, store.Scenario.Setup);
        await security.CreateUserAsync("admin2", "ADMIN");
        var admin = await security.LocalClientAsync("admin2");

        var correction = await ReasonAsync(store.Owner, "CORRECTION");
        await ThresholdAsync(store.Owner, 100_000m);
        var big = (await (await store.Owner.PostAsJsonAsync("/api/v1/inventory/adjustments",
            new { warehouseId = store.Storage, reasonId = correction, notes = "conteo anual", lines = new[] { new { productId = store.Milk, quantity = -35 } } }, Json, Ct))
            .Content.ReadFromJsonAsync<AdjustmentDto>(Json, Ct))!;
        (await (await store.Owner.PostAsync($"/api/v1/inventory/adjustments/{big.Id}/post", null, Ct)).Content.ReadFromJsonAsync<AdjustmentDto>(Json, Ct))!
            .Status.ShouldBe("PENDING_APPROVAL");

        var approved = await admin.PostAsync($"/api/v1/inventory/adjustments/{big.Id}/approve", null, Ct);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        (await approved.Content.ReadFromJsonAsync<AdjustmentDto>(Json, Ct))!.Status.ShouldBe("POSTED");
        (await StockAsync(store.Owner, store.Storage, store.Milk))!.Quantity.ShouldBe(5m);
        (await store.Scenario.ScalarAsync<Guid>("SELECT authorized_by FROM audit.audit_log WHERE action = 'INVENTORY_ADJUSTMENT_POSTED' ORDER BY seq DESC LIMIT 1"))
            .ShouldNotBe(Guid.Empty);

        // Con saldos negativos permitidos en la sucursal, la salida se registra.
        (await store.Owner.PutAsJsonAsync("/api/v1/settings/inventory.allow_negative_stock",
            new { scope = "Branch", scopeId = store.Scenario.Setup.BranchId, value = true }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await AdjustAsync(store.Owner, store.Storage, await ReasonAsync(store.Owner, "LOSS"), new { productId = store.Milk, quantity = 7 });
        (await StockAsync(store.Owner, store.Storage, store.Milk))!.Quantity.ShouldBe(-2m);
    }

    [Fact]
    public async Task Traslado_con_faltante_pasa_por_transito_al_costo_de_origen()
    {
        await using var factory = new PosServerFactory();
        var store = await CreateStoreAsync(factory);
        await InitialBalanceAsync(store);
        var owner = store.Owner;

        var created = await owner.PostAsJsonAsync("/api/v1/inventory/transfers",
            new { originWarehouseId = store.Storage, destinationWarehouseId = store.Floor, lines = new[] { new { productId = store.Rice, quantity = 10 } } }, Json, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var transfer = (await created.Content.ReadFromJsonAsync<TransferDto>(Json, Ct))!;

        var dispatched = (await (await owner.PostAsync($"/api/v1/inventory/transfers/{transfer.Id}/dispatch", null, Ct)).Content.ReadFromJsonAsync<TransferDto>(Json, Ct))!;
        dispatched.Status.ShouldBe("IN_TRANSIT");
        dispatched.Lines.Single().UnitCost.ShouldBe(2_500m);
        (await StockAsync(owner, store.Transit, store.Rice))!.Quantity.ShouldBe(10m);

        var received = await owner.PostAsJsonAsync($"/api/v1/inventory/transfers/{transfer.Id}/receive",
            new { received = new[] { new { productId = store.Rice, quantity = 9 } } }, Json, Ct);
        received.StatusCode.ShouldBe(HttpStatusCode.OK, await received.Content.ReadAsStringAsync(Ct));
        (await received.Content.ReadFromJsonAsync<TransferDto>(Json, Ct))!.Status.ShouldBe("RECEIVED_WITH_DIFFERENCES");

        (await StockAsync(owner, store.Storage, store.Rice))!.Quantity.ShouldBe(90m);
        var floor = (await StockAsync(owner, store.Floor, store.Rice))!;
        (floor.Quantity, floor.AverageCost).ShouldBe((9m, 2_500m));
        (await StockAsync(owner, store.Transit, store.Rice))!.Quantity.ShouldBe(0m);

        var kardex = await GetAsync<KardexDto>(owner, $"/api/v1/inventory/kardex?warehouseId={store.Transit}&productId={store.Rice}");
        kardex.Entries.Select(e => e.MovementType).ShouldBe(["TRANSFER_IN", "TRANSFER_OUT", "LOSS"]);
        kardex.Entries[^1].Reason.ShouldBe("Faltante en traslado");

        // Origen y destino reales: la bodega de tránsito la maneja el sistema.
        await owner.PostAsJsonAsync("/api/v1/inventory/transfers",
                new { originWarehouseId = store.Transit, destinationWarehouseId = store.Floor, lines = new[] { new { productId = store.Rice, quantity = 1 } } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "INVENTORY.IN_TRANSIT_NOT_ALLOWED");
    }

    [Fact]
    public async Task Conteo_ciego_con_varios_contadores_y_ventas_durante_el_conteo()
    {
        await using var factory = new PosServerFactory();
        var store = await CreateStoreAsync(factory);
        await InitialBalanceAsync(store);
        var owner = store.Owner;
        var security = SecurityScenario.ForExisting(factory, store.Owner, store.Scenario.Setup);
        await security.CreateUserAsync("cajera", "CASHIER");
        var cashier = await security.LocalClientAsync("cajera");

        var created = await owner.PostAsJsonAsync("/api/v1/inventory/counts",
            new { warehouseId = store.Storage, type = "Partial", isBlind = true, productIds = new[] { store.Rice, store.Milk } }, Json, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var count = (await created.Content.ReadFromJsonAsync<CountDto>(Json, Ct))!;
        (await owner.PostAsync($"/api/v1/inventory/counts/{count.Id}/start", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // La cajera cuenta a ciegas: no ve el teórico.
        var blind = await GetAsync<CountDto>(cashier, $"/api/v1/inventory/counts/{count.Id}");
        blind.Lines.ShouldAllBe(l => l.SystemQuantity == null);
        (await cashier.PostAsJsonAsync($"/api/v1/inventory/counts/{count.Id}/entries",
            new object[] { new { code = "7702177000014", quantity = 60, location = "estante 1" }, new { code = "LECHE-1", quantity = 38 } }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync($"/api/v1/inventory/counts/{count.Id}/entries",
            new[] { new { productId = store.Rice, quantity = 39, location = "estante 2" } }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Mientras se cuenta, salen 2 arroces (p. ej. una avería): el teórico congelado era 100.
        await AdjustAsync(owner, store.Storage, await ReasonAsync(owner, "DAMAGE"), new { productId = store.Rice, quantity = 2 });

        var reviewed = (await (await owner.PostAsync($"/api/v1/inventory/counts/{count.Id}/review", null, Ct)).Content.ReadFromJsonAsync<CountDto>(Json, Ct))!;
        var riceLine = reviewed.Lines.Single(l => l.ProductId == store.Rice);
        (riceLine.SystemQuantity, riceLine.ExpectedQuantity, riceLine.CountedQuantity, riceLine.Difference).ShouldBe((100m, 98m, 99m, 1m));
        reviewed.Lines.Single(l => l.ProductId == store.Milk).Difference.ShouldBe(-2m);

        var approved = await owner.PostAsync($"/api/v1/inventory/counts/{count.Id}/approve", null, Ct);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        (await StockAsync(owner, store.Storage, store.Rice))!.Quantity.ShouldBe(99m);
        (await StockAsync(owner, store.Storage, store.Milk))!.Quantity.ShouldBe(38m);

        var kardex = await GetAsync<KardexDto>(owner, $"/api/v1/inventory/kardex?warehouseId={store.Storage}&productId={store.Rice}");
        kardex.Entries.Select(e => e.MovementType).ShouldBe(["INITIAL_BALANCE", "DAMAGE", "COUNT_ADJUSTMENT_IN"]);

        // La cajera ve existencias pero no costos.
        var stock = (await StockAsync(cashier, store.Storage, store.Rice))!;
        stock.Quantity.ShouldBe(99m);
        stock.AverageCost.ShouldBeNull();
        (await GetAsync<KardexDto>(cashier, $"/api/v1/inventory/kardex?warehouseId={store.Storage}&productId={store.Rice}")).Entries.ShouldAllBe(e => e.UnitCost == null);
    }

    [Fact]
    public async Task Reglas_del_catalogo_que_dependen_del_inventario()
    {
        await using var factory = new PosServerFactory();
        var store = await CreateStoreAsync(factory);
        await InitialBalanceAsync(store);
        var owner = store.Owner;

        // Precio por debajo del costo: advertencia por defecto…
        var warned = await owner.PostAsJsonAsync($"/api/v1/catalog/products/{store.Rice}/prices", new { price = 2_000, branchId = store.Scenario.Setup.BranchId }, Json, Ct);
        (await warned.Content.ReadFromJsonAsync<SetPriceResultDto>(Json, Ct))!.Warnings.ShouldHaveSingleItem().ShouldStartWith("CATALOG.PRICE_BELOW_COST");

        // …o bloqueo si la empresa lo decide.
        (await owner.PutAsJsonAsync("/api/v1/settings/catalog.price_below_cost", new { scope = "Company", value = "BLOCK" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await owner.PostAsJsonAsync($"/api/v1/catalog/products/{store.Rice}/prices", new { price = 2_000 }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CATALOG.PRICE_BELOW_COST");

        // Con existencias no se descontinúa; con movimientos no cambia la unidad base.
        await owner.PostAsJsonAsync($"/api/v1/catalog/products/{store.Rice}/status", new { status = "Discontinued" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CATALOG.PRODUCT_HAS_STOCK");
        var product = await GetAsync<ProductDetailDto>(owner, $"/api/v1/catalog/products/{store.Rice}");
        await owner.PutAsJsonAsync($"/api/v1/catalog/products/{store.Rice}", new
            {
                sku = product.Sku, name = product.Name, shortName = product.ShortName, description = (string?)null, categoryId = product.CategoryId,
                brandId = (Guid?)null, baseUnitCode = "KG", saleMode = "Weight", productType = "Stockable", isSoldByScale = false, allowsDecimalQuantity = true,
                allowsOpenPrice = false, tracksLots = false, tracksExpiry = false, pluCode = (string?)null, netContent = (decimal?)null, netContentUnit = (string?)null,
            }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "CATALOG.BASE_UNIT_LOCKED");
    }

    [Fact]
    public async Task El_kardex_es_inmutable_y_la_verificacion_detecta_una_alteracion_directa()
    {
        await using var factory = new PosServerFactory();
        var store = await CreateStoreAsync(factory);
        await InitialBalanceAsync(store);
        var owner = store.Owner;

        (await (await owner.PostAsync("/api/v1/inventory/verification", null, Ct)).Content.ReadFromJsonAsync<VerificationDto>(Json, Ct))!.Discrepancies.ShouldBe(0);

        // El rol de la aplicación no puede modificar ni borrar movimientos.
        (await Should.ThrowAsync<PostgresException>(() => store.Scenario.ExecuteAsync("UPDATE inventory.stock_movements SET quantity = 1")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => store.Scenario.ExecuteAsync("DELETE FROM inventory.stock_movements")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // Alguien cambia un saldo directamente en la BD: la verificación lo detecta y queda como incidente crítico.
        await store.Scenario.ExecuteAsync($"UPDATE inventory.stock_balances SET quantity = quantity + 5 WHERE product_id = '{store.Rice}'");
        var check = (await (await owner.PostAsync("/api/v1/inventory/verification", null, Ct)).Content.ReadFromJsonAsync<VerificationDto>(Json, Ct))!;
        check.Discrepancies.ShouldBe(1);
        check.Details.Single().ProductId.ShouldBe(store.Rice);
        (await store.Scenario.ScalarAsync<string>("SELECT severity FROM audit.audit_log WHERE action = 'STOCK_VERIFICATION_FAILED'")).ShouldBe("CRITICAL");

        // Reconstrucción explícita, con motivo y auditada.
        await owner.PostAsJsonAsync("/api/v1/inventory/verification/rebuild", new { warehouseId = store.Storage, productId = store.Rice, reason = "" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "INVENTORY.NOTE_REQUIRED");
        var rebuilt = await owner.PostAsJsonAsync("/api/v1/inventory/verification/rebuild",
            new { warehouseId = store.Storage, productId = store.Rice, reason = "Incidente del 6 de octubre" }, Json, Ct);
        rebuilt.StatusCode.ShouldBe(HttpStatusCode.OK, await rebuilt.Content.ReadAsStringAsync(Ct));
        (await rebuilt.Content.ReadFromJsonAsync<StockDto>(Json, Ct))!.Quantity.ShouldBe(100m);
        (await (await owner.PostAsync("/api/v1/inventory/verification", null, Ct)).Content.ReadFromJsonAsync<VerificationDto>(Json, Ct))!.Discrepancies.ShouldBe(0);
        (await GetAsync<List<VerificationDto>>(owner, "/api/v1/inventory/verification")).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Veinte_publicaciones_simultaneas_sin_bloqueos_mutuos_y_el_saldo_cuadra()
    {
        await using var factory = new PosServerFactory();
        var store = await CreateStoreAsync(factory);
        await InitialBalanceAsync(store);
        var owner = store.Owner;
        var damage = await ReasonAsync(owner, "DAMAGE");
        var correction = await ReasonAsync(owner, "CORRECTION");

        // 20 salidas del mismo producto y 10 ajustes que tocan los dos productos en órdenes opuestos, a la vez.
        var tasks = Enumerable.Range(0, 20).Select(_ => AdjustAsync(factory.WithOwner(owner), store.Storage, damage, new { productId = store.Rice, quantity = 1 }))
            .Concat(Enumerable.Range(0, 10).Select(i => AdjustAsync(factory.WithOwner(owner), store.Storage, correction,
                i % 2 == 0
                    ? [new { productId = store.Rice, quantity = -1 }, new { productId = store.Milk, quantity = 1 }]
                    : new object[] { new { productId = store.Milk, quantity = -1 }, new { productId = store.Rice, quantity = 1 } })))
            .ToList();
        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(r => r.Status == "POSTED");
        (await StockAsync(owner, store.Storage, store.Rice))!.Quantity.ShouldBe(80m);
        (await StockAsync(owner, store.Storage, store.Milk))!.Quantity.ShouldBe(40m);
        (await store.Scenario.ScalarAsync<decimal>($"SELECT SUM(direction * quantity) FROM inventory.stock_movements WHERE product_id = '{store.Rice}'")).ShouldBe(80m);
        (await (await owner.PostAsync("/api/v1/inventory/verification", null, Ct)).Content.ReadFromJsonAsync<VerificationDto>(Json, Ct))!.Discrepancies.ShouldBe(0);
    }
}

internal static class FactoryExtensions
{
    /// <summary>Otro cliente HTTP con la misma sesión (para peticiones en paralelo).</summary>
    public static HttpClient WithOwner(this PosServerFactory factory, HttpClient owner)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;
        return client;
    }
}
