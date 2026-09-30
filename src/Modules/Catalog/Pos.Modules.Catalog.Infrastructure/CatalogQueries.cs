using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Catalog.Application;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.SharedKernel.Text;

namespace Pos.Modules.Catalog.Infrastructure;

/// <summary>
/// Lecturas del catálogo sobre el contexto de la petición (ven lo guardado dentro de la transacción en curso) y sin
/// seguimiento. La búsqueda usa LIKE sobre <c>search_text</c> (índice de trigramas) por cada palabra.
/// </summary>
internal sealed class CatalogQueries(PosDbContext context) : ICatalogQueries
{
    public async Task<IReadOnlyList<UnitDto>> ListUnitsAsync(CancellationToken cancellationToken) =>
        await context.Database.SqlQuery<UnitDto>($"""
            SELECT code, name, dimension, dian_code, decimals_allowed::int AS decimals_allowed
            FROM ref.units_of_measure ORDER BY sort_order
            """).ToListAsync(cancellationToken);

    public async Task<PagedResult<ProductSummaryDto>> SearchProductsAsync(ProductSearch search, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var products = context.Set<Product>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search.Text))
        {
            var raw = search.Text.Trim().ToUpperInvariant();
            var code = Barcodes.NormalizeForLookup(raw);
            var exact = context.Set<ProductBarcode>().Where(b => b.NormalizedCode == code).Select(b => b.ProductId);
            var tokens = TextNormalization.ForSearch(search.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var byText = products;
            foreach (var token in tokens)
            {
                var pattern = $"%{Escape(token)}%";
                byText = byText.Where(p => EF.Functions.Like(p.SearchText, pattern));
            }

            products = products.Where(p => p.Sku == raw || exact.Contains(p.Id) || byText.Select(t => t.Id).Contains(p.Id));
        }

        if (search.CategoryId is { } categoryId)
        {
            var categories = await SubtreeAsync(context, [categoryId], cancellationToken);
            products = products.Where(p => categories.Contains(p.CategoryId));
        }

        if (search.BrandId is { } brandId)
        {
            products = products.Where(p => p.BrandId == brandId);
        }

        if (search.Status is { } status)
        {
            products = products.Where(p => p.Status == status);
        }

        var total = await products.CountAsync(cancellationToken);
        var page = await products
            .OrderBy(p => EF.Functions.Collate(p.Name, "es_co")).ThenBy(p => p.Sku)
            .Skip((search.Page - 1) * search.PageSize).Take(search.PageSize)
            .Select(p => new
            {
                p.Id, p.Sku, p.Name, p.ShortName, p.CategoryId, p.BrandId, p.BaseUnitCode, p.SaleMode, p.ProductType, p.Status,
                Category = context.Set<Category>().Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefault(),
                Brand = context.Set<Brand>().Where(b => b.Id == p.BrandId).Select(b => b.Name).FirstOrDefault(),
                Barcode = context.Set<ProductBarcode>().Where(b => b.ProductId == p.Id && b.IsPrimary).Select(b => b.Code).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var prices = await EffectivePricesAsync(page.Select(p => p.Id).ToList(), search.BranchId, now, cancellationToken);
        return new PagedResult<ProductSummaryDto>(
            [.. page.Select(p => new ProductSummaryDto(p.Id, p.Sku, p.Name, p.ShortName, p.CategoryId, p.Category ?? string.Empty, p.Brand, p.BaseUnitCode,
                p.SaleMode.Db(), p.ProductType.Db(), p.Status.Db(), p.Barcode, prices.GetValueOrDefault(p.Id)))],
            search.Page, search.PageSize, total);
    }

    public async Task<ProductDetailDto?> GetProductAsync(Guid productId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var p = await context.Set<Product>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == productId, cancellationToken);
        if (p is null)
        {
            return null;
        }

        var packagings = await context.Set<ProductPackaging>().AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Factor)
            .Select(x => new PackagingDto(x.Id, x.Name, x.Factor, x.IsSellable, x.IsPurchasable)).ToListAsync(cancellationToken);
        var barcodes = (await context.Set<ProductBarcode>().AsNoTracking().Where(x => x.ProductId == productId).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.IsPrimary).ThenBy(x => x.Code, StringComparer.Ordinal)
            .Select(x => new BarcodeDto(x.Id, x.Code, x.NormalizedCode, x.CodeType.Db(), x.PackagingId, x.IsPrimary)).ToList();
        var taxes = (await (from pt in context.Set<ProductTax>().AsNoTracking()
                            join t in context.Set<Tax>().AsNoTracking() on pt.TaxId equals t.Id
                            where pt.ProductId == productId
                            select new { pt.Id, pt.TaxId, t.Code, t.Name, t.Kind, t.Calculation, pt.FixedAmount }).ToListAsync(cancellationToken))
            .OrderBy(t => t.Code, StringComparer.Ordinal)
            .Select(t => new ProductTaxDto(t.Id, t.TaxId, t.Code, t.Name, t.Kind.Db(), t.Calculation.Db(), t.FixedAmount)).ToList();
        var prices = (await GetPriceHistoryAsync(productId, now, cancellationToken)).Where(x => x.State != "EXPIRED").ToList();

        return new ProductDetailDto(
            p.Id, p.Sku, p.Name, p.ShortName, p.Description, p.CategoryId, p.BrandId, p.BaseUnitCode, p.SaleMode.Db(), p.ProductType.Db(),
            p.IsSoldByScale, p.AllowsDecimalQuantity, p.AllowsOpenPrice, p.TracksLots, p.TracksExpiry, p.PluCode, p.NetContent, p.NetContentUnit,
            p.Status.Db(), packagings, barcodes, taxes, prices);
    }

    public async Task<(Guid ProductId, Guid? PackagingId)?> FindByBarcodeAsync(string normalizedCode, CancellationToken cancellationToken)
    {
        var hit = await context.Set<ProductBarcode>().AsNoTracking().Where(b => b.NormalizedCode == normalizedCode)
            .Select(b => new { b.ProductId, b.PackagingId }).FirstOrDefaultAsync(cancellationToken);
        return hit is null ? null : (hit.ProductId, hit.PackagingId);
    }

    public async Task<Guid?> FindByPluAsync(string plu, CancellationToken cancellationToken) =>
        await context.Set<Product>().AsNoTracking().Where(p => p.PluCode == plu).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken);

    public async Task<Guid?> FindBySkuAsync(string sku, CancellationToken cancellationToken) =>
        await context.Set<Product>().AsNoTracking().Where(p => p.Sku == sku).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken);

    public async Task<(decimal Price, bool IncludesTax)?> GetEffectivePriceAsync(
        Guid productId, Guid? packagingId, Guid? branchId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var list = await context.Set<PriceList>().AsNoTracking().Where(l => l.IsDefault).Select(l => new { l.Id, l.PricesIncludeTax })
            .FirstOrDefaultAsync(cancellationToken);
        if (list is null)
        {
            return null;
        }

        var price = await context.Set<ProductPrice>().AsNoTracking()
            .Where(p => p.PriceListId == list.Id && p.ProductId == productId && p.PackagingId == packagingId
                && (p.BranchId == null || p.BranchId == branchId) && p.ValidFrom <= at && (p.ValidTo == null || p.ValidTo > at))
            .OrderBy(p => p.BranchId == null ? 1 : 0)
            .Select(p => (decimal?)p.Price)
            .FirstOrDefaultAsync(cancellationToken);
        return price is { } value ? (value, list.PricesIncludeTax) : null;
    }

    public async Task<(decimal Price, bool IncludesTax)?> GetListPriceAsync(
        Guid priceListId, Guid productId, Guid? packagingId, Guid? branchId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var list = await context.Set<PriceList>().AsNoTracking().Where(l => l.Id == priceListId).Select(l => new { l.Id, l.PricesIncludeTax })
            .FirstOrDefaultAsync(cancellationToken);
        if (list is null)
        {
            return null;
        }

        var price = await context.Set<ProductPrice>().AsNoTracking()
            .Where(p => p.PriceListId == list.Id && p.ProductId == productId && p.PackagingId == packagingId
                && (p.BranchId == null || p.BranchId == branchId) && p.ValidFrom <= at && (p.ValidTo == null || p.ValidTo > at))
            .OrderBy(p => p.BranchId == null ? 1 : 0)
            .Select(p => (decimal?)p.Price)
            .FirstOrDefaultAsync(cancellationToken);
        return price is { } value ? (value, list.PricesIncludeTax) : null;
    }

    public async Task<IReadOnlyList<(TaxLineDto Line, bool IsVat)>> GetTaxLinesAsync(Guid productId, DateOnly date, CancellationToken cancellationToken)
    {
        var rows = await (from pt in context.Set<ProductTax>().AsNoTracking()
                          join t in context.Set<Tax>().AsNoTracking() on pt.TaxId equals t.Id
                          where pt.ProductId == productId && t.Status == MasterStatus.Active
                          select new
                          {
                              t.Code,
                              t.Kind,
                              ProductAmount = pt.FixedAmount,
                              Rate = context.Set<TaxRate>().Where(r => r.TaxId == t.Id && r.ValidFrom <= date && (r.ValidTo == null || r.ValidTo > date))
                                  .Select(r => new { r.Rate, r.FixedAmount }).FirstOrDefault(),
                          }).ToListAsync(cancellationToken);
        return [.. rows.OrderBy(r => r.Code, StringComparer.Ordinal)
            .Select(r => (new TaxLineDto(r.Code, r.Kind.Db(), r.Rate?.Rate, r.ProductAmount ?? r.Rate?.FixedAmount), r.Kind == TaxKind.Vat))];
    }

    public async Task<IReadOnlyList<PriceDto>> GetPriceHistoryAsync(Guid productId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await (from p in context.Set<ProductPrice>().AsNoTracking()
                          join l in context.Set<PriceList>().AsNoTracking() on p.PriceListId equals l.Id
                          where p.ProductId == productId
                          select new { Price = p, l.Code }).ToListAsync(cancellationToken);
        return [.. rows.OrderBy(r => r.Code, StringComparer.Ordinal).ThenBy(r => r.Price.PackagingId).ThenBy(r => r.Price.BranchId)
            .ThenByDescending(r => r.Price.ValidFrom).Select(r => r.Price.ToDto(r.Code, now))];
    }

    private async Task<Dictionary<Guid, decimal>> EffectivePricesAsync(List<Guid> productIds, Guid? branchId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var list = await context.Set<PriceList>().AsNoTracking().Where(l => l.IsDefault).Select(l => (Guid?)l.Id).FirstOrDefaultAsync(cancellationToken);
        if (list is null || productIds.Count == 0)
        {
            return [];
        }

        var rows = await context.Set<ProductPrice>().AsNoTracking()
            .Where(p => p.PriceListId == list && productIds.Contains(p.ProductId) && p.PackagingId == null
                && (p.BranchId == null || p.BranchId == branchId) && p.ValidFrom <= now && (p.ValidTo == null || p.ValidTo > now))
            .Select(p => new { p.ProductId, p.BranchId, p.Price })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.BranchId is null ? 1 : 0).First().Price);
    }

    /// <summary>Ids de las categorías indicadas y de todas sus descendientes (la tabla es pequeña: se resuelve en memoria).</summary>
    internal static async Task<List<Guid>> SubtreeAsync(PosDbContext context, IReadOnlyCollection<Guid> roots, CancellationToken cancellationToken)
    {
        var all = await context.Set<Category>().AsNoTracking().Select(c => new { c.Id, c.Path }).ToListAsync(cancellationToken);
        var paths = all.Where(c => roots.Contains(c.Id)).Select(c => c.Path).ToList();
        return [.. all.Where(c => paths.Any(path => c.Path.StartsWith(path, StringComparison.Ordinal))).Select(c => c.Id)];
    }

    private static string Escape(string token) =>
        token.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}

