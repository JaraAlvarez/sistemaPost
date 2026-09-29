using System.Net;
using System.Net.Http.Json;
using MiniExcelLibs;
using Pos.Modules.Catalog.Contracts;
using static Pos.Server.IntegrationTests.Phase2.ApiClient;

namespace Pos.Server.IntegrationTests.Phase4;

/// <summary>Criterios de aceptación del catálogo (propuesta §14).</summary>
public class CatalogApiTests
{
    [Fact]
    public async Task La_empresa_nace_con_sus_datos_iniciales_de_catalogo()
    {
        await using var factory = new PosServerFactory();
        var scenario = await CatalogScenario.CreateAsync(factory);
        var owner = scenario.Owner;

        var units = await GetAsync<List<UnitDto>>(owner, "/api/v1/catalog/units");
        units.ShouldContain(u => u.Code == "KG" && u.DianCode == "KGM" && u.Dimension == "WEIGHT");

        var taxes = await GetAsync<List<TaxDto>>(owner, "/api/v1/catalog/taxes");
        taxes.Single(t => t.Code == "IVA19").CurrentRate!.Rate.ShouldBe(19m);
        taxes.Single(t => t.Code == "IVA_EXCLUIDO").IsExcluded.ShouldBeTrue();
        taxes.Single(t => t.Code == "INC_BOLSA").Status.ShouldBe("INACTIVE");
        taxes.Single(t => t.Code == "IBUA").CurrentRate.ShouldBeNull();

        (await GetAsync<List<PriceListDto>>(owner, "/api/v1/catalog/price-lists")).ShouldHaveSingleItem().IsDefault.ShouldBeTrue();
        (await GetAsync<List<CategoryDto>>(owner, "/api/v1/catalog/categories")).ShouldHaveSingleItem().Name.ShouldBe("General");
        (await GetAsync<List<BarcodeRuleDto>>(owner, "/api/v1/catalog/barcode-rules")).Select(r => (r.Prefix, r.Status))
            .ShouldBe([("20", "INACTIVE"), ("23", "INACTIVE")]);
    }

