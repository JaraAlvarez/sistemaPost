using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Catalog.Contracts;

/// <summary>Permisos del módulo Catalog (docs/fases/fase-04-propuesta.md §7).</summary>
public static class CatalogPermissions
{
    public const string ProductView = "catalog.product.view";
    public const string ProductManage = "catalog.product.manage";
    public const string PriceManage = "catalog.price.manage";
    public const string MasterManage = "catalog.master.manage";
    public const string TaxManage = "catalog.tax.manage";
    public const string ImportRun = "catalog.import.run";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(ProductView, "Consultar productos, códigos y precios vigentes", isSensitive: false),
        new(ProductManage, "Crear y modificar productos, presentaciones, códigos e impuestos del producto", isSensitive: false),
        new(PriceManage, "Fijar, programar y cancelar precios de venta", isSensitive: true),
        new(MasterManage, "Categorías, marcas, listas de precios y reglas de báscula", isSensitive: false),
        new(TaxManage, "Crear impuestos y cambiar sus tarifas", isSensitive: true),
        new(ImportRun, "Importar productos y precios desde archivos", isSensitive: true),
    ];
}

/// <summary>Producto visto por otros módulos (inventario, compras, ventas).</summary>
public sealed record CatalogProductInfo(
    Guid Id,
    string Sku,
    string Name,
    string BaseUnitCode,
    bool IsStockable,
    bool AllowsDecimalQuantity,
    bool TracksLots,
    string Status,
    Guid CategoryId,
    bool TracksExpiry = false);

/// <summary>Presentación de un producto (factor = unidades base por presentación).</summary>
public sealed record CatalogPackagingInfo(Guid Id, Guid ProductId, string Name, decimal Factor, bool IsPurchasable);

/// <summary>Impuesto de un producto con su tarifa vigente en una fecha (porcentaje o valor fijo por unidad base).</summary>
public sealed record CatalogTaxInfo(Guid TaxId, string Code, string Kind, bool IsVat, decimal? Rate, decimal? FixedAmount);

/// <summary>Consultas del catálogo para otros módulos.</summary>
public interface ICatalogReader
{
    /// <summary>Productos por Id (incluye inactivos y descontinuados; excluye borrados).</summary>
    Task<IReadOnlyDictionary<Guid, CatalogProductInfo>> GetProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default);

    /// <summary>Producto por SKU o por código de barras (normalizado).</summary>
    Task<CatalogProductInfo?> FindByCodeAsync(string skuOrBarcode, CancellationToken cancellationToken = default);

    /// <summary>Productos inventariables activos o inactivos (no descontinuados) de las categorías indicadas y sus subcategorías; todas si es null.</summary>
    Task<IReadOnlyList<Guid>> ListStockableProductIdsAsync(IReadOnlyCollection<Guid>? categoryIds, CancellationToken cancellationToken = default);

    /// <summary>Presentaciones vigentes (no borradas) de los productos.</summary>
    Task<IReadOnlyList<CatalogPackagingInfo>> GetPackagingsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default);

    /// <summary>Impuestos activos de cada producto con la tarifa vigente en la fecha (los que no tienen tarifa no aparecen).</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<CatalogTaxInfo>>> GetTaxesAsync(
        IReadOnlyCollection<Guid> productIds, DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>
    /// Precio de venta vigente por unidad base en la lista por defecto (el de la sucursal si existe), SIN los impuestos
    /// porcentuales cuando la lista los incluye. Para comparar contra el costo (alerta de precio bajo el costo).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetNetSalePricesAsync(
        IReadOnlyCollection<Guid> productIds, Guid branchId, DateTimeOffset at, CancellationToken cancellationToken = default);
}

public sealed record UnitDto(string Code, string Name, string Dimension, string DianCode, int DecimalsAllowed);

public sealed record CategoryDto(Guid Id, Guid? ParentId, string Name, int Level, string Path, int SortOrder, string Status);

public sealed record BrandDto(Guid Id, string Name, string Status);

public sealed record TaxRateDto(Guid Id, decimal? Rate, decimal? FixedAmount, DateOnly ValidFrom, DateOnly? ValidTo);

public sealed record TaxDto(
    Guid Id,
    string Code,
    string Name,
    string Kind,
    string Calculation,
    bool IsExempt,
    bool IsExcluded,
    string? DianCode,
    bool IsSystem,
    string Status,
    TaxRateDto? CurrentRate,
    IReadOnlyList<TaxRateDto> Rates);

public sealed record PriceListDto(Guid Id, string Code, string Name, bool IsDefault, bool PricesIncludeTax, string Status);

public sealed record BarcodeRuleDto(
    Guid Id, string Prefix, string Content, int PluStart, int PluLength, int ValueStart, int ValueLength, int ValueDecimals, string Status);

public sealed record PackagingDto(Guid Id, string Name, decimal Factor, bool IsSellable, bool IsPurchasable);

public sealed record BarcodeDto(Guid Id, string Code, string NormalizedCode, string CodeType, Guid? PackagingId, bool IsPrimary);

