using System.Globalization;
using System.Text;
using System.Text.Json;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Data;
using Pos.Application.Abstractions.Files;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Text;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Catalog.Application;

public enum ImportKind
{
    Products,
    Prices,
}

/// <summary>Fila de la plantilla de productos tal como vino en el archivo (texto).</summary>
public sealed record ProductImportRow(
    string? Sku, string? Name, string? ShortName, string? Description, string? Category, string? Brand, string? Unit, string? SaleMode,
    string? Type, string? Vat, string? Barcode, string? Price, string? Plu, string? Scale, string? NetContent, string? NetContentUnit);

/// <summary>Fila de la plantilla de precios tal como vino en el archivo (texto).</summary>
public sealed record PriceImportRow(string? Sku, string? Barcode, string? Packaging, string? Branch, string? Price, string? ValidFrom);

/// <summary>Columnas de las plantillas (encabezados normalizados: minúsculas, sin tildes, "_" en vez de espacios).</summary>
public static class ImportColumns
{
    public static readonly string[] Products =
        ["sku", "nombre", "nombre_corto", "descripcion", "categoria", "marca", "unidad", "modo_venta", "tipo", "iva", "codigo_barras",
         "precio", "plu", "bascula", "contenido_neto", "unidad_contenido"];

    public static readonly string[] Prices = ["sku", "codigo_barras", "presentacion", "sucursal", "precio", "vigente_desde"];

    public static string Template(ImportKind kind)
    {
        var builder = new StringBuilder();
        if (kind == ImportKind.Products)
        {
            builder.AppendLine(string.Join(';', Products));
            builder.AppendLine("LECHE-ENT-1100;Leche entera Alquería 1100 ml;Leche entera 1100;;Lácteos > Leches;Alquería;UND;UNIDAD;INVENTARIABLE;IVA5;7702177000014;4.980;;NO;1100;ML");
            builder.AppendLine(";Tomate chonto;Tomate chonto;;Fruver > Verduras;;KG;PESO;INVENTARIABLE;IVA_EXCLUIDO;;4.980;123;SI;;");
        }
        else
        {
            builder.AppendLine(string.Join(';', Prices));
            builder.AppendLine("LECHE-ENT-1100;;;;5.200;");
            builder.AppendLine(";7702177000014;;S01;5.150;2026-10-06");
        }

        return builder.ToString();
    }
}

internal sealed record AnalyzedRow<T>(int RowNumber, string Key, T Row, string Action, List<string> Errors, List<string> Warnings);

