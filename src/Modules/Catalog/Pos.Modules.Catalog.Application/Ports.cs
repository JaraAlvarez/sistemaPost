using System.Text.Json;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Catalog.Application;

/// <summary>Unidad de medida del catálogo de referencia.</summary>
public sealed record UnitInfo(string Code, string Dimension);

/// <summary>Puerto de persistencia de los agregados del catálogo (EF Core en la capa Infrastructure).</summary>
public interface ICatalogStore
{
    void Add(Category category);

    void Add(Brand brand);

    void Add(Tax tax);

    void Add(TaxRate rate);

    void Add(Product product);

    void Add(ProductPackaging packaging);

    void Add(ProductBarcode barcode);

    void Add(ProductTax productTax);

    void Add(PriceList priceList);

    void Add(ProductPrice price);

    void Add(VariableBarcodeRule rule);

    /// <summary>Borrado lógico (la infraestructura marca deleted_at/deleted_by).</summary>
    void Remove(object entity);

    Task<Category?> GetCategoryAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken);

    Task<bool> CategoryInUseAsync(Guid id, CancellationToken cancellationToken);

    Task<Brand?> GetBrandAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Brand>> GetBrandsAsync(CancellationToken cancellationToken);

    Task<bool> BrandInUseAsync(Guid id, CancellationToken cancellationToken);

    Task<Tax?> GetTaxAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Tax>> GetTaxesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TaxRate>> GetTaxRatesAsync(Guid taxId, CancellationToken cancellationToken);

    Task<PriceList?> GetPriceListAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PriceList>> GetPriceListsAsync(CancellationToken cancellationToken);

    Task<Product?> GetProductAsync(Guid id, CancellationToken cancellationToken);

    Task<Product?> GetProductBySkuAsync(string sku, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductPackaging>> GetPackagingsAsync(Guid productId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductBarcode>> GetBarcodesAsync(Guid productId, CancellationToken cancellationToken);

    Task<ProductBarcode?> FindBarcodeAsync(string normalizedCode, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductBarcode>> GetBarcodesAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductTax>> GetProductTaxesAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>Precios no borrados de una combinación lista × producto × presentación × sucursal.</summary>
    Task<IReadOnlyList<ProductPrice>> GetPricesAsync(Guid priceListId, Guid productId, Guid? packagingId, Guid? branchId, CancellationToken cancellationToken);

    Task<ProductPrice?> GetPriceAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<VariableBarcodeRule>> GetBarcodeRulesAsync(CancellationToken cancellationToken);

    Task<UnitInfo?> GetUnitAsync(string code, CancellationToken cancellationToken);

    Task<bool> SkuExistsAsync(string sku, Guid? exceptProductId, CancellationToken cancellationToken);

    Task<bool> PluExistsAsync(string plu, Guid? exceptProductId, CancellationToken cancellationToken);

    Task<IReadOnlyList<UnitInfo>> GetUnitsAsync(CancellationToken cancellationToken);

    /// <summary>Productos (con seguimiento) por SKU normalizado; para importaciones.</summary>
    Task<IReadOnlyList<Product>> GetProductsBySkusAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken);

    Task<IReadOnlyList<Product>> GetProductsByIdsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductBarcode>> FindBarcodesAsync(IReadOnlyCollection<string> normalizedCodes, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductPackaging>> GetPackagingsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductTax>> GetProductTaxesAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);

    /// <summary>Precios no borrados de una lista para varios productos (importación de precios).</summary>
    Task<IReadOnlyList<ProductPrice>> GetPricesAsync(Guid priceListId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);

    /// <summary>Siguiente consecutivo del nodo para códigos internos (SKU o BARCODE), dentro de la transacción.</summary>
    Task<long> NextCodeSequenceAsync(Guid nodeId, string kind, CancellationToken cancellationToken);
}

/// <summary>Lecturas del catálogo optimizadas (SQL directo), sin seguimiento de cambios.</summary>
public interface ICatalogQueries
{
    Task<IReadOnlyList<UnitDto>> ListUnitsAsync(CancellationToken cancellationToken);

    Task<PagedResult<ProductSummaryDto>> SearchProductsAsync(ProductSearch search, DateTimeOffset now, CancellationToken cancellationToken);

    Task<ProductDetailDto?> GetProductAsync(Guid productId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Producto y presentación de un código normalizado.</summary>
    Task<(Guid ProductId, Guid? PackagingId)?> FindByBarcodeAsync(string normalizedCode, CancellationToken cancellationToken);

    Task<Guid?> FindByPluAsync(string plu, CancellationToken cancellationToken);

    Task<Guid?> FindBySkuAsync(string sku, CancellationToken cancellationToken);

    /// <summary>Precio vigente en la lista por defecto: primero el de la sucursal, si no el general.</summary>
    Task<(decimal Price, bool IncludesTax)?> GetEffectivePriceAsync(
        Guid productId, Guid? packagingId, Guid? branchId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Impuestos del producto con la tarifa vigente en la fecha.</summary>
    Task<IReadOnlyList<(TaxLineDto Line, bool IsVat)>> GetTaxLinesAsync(Guid productId, DateOnly date, CancellationToken cancellationToken);

    Task<IReadOnlyList<PriceDto>> GetPriceHistoryAsync(Guid productId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Filtro de búsqueda de productos (texto sin tildes por palabras, SKU o código exacto).</summary>
public sealed record ProductSearch(string? Text, Guid? CategoryId, Guid? BrandId, ProductStatus? Status, int Page, int PageSize, Guid? BranchId);

/// <summary>Lote de importación (estado local del nodo).</summary>
public sealed class ImportBatchData
{
    public Guid Id { get; init; }

    public Guid CompanyId { get; init; }

    public string Kind { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string Status { get; set; } = "PREVIEW";

    public DateTimeOffset CreatedAt { get; init; }

    public Guid CreatedBy { get; init; }

    public DateTimeOffset? AppliedAt { get; set; }

    public Guid? AppliedBy { get; set; }

    public List<ImportRowData> Rows { get; init; } = [];
}

public sealed record ImportRowData(int RowNumber, string Action, string Key, JsonElement Data, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public interface ICatalogImportRepository
{
    void Add(ImportBatchData batch);

    Task<ImportBatchData?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task UpdateStatusAsync(ImportBatchData batch, CancellationToken cancellationToken);
}

/// <summary>Configuraciones del catálogo. REGLA: el valor por defecto de una clave publicada no se cambia.</summary>
public static class CatalogSettings
{
    public const string Warn = "WARN";
    public const string Block = "BLOCK";

    /// <summary>RN-CAT-06: precio (sin impuestos) por debajo del costo promedio → advertencia o bloqueo.</summary>
    public static readonly SettingDefinition<string> PriceBelowCost = new(
        "catalog.price_below_cost",
        Warn,
        SettingScope.Company,
        "Qué hacer si un precio queda por debajo del costo: WARN (advertir) o BLOCK (bloquear).",
        v => v is Warn or Block ? null : "Valores permitidos: WARN, BLOCK.");

    /// <summary>RN-CAT-07: no descontinuar un producto con existencias.</summary>
    public static readonly SettingDefinition<bool> BlockDiscontinueWithStock = new(
        "catalog.block_discontinue_with_stock",
        true,
        SettingScope.Company,
        "Impedir descontinuar un producto que todavía tiene existencias.");

    /// <summary>Máximo de filas por archivo: la importación se aplica en UNA transacción (límite de 30 s del rol de la aplicación).</summary>
    public static readonly SettingDefinition<int> ImportMaxRows = new(
        "catalog.import_max_rows",
        5000,
        SettingScope.Company,
        "Máximo de filas por archivo de importación (los archivos más grandes se dividen).",
        v => v is >= 100 and <= 5000 ? null : "Debe estar entre 100 y 5000.");

    /// <summary>Impuesto que reciben los productos nuevos si no se indica ninguno.</summary>
    public static readonly SettingDefinition<string> DefaultVatCode = new(
        "catalog.default_vat_code",
        CatalogInitializer.Vat19,
        SettingScope.Company,
        "Código del IVA que se asigna a los productos nuevos cuando no se indica otro.",
        v => v.Length is >= 2 and <= 20 ? null : "Código de 2 a 20 caracteres.");

    public static IEnumerable<SettingDefinition> All => [PriceBelowCost, BlockDiscontinueWithStock, DefaultVatCode, ImportMaxRows];
}

public sealed class CatalogSettingsProvider : ISettingDefinitionProvider
{
    public IEnumerable<SettingDefinition> GetDefinitions() => CatalogSettings.All;
}

public sealed class CatalogPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => CatalogPermissions.All;
}

internal static class CatalogContext
{
    public static readonly Error SetupRequired =
        Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");

    public static Result<Guid> RequireCompany(this IInstallationContext installation) =>
        installation.CompanyId is { } id ? id : SetupRequired;

    public static string Db<TEnum>(this TEnum value)
        where TEnum : struct, Enum
    {
        var name = value.ToString();
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