    [Fact]
    public async Task Productos_codigos_presentaciones_y_escaneo()
    {
        await using var factory = new PosServerFactory();
        var scenario = await CatalogScenario.CreateAsync(factory);
        var owner = scenario.Owner;
        var dairy = await scenario.CategoryAsync("Lácteos");
        var milk = await scenario.CategoryAsync("Leches", dairy);

        var product = await scenario.ProductAsync("leche-ent-1100", "Leche entera Alquería 1100 ml", milk, barcode: "7702177000014", price: 4_980m);
        product.Sku.ShouldBe("LECHE-ENT-1100");
        product.Taxes.ShouldHaveSingleItem().Code.ShouldBe("IVA19");
        product.Barcodes.ShouldHaveSingleItem().IsPrimary.ShouldBeTrue();
        product.Prices.ShouldHaveSingleItem().State.ShouldBe("CURRENT");

        // Un código identifica una sola cosa; el dígito de control se valida.
        (await owner.PostAsJsonAsync($"/api/v1/catalog/products/{product.Id}/barcodes", new { code = "7702177000014" }, Json, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await owner.PostAsJsonAsync($"/api/v1/catalog/products/{product.Id}/barcodes", new { code = "7702177000015" }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "CATALOG.INVALID_CHECK_DIGIT");

        // UPC-A: se encuentra con o sin el 0 inicial.
        (await owner.PostAsJsonAsync($"/api/v1/catalog/products/{product.Id}/barcodes", new { code = "036000291452" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await scenario.ScanAsync("036000291452")).ProductId.ShouldBe(product.Id);
        (await scenario.ScanAsync("0036000291452")).ProductId.ShouldBe(product.Id);

        // Presentación con su propio código y precio.
        var pack = await owner.PostAsJsonAsync($"/api/v1/catalog/products/{product.Id}/packagings",
            new { name = "Paquete x6", factor = 6, barcode = "7702177000021", price = 28_000 }, Json, Ct);
        pack.StatusCode.ShouldBe(HttpStatusCode.Created, await pack.Content.ReadAsStringAsync(Ct));
        var packScan = await scenario.ScanAsync("7702177000021");
        packScan.PackagingName.ShouldBe("Paquete x6");
        packScan.PackagingFactor.ShouldBe(6m);
        packScan.UnitPrice.ShouldBe(28_000m);
        packScan.IsSellable.ShouldBeTrue();

        var unitScan = await scenario.ScanAsync("7702177000014");
        unitScan.Source.ShouldBe("BARCODE");
        unitScan.UnitPrice.ShouldBe(4_980m);
        unitScan.Taxes.ShouldHaveSingleItem().Rate.ShouldBe(19m);
        (await scenario.ScanAsync("leche-ent-1100")).Source.ShouldBe("SKU");
        await owner.GetAsync("/api/v1/catalog/scan/9999999999994", Ct).ShouldFailWithAsync(HttpStatusCode.NotFound, "CATALOG.CODE_NOT_FOUND");

        // Sin SKU ni código: se generan con el número del nodo (001) y el prefijo 29.
        var generated = await scenario.ProductAsync(null, "Pan de la casa", dairy, generateBarcode: true);
        generated.Sku.ShouldBe("001-000001");
        generated.Barcodes.ShouldHaveSingleItem().Code.ShouldStartWith("29001");
        var noPrice = await scenario.ScanAsync(generated.Barcodes[0].Code);
        noPrice.IsSellable.ShouldBeFalse();
        noPrice.NotSellableReasons.ShouldContain(r => r.Contains("precio", StringComparison.Ordinal));

        // Búsqueda sin tildes, por palabras, por SKU y por código.
        (await GetAsync<PagedResult<ProductSummaryDto>>(owner, "/api/v1/catalog/products?search=alqueria%20entera")).Items.ShouldHaveSingleItem().Id.ShouldBe(product.Id);
        (await GetAsync<PagedResult<ProductSummaryDto>>(owner, "/api/v1/catalog/products?search=7702177000014")).TotalCount.ShouldBe(1);
        (await GetAsync<PagedResult<ProductSummaryDto>>(owner, $"/api/v1/catalog/products?categoryId={dairy}")).TotalCount.ShouldBe(2);
        (await GetAsync<PagedResult<ProductSummaryDto>>(owner, "/api/v1/catalog/products?search=LECHE-ENT-1100")).Items[0].Price.ShouldBe(4_980m);
    }

    [Fact]
    public async Task Etiquetas_de_bascula_por_peso_y_por_precio()
    {
        await using var factory = new PosServerFactory();
        var scenario = await CatalogScenario.CreateAsync(factory);
        var owner = scenario.Owner;
        var produce = await scenario.CategoryAsync("Fruver");
        var excluded = await scenario.TaxIdAsync("IVA_EXCLUIDO");
        var tomato = await scenario.ProductAsync("TOMATE", "Tomate chonto", produce, price: 4_980m, unit: "KG", saleMode: "Weight", scale: true, plu: "00123",
            taxes: [new { taxId = excluded }]);
        tomato.PluCode.ShouldBe("123");

        var rules = await GetAsync<List<BarcodeRuleDto>>(owner, "/api/v1/catalog/barcode-rules");
        foreach (var rule in rules)
        {
            (await owner.PutAsJsonAsync($"/api/v1/catalog/barcode-rules/{rule.Id}", new
            {
                prefix = rule.Prefix, content = rule.Content == "WEIGHT" ? "Weight" : "Price", pluStart = rule.PluStart, pluLength = rule.PluLength,
                valueStart = rule.ValueStart, valueLength = rule.ValueLength, valueDecimals = rule.ValueDecimals, isActive = true,
            }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var weight = await scenario.ScanAsync(CatalogScenario.Ean13("200012301250"));
        weight.Source.ShouldBe("SCALE_WEIGHT");
        weight.Quantity.ShouldBe(1.250m);
        weight.UnitPrice.ShouldBe(4_980m);
        weight.Amount.ShouldBe(6_225m);
        weight.Taxes.ShouldHaveSingleItem().Code.ShouldBe("IVA_EXCLUIDO");

        var price = await scenario.ScanAsync(CatalogScenario.Ean13("230012306225"));
        price.Source.ShouldBe("SCALE_PRICE");
        price.Amount.ShouldBe(6_225m);
        price.Quantity.ShouldBe(1.25m);

        // El prefijo 29 es de los códigos internos.
        await owner.PostAsJsonAsync("/api/v1/catalog/barcode-rules",
                new { prefix = "29", content = "Weight", pluStart = 3, pluLength = 5, valueStart = 8, valueLength = 5, valueDecimals = 3, isActive = true }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "CATALOG.PREFIX_RESERVED");
    }

    [Fact]
    public async Task Precios_con_vigencia_programados_historial_y_cancelacion()
    {
        await using var factory = new PosServerFactory();
        var scenario = await CatalogScenario.CreateAsync(factory);
        var owner = scenario.Owner;
        var category = await scenario.CategoryAsync("Granos");
        var rice = await scenario.ProductAsync("ARROZ-500", "Arroz Diana 500 g", category, price: 2_900m);

        var nextWeek = DateTimeOffset.UtcNow.AddDays(7);
        var scheduled = await owner.PostAsJsonAsync($"/api/v1/catalog/products/{rice.Id}/prices", new { price = 3_100, validFrom = nextWeek }, Json, Ct);
        scheduled.StatusCode.ShouldBe(HttpStatusCode.OK, await scheduled.Content.ReadAsStringAsync(Ct));
        var scheduledPrice = (await scheduled.Content.ReadFromJsonAsync<SetPriceResultDto>(Json, Ct))!.Price;
        scheduledPrice.State.ShouldBe("SCHEDULED");

        // Cambio de hoy: cierra el vigente y termina donde empieza el programado.
        (await owner.PostAsJsonAsync($"/api/v1/catalog/products/{rice.Id}/prices", new { price = 3_000 }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await scenario.ScanAsync("ARROZ-500")).UnitPrice.ShouldBe(3_000m);

        var history = await GetAsync<List<PriceDto>>(owner, $"/api/v1/catalog/products/{rice.Id}/prices");
        history.Count.ShouldBe(3);
        history.Single(p => p.Price == 2_900m).State.ShouldBe("EXPIRED");
        history.Single(p => p.Price == 3_000m).ValidTo.ShouldNotBeNull();

        // Precio en el pasado: rechazado. Cancelar el vigente: no se puede.
        await owner.PostAsJsonAsync($"/api/v1/catalog/products/{rice.Id}/prices", new { price = 1, validFrom = DateTimeOffset.UtcNow.AddDays(-1) }, Json, Ct)
            .ShouldFailWithAsync(HttpStatusCode.BadRequest, "CATALOG.PRICE_IN_THE_PAST");
        var current = history.Single(p => p.Price == 3_000m);
        await owner.DeleteAsync($"/api/v1/catalog/products/{rice.Id}/prices/{current.Id}", Ct)
            .ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CATALOG.PRICE_NOT_SCHEDULED");

        // Cancelar el programado: el de hoy recupera su vigencia abierta.
        (await owner.DeleteAsync($"/api/v1/catalog/products/{rice.Id}/prices/{scheduledPrice.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var after = await GetAsync<List<PriceDto>>(owner, $"/api/v1/catalog/products/{rice.Id}/prices");
        after.Count.ShouldBe(2);
        after.Single(p => p.Price == 3_000m).ValidTo.ShouldBeNull();

        // Tarifa de IVA programada.
        var vat5 = await scenario.TaxIdAsync("IVA5");
        (await owner.PostAsJsonAsync($"/api/v1/catalog/taxes/{vat5}/rates", new { rate = 7, validFrom = "2027-01-01" }, Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var tax = (await GetAsync<List<TaxDto>>(owner, "/api/v1/catalog/taxes")).Single(t => t.Code == "IVA5");
        tax.CurrentRate!.Rate.ShouldBe(5m);
        tax.Rates.Count.ShouldBe(2);

        (await scenario.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action IN ('PRODUCT_PRICE_CHANGED', 'PRODUCT_PRICE_SCHEDULED')")).ShouldBe(3);
    }

    [Fact]
    public async Task Un_cambio_de_producto_viaja_a_la_sincronizacion_solo_con_los_campos_modificados()
    {
        await using var factory = new PosServerFactory();
        var scenario = await CatalogScenario.CreateAsync(factory);
        var category = await scenario.CategoryAsync("Aseo");
        var soap = await scenario.ProductAsync("JABON-1", "Jabón de baño", category);

        var update = await scenario.Owner.PutAsJsonAsync($"/api/v1/catalog/products/{soap.Id}", new
        {
            sku = "JABON-1", name = "Jabón de baño Protex", shortName = soap.ShortName, description = (string?)null, categoryId = category,
            brandId = (Guid?)null, baseUnitCode = "UND", saleMode = "Unit", productType = "Stockable", isSoldByScale = false, allowsDecimalQuantity = false,
            allowsOpenPrice = false, tracksLots = false, tracksExpiry = false, pluCode = (string?)null, netContent = (decimal?)null, netContentUnit = (string?)null,
        }, Json, Ct);
        update.StatusCode.ShouldBe(HttpStatusCode.OK, await update.Content.ReadAsStringAsync(Ct));

        var payload = await scenario.ScalarAsync<string>(
            $"""
            SELECT payload::text FROM system.outbox_messages
            WHERE destination = 'SYNC' AND type = 'sync.entity_changed.v1' AND payload->>'id' = '{soap.Id}' AND payload->>'operation' = 'UPDATED'
            """);
        var json = System.Text.Json.JsonDocument.Parse(payload!).RootElement;
        json.GetProperty("entity").GetString().ShouldBe("catalog.products");
        json.GetProperty("baseVersion").GetInt64().ShouldBe(1);
        json.GetProperty("version").GetInt64().ShouldBe(2);
        json.GetProperty("changes").EnumerateObject().Select(p => p.Name).ShouldBe(["name", "search_text"], ignoreOrder: true);
        json.GetProperty("changes").GetProperty("name").GetString().ShouldBe("Jabón de baño Protex");
    }

    [Fact]
    public async Task Importacion_de_productos_y_precios_con_vista_previa_y_aplicacion_atomica()
    {
        await using var factory = new PosServerFactory();
        var scenario = await CatalogScenario.CreateAsync(factory);
        var owner = scenario.Owner;
        var dairy = await scenario.CategoryAsync("Lácteos");
        var milk = await scenario.ProductAsync("LECHE-1", "Leche entera", dairy, barcode: "7702177000014", price: 4_980m);

        var template = await owner.GetAsync("/api/v1/catalog/imports/templates/products", Ct);
        template.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await template.Content.ReadAsStringAsync(Ct)).ShouldContain("codigo_barras");

        // Un archivo con un error no aplica nada.
        var withError = CatalogScenario.Csv(
            "sku;nombre;categoria;marca;iva;codigo_barras;precio",
            "LECHE-1;Leche entera 1100 ml;;;;;5.200",
            ";Jabón Rey;Aseo > Jabones;Rey;IVA19;7702177000021;3.500",
            ";Mal código;Aseo;;;7702177000015;1");
        var preview = await scenario.UploadAsync("/api/v1/catalog/imports?kind=products", withError, "productos.csv");
        preview.StatusCode.ShouldBe(HttpStatusCode.Created, await preview.Content.ReadAsStringAsync(Ct));
        var batch = (await preview.Content.ReadFromJsonAsync<ImportBatchDto>(Json, Ct))!;
        (batch.CreateRows, batch.UpdateRows, batch.ErrorRows).ShouldBe((1, 1, 1));
        batch.Rows.Single(r => r.RowNumber == 3).Warnings.ShouldContain(w => w.Contains("Aseo > Jabones", StringComparison.Ordinal));
        batch.Rows.Single(r => r.RowNumber == 4).Errors.ShouldContain(e => e.Contains("dígito de control", StringComparison.Ordinal));
        await owner.PostAsync($"/api/v1/catalog/imports/{batch.Id}/apply", null, Ct).ShouldFailWithAsync(HttpStatusCode.UnprocessableEntity, "CATALOG.IMPORT_HAS_ERRORS");
        (await GetAsync<PagedResult<ProductSummaryDto>>(owner, "/api/v1/catalog/products?search=jabon")).TotalCount.ShouldBe(0);

        // Corregido: se aplica todo en una transacción.
        var fixedFile = CatalogScenario.Csv(
            "sku;nombre;categoria;marca;iva;codigo_barras;precio",
            "LECHE-1;Leche entera 1100 ml;;;;;5.200",
            ";Jabón Rey;Aseo > Jabones;Rey;IVA19;7702177000021;3.500");
        var ok = (await (await scenario.UploadAsync("/api/v1/catalog/imports?kind=products", fixedFile, "productos.csv")).Content.ReadFromJsonAsync<ImportBatchDto>(Json, Ct))!;
        var applied = await owner.PostAsync($"/api/v1/catalog/imports/{ok.Id}/apply", null, Ct);
        applied.StatusCode.ShouldBe(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        (await applied.Content.ReadFromJsonAsync<ImportBatchDto>(Json, Ct))!.Status.ShouldBe("APPLIED");
        (await owner.PostAsync($"/api/v1/catalog/imports/{ok.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await scenario.ScanAsync("7702177000014")).UnitPrice.ShouldBe(5_200m);
        var soap = await scenario.ScanAsync("7702177000021");
        soap.Name.ShouldBe("Jabón Rey");
        soap.UnitPrice.ShouldBe(3_500m);
        (await GetAsync<List<CategoryDto>>(owner, "/api/v1/catalog/categories")).ShouldContain(c => c.Name == "Jabones" && c.Level == 2);
        (await GetAsync<List<BrandDto>>(owner, "/api/v1/catalog/brands")).ShouldContain(b => b.Name == "Rey");
        (await GetAsync<ProductDetailDto>(owner, $"/api/v1/catalog/products/{milk.Id}")).Name.ShouldBe("Leche entera 1100 ml");

        // Precios desde Excel, uno programado para una sucursal.
        using var excel = new MemoryStream();
        await excel.SaveAsAsync(
            new[]
            {
                new Dictionary<string, object?> { ["sku"] = "LECHE-1", ["codigo_barras"] = null, ["sucursal"] = null, ["precio"] = 5_300, ["vigente_desde"] = null },
                new Dictionary<string, object?> { ["sku"] = null, ["codigo_barras"] = "7702177000021", ["sucursal"] = "S01", ["precio"] = 3_400, ["vigente_desde"] = DateTime.Today.AddDays(3) },
                new Dictionary<string, object?> { ["sku"] = "NO-EXISTE", ["codigo_barras"] = null, ["sucursal"] = null, ["precio"] = 1, ["vigente_desde"] = null },
            },
            cancellationToken: Ct);
        var pricePreview = (await (await scenario.UploadAsync("/api/v1/catalog/imports?kind=prices", excel.ToArray(), "precios.xlsx")).Content
            .ReadFromJsonAsync<ImportBatchDto>(Json, Ct))!;
        (pricePreview.CreateRows, pricePreview.ErrorRows).ShouldBe((2, 1));
        await owner.PostAsync($"/api/v1/catalog/imports/{pricePreview.Id}/discard", null, Ct);
        (await GetAsync<ImportBatchDto>(owner, $"/api/v1/catalog/imports/{pricePreview.Id}")).Status.ShouldBe("DISCARDED");

        using var good = new MemoryStream();
        await good.SaveAsAsync(new[] { new Dictionary<string, object?> { ["sku"] = "LECHE-1", ["precio"] = 5_300 } }, cancellationToken: Ct);
        var priceBatch = (await (await scenario.UploadAsync("/api/v1/catalog/imports?kind=prices", good.ToArray(), "precios.xlsx")).Content
            .ReadFromJsonAsync<ImportBatchDto>(Json, Ct))!;
        (await owner.PostAsync($"/api/v1/catalog/imports/{priceBatch.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await scenario.ScanAsync("LECHE-1")).UnitPrice.ShouldBe(5_300m);
        (await scenario.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'CATALOG_IMPORT_APPLIED'")).ShouldBe(2);
    }
}