/// <summary>
/// Importación de productos y precios (propuesta §5.6, D4-13): se valida TODO el archivo sin guardar nada, se muestra la
/// vista previa por fila (crear, actualizar, sin cambios, error) y solo si el usuario confirma y no hay errores se aplica
/// en UNA transacción, volviendo a validar contra el estado actual. Una celda vacía nunca borra un dato existente.
/// </summary>
public sealed class CatalogImportService(
    IInstallationContext installation,
    ICatalogStore store,
    IInventoryQueries inventory,
    IWarehouseDirectory directory,
    ISettingsReader settings,
    IUnitOfWork unitOfWork,
    IIdGenerator ids,
    IClock clock)
{
    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "dd/MM/yyyy", "dd/MM/yyyy HH:mm", "d/M/yyyy", "d/M/yyyy H:mm"];

    // ─────────────────────────── Productos ───────────────────────────

    internal static ProductImportRow ReadProduct(TabularRow row) => new(
        row.Get("sku") ?? row.Get("codigo_interno"), row.Get("nombre"), row.Get("nombre_corto"), row.Get("descripcion"), row.Get("categoria"),
        row.Get("marca"), row.Get("unidad") ?? row.Get("unidad_base"), row.Get("modo_venta"), row.Get("tipo"), row.Get("iva") ?? row.Get("impuesto"),
        row.Get("codigo_barras") ?? row.Get("codigo_de_barras") ?? row.Get("ean"), row.Get("precio") ?? row.Get("precio_venta"), row.Get("plu"),
        row.Get("bascula"), row.Get("contenido_neto"), row.Get("unidad_contenido"));

    internal async Task<List<AnalyzedRow<ProductImportRow>>> AnalyzeProductsAsync(
        Guid companyId, IReadOnlyList<(int Number, ProductImportRow Row)> rows, CancellationToken cancellationToken)
    {
        var lookups = await ProductLookups.LoadAsync(store, inventory, rows.Select(r => r.Row).ToList(), clock.UtcNow, cancellationToken);
        var defaultVat = await settings.GetAsync(CatalogSettings.DefaultVatCode, new SettingContext(companyId), cancellationToken);
        var seenSkus = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenBarcodes = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenPlus = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<AnalyzedRow<ProductImportRow>>();

        foreach (var (number, row) in rows)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var sku = row.Sku is null ? null : Product.NormalizeSku(row.Sku);
            NormalizedBarcode? barcode = null;
            if (row.Barcode is not null)
            {
                var normalized = Barcodes.Normalize(row.Barcode);
                if (normalized.IsFailure)
                {
                    errors.Add($"codigo_barras: {normalized.Error.Message}");
                }
                else
                {
                    barcode = normalized.Value;
                }
            }

            if (sku is not null && !seenSkus.TryAdd(sku, number))
            {
                errors.Add($"sku: repetido en la fila {seenSkus[sku]}.");
            }

            if (barcode is not null && !seenBarcodes.TryAdd(barcode.NormalizedCode, number))
            {
                errors.Add($"codigo_barras: repetido en la fila {seenBarcodes[barcode.NormalizedCode]}.");
            }

            var bySku = sku is not null ? lookups.ProductBySku.GetValueOrDefault(sku) : null;
            var barcodeOwner = barcode is not null ? lookups.BarcodeOwner.GetValueOrDefault(barcode.NormalizedCode) : null;
            var existing = bySku ?? (sku is null && barcodeOwner is { } owner ? lookups.ProductById.GetValueOrDefault(owner.ProductId) : null);
            if (barcodeOwner is not null && (existing is null || barcodeOwner.ProductId != existing.Id))
            {
                errors.Add("codigo_barras: ya pertenece a otro producto.");
            }

            if (existing is null && row.Name is null)
            {
                errors.Add("nombre: obligatorio para crear el producto.");
            }

            var categoryPath = SplitPath(row.Category);
            if (categoryPath.Count > Category.MaxLevel)
            {
                errors.Add("categoria: máximo 4 niveles (Nivel1 > Nivel2 > …).");
            }
            else if (categoryPath.Count > 0 && lookups.FindCategory(categoryPath) is null)
            {
                warnings.Add($"Se creará la categoría '{string.Join(" > ", categoryPath)}'.");
            }

            if (row.Brand is not null && lookups.FindBrand(row.Brand) is null)
            {
                warnings.Add($"Se creará la marca '{row.Brand}'.");
            }

            var unitCode = row.Unit?.ToUpperInvariant() ?? existing?.BaseUnitCode ?? "UND";
            var unit = lookups.Units.GetValueOrDefault(unitCode);
            if (unit is null)
            {
                errors.Add($"unidad: '{unitCode}' no existe (UND, KG, G, LB, L, ML, GAL, M, CM).");
            }

            var saleMode = ParseSaleMode(row.SaleMode, unit, existing, errors);
            var productType = ParseType(row.Type, existing, errors);
            var scale = row.Scale is null ? existing?.IsSoldByScale ?? false : ParseBool(row.Scale);
            decimal? netContent = existing?.NetContent;
            if (row.NetContent is not null)
            {
                netContent = DecimalParsing.TryParse(row.NetContent, out var content) ? content : null;
                if (netContent is null)
                {
                    errors.Add("contenido_neto: número inválido.");
                }
            }

            var vatCode = row.Vat?.ToUpperInvariant() ?? (existing is null ? defaultVat : null);
            if (vatCode is not null && lookups.Taxes.GetValueOrDefault(vatCode) is not { IsVat: true, Status: MasterStatus.Active })
            {
                errors.Add($"iva: '{vatCode}' no es un IVA activo (IVA19, IVA5, IVA0_EXENTO, IVA_EXCLUIDO).");
            }

            decimal? price = null;
            if (row.Price is not null)
            {
                price = DecimalParsing.TryParse(row.Price, out var parsed) && ProductPrice.IsValidPrice(parsed) ? parsed : null;
                if (price is null)
                {
                    errors.Add("precio: debe ser un número mayor o igual a cero con máximo 2 decimales.");
                }
            }

            var plu = row.Plu ?? existing?.PluCode;
            if (Product.NormalizePlu(plu) is { } normalizedPlu && !seenPlus.TryAdd(normalizedPlu, number))
            {
                errors.Add($"plu: repetido en la fila {seenPlus[normalizedPlu]}.");
            }

            if (unit is not null && saleMode is not null && productType is not null && (row.Name ?? existing?.Name) is not null)
            {
                var data = new ProductData(
                    sku ?? existing?.Sku ?? "PENDIENTE", row.Name ?? existing!.Name, row.ShortName ?? existing?.ShortName, row.Description ?? existing?.Description,
                    Guid.Empty, null, unit.Code, unit.Dimension, saleMode.Value, productType.Value, scale,
                    existing?.AllowsDecimalQuantity ?? false, existing?.AllowsOpenPrice ?? false, existing?.TracksLots ?? false,
                    existing?.TracksExpiry ?? false, plu, netContent, row.NetContentUnit?.ToUpperInvariant() ?? existing?.NetContentUnit);
                var check = Product.Create(Guid.Empty, companyId, data);
                if (check.IsFailure)
                {
                    errors.Add(check.Error.Message);
                }
                else if (existing is not null && lookups.WithMovements.Contains(existing.Id)
                    && (data.BaseUnitCode != existing.BaseUnitCode || data.ProductType != existing.ProductType))
                {
                    errors.Add(CatalogErrors.BaseUnitLocked.Message);
                }
            }

            var action = errors.Count > 0 ? "ERROR"
                : existing is null ? "CREATE"
                : ProductChanges(existing, row, lookups, sku, barcode, vatCode, price, saleMode, productType, scale, netContent, unitCode) ? "UPDATE"
                : "UNCHANGED";
            result.Add(new AnalyzedRow<ProductImportRow>(number, sku ?? barcode?.Code ?? row.Name ?? string.Empty, row, action, errors, warnings));
        }

        return result;
    }

    internal async Task ApplyProductsAsync(Guid companyId, List<AnalyzedRow<ProductImportRow>> rows, CancellationToken cancellationToken)
    {
        var lookups = await ProductLookups.LoadAsync(store, inventory, rows.Select(r => r.Row).ToList(), clock.UtcNow, cancellationToken);
        var defaultVat = await settings.GetAsync(CatalogSettings.DefaultVatCode, new SettingContext(companyId), cancellationToken);
        var list = (await store.GetPriceListsAsync(cancellationToken)).Single(l => l.IsDefault);
        var now = clock.UtcNow;
        var newPrices = new List<(Product Product, decimal Price)>();
        var existingPrices = (await store.GetPricesAsync(list.Id, lookups.ProductById.Keys.ToList(), cancellationToken))
            .Where(p => p.PackagingId is null && p.BranchId is null).ToList();

        foreach (var analyzed in rows.Where(r => r.Action is "CREATE" or "UPDATE"))
        {
            var row = analyzed.Row;
            var sku = row.Sku is null ? null : Product.NormalizeSku(row.Sku);
            var barcode = row.Barcode is null ? null : Barcodes.Normalize(row.Barcode).Value;
            var existing = (sku is null ? null : lookups.ProductBySku.GetValueOrDefault(sku))
                ?? (sku is null && barcode is not null && lookups.BarcodeOwner.GetValueOrDefault(barcode.NormalizedCode) is { } owner
                    ? lookups.ProductById.GetValueOrDefault(owner.ProductId) : null);

            var categoryId = EnsureCategory(companyId, lookups, SplitPath(row.Category), existing?.CategoryId);
            var brandId = row.Brand is null ? existing?.BrandId : EnsureBrand(companyId, lookups, row.Brand);
            var unit = lookups.Units[row.Unit?.ToUpperInvariant() ?? existing?.BaseUnitCode ?? "UND"];
            var errors = new List<string>();
            var data = new ProductData(
                sku ?? existing?.Sku ?? await NextSkuAsync(cancellationToken), row.Name ?? existing!.Name, row.ShortName ?? existing?.ShortName,
                row.Description ?? existing?.Description, categoryId, brandId, unit.Code, unit.Dimension, ParseSaleMode(row.SaleMode, unit, existing, errors)!.Value,
                ParseType(row.Type, existing, errors)!.Value, row.Scale is null ? existing?.IsSoldByScale ?? false : ParseBool(row.Scale),
                existing?.AllowsDecimalQuantity ?? false, existing?.AllowsOpenPrice ?? false, existing?.TracksLots ?? false, existing?.TracksExpiry ?? false,
                row.Plu ?? existing?.PluCode, row.NetContent is null ? existing?.NetContent : DecimalParsing.TryParse(row.NetContent, out var content) ? content : null,
                row.NetContentUnit?.ToUpperInvariant() ?? existing?.NetContentUnit);

            Product product;
            if (existing is null)
            {
                product = Product.Create(ids.NewId(), companyId, data).Value;
                store.Add(product);
                lookups.ProductById[product.Id] = product;
            }
            else
            {
                product = existing;
                product.Update(data, lookups.WithMovements.Contains(product.Id));
            }

            if (barcode is not null && !lookups.BarcodeOwner.ContainsKey(barcode.NormalizedCode))
            {
                var hasPrimary = lookups.PrimaryBarcodeProducts.Contains(product.Id);
                store.Add(ProductBarcode.Create(ids.NewId(), product, null, barcode, isPrimary: !hasPrimary));
                lookups.PrimaryBarcodeProducts.Add(product.Id);
                lookups.BarcodeOwner[barcode.NormalizedCode] = new BarcodeOwnerInfo(product.Id);
            }

            var vatCode = row.Vat?.ToUpperInvariant() ?? (existing is null ? defaultVat : null);
            if (vatCode is not null && lookups.Taxes.GetValueOrDefault(vatCode) is { } vat && lookups.VatByProduct.GetValueOrDefault(product.Id) != vat.Id)
            {
                foreach (var old in lookups.ProductTaxes.Where(t => t.ProductId == product.Id && lookups.TaxById[t.TaxId].IsVat).ToList())
                {
                    store.Remove(old);
                }

                store.Add(ProductTax.Create(ids.NewId(), product, vat, null).Value);
                lookups.VatByProduct[product.Id] = vat.Id;
            }

            if (row.Price is not null && DecimalParsing.TryParse(row.Price, out var price))
            {
                var current = existingPrices.FirstOrDefault(p => p.ProductId == product.Id && p.IsValidAt(now));
                if (current?.Price != price)
                {
                    current?.CloseAt(now);
                    newPrices.Add((product, price));
                }
            }
        }

        // Las vigencias cerradas se guardan ANTES de insertar los precios nuevos (restricción de exclusión).
        await unitOfWork.SaveChangesAsync(cancellationToken);
        foreach (var (product, price) in newPrices)
        {
            var next = existingPrices.Where(p => p.ProductId == product.Id && p.ValidFrom > now).OrderBy(p => p.ValidFrom).FirstOrDefault();
            store.Add(ProductPrice.Create(ids.NewId(), list, product, null, null, price, now, next?.ValidFrom).Value);
        }
    }

    private static bool ProductChanges(
        Product existing, ProductImportRow row, ProductLookups lookups, string? sku, NormalizedBarcode? barcode, string? vatCode, decimal? price,
        SaleMode? saleMode, ProductType? type, bool scale, decimal? netContent, string unitCode)
    {
        var category = SplitPath(row.Category);
        var categoryChanged = category.Count > 0 && lookups.FindCategory(category)?.Id != existing.CategoryId;
        var brandChanged = row.Brand is not null && lookups.FindBrand(row.Brand)?.Id != existing.BrandId;
        var vatChanged = vatCode is not null && lookups.Taxes.GetValueOrDefault(vatCode)?.Id != lookups.VatByProduct.GetValueOrDefault(existing.Id);
        var priceChanged = price is not null && lookups.CurrentPrice.GetValueOrDefault(existing.Id) != price;
        var barcodeNew = barcode is not null && !lookups.BarcodeOwner.ContainsKey(barcode.NormalizedCode);
        return (sku is not null && sku != existing.Sku) || (row.Name is not null && row.Name != existing.Name)
            || (row.ShortName is not null && row.ShortName != existing.ShortName) || (row.Description is not null && row.Description != existing.Description)
            || unitCode != existing.BaseUnitCode || saleMode != existing.SaleMode || type != existing.ProductType || scale != existing.IsSoldByScale
            || (row.Plu is not null && Product.NormalizePlu(row.Plu) != existing.PluCode) || netContent != existing.NetContent
            || (row.NetContentUnit is not null && !string.Equals(row.NetContentUnit, existing.NetContentUnit, StringComparison.OrdinalIgnoreCase))
            || categoryChanged || brandChanged || vatChanged || priceChanged || barcodeNew;
    }

    private Guid EnsureCategory(Guid companyId, ProductLookups lookups, List<string> path, Guid? current)
    {
        if (path.Count == 0)
        {
            return current ?? lookups.DefaultCategoryId()
                ?? throw new InvalidOperationException("No hay categorías: la empresa no recibió sus datos iniciales.");
        }

        Category? parent = null;
        foreach (var name in path)
        {
            var existing = lookups.Categories.FirstOrDefault(c => c.ParentId == parent?.Id && Same(c.Name, name));
            if (existing is null)
            {
                existing = Category.Create(ids.NewId(), companyId, name, parent).Value;
                store.Add(existing);
                lookups.Categories.Add(existing);
            }

            parent = existing;
        }

        return parent!.Id;
    }

    private Guid EnsureBrand(Guid companyId, ProductLookups lookups, string name)
    {
        var brand = lookups.FindBrand(name);
        if (brand is null)
        {
            brand = Brand.Create(ids.NewId(), companyId, name).Value;
            store.Add(brand);
            lookups.Brands.Add(brand);
        }

        return brand.Id;
    }

    private async Task<string> NextSkuAsync(CancellationToken cancellationToken)
    {
        var sequence = await store.NextCodeSequenceAsync(installation.NodeId, "SKU", cancellationToken);
        return Barcodes.InternalSku(await directory.GetLocalNodeNumberAsync(cancellationToken), sequence);
    }

    private static SaleMode? ParseSaleMode(string? text, UnitInfo? unit, Product? existing, List<string> errors)
    {
        if (text is null)
        {
            return existing?.SaleMode ?? unit?.Dimension switch
            {
                "WEIGHT" => SaleMode.Weight,
                "VOLUME" => SaleMode.Volume,
                _ => SaleMode.Unit,
            };
        }

        switch (TextNormalization.ForSearch(text))
        {
            case "unidad" or "und" or "u":
                return SaleMode.Unit;
            case "peso" or "kg" or "kilo":
                return SaleMode.Weight;
            case "volumen" or "litro":
                return SaleMode.Volume;
            default:
                errors.Add("modo_venta: use UNIDAD, PESO o VOLUMEN.");
                return null;
        }
    }

    private static ProductType? ParseType(string? text, Product? existing, List<string> errors)
    {
        if (text is null)
        {
            return existing?.ProductType ?? ProductType.Stockable;
        }

        switch (TextNormalization.ForSearch(text))
        {
            case "inventariable" or "producto":
                return ProductType.Stockable;
            case "servicio":
                return ProductType.Service;
            default:
                errors.Add("tipo: use INVENTARIABLE o SERVICIO.");
                return null;
        }
    }

    internal static bool ParseBool(string text) => TextNormalization.ForSearch(text) is "si" or "s" or "x" or "1" or "true" or "verdadero";

    internal static List<string> SplitPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? [] : [.. path.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    internal static bool Same(string a, string b) => TextNormalization.ForSearch(a) == TextNormalization.ForSearch(b);

    // ─────────────────────────── Precios ───────────────────────────

    internal static PriceImportRow ReadPrice(TabularRow row) => new(
        row.Get("sku") ?? row.Get("codigo_interno"), row.Get("codigo_barras") ?? row.Get("codigo_de_barras") ?? row.Get("ean"),
        row.Get("presentacion"), row.Get("sucursal"), row.Get("precio") ?? row.Get("precio_venta"), row.Get("vigente_desde"));

    internal sealed record PricePlan(Product Product, ProductPackaging? Packaging, Guid? BranchId, decimal Price, DateTimeOffset From);

    internal async Task<List<(AnalyzedRow<PriceImportRow> Row, PricePlan? Plan)>> AnalyzePricesAsync(
        Guid companyId, IReadOnlyList<(int Number, PriceImportRow Row)> rows, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var skus = rows.Select(r => r.Row.Sku).OfType<string>().Select(Product.NormalizeSku).Distinct(StringComparer.Ordinal).ToList();
        var codes = rows.Select(r => r.Row.Barcode).OfType<string>().Select(Barcodes.NormalizeForLookup).Distinct(StringComparer.Ordinal).ToList();
        var bySku = (await store.GetProductsBySkusAsync(skus, cancellationToken)).ToDictionary(p => p.Sku, StringComparer.Ordinal);
        var barcodes = (await store.FindBarcodesAsync(codes, cancellationToken)).ToDictionary(b => b.NormalizedCode, StringComparer.Ordinal);
        var byId = bySku.Values.Concat(await store.GetProductsByIdsAsync(barcodes.Values.Select(b => b.ProductId).Distinct().ToList(), cancellationToken))
            .DistinctBy(p => p.Id).ToDictionary(p => p.Id);
        var packagings = (await store.GetPackagingsAsync(byId.Keys.ToList(), cancellationToken)).ToLookup(p => p.ProductId);
        var list = (await store.GetPriceListsAsync(cancellationToken)).Single(l => l.IsDefault);
        var prices = await store.GetPricesAsync(list.Id, byId.Keys.ToList(), cancellationToken);
        var policy = await settings.GetAsync(CatalogSettings.PriceBelowCost, new SettingContext(companyId), cancellationToken);
        var costs = installation.BranchId is { } home ? await inventory.GetAverageCostsAsync(byId.Keys.ToList(), home, cancellationToken) : new Dictionary<Guid, decimal>();
        var branches = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<(Guid, Guid?, Guid?, DateTimeOffset), int>();
        var result = new List<(AnalyzedRow<PriceImportRow>, PricePlan?)>();

        foreach (var (number, row) in rows)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            Product? product = null;
            ProductPackaging? packaging = null;
            if (row.Sku is not null)
            {
                product = bySku.GetValueOrDefault(Product.NormalizeSku(row.Sku));
            }
            else if (row.Barcode is not null && barcodes.GetValueOrDefault(Barcodes.NormalizeForLookup(row.Barcode)) is { } barcode)
            {
                product = byId.GetValueOrDefault(barcode.ProductId);
                packaging = barcode.PackagingId is { } pid ? packagings[barcode.ProductId].SingleOrDefault(p => p.Id == pid) : null;
            }

            if (product is null)
            {
                errors.Add(row.Sku is null && row.Barcode is null ? "sku o codigo_barras: indique uno de los dos." : "El producto no existe.");
            }

            if (product is not null && row.Packaging is not null)
            {
                packaging = packagings[product.Id].FirstOrDefault(p => Same(p.Name, row.Packaging));
                if (packaging is null)
                {
                    errors.Add($"presentacion: el producto no tiene la presentación '{row.Packaging}'.");
                }
            }

            if (packaging is { IsSellable: false })
            {
                errors.Add(CatalogErrors.PackagingNotSellable.Message);
            }

            Guid? branchId = null;
            if (row.Branch is not null)
            {
                if (!branches.TryGetValue(row.Branch, out branchId))
                {
                    branchId = await directory.FindBranchIdByCodeAsync(row.Branch, cancellationToken);
                    branches[row.Branch] = branchId;
                }

                if (branchId is null)
                {
                    errors.Add($"sucursal: '{row.Branch}' no existe.");
                }
            }

            var valid = DecimalParsing.TryParse(row.Price, out var price) && ProductPrice.IsValidPrice(price);
            if (!valid)
            {
                errors.Add("precio: debe ser un número mayor o igual a cero con máximo 2 decimales.");
            }

            var from = now;
            if (row.ValidFrom is not null)
            {
                if (ParseDate(row.ValidFrom) is { } parsed)
                {
                    from = parsed < now - PriceService.PastTolerance ? DateTimeOffset.MinValue : parsed < now ? now : parsed;
                    if (from == DateTimeOffset.MinValue)
                    {
                        errors.Add("vigente_desde: " + CatalogErrors.PriceInThePast.Message);
                    }
                }
                else
                {
                    errors.Add("vigente_desde: fecha inválida (use AAAA-MM-DD o DD/MM/AAAA).");
                }
            }

            string action = "ERROR";
            PricePlan? plan = null;
            if (errors.Count == 0 && product is not null)
            {
                var key = (product.Id, packaging?.Id, branchId, from);
                if (!seen.TryAdd(key, number))
                {
                    errors.Add($"Precio repetido en la fila {seen[key]} (mismo producto, presentación, sucursal y fecha).");
                }
                else
                {
                    var current = prices.FirstOrDefault(p => p.ProductId == product.Id && p.PackagingId == packaging?.Id && p.BranchId == branchId && p.IsValidAt(from));
                    action = current?.Price == price && current.ValidFrom <= now ? "UNCHANGED" : "CREATE";
                    plan = new PricePlan(product, packaging, branchId, price, from);
                    if (costs.TryGetValue(product.Id, out var cost) && price / (packaging?.Factor ?? 1m) < cost)
                    {
                        (policy == CatalogSettings.Block ? errors : warnings).Add(
                            string.Create(CultureInfo.InvariantCulture, $"El precio por {product.BaseUnitCode} queda por debajo del costo promedio ({cost:0.##})."));
                    }
                }
            }

            result.Add((new AnalyzedRow<PriceImportRow>(number, product?.Sku ?? row.Sku ?? row.Barcode ?? string.Empty, row,
                errors.Count > 0 ? "ERROR" : action, errors, warnings), errors.Count > 0 ? null : plan));
        }

        return result;
    }

    internal async Task ApplyPricesAsync(IReadOnlyList<PricePlan> plans, CancellationToken cancellationToken)
    {
        var list = (await store.GetPriceListsAsync(cancellationToken)).Single(l => l.IsDefault);
        var prices = (await store.GetPricesAsync(list.Id, plans.Select(p => p.Product.Id).Distinct().ToList(), cancellationToken)).ToList();
        var inserts = new List<ProductPrice>();
        foreach (var plan in plans.OrderBy(p => p.From))
        {
            var key = prices.Concat(inserts)
                .Where(p => p.ProductId == plan.Product.Id && p.PackagingId == plan.Packaging?.Id && p.BranchId == plan.BranchId)
                .OrderBy(p => p.ValidFrom).ToList();
            var decision = ValidityPlanner.PlanInsert<DateTimeOffset>([.. key.Select(p => (p.ValidFrom, p.ValidTo))], plan.From);
            if (decision.ReplaceIndex is { } replace)
            {
                key[replace].Reprice(plan.Price);
                continue;
            }

            if (decision.CloseIndex is { } close)
            {
                key[close].CloseAt(plan.From);
            }

            inserts.Add(ProductPrice.Create(ids.NewId(), list, plan.Product, plan.Packaging, plan.BranchId, plan.Price, plan.From, decision.NewValidTo).Value);
        }

        // Cierres primero (restricción de exclusión), luego los precios nuevos.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        foreach (var price in inserts)
        {
            store.Add(price);
        }
    }

    private DateTimeOffset? ParseDate(string text)
    {
        if (!DateTime.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var offset = clock.BusinessTimeZone.GetUtcOffset(local);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).ToUniversalTime();
    }
}

