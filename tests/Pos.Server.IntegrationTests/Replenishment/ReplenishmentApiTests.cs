using System.Net;
using System.Net.Http.Json;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Server.IntegrationTests.Phase3;
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
        var arrocera = await ArroceraAsync(shop);
        var pantry = await shop.Catalog.CategoryAsync("Despensa");
        var panela = (await shop.Catalog.ProductAsync("PANELA-1", "Panela 1 kg", pantry, price: 4_000m)).Id;

        await PutOkAsync(owner, $"/api/v1/purchasing/suppliers/{shop.SupplierId}/products",
            new { productId = shop.Yogurt, packagingId = shop.YogurtBox, supplierCode = "YF1", leadTimeDays = 2, isPreferred = true });
        await PutOkAsync(owner, $"/api/v1/purchasing/suppliers/{arrocera}/products",
            new { productId = shop.Rice, packagingId = (Guid?)null, supplierCode = "A500", leadTimeDays = 5, isPreferred = true });

        // Mínimos y máximos (pestaña Existencias del producto): yogurt 10–50, arroz mínimo 20 sin máximo, panela 5–15.
        await PolicyAsync(shop, shop.Yogurt, 10, 50);
        await PolicyAsync(shop, shop.Rice, 20, null);
        await PolicyAsync(shop, panela, 5, 15);
        await owner.PutAsJsonAsync("/api/v1/inventory/policies", new { warehouseId = shop.Floor, productId = panela, minQuantity = 20, maxQuantity = 15 }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "INVENTORY.INVALID_POLICY");
        (await GetAsync<List<StockPolicyDto>>(owner, $"/api/v1/inventory/policies?productId={shop.Yogurt}")).ShouldHaveSingleItem().MaxQuantity.ShouldBe(50m);

        // Pedido sugerido: yogurt 50 unidades → 3 cajas x24; arroz hasta el mínimo (20); panela sin proveedor.
        var lines = await LinesAsync(owner, $"?warehouseId={shop.Floor}");
        lines.Count.ShouldBe(3);
        var yogurt = lines.Single(l => l.ProductId == shop.Yogurt);
        yogurt.SupplierId.ShouldBe(shop.SupplierId);
        yogurt.PackagingId.ShouldBe(shop.YogurtBox);
        yogurt.PackagingFactor.ShouldBe(24m);
        yogurt.SuggestedQuantity.ShouldBe(3m);
        yogurt.SuggestedBaseQuantity.ShouldBe(72m);
        var rice = lines.Single(l => l.ProductId == shop.Rice);
        rice.SupplierId.ShouldBe(arrocera);
        rice.SuggestedQuantity.ShouldBe(20m);
        yogurt.LastCost.ShouldBeNull(); // Sin compras no hay último costo (aunque el dueño puede ver costos).
        var noSupplier = lines.Single(l => l.ProductId == panela);
        noSupplier.SupplierId.ShouldBeNull();
        noSupplier.SuggestedQuantity.ShouldBe(15m);

        // Filtros: por categoría y por proveedor.
        (await LinesAsync(owner, $"?categoryId={pantry}")).ShouldHaveSingleItem().ProductId.ShouldBe(panela);
        (await LinesAsync(owner, $"?supplierId={arrocera}")).ShouldHaveSingleItem().ProductId.ShouldBe(shop.Rice);

        // Sin clave de envío, o con una línea sin proveedor, no se crea nada.
        var request = lines.Select(l => new SuggestedOrderLineRequest(
            l.WarehouseId, l.SupplierId, l.ProductId, l.PackagingId, l.SuggestedQuantity, SeenPendingQuantity: l.PendingQuantity)).ToList();
        var batch = Guid.NewGuid();
        await owner.PostAsJsonAsync("/api/v1/purchasing/replenishment/orders", new { lines = request }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "PURCHASING.SUGGESTION_BATCH_REQUIRED");
        await owner.PostAsJsonAsync("/api/v1/purchasing/replenishment/orders", new { batchId = batch, lines = request }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "PURCHASING.SUPPLIER_REQUIRED");
        (await OrderCountAsync(shop)).ShouldBe(0);

        // Se asigna la panela a la arrocera y se crean las órdenes: una por proveedor, en borrador.
        request = [.. request.Select(l => l.ProductId == panela ? l with { SupplierId = arrocera } : l)];
        var orders = await shop.SendAsync<List<SuggestedOrderDto>>("/api/v1/purchasing/replenishment/orders", new { batchId = batch, lines = request });
        orders.Count.ShouldBe(2);
        orders.Single(o => o.SupplierId == shop.SupplierId).LineCount.ShouldBe(1);
        orders.Single(o => o.SupplierId == arrocera).LineCount.ShouldBe(2);
        foreach (var created in orders)
        {
            var order = await GetAsync<PurchaseOrderDto>(owner, $"/api/v1/purchasing/orders/{created.OrderId}");
            order.Status.ShouldBe("DRAFT");
            order.WarehouseId.ShouldBe(shop.Floor);
            order.Number.ShouldBe(created.Number);
        }

        var boxes = (await GetAsync<PurchaseOrderDto>(owner, $"/api/v1/purchasing/orders/{orders.Single(o => o.SupplierId == shop.SupplierId).OrderId}"))
            .Lines.ShouldHaveSingleItem();
        boxes.PackagingId.ShouldBe(shop.YogurtBox);
        boxes.Quantity.ShouldBe(3m);
        boxes.BaseQuantity.ShouldBe(72m);

        // El mismo envío repetido (doble clic, reintento tras un corte) devuelve las mismas órdenes y no crea otras.
        var again = await shop.SendAsync<List<SuggestedOrderDto>>("/api/v1/purchasing/replenishment/orders", new { batchId = batch, lines = request });
        again.Select(o => o.OrderId).ShouldBe(orders.Select(o => o.OrderId), ignoreOrder: true);
        (await OrderCountAsync(shop)).ShouldBe(2);
        await owner.PostAsJsonAsync("/api/v1/purchasing/replenishment/orders", new { batchId = batch, lines = request.Take(1) }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Conflict, "PURCHASING.SUGGESTION_BATCH_REUSED");

        // Otro envío con lo que se veía antes (otra pestaña): otra persona ya pidió esos productos → recargar; el aviso los nombra.
        var stale = await owner.PostAsJsonAsync("/api/v1/purchasing/replenishment/orders", new { batchId = Guid.NewGuid(), lines = request }, Json, Ct);
        var staleProblem = await stale.Content.ReadAsStringAsync(Ct);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, staleProblem);
        staleProblem.ShouldContain("PURCHASING.SUGGESTION_CHANGED");
        staleProblem.ShouldContain("YOG-1");
        (await OrderCountAsync(shop)).ShouldBe(2);

        // Lo pedido (aunque esté en borrador) cuenta como pendiente: el sugerido baja a cero.
        var after = await LinesAsync(owner, $"?warehouseId={shop.Floor}");
        after.Single(l => l.ProductId == shop.Yogurt).PendingQuantity.ShouldBe(72m);
        after.ShouldAllBe(l => l.SuggestedQuantity == 0m);

        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'PURCHASE_ORDERS_SUGGESTED'")).ShouldBe(1);
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'STOCK_POLICY_CREATED'")).ShouldBe(3);

        // Una clave vencida (más de 30 días) ya no repite el envío: se crean órdenes nuevas y la clave vencida se reemplaza.
        await shop.Catalog.ExecuteAsync(
            "UPDATE system.idempotency_keys SET created_at = now() - interval '40 days', expires_at = now() - interval '10 days'");
        var fresh = await shop.SendAsync<List<SuggestedOrderDto>>("/api/v1/purchasing/replenishment/orders",
            new { batchId = batch, lines = request.Select(l => l with { SeenPendingQuantity = null }) });
        fresh.Select(o => o.OrderId).ShouldNotContain(orders[0].OrderId);
        fresh.Select(o => o.OrderId).ShouldNotContain(orders[1].OrderId);
        (await OrderCountAsync(shop)).ShouldBe(4);
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM system.idempotency_keys WHERE expires_at > now()")).ShouldBe(1);
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM system.idempotency_keys WHERE expires_at <= now()")).ShouldBe(0);
    }

    [Fact]
    public async Task Bodega_inactiva_proveedor_bloqueado_punto_de_pedido_y_envios_simultaneos()
    {
        await using var factory = new PosServerFactory();
        var shop = await PurchasingScenario.CreateAsync(factory);
        var owner = shop.Owner;
        var arrocera = await ArroceraAsync(shop);
        await PutOkAsync(owner, $"/api/v1/purchasing/suppliers/{shop.SupplierId}/products",
            new { productId = shop.Yogurt, packagingId = (Guid?)null, supplierCode = "YF1", leadTimeDays = 2, isPreferred = true });
        await PutOkAsync(owner, $"/api/v1/purchasing/suppliers/{arrocera}/products",
            new { productId = shop.Rice, packagingId = (Guid?)null, supplierCode = "A500", leadTimeDays = 5, isPreferred = true });

        // Un punto de pedido nuevo necesita máximo o cantidad a pedir (también por la API, no solo en la pantalla).
        await owner.PutAsJsonAsync("/api/v1/inventory/policies", new { warehouseId = shop.Floor, productId = shop.Rice, minQuantity = 5, reorderPoint = 8 }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "INVENTORY.REORDER_POINT_NEEDS_TARGET");

        // Una política antigua con punto de pedido sin máximo ni cantidad: se pide hasta el punto de pedido (8), no hasta el mínimo (5).
        await PutOkAsync(owner, "/api/v1/inventory/policies", new { warehouseId = shop.Floor, productId = shop.Rice, minQuantity = 5 });
        await shop.Catalog.ExecuteAsync($"UPDATE inventory.stock_policies SET reorder_point = 8 WHERE product_id = '{shop.Rice}'");
        (await LinesAsync(owner, "")).Single(l => l.ProductId == shop.Rice).SuggestedQuantity.ShouldBe(8m);

        // Bodega inactiva: sus productos no aparecen y una orden para ella dice cuál falló (y no se crea ninguna).
        var created = await owner.PostAsJsonAsync($"/api/v1/organization/branches/{shop.Catalog.Setup.BranchId}/warehouses",
            new { code = "BODEGA", name = "Bodega trasera", kind = "Storage", allowsSales = false }, Json, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var storage = (await GetAsync<BranchDetailDto>(owner, $"/api/v1/organization/branches/{shop.Catalog.Setup.BranchId}"))
            .Warehouses.Single(w => w.Code == "BODEGA").Id;
        await PutOkAsync(owner, "/api/v1/inventory/policies", new { warehouseId = storage, productId = shop.Yogurt, minQuantity = 5, maxQuantity = 10 });
        (await LinesAsync(owner, $"?warehouseId={storage}")).ShouldHaveSingleItem();
        (await owner.PostAsync($"/api/v1/organization/warehouses/{storage}/deactivate", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LinesAsync(owner, $"?warehouseId={storage}")).ShouldBeEmpty();

        // Las bodegas de averías y las borradas tampoco entran al pedido sugerido.
        var branch = await GetAsync<BranchDetailDto>(owner, $"/api/v1/organization/branches/{shop.Catalog.Setup.BranchId}");
        var damaged = branch.Warehouses.Single(w => w.Kind == "DAMAGED").Id;
        await PutOkAsync(owner, "/api/v1/inventory/policies", new { warehouseId = damaged, productId = shop.Yogurt, minQuantity = 5, maxQuantity = 10 });
        (await LinesAsync(owner, $"?warehouseId={damaged}")).ShouldBeEmpty();
        var other = await owner.PostAsJsonAsync($"/api/v1/organization/branches/{shop.Catalog.Setup.BranchId}/warehouses",
            new { code = "BORRADA", name = "Bodega borrada", kind = "Storage", allowsSales = false }, Json, Ct);
        other.StatusCode.ShouldBe(HttpStatusCode.Created, await other.Content.ReadAsStringAsync(Ct));
        var removed = (await GetAsync<BranchDetailDto>(owner, $"/api/v1/organization/branches/{shop.Catalog.Setup.BranchId}"))
            .Warehouses.Single(w => w.Code == "BORRADA").Id;
        await PutOkAsync(owner, "/api/v1/inventory/policies", new { warehouseId = removed, productId = shop.Yogurt, minQuantity = 5, maxQuantity = 10 });
        (await LinesAsync(owner, $"?warehouseId={removed}")).ShouldHaveSingleItem();
        await shop.Catalog.ExecuteAsync($"UPDATE org.warehouses SET deleted_at = now(), deleted_by = gen_random_uuid() WHERE id = '{removed}'");
        (await LinesAsync(owner, $"?warehouseId={removed}")).ShouldBeEmpty();

        var rice = new SuggestedOrderLineRequest(shop.Floor, arrocera, shop.Rice, null, 8m);
        var inactive = await owner.PostAsJsonAsync("/api/v1/purchasing/replenishment/orders", new
        {
            batchId = Guid.NewGuid(), lines = new[] { rice, new SuggestedOrderLineRequest(storage, shop.SupplierId, shop.Yogurt, null, 10m) },
        }, Json, Ct);
        var problem = await inactive.Content.ReadAsStringAsync(Ct);
        inactive.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem);
        problem.ShouldContain("INVENTORY.WAREHOUSE_INACTIVE");
        problem.ShouldContain("BODEGA");
        (await OrderCountAsync(shop)).ShouldBe(0);

        // Si la segunda orden falla (proveedor bloqueado), la primera tampoco queda (todo o nada).
        await PolicyAsync(shop, shop.Yogurt, 10, 20);
        await shop.SendAsync<SupplierDto>($"/api/v1/purchasing/suppliers/{shop.SupplierId}/status", new { status = "Blocked" });
        var blocked = await owner.PostAsJsonAsync("/api/v1/purchasing/replenishment/orders", new
        {
            batchId = Guid.NewGuid(), lines = new[] { rice, new SuggestedOrderLineRequest(shop.Floor, shop.SupplierId, shop.Yogurt, null, 20m) },
        }, Json, Ct);
        problem = await blocked.Content.ReadAsStringAsync(Ct);
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem);
        problem.ShouldContain("PURCHASING.SUPPLIER_BLOCKED");
        problem.ShouldContain("Distribuidora Láctea");
        (await OrderCountAsync(shop)).ShouldBe(0);
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM system.idempotency_keys")).ShouldBe(0);

        // Una venta o una entrada de mercancía entre cargar y crear NO bloquea: se pide lo que el usuario eligió.
        var seen = (await LinesAsync(owner, $"?supplierId={arrocera}")).Single();
        var adjusted = await owner.PostAsJsonAsync("/api/v1/inventory/quick-adjustments",
            new { warehouseId = shop.Floor, productId = shop.Rice, quantity = 3m, reason = "Llegó mercancía sin registrar" }, Json, Ct);
        adjusted.StatusCode.ShouldBe(HttpStatusCode.Created, await adjusted.Content.ReadAsStringAsync(Ct));
        (await LinesAsync(owner, $"?supplierId={arrocera}")).Single().SuggestedQuantity.ShouldBe(5m);
        var chosen = await shop.SendAsync<List<SuggestedOrderDto>>("/api/v1/purchasing/replenishment/orders", new
        {
            batchId = Guid.NewGuid(),
            lines = new[] { new SuggestedOrderLineRequest(shop.Floor, arrocera, shop.Rice, null, seen.SuggestedQuantity, SeenPendingQuantity: seen.PendingQuantity) },
        });
        (await GetAsync<PurchaseOrderDto>(owner, $"/api/v1/purchasing/orders/{chosen.Single().OrderId}")).Lines.Single().Quantity.ShouldBe(8m);
        (await OrderCountAsync(shop)).ShouldBe(1);

        // Dos envíos a la vez (dos personas, dos pestañas) con lo mismo que veían: uno crea la orden, el otro debe recargar.
        seen = (await LinesAsync(owner, $"?supplierId={arrocera}")).Single();
        var line = new SuggestedOrderLineRequest(shop.Floor, arrocera, shop.Rice, null, 5m, SeenPendingQuantity: seen.PendingQuantity);
        var both = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            owner.PostAsJsonAsync("/api/v1/purchasing/replenishment/orders", new { batchId = Guid.NewGuid(), lines = new[] { line } }, Json, Ct)));
        both.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        both.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        (await ErrorCodeAsync(both.Single(r => r.StatusCode == HttpStatusCode.Conflict))).ShouldBe("PURCHASING.SUGGESTION_CHANGED");
        (await OrderCountAsync(shop)).ShouldBe(2);
    }

    [Fact]
    public async Task Permisos_costos_y_roles()
    {
        await using var factory = new PosServerFactory();
        var shop = await PurchasingScenario.CreateAsync(factory);
        var owner = shop.Owner;
        var security = SecurityScenario.ForExisting(factory, owner, shop.Catalog.Setup);

        // La compra deja el último costo del proveedor; el arroz queda bajo el mínimo.
        await shop.BuyAsync("FE-1", new { productId = shop.Rice, quantity = 2, unitCost = 1_800 });
        await PolicyAsync(shop, shop.Rice, 20, 40);
        (await LinesAsync(owner, "")).Single().LastCost.ShouldNotBeNull();

        // Quien solo consulta compras ve el sugerido pero no el costo, ni crea órdenes.
        await CreateRoleAsync(owner, "CONSULTA_COMPRAS", ["purchasing.purchase.view"]);
        await security.CreateUserAsync("consulta", "CONSULTA_COMPRAS");
        var viewer = await security.LocalClientAsync("consulta");
        var line = (await LinesAsync(viewer, "")).Single();
        line.LastCost.ShouldBeNull();
        line.SuggestedQuantity.ShouldBe(38m);

        // El rol Inventario define mínimos pero no crea órdenes de compra.
        await security.CreateUserAsync("bodeguero", "INVENTORY");
        var inventory = await security.LocalClientAsync("bodeguero");
        await PutOkAsync(inventory, "/api/v1/inventory/policies", new { warehouseId = shop.Floor, productId = shop.Rice, minQuantity = 20, maxQuantity = 50 });
        (await inventory.PostAsJsonAsync("/api/v1/purchasing/replenishment/orders", new
            {
                batchId = Guid.NewGuid(), lines = new[] { new SuggestedOrderLineRequest(shop.Floor, shop.SupplierId, shop.Rice, null, 48m) },
            }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Quien crea órdenes pero no ve costos: las crea y no recibe el total (ni al crearlas ni al repetir el envío).
        await CreateRoleAsync(owner, "COMPRAS_SIN_COSTO", ["purchasing.purchase.view", "purchasing.order.manage"]);
        await security.CreateUserAsync("comprador", "COMPRAS_SIN_COSTO");
        var buyer = await security.LocalClientAsync("comprador");
        var toOrder = (await LinesAsync(buyer, "")).Single();
        var body = new
        {
            batchId = Guid.NewGuid(),
            lines = new[]
            {
                new SuggestedOrderLineRequest(toOrder.WarehouseId, toOrder.SupplierId, toOrder.ProductId, toOrder.PackagingId, toOrder.SuggestedQuantity,
                    SeenPendingQuantity: toOrder.PendingQuantity),
            },
        };
        for (var i = 0; i < 2; i++)
        {
            var response = await buyer.PostAsJsonAsync("/api/v1/purchasing/replenishment/orders", body, Json, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            (await response.Content.ReadFromJsonAsync<List<SuggestedOrderDto>>(Json, Ct))!.ShouldHaveSingleItem().Total.ShouldBeNull();
        }

        (await OrderCountAsync(shop)).ShouldBe(1);

        // Un rol nuevo con solo ajustes de inventario ya no edita mínimos (los roles que existían antes lo reciben en la migración 047).
        await CreateRoleAsync(owner, "AJUSTES", ["inventory.stock.view", "inventory.adjustment.manage"]);
        await security.CreateUserAsync("ajustes", "AJUSTES");
        var adjuster = await security.LocalClientAsync("ajustes");
        (await adjuster.PutAsJsonAsync("/api/v1/inventory/policies", new { warehouseId = shop.Floor, productId = shop.Rice, minQuantity = 1 }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Importar_minimos_y_maximos_con_vista_previa_y_calcular_sugeridos()
    {
        await using var factory = new PosServerFactory();
        var shop = await PurchasingScenario.CreateAsync(factory);
        var owner = shop.Owner;
        await PolicyAsync(shop, shop.Rice, 4, 8);

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

        // Calcular mínimos sugeridos: parámetros fuera de rango se rechazan; sin ventas no hay sugerencias.
        await owner.GetAsync($"/api/v1/inventory/policies/suggestions?warehouseId={shop.Floor}&days=0", Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "INVENTORY.INVALID_SUGGESTION_PARAMETERS");
        var url = $"/api/v1/inventory/policies/suggestions?warehouseId={shop.Floor}&days=30&leadTimeDays=3&coverageDays=7";
        (await GetAsync<MinimumSuggestionsDto>(owner, url)).Items.ShouldBeEmpty();

        // Con ventas en el kardex: 60 arroces vendidos (y 6 devueltos) en los últimos 30 días = 1,8 diarios → mínimo 6, máximo 18.
        var yesterday = PurchasingScenario.Today.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (type, direction, quantity) in new[] { ("SALE", -1, 40), ("SALE", -1, 20), ("CUSTOMER_RETURN", 1, 6) })
        {
            await shop.Catalog.ExecuteAsync(
                $"""
                INSERT INTO inventory.stock_movements (id, company_id, node_id, branch_id, warehouse_id, product_id, movement_type, direction, quantity,
                    unit_cost, total_cost, balance_quantity, balance_value, balance_avg_cost, source_type, source_id, business_date, occurred_at, user_id)
                SELECT gen_random_uuid(), w.company_id, gen_random_uuid(), w.branch_id, w.id, '{shop.Rice}', '{type}', {direction}, {quantity},
                    0, 0, 0, 0, 0, 'SALE', gen_random_uuid(), DATE '{yesterday}', now(), gen_random_uuid()
                FROM org.warehouses w WHERE w.id = '{shop.Floor}'
                """);
        }

        var suggested = (await GetAsync<MinimumSuggestionsDto>(owner, url)).Items.ShouldHaveSingleItem();
        suggested.ProductId.ShouldBe(shop.Rice);
        suggested.SoldQuantity.ShouldBe(54m);
        (suggested.SuggestedMinQuantity, suggested.SuggestedMaxQuantity).ShouldBe((6m, 18m));
        (suggested.CurrentMinQuantity, suggested.CurrentMaxQuantity).ShouldBe((10m, 40m));

        // Aplicar los elegidos: conserva el punto de pedido que ya tenía.
        var result = await shop.SendAsync<StockPolicyBulkResultDto>("/api/v1/inventory/policies/apply-minimums", new
        {
            items = new[] { new MinMaxInput(shop.Floor, shop.Yogurt, 12m, 30m), new MinMaxInput(shop.Floor, shop.Rice, 6m, 18m) },
        });
        result.Updated.ShouldBe(2);
        var yogurt = (await GetAsync<List<StockPolicyDto>>(owner, $"/api/v1/inventory/policies?productId={shop.Yogurt}")).Single();
        (yogurt.MinQuantity, yogurt.MaxQuantity, yogurt.ReorderPoint).ShouldBe((12m, 30m, 8m));
        (await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'STOCK_POLICIES_SUGGESTED'")).ShouldBe(1);

        // Sin máximo se conserva el actual. Un producto con punto de pedido que quedaría sin máximo ni cantidad a pedir se rechaza con su
        // SKU (el punto de pedido no se quita) y los demás se guardan.
        await shop.Catalog.ExecuteAsync(
            $"UPDATE inventory.stock_policies SET max_qty = NULL, reorder_qty = NULL, reorder_point = 9 WHERE product_id = '{shop.Rice}'");
        var partial = await shop.SendAsync<StockPolicyBulkResultDto>("/api/v1/inventory/policies/apply-minimums", new
        {
            items = new[] { new MinMaxInput(shop.Floor, shop.Yogurt, 14m, null), new MinMaxInput(shop.Floor, shop.Rice, 7m, null) },
        });
        partial.Updated.ShouldBe(1);
        partial.Rejected.ShouldNotBeNull().ShouldHaveSingleItem().ShouldContain("ARROZ-500");
        yogurt = (await GetAsync<List<StockPolicyDto>>(owner, $"/api/v1/inventory/policies?productId={shop.Yogurt}")).Single();
        (yogurt.MinQuantity, yogurt.MaxQuantity, yogurt.ReorderPoint).ShouldBe((14m, 30m, 8m));
        var keptRice = (await GetAsync<List<StockPolicyDto>>(owner, $"/api/v1/inventory/policies?productId={shop.Rice}")).Single();
        (keptRice.MinQuantity, keptRice.ReorderPoint).ShouldBe((6m, 9m));
    }

    [Fact]
    public async Task Importar_rechaza_descontinuados_y_avisa_de_inactivos()
    {
        await using var factory = new PosServerFactory();
        var shop = await PurchasingScenario.CreateAsync(factory);
        var owner = shop.Owner;
        var pantry = await shop.Catalog.CategoryAsync("Despensa");
        var old = (await shop.Catalog.ProductAsync("VIEJO-1", "Producto descontinuado", pantry, price: 1_000m)).Id;
        var paused = (await shop.Catalog.ProductAsync("PAUSA-1", "Producto inactivo", pantry, price: 1_000m)).Id;
        foreach (var (product, status) in new[] { (old, "Discontinued"), (paused, "Inactive") })
        {
            var changed = await owner.PostAsJsonAsync($"/api/v1/catalog/products/{product}/status", new { status }, Json, Ct);
            changed.IsSuccessStatusCode.ShouldBeTrue(await changed.Content.ReadAsStringAsync(Ct));
        }

        // Punto de pedido sin máximo en el archivo (y sin cantidad a pedir guardada): error en la fila.
        var noTarget = await ReadAsync<StockPolicyImportDto>(await shop.Catalog.UploadAsync(
            $"/api/v1/inventory/policies/import?warehouseId={shop.Floor}", CatalogScenario.Csv("sku;minimo;maximo;punto_pedido", "YOG-1;3;;5"), "minimos.csv"));
        noTarget.Rows.Single().Errors.ShouldHaveSingleItem().ShouldContain("punto_pedido");

        var file = CatalogScenario.Csv("sku;minimo;maximo", "VIEJO-1;2;6", "PAUSA-1;3;9");
        var preview = await ReadAsync<StockPolicyImportDto>(
            await shop.Catalog.UploadAsync($"/api/v1/inventory/policies/import?warehouseId={shop.Floor}&apply=true", file, "minimos.csv"));

        preview.Applied.ShouldBeFalse();
        var discontinued = preview.Rows.Single(r => r.Code == "VIEJO-1");
        discontinued.Action.ShouldBe("ERROR");
        discontinued.Errors.ShouldHaveSingleItem().ShouldContain("descontinuado");
        var inactive = preview.Rows.Single(r => r.Code == "PAUSA-1");
        inactive.Action.ShouldBe("CREATE");
        inactive.Errors.ShouldBeEmpty();
        inactive.Warnings.ShouldHaveSingleItem().ShouldContain("inactivo");
        (await GetAsync<List<StockPolicyDto>>(owner, $"/api/v1/inventory/policies?productId={paused}")).ShouldBeEmpty();

        // Sin la fila del descontinuado, el inactivo se guarda (con su aviso).
        var applied = await ReadAsync<StockPolicyImportDto>(await shop.Catalog.UploadAsync(
            $"/api/v1/inventory/policies/import?warehouseId={shop.Floor}&apply=true", CatalogScenario.Csv("sku;minimo;maximo", "PAUSA-1;3;9"), "minimos.csv"));
        applied.Applied.ShouldBeTrue();
        applied.Rows.Single().Warnings.ShouldHaveSingleItem();
        (await GetAsync<List<StockPolicyDto>>(owner, $"/api/v1/inventory/policies?productId={paused}")).Single().MaxQuantity.ShouldBe(9m);
    }

    private static async Task<Guid> ArroceraAsync(PurchasingScenario shop)
    {
        var party = await shop.CreatePartyAsync(new
        {
            personType = "Legal", identificationType = "NIT", identificationNumber = "830114921", checkDigit = "1", legalName = "Arrocera del Llano SAS",
            taxRegime = "48", fiscalResponsibilities = GreatTaxpayer,
        });
        return (await shop.SendAsync<SupplierDto>("/api/v1/purchasing/suppliers",
            new { partyId = party.Id, supplier = new { code = "ARROCERA", paymentTermDays = 15, issuesInvoices = true } }, HttpStatusCode.Created)).Id;
    }

    private static Task PolicyAsync(PurchasingScenario shop, Guid product, decimal min, decimal? max) =>
        PutOkAsync(shop.Owner, "/api/v1/inventory/policies", new { warehouseId = shop.Floor, productId = product, minQuantity = min, maxQuantity = max });

    private static async Task<List<ReplenishmentLineDto>> LinesAsync(HttpClient client, string query)
    {
        var result = await GetAsync<ReplenishmentDto>(client, "/api/v1/purchasing/replenishment" + query);
        result.Truncated.ShouldBeFalse();
        return [.. result.Lines];
    }

    private static async Task<long> OrderCountAsync(PurchasingScenario shop) =>
        await shop.Catalog.ScalarAsync<long>("SELECT count(*) FROM purchasing.purchase_orders");

    private static async Task CreateRoleAsync(HttpClient owner, string code, string[] permissions)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/identity/roles", new { code, name = code, permissions }, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
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