public sealed record ProductTaxDto(Guid Id, Guid TaxId, string Code, string Name, string Kind, string Calculation, decimal? FixedAmount);

/// <summary>Precio con su estado respecto a hoy: CURRENT (vigente), SCHEDULED (programado) o EXPIRED.</summary>
public sealed record PriceDto(
    Guid Id,
    Guid PriceListId,
    string PriceListCode,
    Guid? PackagingId,
    Guid? BranchId,
    decimal Price,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    string State);

public sealed record SetPriceResultDto(PriceDto Price, IReadOnlyList<string> Warnings);

public sealed record ProductSummaryDto(
    Guid Id,
    string Sku,
    string Name,
    string ShortName,
    Guid CategoryId,
    string CategoryName,
    string? BrandName,
    string BaseUnitCode,
    string SaleMode,
    string ProductType,
    string Status,
    string? PrimaryBarcode,
    decimal? Price);

public sealed record ProductDetailDto(
    Guid Id,
    string Sku,
    string Name,
    string ShortName,
    string? Description,
    Guid CategoryId,
    Guid? BrandId,
    string BaseUnitCode,
    string SaleMode,
    string ProductType,
    bool IsSoldByScale,
    bool AllowsDecimalQuantity,
    bool AllowsOpenPrice,
    bool TracksLots,
    bool TracksExpiry,
    string? PluCode,
    decimal? NetContent,
    string? NetContentUnit,
    string Status,
    IReadOnlyList<PackagingDto> Packagings,
    IReadOnlyList<BarcodeDto> Barcodes,
    IReadOnlyList<ProductTaxDto> Taxes,
    IReadOnlyList<PriceDto> Prices);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record TaxLineDto(string Code, string Kind, decimal? Rate, decimal? FixedAmount);

/// <summary>
/// Resultado de leer un código en la caja. <c>Source</c>: BARCODE, SKU, SCALE_WEIGHT o SCALE_PRICE. <c>Quantity</c> en
/// unidades de la presentación (o kilos en báscula); <c>UnitPrice</c> por presentación (o por kilo); <c>Amount</c> =
/// importe de la etiqueta o cantidad × precio.
/// </summary>
public sealed record ScanResultDto(
    Guid ProductId,
    string Sku,
    string Name,
    string ShortName,
    Guid? PackagingId,
    string? PackagingName,
    decimal PackagingFactor,
    string BaseUnitCode,
    string SaleMode,
    string Source,
    decimal Quantity,
    decimal? UnitPrice,
    decimal? Amount,
    bool PriceIncludesTax,
    IReadOnlyList<TaxLineDto> Taxes,
    bool IsSellable,
    IReadOnlyList<string> NotSellableReasons);

public sealed record ImportRowDto(int RowNumber, string Action, string Key, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public sealed record ImportBatchDto(
    Guid Id,
    string Kind,
    string FileName,
    string Status,
    int TotalRows,
    int CreateRows,
    int UpdateRows,
    int UnchangedRows,
    int ErrorRows,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AppliedAt,
    IReadOnlyList<ImportRowDto> Rows);

/// <summary>
/// Producto listo para vender (Fase 7). <c>Source</c>: BARCODE, SKU, SCALE_WEIGHT, SCALE_PRICE o PRODUCT (elegido por búsqueda).
/// <c>Quantity</c> en la unidad de venta (presentación o kilos); <c>UnitPrice</c> por unidad de venta (null = sin precio);
/// <c>Taxes</c> con la tarifa vigente hoy.
/// </summary>
public sealed record CatalogSaleItem(
    Guid ProductId,
    string Sku,
    string Name,
    string ShortName,
    Guid? PackagingId,
    string? PackagingName,
    decimal Factor,
    string BaseUnitCode,
    string SaleMode,
    string Source,
    decimal Quantity,
    decimal? UnitPrice,
    decimal? Amount,
    bool PriceIncludesTax,
    IReadOnlyList<CatalogTaxInfo> Taxes,
    Guid CategoryId,
    Guid? BrandId,
    bool IsStockable,
    bool AllowsDecimalQuantity,
    bool AllowsOpenPrice,
    bool IsSellable,
    IReadOnlyList<string> NotSellableReasons);

/// <summary>El catálogo visto por la caja y por las promociones.</summary>
public interface ICatalogSaleItems
{
    /// <summary>Código leído en la caja (barras, báscula o SKU); null si no existe.</summary>
    Task<CatalogSaleItem?> FindByCodeAsync(string code, Guid? branchId, CancellationToken cancellationToken = default);

    /// <summary>Producto elegido por búsqueda (con presentación opcional); null si no existe.</summary>
    Task<CatalogSaleItem?> GetAsync(Guid productId, Guid? packagingId, Guid? branchId, CancellationToken cancellationToken = default);

    /// <summary>Cada categoría con todas sus subcategorías (incluida ella misma).</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetCategorySubtreesAsync(IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken = default);

    /// <summary>Nombres de categorías y marcas por Id.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetMasterNamesAsync(
        IReadOnlyCollection<Guid> categoryIds, IReadOnlyCollection<Guid> brandIds, CancellationToken cancellationToken = default);
}