internal sealed record BarcodeOwnerInfo(Guid ProductId);

/// <summary>Datos precargados para analizar y aplicar una importación de productos sin una consulta por fila.</summary>
internal sealed class ProductLookups
{
    public Dictionary<string, Product> ProductBySku { get; } = new(StringComparer.Ordinal);

    public Dictionary<Guid, Product> ProductById { get; } = [];

    public Dictionary<string, BarcodeOwnerInfo> BarcodeOwner { get; } = new(StringComparer.Ordinal);

    public HashSet<Guid> PrimaryBarcodeProducts { get; } = [];

    public List<Category> Categories { get; private set; } = [];

    public List<Brand> Brands { get; private set; } = [];

    public Dictionary<string, UnitInfo> Units { get; private set; } = new(StringComparer.Ordinal);

    public Dictionary<string, Tax> Taxes { get; private set; } = new(StringComparer.Ordinal);

    public Dictionary<Guid, Tax> TaxById { get; private set; } = [];

    public List<ProductTax> ProductTaxes { get; private set; } = [];

    public Dictionary<Guid, Guid> VatByProduct { get; } = [];

    public Dictionary<Guid, decimal> CurrentPrice { get; } = [];

    public IReadOnlySet<Guid> WithMovements { get; private set; } = new HashSet<Guid>();

