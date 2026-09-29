using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Catalog.Application;

/// <summary>
/// Resuelve un código leído en la caja (propuesta de la Fase 4 §5.1): código de barras (UPC con o sin 0), etiqueta de báscula
/// (peso o precio) o SKU; o un producto elegido por búsqueda. Devuelve producto, presentación, cantidad, precio vigente de la
/// sucursal, impuestos vigentes y si se puede vender (RN-CAT-02) con los motivos. Lo usan la consulta de precio y la venta.
/// </summary>
public sealed class CatalogSaleItems(ICatalogStore store, ICatalogQueries queries, ICatalogReader reader, IClock clock) : ICatalogSaleItems
{
    public const int ScaleQuantityDecimals = 3;

    public async Task<CatalogSaleItem?> FindByCodeAsync(string code, Guid? branchId, CancellationToken cancellationToken = default)
    {
        var normalized = Barcodes.NormalizeForLookup(code ?? string.Empty);
        if (normalized.Length == 0)
        {
            return null;
        }

        Guid productId;
        Guid? packagingId = null;
        VariableBarcodeReading? reading = null;
        var source = "BARCODE";
        if (await queries.FindByBarcodeAsync(normalized, cancellationToken) is { } byBarcode)
        {
            (productId, packagingId) = byBarcode;
        }
        else if ((reading = (await store.GetBarcodeRulesAsync(cancellationToken)).Select(r => r.Read(normalized)).FirstOrDefault(r => r is not null)) is not null
            && await queries.FindByPluAsync(reading.Plu, cancellationToken) is { } byPlu)
        {
            productId = byPlu;
            source = reading.Content == VariableBarcodeContent.Weight ? "SCALE_WEIGHT" : "SCALE_PRICE";
        }
        else if (await queries.FindBySkuAsync(Product.NormalizeSku(code), cancellationToken) is { } bySku)
        {
            productId = bySku;
            reading = null;
            source = "SKU";
        }
        else
        {
            return null;
        }

        return await BuildAsync(productId, packagingId, source, reading, branchId, cancellationToken);
    }

    public Task<CatalogSaleItem?> GetAsync(Guid productId, Guid? packagingId, Guid? branchId, CancellationToken cancellationToken = default) =>
        BuildAsync(productId, packagingId, "PRODUCT", null, branchId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetCategorySubtreesAsync(
        IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken = default)
    {
        var categories = await store.GetCategoriesAsync(cancellationToken);
        var children = categories.Where(c => c.ParentId is not null).ToLookup(c => c.ParentId!.Value, c => c.Id);
        var result = new Dictionary<Guid, IReadOnlySet<Guid>>();
        foreach (var root in categoryIds.Distinct())
        {
            var subtree = new HashSet<Guid>();
            var pending = new Stack<Guid>([root]);
            while (pending.Count > 0)
            {
                var id = pending.Pop();
                if (subtree.Add(id))
                {
                    foreach (var child in children[id])
                    {
                        pending.Push(child);
                    }
                }
            }

            result[root] = subtree;
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetMasterNamesAsync(
        IReadOnlyCollection<Guid> categoryIds, IReadOnlyCollection<Guid> brandIds, CancellationToken cancellationToken = default)
    {
        var names = new Dictionary<Guid, string>();
        if (categoryIds.Count > 0)
        {
            foreach (var category in (await store.GetCategoriesAsync(cancellationToken)).Where(c => categoryIds.Contains(c.Id)))
            {
                names[category.Id] = category.Name;
            }
        }

        if (brandIds.Count > 0)
        {
            foreach (var brand in (await store.GetBrandsAsync(cancellationToken)).Where(b => brandIds.Contains(b.Id)))
            {
                names[brand.Id] = brand.Name;
            }
        }

        return names;
    }

    private async Task<CatalogSaleItem?> BuildAsync(
        Guid productId, Guid? packagingId, string source, VariableBarcodeReading? reading, Guid? branchId, CancellationToken cancellationToken)
    {
        var product = await store.GetProductAsync(productId, cancellationToken);
        if (product is null)
        {
            return null;
        }

        var packaging = packagingId is { } pid ? (await store.GetPackagingsAsync(productId, cancellationToken)).SingleOrDefault(p => p.Id == pid) : null;
        if (packagingId is not null && packaging is null)
        {
            return null;
        }

        var now = clock.UtcNow;
        var price = await queries.GetEffectivePriceAsync(productId, packaging?.Id, branchId, now, cancellationToken);
        var taxes = (await reader.GetTaxesAsync([productId], clock.BusinessDateOf(now), cancellationToken)).GetValueOrDefault(productId) ?? [];

        decimal quantity = 1m;
        decimal? amount = price?.Price;
        if (reading is { Content: VariableBarcodeContent.Weight })
        {
            quantity = reading.Value;
            amount = price is { } p ? decimal.Round(quantity * p.Price, ProductPrice.PriceDecimals, MidpointRounding.AwayFromZero) : null;
        }
        else if (reading is { Content: VariableBarcodeContent.Price })
        {
            amount = reading.Value;
            quantity = price is { Price: > 0m } p ? decimal.Round(reading.Value / p.Price, ScaleQuantityDecimals, MidpointRounding.AwayFromZero) : 0m;
        }

        var reasons = new List<string>();
        if (product.Status != ProductStatus.Active)
        {
            reasons.Add(product.Status == ProductStatus.Inactive ? "El producto está inactivo." : "El producto está descontinuado.");
        }

        if (packaging is { IsSellable: false })
        {
            reasons.Add("La presentación no se vende.");
        }

        if (!taxes.Any(t => t.IsVat))
        {
            reasons.Add("El producto no tiene IVA asignado (use IVA excluido o exento si corresponde).");
        }

        if (price is null && !product.AllowsOpenPrice)
        {
            reasons.Add("El producto no tiene precio vigente en la lista predeterminada.");
        }

        return new CatalogSaleItem(
            product.Id, product.Sku, product.Name, product.ShortName, packaging?.Id, packaging?.Name, packaging?.Factor ?? 1m, product.BaseUnitCode,
            product.SaleMode.Db(), source, quantity, price?.Price, amount, price?.IncludesTax ?? true, taxes, product.CategoryId, product.BrandId, product.IsStockable,
            product.AllowsDecimalQuantity, product.AllowsOpenPrice, reasons.Count == 0, reasons);
    }
}

/// <summary>Consulta de precio: lee un código en la caja sin venderlo.</summary>
public sealed record ScanCodeQuery(string Code, Guid? BranchId = null) : IQuery<ScanResultDto>;

internal sealed class ScanCodeHandler(ICatalogSaleItems items, ICurrentUser currentUser, IInstallationContext installation)
    : IQueryHandler<ScanCodeQuery, ScanResultDto>
{
    public async Task<Result<ScanResultDto>> Handle(ScanCodeQuery request, CancellationToken cancellationToken)
    {
        var branchId = request.BranchId ?? currentUser.BranchId ?? installation.BranchId;
        if (await items.FindByCodeAsync(request.Code, branchId, cancellationToken) is not { } item)
        {
            return CatalogErrors.CodeNotFound;
        }

        return new ScanResultDto(
            item.ProductId, item.Sku, item.Name, item.ShortName, item.PackagingId, item.PackagingName, item.Factor, item.BaseUnitCode, item.SaleMode,
            item.Source, item.Quantity, item.UnitPrice, item.Amount, item.PriceIncludesTax,
            [.. item.Taxes.Select(t => new TaxLineDto(t.Code, t.Kind, t.Rate, t.FixedAmount))], item.IsSellable, item.NotSellableReasons);
    }
}