internal static class QueryMapping
{
    public static string Db<TEnum>(this TEnum value)
        where TEnum : struct, Enum => UpperSnake(value.ToString());

    public static PriceDto ToDto(this ProductPrice p, string listCode, DateTimeOffset now) => new(
        p.Id, p.PriceListId, listCode, p.PackagingId, p.BranchId, p.Price, p.ValidFrom, p.ValidTo,
        p.ValidFrom > now ? "SCHEDULED" : p.ValidTo is { } to && to <= now ? "EXPIRED" : "CURRENT");

    private static string UpperSnake(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }
}

/// <summary>Contrato <see cref="ICatalogReader"/> para otros módulos.</summary>
internal sealed class CatalogReader(PosDbContext context) : ICatalogReader
{
    public async Task<IReadOnlyDictionary<Guid, CatalogProductInfo>> GetProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<Guid, CatalogProductInfo>();
        }

        var products = await context.Set<Product>().AsNoTracking().Where(p => productIds.Contains(p.Id)).ToListAsync(cancellationToken);
        return products.ToDictionary(p => p.Id, ToInfo);
    }

    public async Task<CatalogProductInfo?> FindByCodeAsync(string skuOrBarcode, CancellationToken cancellationToken = default)
    {
        var sku = Product.NormalizeSku(skuOrBarcode);
        var code = Barcodes.NormalizeForLookup(skuOrBarcode);
        var product = await context.Set<Product>().AsNoTracking().FirstOrDefaultAsync(p => p.Sku == sku, cancellationToken)
            ?? await context.Set<Product>().AsNoTracking()
                .Where(p => context.Set<ProductBarcode>().Any(b => b.ProductId == p.Id && b.NormalizedCode == code && b.PackagingId == null))
                .FirstOrDefaultAsync(cancellationToken);
        return product is null ? null : ToInfo(product);
    }

    public async Task<IReadOnlyList<Guid>> ListStockableProductIdsAsync(IReadOnlyCollection<Guid>? categoryIds, CancellationToken cancellationToken = default)
    {
        var products = context.Set<Product>().AsNoTracking()
            .Where(p => p.ProductType == ProductType.Stockable && p.Status != ProductStatus.Discontinued);
        if (categoryIds is { Count: > 0 })
        {
            var categories = await CatalogQueries.SubtreeAsync(context, categoryIds, cancellationToken);
            products = products.Where(p => categories.Contains(p.CategoryId));
        }

        return await products.Select(p => p.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CatalogPackagingInfo>> GetPackagingsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default) =>
        productIds.Count == 0
            ? []
            : await context.Set<ProductPackaging>().AsNoTracking().Where(p => productIds.Contains(p.ProductId))
                .Select(p => new CatalogPackagingInfo(p.Id, p.ProductId, p.Name, p.Factor, p.IsPurchasable))
                .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CatalogTaxInfo>>> GetTaxesAsync(
        IReadOnlyCollection<Guid> productIds, DateOnly date, CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<CatalogTaxInfo>>();
        }

        var rows = await (from pt in context.Set<ProductTax>().AsNoTracking()
                          join t in context.Set<Tax>().AsNoTracking() on pt.TaxId equals t.Id
                          where productIds.Contains(pt.ProductId) && t.Status == MasterStatus.Active
                          select new
                          {
                              pt.ProductId,
                              t.Id,
                              t.Code,
                              t.Kind,
                              ProductAmount = pt.FixedAmount,
                              Rate = context.Set<TaxRate>().Where(r => r.TaxId == t.Id && r.ValidFrom <= date && (r.ValidTo == null || r.ValidTo > date))
                                  .Select(r => new { r.Rate, r.FixedAmount }).FirstOrDefault(),
                          }).ToListAsync(cancellationToken);
        return rows.Where(r => r.Rate is not null || r.ProductAmount is not null)
            .GroupBy(r => r.ProductId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CatalogTaxInfo>)[.. g.OrderBy(r => r.Code, StringComparer.Ordinal).Select(r => new CatalogTaxInfo(
                    r.Id, r.Code, r.Kind.Db(), r.Kind == TaxKind.Vat, r.ProductAmount is null ? r.Rate?.Rate : null,
                    r.ProductAmount ?? (r.Rate?.Rate is null ? r.Rate?.FixedAmount : null)))]);
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetNetSalePricesAsync(
        IReadOnlyCollection<Guid> productIds, Guid branchId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var list = await context.Set<PriceList>().AsNoTracking().Where(l => l.IsDefault).Select(l => new { l.Id, l.PricesIncludeTax })
            .FirstOrDefaultAsync(cancellationToken);
        if (list is null || productIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var prices = (await context.Set<ProductPrice>().AsNoTracking()
                .Where(p => p.PriceListId == list.Id && productIds.Contains(p.ProductId) && p.PackagingId == null
                    && (p.BranchId == null || p.BranchId == branchId) && p.ValidFrom <= at && (p.ValidTo == null || p.ValidTo > at))
                .Select(p => new { p.ProductId, p.BranchId, p.Price })
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.ProductId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.BranchId is null ? 1 : 0).First().Price);
        if (!list.PricesIncludeTax)
        {
            return prices;
        }

        var taxes = await GetTaxesAsync([.. prices.Keys], DateOnly.FromDateTime(at.UtcDateTime), cancellationToken);
        return prices.ToDictionary(p => p.Key, p =>
        {
            var rate = taxes.GetValueOrDefault(p.Key)?.Where(t => t.Rate is not null).Sum(t => t.Rate!.Value) ?? 0m;
            var fixedAmount = taxes.GetValueOrDefault(p.Key)?.Where(t => t.FixedAmount is not null).Sum(t => t.FixedAmount!.Value) ?? 0m;
            return decimal.Round((p.Value - fixedAmount) / (1m + (rate / 100m)), 4, MidpointRounding.AwayFromZero);
        });
    }

    private static CatalogProductInfo ToInfo(Product p) =>
        new(p.Id, p.Sku, p.Name, p.BaseUnitCode, p.IsStockable, p.AllowsDecimalQuantity, p.TracksLots, p.Status.Db(), p.CategoryId, p.TracksExpiry);
}
