using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pos.Modules.Catalog.Contracts;
using Pos.Server.IntegrationTests.Phase5;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Labels;

/// <summary>Etiquetas de góndola: lista por compra, categoría y cambios de precio; PDF por plantilla; tiquete; códigos internos auditados.</summary>
public class LabelsApiTests
{
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

        // Por número interno (sin distinguir mayúsculas) o por la factura del proveedor: cantidad = unidades recibidas.
        var preview = await PreviewAsync(owner, new { source = "PURCHASE", purchaseNumber = purchase.Number.ToLowerInvariant() });
        preview.Reference.ShouldBe(purchase.Number);
        preview.Items.Single(i => i.ProductId == soda.Id).Quantity.ShouldBe(24);
        var rice = preview.Items.Single(i => i.ProductId == scenario.Rice);
        rice.Quantity.ShouldBe(50);
        rice.Price.ShouldBe(3_000m);
        rice.HasBarcode.ShouldBeFalse();
        preview.Warnings.ShouldContain(w => w.Contains("código de barras", StringComparison.Ordinal));
        (await PreviewAsync(owner, new { source = "PURCHASE", purchaseNumber = "fe-etq-1" })).Items.Count.ShouldBe(2);

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

        await owner.PostAsJsonAsync("/api/v1/catalog/labels/pdf",
                new { template = "Shelf3x8", items = new[] { new { productId = soda.Id, quantity = 501 } } }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "VALIDATION.FAILED");

        // Tiquete neutro para la impresora de la caja (CODE128 con el código del producto).
        var ticket = await owner.PostAsJsonAsync("/api/v1/catalog/labels/ticket", new { items = new[] { new { productId = soda.Id, quantity = 2 } } }, Json, Ct);
        ticket.StatusCode.ShouldBe(HttpStatusCode.OK, await ticket.Content.ReadAsStringAsync(Ct));
        var elements = (await ticket.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("elements").EnumerateArray().ToList();
        elements.Count(e => e.GetProperty("type").GetString() == "barcode" && e.GetProperty("data").GetString() == "7702177000014").ShouldBe(2);

        // Categoría y cambios de precio desde hoy.
        (await PreviewAsync(owner, new { source = "CATEGORY", categoryId = drinks })).Items.Select(i => i.ProductId).ShouldBe([soda.Id]);
        (await PreviewAsync(owner, new { source = "PRICE_CHANGES", since = PurchasingScenario.Today.AddDays(-1) })).Items.Select(i => i.ProductId)
            .ShouldContain(soda.Id);
        (await PreviewAsync(owner, new { source = "PRICE_CHANGES", since = PurchasingScenario.Today.AddDays(1) })).Items.ShouldBeEmpty();

        // Código interno 29 para el que no tiene código (una sola vez) y queda en la auditoría.
        var withoutCode = await PreviewAsync(owner, new { source = "WITHOUT_BARCODE" });
        withoutCode.Items.ShouldContain(i => i.ProductId == scenario.Rice);
        withoutCode.Items.ShouldNotContain(i => i.ProductId == soda.Id);
        var generated = await PostAsync<List<GeneratedLabelCodeDto>>(owner, "/api/v1/catalog/labels/internal-codes", new { productIds = new[] { scenario.Rice, soda.Id } });
        var code = generated.ShouldHaveSingleItem();
        code.ProductId.ShouldBe(scenario.Rice);
        code.Barcode.ShouldStartWith("29");
        code.Barcode.Length.ShouldBe(13);
        (await PostAsync<List<GeneratedLabelCodeDto>>(owner, "/api/v1/catalog/labels/internal-codes", new { productIds = new[] { scenario.Rice } })).ShouldBeEmpty();
        (await scenario.Catalog.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'PRODUCT_INTERNAL_CODES_GENERATED'")).ShouldBe(1);
        (await PreviewAsync(owner, new { source = "PRODUCTS", productIds = new[] { scenario.Rice } })).Items.Single().Barcode.ShouldBe(code.Barcode);
        (await scenario.Catalog.ScanAsync(code.Barcode)).ProductId.ShouldBe(scenario.Rice);
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