    public static async Task<ProductLookups> LoadAsync(
        ICatalogStore store, IInventoryQueries inventory, IReadOnlyList<ProductImportRow> rows, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lookups = new ProductLookups();
        var skus = rows.Select(r => r.Sku).OfType<string>().Select(Product.NormalizeSku).Distinct(StringComparer.Ordinal).ToList();
        var codes = rows.Select(r => r.Barcode).OfType<string>().Select(Barcodes.NormalizeForLookup).Distinct(StringComparer.Ordinal).ToList();
        foreach (var product in await store.GetProductsBySkusAsync(skus, cancellationToken))
        {
            lookups.ProductBySku[product.Sku] = product;
            lookups.ProductById[product.Id] = product;
        }

        var barcodes = await store.FindBarcodesAsync(codes, cancellationToken);
        foreach (var product in await store.GetProductsByIdsAsync(barcodes.Select(b => b.ProductId).Where(id => !lookups.ProductById.ContainsKey(id)).Distinct().ToList(), cancellationToken))
        {
            lookups.ProductById[product.Id] = product;
        }

        foreach (var barcode in barcodes)
        {
            lookups.BarcodeOwner[barcode.NormalizedCode] = new BarcodeOwnerInfo(barcode.ProductId);
        }

        var productIds = lookups.ProductById.Keys.ToList();
        foreach (var barcode in await store.GetBarcodesAsync(productIds, cancellationToken))
        {
            lookups.BarcodeOwner.TryAdd(barcode.NormalizedCode, new BarcodeOwnerInfo(barcode.ProductId));
            if (barcode.IsPrimary)
            {
                lookups.PrimaryBarcodeProducts.Add(barcode.ProductId);
            }
        }

        lookups.Categories = [.. await store.GetCategoriesAsync(cancellationToken)];
        lookups.Brands = [.. await store.GetBrandsAsync(cancellationToken)];
        lookups.Units = (await store.GetUnitsAsync(cancellationToken)).ToDictionary(u => u.Code, StringComparer.Ordinal);
        var taxes = await store.GetTaxesAsync(cancellationToken);
        lookups.Taxes = taxes.ToDictionary(t => t.Code, StringComparer.Ordinal);
        lookups.TaxById = taxes.ToDictionary(t => t.Id);
        lookups.ProductTaxes = [.. await store.GetProductTaxesAsync(productIds, cancellationToken)];
        foreach (var productTax in lookups.ProductTaxes.Where(t => lookups.TaxById.TryGetValue(t.TaxId, out var tax) && tax.IsVat))
        {
            lookups.VatByProduct[productTax.ProductId] = productTax.TaxId;
        }

        if ((await store.GetPriceListsAsync(cancellationToken)).SingleOrDefault(l => l.IsDefault) is { } list)
        {
            foreach (var price in (await store.GetPricesAsync(list.Id, productIds, cancellationToken))
                .Where(p => p.PackagingId is null && p.BranchId is null && p.IsValidAt(now)))
            {
                lookups.CurrentPrice[price.ProductId] = price.Price;
            }
        }

        lookups.WithMovements = await inventory.GetProductsWithMovementsAsync(productIds, cancellationToken);
        return lookups;
    }

