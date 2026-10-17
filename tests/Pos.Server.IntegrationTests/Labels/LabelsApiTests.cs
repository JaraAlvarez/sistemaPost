using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.Server.IntegrationTests.Phase3;
using Pos.Server.IntegrationTests.Phase5;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Labels;

/// <summary>Etiquetas de góndola: lista por compra, categoría y cambios de precio; PDF por plantilla; tiquete; códigos internos auditados.</summary>
public class LabelsApiTests
{
    private static readonly string[] OnlyView = ["catalog.product.view"];

    [Fact]
    public async Task Etiquetas_de_una_compra_en_PDF_tiquete_y_codigos_internos()
    {
        await using var factory = new PosServerFactory();
        var scenario = await PurchasingScenario.CreateAsync(factory);
        var owner = scenario.Owner;
        var drinks = await scenario.Catalog.CategoryAsync("Bebidas");
        var soda = await scenario.Catalog.ProductAsync("GAS-400", "Gaseosa cola 400 ml", drinks, barcode: "7702177000014", price: 2_500m);
        var box = await owner.PostAsJsonAsync($"/api/v1/catalog/products/{soda.Id}/packagings", new { name = "Caja x12", factor = 12 }, Json, Ct);
        box.StatusCode.ShouldBe(HttpStatusCode.Created, await box.Content.ReadAsStringAsync(Ct));
        var boxId = (await box.Content.ReadFromJsonAsync<ProductDetailDto>(Json, Ct))!.Packagings.Single().Id;

        (await GetAsync<List<LabelTemplateDto>>(owner, "/api/v1/catalog/labels/templates")).Select(t => t.Code)
            .ShouldBe(["Shelf3x8", "Shelf2x7", "Sticker40x25", "Sticker50x30"]);

        // Una compra en borrador todavía no trae mercancía.
        var draft = await scenario.DraftAsync("FE-ETQ-0", [new { productId = scenario.Rice, quantity = 1, unitCost = 2_000 }]);
        await owner.PostAsJsonAsync("/api/v1/catalog/labels/preview", new { source = "PURCHASE", purchaseNumber = draft.Number }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CATALOG.LABELS_PURCHASE_NOT_POSTED");
        await owner.PostAsJsonAsync("/api/v1/catalog/labels/preview", new { source = "PURCHASE", purchaseNumber = "NO-EXISTE" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.NotFound, "CATALOG.LABELS_PURCHASE_NOT_FOUND");

        var purchase = await scenario.BuyAsync("FE-ETQ-1",
            new { productId = soda.Id, packagingId = boxId, quantity = 2, unitCost = 18_000 },
            new { productId = scenario.Rice, quantity = 50, unitCost = 2_000 });

        // Por número interno (sin distinguir mayúsculas) o por Id: cantidad = unidades recibidas. La factura del proveedor no sirve (se repite).
        var preview = await PreviewAsync(owner, new { source = "PURCHASE", purchaseNumber = purchase.Number.ToLowerInvariant() });
        preview.Reference.ShouldBe(purchase.Number);
        preview.Items.Single(i => i.ProductId == soda.Id).Quantity.ShouldBe(24);
        var rice = preview.Items.Single(i => i.ProductId == scenario.Rice);
        rice.Quantity.ShouldBe(50);
        rice.Price.ShouldBe(3_000m);
        rice.HasBarcode.ShouldBeFalse();
        preview.Warnings.ShouldContain(w => w.Contains("código de barras", StringComparison.Ordinal));
        (await PreviewAsync(owner, new { source = "PURCHASE", purchaseNumber = purchase.Id.ToString() })).Items.Count.ShouldBe(2);
        await owner.PostAsJsonAsync("/api/v1/catalog/labels/preview", new { source = "PURCHASE", purchaseNumber = "FE-ETQ-1" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.NotFound, "CATALOG.LABELS_PURCHASE_NOT_FOUND");

        // Hoja A4 de 24: 74 etiquetas → 4 páginas.
        var pdf = await owner.PostAsJsonAsync("/api/v1/catalog/labels/pdf",
            new { template = "Shelf3x8", items = preview.Items.Select(i => new { i.ProductId, i.Quantity }), showDate = true }, Json, Ct);
        pdf.StatusCode.ShouldBe(HttpStatusCode.OK, await pdf.Content.ReadAsStringAsync(Ct));
        pdf.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        pdf.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldStartWith("etiquetas_");
        var bytes = await pdf.Content.ReadAsByteArrayAsync(Ct);
        Encoding.ASCII.GetString(bytes, 0, 5).ShouldBe("%PDF-");
        Pages(bytes).ShouldBe(4);

        // Adhesivo de 40 × 25 mm: una etiqueta por página.
        var sticker = await owner.PostAsJsonAsync("/api/v1/catalog/labels/pdf",
            new { template = "Sticker40x25", items = new[] { new { productId = soda.Id, quantity = 3 } }, showDate = false }, Json, Ct);
        sticker.StatusCode.ShouldBe(HttpStatusCode.OK, await sticker.Content.ReadAsStringAsync(Ct));
        Pages(await sticker.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(3);

        // Topes: 500 copias por producto, 2.000 etiquetas por PDF y 200 en la impresora de tiquetes.
        await owner.PostAsJsonAsync("/api/v1/catalog/labels/pdf",
                new { template = "Shelf3x8", items = new[] { new { productId = soda.Id, quantity = 501 } } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "VALIDATION.FAILED");
        var extra1 = (await scenario.Catalog.ProductAsync("EXT-1", "Extra uno", drinks, price: 1_000m)).Id;
        var extra2 = (await scenario.Catalog.ProductAsync("EXT-2", "Extra dos", drinks, price: 1_000m)).Id;
        Guid[] five = [soda.Id, scenario.Rice, scenario.Yogurt, extra1, extra2];
        await owner.PostAsJsonAsync("/api/v1/catalog/labels/pdf",
                new { template = "Shelf3x8", items = five.Select(id => new { productId = id, quantity = 500 }) }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "CATALOG.LABELS_TOO_MANY");
        await owner.PostAsJsonAsync("/api/v1/catalog/labels/ticket", new { items = new[] { new { productId = soda.Id, quantity = 201 } } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "CATALOG.LABELS_TOO_MANY");

        // Un código que quedaría ilegible (barras de menos de 0,25 mm) se rechaza con un mensaje claro.
        var longSku = (await scenario.Catalog.ProductAsync("PRODUCTO-ABCDEFGHIJKLMNOP", "Producto de SKU largo", drinks, price: 1_000m)).Id;
        await owner.PostAsJsonAsync("/api/v1/catalog/labels/pdf",
                new { template = "Shelf3x8", items = new[] { new { productId = longSku, quantity = 1 } } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "CATALOG.LABELS_BARCODE_TOO_LONG");

        // Tiquete neutro para la impresora de la caja (EAN-13 con la función EAN de la impresora).
        var ticket = await owner.PostAsJsonAsync("/api/v1/catalog/labels/ticket", new { items = new[] { new { productId = soda.Id, quantity = 2 } } }, Json, Ct);
        ticket.StatusCode.ShouldBe(HttpStatusCode.OK, await ticket.Content.ReadAsStringAsync(Ct));
        var elements = (await ticket.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("elements").EnumerateArray().ToList();
        elements.Count(e => e.GetProperty("type").GetString() == "barcode" && e.GetProperty("data").GetString() == "7702177000014"
            && e.GetProperty("symbology").GetString() == "EAN13").ShouldBe(2);

        // Categoría y cambios de precio desde hoy; los descontinuados no salen.
        (await PreviewAsync(owner, new { source = "CATEGORY", categoryId = drinks })).Items.Select(i => i.ProductId).ShouldContain(soda.Id);
        (await PreviewAsync(owner, new { source = "PRICE_CHANGES", since = PurchasingScenario.Today.AddDays(-1) })).Items.Select(i => i.ProductId)
            .ShouldContain(soda.Id);
        (await PreviewAsync(owner, new { source = "PRICE_CHANGES", since = PurchasingScenario.Today.AddDays(1) })).Items.ShouldBeEmpty();
        (await owner.PostAsJsonAsync($"/api/v1/catalog/products/{extra2}/status", new { status = "Discontinued" }, Json, Ct)).IsSuccessStatusCode.ShouldBeTrue();
        var withDiscontinued = await PreviewAsync(owner, new { source = "PRODUCTS", productIds = new[] { extra1, extra2 } });
        withDiscontinued.Items.Select(i => i.ProductId).ShouldBe([extra1]);
        withDiscontinued.Warnings.ShouldContain(w => w.Contains("descontinuados", StringComparison.Ordinal));

        // Código interno 29 para el que no tiene código (una sola vez) y queda en la auditoría; resultado producto por producto.
        var withoutCode = await PreviewAsync(owner, new { source = "WITHOUT_BARCODE" });
        withoutCode.Items.ShouldContain(i => i.ProductId == scenario.Rice);
        withoutCode.Items.ShouldNotContain(i => i.ProductId == soda.Id);
        var generated = await PostAsync<List<GeneratedLabelCodeDto>>(owner, "/api/v1/catalog/labels/internal-codes", new { productIds = new[] { scenario.Rice, soda.Id } });
        var code = generated.Single(g => g.Generated);
        code.ProductId.ShouldBe(scenario.Rice);
        code.Barcode!.ShouldStartWith("29");
        code.Barcode.Length.ShouldBe(13);
        generated.Single(g => g.ProductId == soda.Id).ShouldSatisfyAllConditions(g => g.Generated.ShouldBeFalse(), g => g.Barcode.ShouldBe("7702177000014"));
        (await PostAsync<List<GeneratedLabelCodeDto>>(owner, "/api/v1/catalog/labels/internal-codes", new { productIds = new[] { scenario.Rice } }))
            .ShouldHaveSingleItem().Generated.ShouldBeFalse();
        (await scenario.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'PRODUCT_INTERNAL_CODES_GENERATED'")).ShouldBe(1);
        (await PreviewAsync(owner, new { source = "PRODUCTS", productIds = new[] { scenario.Rice } })).Items.Single().Barcode.ShouldBe(code.Barcode);
        (await scenario.Catalog.ScanAsync(code.Barcode)).ProductId.ShouldBe(scenario.Rice);

        // Si los siguientes 26 números del consecutivo ya existen (escritos a mano), se saltan todos de una vez; un descontinuado no detiene el lote.
        var node = short.Parse(code.Barcode[2..5], CultureInfo.InvariantCulture);
        var sequence = long.Parse(code.Barcode[5..12], CultureInfo.InvariantCulture);
        for (var i = 1; i <= 26; i++)
        {
            await scenario.Catalog.ProductAsync($"A-MANO-{i}", $"Código escrito a mano {i}", drinks, barcode: Barcodes.Internal(node, sequence + i), price: 1_000m);
        }

        var first = (await scenario.Catalog.ProductAsync("SIN-COD-1", "Sin código uno", drinks, price: 1_000m)).Id;
        var second = (await scenario.Catalog.ProductAsync("SIN-COD-2", "Sin código dos", drinks, price: 1_000m)).Id;
        var batch = await PostAsync<List<GeneratedLabelCodeDto>>(owner, "/api/v1/catalog/labels/internal-codes", new { productIds = new[] { extra2, first, second } });
        batch.Single(g => g.ProductId == extra2).Message.ShouldBe("El producto está descontinuado.");
        batch.Single(g => g.ProductId == first).Barcode.ShouldBe(Barcodes.Internal(node, sequence + 27));
        batch.Single(g => g.ProductId == second).Barcode.ShouldBe(Barcodes.Internal(node, sequence + 28));
        (await scenario.Catalog.ScalarAsync<long>("SELECT last_value FROM catalog.code_sequences WHERE kind = 'BARCODE'")).ShouldBe(sequence + 28);
    }

    [Fact]
    public async Task Sucursal_promociones_y_compras_de_otra_sucursal()
    {
        await using var factory = new PosServerFactory();
        var scenario = await PurchasingScenario.CreateAsync(factory);
        var owner = scenario.Owner;
        var home = scenario.Catalog.Setup.BranchId;
        var created = await owner.PostAsJsonAsync("/api/v1/organization/branches", new { code = "S02", name = "Norte", municipalityCode = "11001", address = "Cra 7" }, Json, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var northDetail = (await created.Content.ReadFromJsonAsync<Pos.Modules.Organization.Contracts.BranchDetailDto>(Json, Ct))!;
        var north = northDetail.Branch.Id;
        var northFloor = northDetail.Warehouses.First(w => w.Kind == "SALES_FLOOR").Id;

        // Precio propio de la sucursal Norte: la etiqueta de Norte lo usa; la de la sucursal principal, el general.
        var price = await owner.PostAsJsonAsync($"/api/v1/catalog/products/{scenario.Rice}/prices", new { price = 2_800m, branchId = north }, Json, Ct);
        price.IsSuccessStatusCode.ShouldBeTrue(await price.Content.ReadAsStringAsync(Ct));
        (await PreviewAsync(owner, new { source = "PRODUCTS", productIds = new[] { scenario.Rice }, branchId = north })).Items.Single().Price.ShouldBe(2_800m);
        (await PreviewAsync(owner, new { source = "PRODUCTS", productIds = new[] { scenario.Rice } })).Items.Single().Price.ShouldBe(3_000m);
        await owner.PostAsJsonAsync("/api/v1/catalog/labels/preview", new { source = "PRODUCTS", productIds = new[] { scenario.Rice }, branchId = Guid.NewGuid() }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.NotFound, "CATALOG.LABELS_BRANCH_NOT_FOUND");

        // Un usuario de la sucursal principal no arma etiquetas de Norte.
        var role = await owner.PostAsJsonAsync("/api/v1/identity/roles", new { code = "ETIQUETAS", name = "Etiquetas", permissions = OnlyView }, Json, Ct);
        role.StatusCode.ShouldBe(HttpStatusCode.Created, await role.Content.ReadAsStringAsync(Ct));
        var security = SecurityScenario.ForExisting(factory, owner, scenario.Catalog.Setup);
        await security.CreateUserAsync("etiquetas.principal", "ETIQUETAS", branchId: home);
        var local = await security.LocalClientAsync("etiquetas.principal");
        await local.PostAsJsonAsync("/api/v1/catalog/labels/preview", new { source = "PRODUCTS", productIds = new[] { scenario.Rice }, branchId = north }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "CATALOG.LABELS_BRANCH_FORBIDDEN");

        // E8: una compra de otra sucursal no se encuentra.
        var purchase = await scenario.BuyAsync("FE-NORTE", new { productId = scenario.Rice, quantity = 5, unitCost = 2_000 });
        await scenario.Catalog.ScalarAsync<int?>($"UPDATE purchasing.purchases SET branch_id = '{north}', warehouse_id = '{northFloor}' WHERE id = '{purchase.Id}'");
        await owner.PostAsJsonAsync("/api/v1/catalog/labels/preview", new { source = "PURCHASE", purchaseNumber = purchase.Number }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.NotFound, "CATALOG.LABELS_PURCHASE_NOT_FOUND");
        (await PreviewAsync(owner, new { source = "PURCHASE", purchaseNumber = purchase.Number, branchId = north })).Items.Single().Quantity.ShouldBe(5);

        // Promociones de punta a punta (ActivePromotions): la de horario no va en la etiqueta; la que tiene fecha final dice hasta cuándo.
        var drinks = await scenario.Catalog.CategoryAsync("Bebidas");
        var scheduled = (await scenario.Catalog.ProductAsync("HORARIO", "Con horario", drinks, price: 5_000m)).Id;
        var dated = (await scenario.Catalog.ProductAsync("FECHADA", "Con fecha", drinks, price: 5_000m)).Id;
        var validTo = DateTimeOffset.UtcNow.AddDays(3);
        await PromotionAsync(owner, new
        {
            name = "Hora feliz", type = "SpecialPrice", validFrom = DateTimeOffset.UtcNow.AddHours(-1), startTime = "00:00:00", endTime = "23:59:59",
            price = 4_000m, items = new[] { new { productId = scheduled } },
        });
        await PromotionAsync(owner, new
        {
            name = "Hasta el viernes", type = "SpecialPrice", validFrom = DateTimeOffset.UtcNow.AddHours(-1), validTo, price = 4_500m,
            items = new[] { new { productId = dated } },
        });

        // Datos viejos: la lista general con "no admite promociones" se trata como si las admitiera (igual que la caja).
        await scenario.Catalog.ScalarAsync<int?>("UPDATE catalog.price_lists SET allows_promotions = false WHERE is_default");
        var items = (await PreviewAsync(owner, new { source = "PRODUCTS", productIds = new[] { scheduled, dated } })).Items;
        items.Single(i => i.ProductId == scheduled).PromoText.ShouldBeNull();
        var withOffer = items.Single(i => i.ProductId == dated);
        withOffer.PromoPrice.ShouldBe(4_500m);
        withOffer.PromoValidUntil.ShouldBe(DateOnly.FromDateTime(validTo.AddTicks(-1).ToOffset(TimeSpan.FromHours(-5)).DateTime));

        // EN2: la lista general no se puede marcar sin promociones.
        await scenario.Catalog.ScalarAsync<int?>("UPDATE catalog.price_lists SET allows_promotions = true WHERE is_default");
        var general = (await GetAsync<List<PriceListDto>>(owner, "/api/v1/catalog/price-lists")).Single(l => l.IsDefault);
        await owner.PutAsJsonAsync($"/api/v1/catalog/price-lists/{general.Id}",
                new { name = general.Name, pricesIncludeTax = general.PricesIncludeTax, isDefault = true, isActive = true, allowsPromotions = false }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "CATALOG.DEFAULT_LIST_ALLOWS_PROMOTIONS");
    }

    private static async Task PromotionAsync(HttpClient owner, object promotion)
    {
        var created = await owner.PostAsJsonAsync("/api/v1/promotions", promotion, Json, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var id = (await created.Content.ReadFromJsonAsync<Pos.Modules.Promotions.Contracts.PromotionDto>(Json, Ct))!.Id;
        var activated = await owner.PostAsync($"/api/v1/promotions/{id}/activate", null, Ct);
        activated.StatusCode.ShouldBe(HttpStatusCode.OK, await activated.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Quien_solo_consulta_productos_no_genera_codigos_ni_ve_compras()
    {
        await using var factory = new PosServerFactory();
        var scenario = await PurchasingScenario.CreateAsync(factory);
        var purchase = await scenario.BuyAsync("FE-ETQ-9", new { productId = scenario.Rice, quantity = 5, unitCost = 2_000 });
        var role = await scenario.Owner.PostAsJsonAsync("/api/v1/identity/roles", new { code = "ETIQUETAS", name = "Etiquetas", permissions = OnlyView }, Json, Ct);
        role.StatusCode.ShouldBe(HttpStatusCode.Created, await role.Content.ReadAsStringAsync(Ct));
        var security = SecurityScenario.ForExisting(factory, scenario.Owner, scenario.Catalog.Setup);
        await security.CreateUserAsync("etiquetas", "ETIQUETAS");
        var user = await security.LocalClientAsync("etiquetas");

        (await PreviewAsync(user, new { source = "PRODUCTS", productIds = new[] { scenario.Rice } })).Items.ShouldHaveSingleItem();
        (await user.PostAsJsonAsync("/api/v1/catalog/labels/internal-codes", new { productIds = new[] { scenario.Rice } }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await user.PostAsJsonAsync("/api/v1/catalog/labels/preview", new { source = "PURCHASE", purchaseNumber = purchase.Number }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.Forbidden, "CATALOG.LABELS_PURCHASE_FORBIDDEN");
        await user.PostAsJsonAsync("/api/v1/catalog/labels/preview", new { source = "PRODUCTS", productIds = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()) }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "VALIDATION.FAILED");
    }

    private static async Task<LabelPreviewDto> PreviewAsync(HttpClient client, object request) =>
        await PostAsync<LabelPreviewDto>(client, "/api/v1/catalog/labels/preview", request);

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    /// <summary>Páginas del PDF (objetos /Type /Page, sin contar el /Pages del árbol).</summary>
    private static int Pages(byte[] pdf) => Regex.Count(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?![a-zA-Z])");
}