    public Category? FindCategory(IReadOnlyList<string> path)
    {
        Category? current = null;
        foreach (var name in path)
        {
            current = Categories.FirstOrDefault(c => c.ParentId == current?.Id && CatalogImportService.Same(c.Name, name));
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public Brand? FindBrand(string name) => Brands.FirstOrDefault(b => CatalogImportService.Same(b.Name, name));

    public Guid? DefaultCategoryId() =>
        (Categories.FirstOrDefault(c => c.ParentId is null && c.Name == CatalogInitializer.DefaultCategoryName) ?? Categories.FirstOrDefault())?.Id;
}

// ─────────────────────────────── Casos de uso ───────────────────────────────

/// <summary>Sube un archivo y devuelve la vista previa por fila (no cambia el catálogo).</summary>
public sealed record UploadImportCommand(ImportKind Kind, string FileName, Stream Content) : ICommand<ImportBatchDto>;

internal sealed class UploadImportHandler(
    IInstallationContext installation,
    ITabularFileReader reader,
    CatalogImportService service,
    ICatalogImportRepository repository,
    ISettingsReader settings,
    IActorContext actor,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<UploadImportCommand, ImportBatchDto>
{
    public async Task<Result<ImportBatchDto>> Handle(UploadImportCommand request, CancellationToken cancellationToken)
    {
        var companyId = installation.RequireCompany();
        if (companyId.IsFailure)
        {
            return companyId.Error;
        }

        var maxRows = await settings.GetAsync(CatalogSettings.ImportMaxRows, new SettingContext(companyId.Value), cancellationToken);
        var table = await reader.ReadAsync(request.Content, request.FileName, maxRows, cancellationToken);
        if (table.IsFailure)
        {
            return table.Error;
        }

        var required = request.Kind == ImportKind.Products ? new[] { "nombre" } : ["precio"];
        var missing = required.Where(c => !table.Value.Headers.Contains(c)).ToList();
        if (missing.Count > 0)
        {
            return Error.Validation(CatalogErrors.ImportMissingColumns.Code,
                $"Faltan columnas obligatorias: {string.Join(", ", missing)}. Descargue la plantilla para ver el formato.");
        }

        var batch = new ImportBatchData
        {
            Id = ids.NewId(),
            CompanyId = companyId.Value,
            Kind = request.Kind == ImportKind.Products ? "PRODUCTS" : "PRICES",
            FileName = Path.GetFileName(request.FileName ?? "archivo"),
            CreatedAt = clock.UtcNow,
            CreatedBy = actor.ActorId!.Value,
        };

        if (request.Kind == ImportKind.Products)
        {
            var rows = table.Value.Rows.Select(r => (r.Number, CatalogImportService.ReadProduct(r))).ToList();
            foreach (var row in await service.AnalyzeProductsAsync(companyId.Value, rows, cancellationToken))
            {
                batch.Rows.Add(ToData(row));
            }
        }
        else
        {
            var rows = table.Value.Rows.Select(r => (r.Number, CatalogImportService.ReadPrice(r))).ToList();
            foreach (var (row, _) in await service.AnalyzePricesAsync(companyId.Value, rows, cancellationToken))
            {
                batch.Rows.Add(ToData(row));
            }
        }

        repository.Add(batch);
        return ImportMapping.ToDto(batch);
    }

    private static ImportRowData ToData<T>(AnalyzedRow<T> row) =>
        new(row.RowNumber, row.Action, row.Key, JsonSerializer.SerializeToElement(row.Row, ImportMapping.Json), row.Errors, row.Warnings);
}

/// <summary>Aplica una importación sin errores, en una transacción, volviendo a validar contra el estado actual.</summary>
public sealed record ApplyImportCommand(Guid ImportId) : ICommand<ImportBatchDto>;

internal sealed class ApplyImportHandler(
    IInstallationContext installation,
    CatalogImportService service,
    ICatalogImportRepository repository,
    IAuditWriter audit,
    IActorContext actor,
    IClock clock) : ICommandHandler<ApplyImportCommand, ImportBatchDto>
{
    public async Task<Result<ImportBatchDto>> Handle(ApplyImportCommand request, CancellationToken cancellationToken)
    {
        var companyId = installation.RequireCompany();
        if (companyId.IsFailure)
        {
            return companyId.Error;
        }

        var batch = await repository.GetAsync(request.ImportId, cancellationToken);
        if (batch is null)
        {
            return CatalogErrors.ImportNotFound;
        }

        if (batch.Status != "PREVIEW")
        {
            return CatalogErrors.ImportAlreadyApplied;
        }

        if (batch.Rows.Any(r => r.Action == "ERROR"))
        {
            return CatalogErrors.ImportHasErrors;
        }

        List<string> errors;
        if (batch.Kind == "PRODUCTS")
        {
            var rows = batch.Rows.Select(r => (r.RowNumber, r.Data.Deserialize<ProductImportRow>(ImportMapping.Json)!)).ToList();
            var analyzed = await service.AnalyzeProductsAsync(companyId.Value, rows, cancellationToken);
            errors = [.. analyzed.Where(r => r.Errors.Count > 0).Select(r => $"Fila {r.RowNumber}: {string.Join(" ", r.Errors)}")];
            if (errors.Count == 0)
            {
                await service.ApplyProductsAsync(companyId.Value, analyzed, cancellationToken);
            }
        }
        else
        {
            var rows = batch.Rows.Select(r => (r.RowNumber, r.Data.Deserialize<PriceImportRow>(ImportMapping.Json)!)).ToList();
            var analyzed = await service.AnalyzePricesAsync(companyId.Value, rows, cancellationToken);
            errors = [.. analyzed.Where(r => r.Row.Errors.Count > 0).Select(r => $"Fila {r.Row.RowNumber}: {string.Join(" ", r.Row.Errors)}")];
            if (errors.Count == 0)
            {
                await service.ApplyPricesAsync([.. analyzed.Where(r => r.Row.Action == "CREATE").Select(r => r.Plan!)], cancellationToken);
            }
        }

        if (errors.Count > 0)
        {
            return Error.BusinessRule(CatalogErrors.ImportHasErrors.Code,
                "Los datos cambiaron desde la vista previa y el archivo ya no es válido: " + string.Join(" ", errors.Take(5)));
        }

        batch.Status = "APPLIED";
        batch.AppliedAt = clock.UtcNow;
        batch.AppliedBy = actor.ActorId;
        await repository.UpdateStatusAsync(batch, cancellationToken);
        var summary = ImportMapping.ToDto(batch);
        await audit.WriteAsync(
            new AuditEntry("catalog", "CATALOG_IMPORT_APPLIED", "ImportBatch", batch.Id, batch.FileName,
                $"Importación de {(batch.Kind == "PRODUCTS" ? "productos" : "precios")} '{batch.FileName}': {summary.CreateRows} nuevos, " +
                $"{summary.UpdateRows} actualizados, {summary.UnchangedRows} sin cambios.",
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return summary;
    }
}

public sealed record DiscardImportCommand(Guid ImportId) : ICommand;

internal sealed class DiscardImportHandler(ICatalogImportRepository repository) : ICommandHandler<DiscardImportCommand>
{
    public async Task<Result> Handle(DiscardImportCommand request, CancellationToken cancellationToken)
    {
        var batch = await repository.GetAsync(request.ImportId, cancellationToken);
        if (batch is null)
        {
            return CatalogErrors.ImportNotFound;
        }

        if (batch.Status != "PREVIEW")
        {
            return CatalogErrors.ImportAlreadyApplied;
        }

        batch.Status = "DISCARDED";
        await repository.UpdateStatusAsync(batch, cancellationToken);
        return Result.Success();
    }
}

public sealed record GetImportQuery(Guid ImportId) : IQuery<ImportBatchDto>;

internal sealed class GetImportHandler(ICatalogImportRepository repository) : IQueryHandler<GetImportQuery, ImportBatchDto>
{
    public async Task<Result<ImportBatchDto>> Handle(GetImportQuery request, CancellationToken cancellationToken) =>
        await repository.GetAsync(request.ImportId, cancellationToken) is { } batch ? ImportMapping.ToDto(batch) : CatalogErrors.ImportNotFound;
}

internal static class ImportMapping
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    public static ImportBatchDto ToDto(ImportBatchData batch) => new(
        batch.Id, batch.Kind, batch.FileName, batch.Status, batch.Rows.Count,
        batch.Rows.Count(r => r.Action == "CREATE"), batch.Rows.Count(r => r.Action == "UPDATE"), batch.Rows.Count(r => r.Action == "UNCHANGED"),
        batch.Rows.Count(r => r.Action == "ERROR"), batch.CreatedAt, batch.AppliedAt,
        [.. batch.Rows.OrderBy(r => r.RowNumber).Select(r => new ImportRowDto(r.RowNumber, r.Action, r.Key, r.Errors, r.Warnings))]);
}
