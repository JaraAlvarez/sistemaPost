using System.Text.RegularExpressions;
using Pos.SharedKernel;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Finance;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Text;

namespace Pos.Modules.Catalog.Domain;

public enum SaleMode
{
    Unit,
    Weight,
    Volume,
}

public enum ProductType
{
    /// <summary>Maneja inventario (kardex).</summary>
    Stockable,

    /// <summary>No afecta inventario (RN-INV-10).</summary>
    Service,
}

public enum ProductStatus
{
    Active,
    Inactive,
    Discontinued,
}

/// <summary>Datos editables de un producto. <c>BaseUnitDimension</c>: UNIT, WEIGHT o VOLUME (de la tabla de unidades).</summary>
public sealed record ProductData(
    string Sku,
    string Name,
    string? ShortName,
    string? Description,
    Guid CategoryId,
    Guid? BrandId,
    string BaseUnitCode,
    string BaseUnitDimension,
    SaleMode SaleMode,
    ProductType ProductType,
    bool IsSoldByScale,
    bool AllowsDecimalQuantity,
    bool AllowsOpenPrice,
    bool TracksLots,
    bool TracksExpiry,
    string? PluCode,
    decimal? NetContent,
    string? NetContentUnit);

/// <summary>
/// Producto (doc 04 §H.5). El SKU y el PLU son únicos en la empresa; la unidad base y el tipo quedan fijos cuando el
/// producto tiene movimientos de inventario (RN-CAT-08). <see cref="SearchText"/> alimenta la búsqueda sin tildes.
/// </summary>
[Audited("catalog")]
public sealed partial class Product : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    public const string ScaleUnit = "KG";

    public const int MaxNameLength = 200;

    public const int MaxShortNameLength = 40;

    private Product(Guid id, Guid companyId, string sku, string name)
        : base(id)
    {
        CompanyId = companyId;
        Sku = sku;
        Name = name;
    }

    public Guid CompanyId { get; private set; }

    public string Sku { get; private set; }

    public string Name { get; private set; }

    public string ShortName { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public Guid CategoryId { get; private set; }

    public Guid? BrandId { get; private set; }

    public string BaseUnitCode { get; private set; } = string.Empty;

    public SaleMode SaleMode { get; private set; }

    public ProductType ProductType { get; private set; }

    public bool IsSoldByScale { get; private set; }

    public bool AllowsDecimalQuantity { get; private set; }

    public bool AllowsOpenPrice { get; private set; }

    public bool TracksLots { get; private set; }

    public bool TracksExpiry { get; private set; }

    /// <summary>PLU de báscula sin ceros a la izquierda ("00123" se guarda como "123").</summary>
    public string? PluCode { get; private set; }

    public decimal? NetContent { get; private set; }

    public string? NetContentUnit { get; private set; }

    [NotAudited]
    public string SearchText { get; private set; } = string.Empty;

    public ProductStatus Status { get; private set; } = ProductStatus.Active;

    public bool IsStockable => ProductType == ProductType.Stockable;

    public string AuditLabel => $"Producto {Sku} · {Name}";

    public static Result<Product> Create(Guid id, Guid companyId, ProductData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var product = new Product(id, companyId, string.Empty, string.Empty);
        var applied = product.Apply(data);
        return applied.IsSuccess ? product : applied.Error;
    }

    /// <summary>Modifica el producto. Con movimientos de inventario, la unidad base y el tipo no cambian (RN-CAT-08).</summary>
    public Result Update(ProductData data, bool hasInventoryMovements)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (hasInventoryMovements && (!string.Equals(data.BaseUnitCode, BaseUnitCode, StringComparison.OrdinalIgnoreCase) || data.ProductType != ProductType))
        {
            return CatalogErrors.BaseUnitLocked;
        }

        return Apply(data);
    }

    /// <summary>
    /// Cambia el estado. Descontinuar un producto con existencias exige ajustarlas antes (RN-CAT-07), salvo que la empresa
    /// lo permita.
    /// </summary>
    public Result ChangeStatus(ProductStatus status, decimal onHand, bool blockDiscontinueWithStock)
    {
        if (status == ProductStatus.Discontinued && Status != ProductStatus.Discontinued && onHand != 0m && blockDiscontinueWithStock)
        {
            return CatalogErrors.ProductHasStock;
        }

        Status = status;
        return Result.Success();
    }

    public static string NormalizeSku(string? sku) => (sku ?? string.Empty).Trim().ToUpperInvariant();

    public static string? NormalizePlu(string? plu)
    {
        var trimmed = plu?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var withoutZeros = trimmed.TrimStart('0');
        return withoutZeros.Length == 0 ? "0" : withoutZeros;
    }

    private Result Apply(ProductData data)
    {
        var sku = NormalizeSku(data.Sku);
        if (!SkuPattern().IsMatch(sku))
        {
            return CatalogErrors.InvalidSku;
        }

        var name = (data.Name ?? string.Empty).Trim();
        var shortName = string.IsNullOrWhiteSpace(data.ShortName) ? Truncate(name, MaxShortNameLength) : data.ShortName.Trim();
        if (name.Length is 0 or > MaxNameLength || shortName.Length > MaxShortNameLength || (data.Description?.Length ?? 0) > 1000)
        {
            return CatalogErrors.InvalidName;
        }

        var expectedDimension = data.SaleMode switch
        {
            SaleMode.Weight => "WEIGHT",
            SaleMode.Volume => "VOLUME",
            _ => "UNIT",
        };
        if (!string.Equals(data.BaseUnitDimension, expectedDimension, StringComparison.Ordinal))
        {
            return CatalogErrors.UnitDoesNotMatchSaleMode;
        }

        var baseUnit = (data.BaseUnitCode ?? string.Empty).Trim().ToUpperInvariant();
        var plu = NormalizePlu(data.PluCode);
        if (data.PluCode is not null && (plu is null || plu.Length > 6 || !data.PluCode.Trim().All(char.IsAsciiDigit)))
        {
            return CatalogErrors.InvalidPlu;
        }

        if (data.IsSoldByScale && (data.SaleMode != SaleMode.Weight || baseUnit != ScaleUnit || plu is null))
        {
            return CatalogErrors.ScaleRequiresWeight;
        }

        if (data.ProductType == ProductType.Service && (data.TracksLots || data.TracksExpiry))
        {
            return CatalogErrors.ServiceCannotTrackLots;
        }

        if (data.TracksExpiry && !data.TracksLots)
        {
            return CatalogErrors.ExpiryRequiresLots;
        }

        if ((data.NetContent is null) != string.IsNullOrWhiteSpace(data.NetContentUnit) || data.NetContent is <= 0
            || (data.NetContent is { } content && !Guard.HasAtMostDecimals(content, RoundingPolicy.QuantityDecimals)))
        {
            return CatalogErrors.InvalidNetContent;
        }

        Sku = sku;
        Name = name;
        ShortName = shortName;
        Description = string.IsNullOrWhiteSpace(data.Description) ? null : data.Description.Trim();
        CategoryId = data.CategoryId;
        BrandId = data.BrandId;
        BaseUnitCode = baseUnit;
        SaleMode = data.SaleMode;
        ProductType = data.ProductType;
        IsSoldByScale = data.IsSoldByScale;
        AllowsDecimalQuantity = data.SaleMode != SaleMode.Unit || data.AllowsDecimalQuantity;
        AllowsOpenPrice = data.AllowsOpenPrice;
        TracksLots = data.TracksLots;
        TracksExpiry = data.TracksExpiry;
        PluCode = plu;
        NetContent = data.NetContent;
        NetContentUnit = string.IsNullOrWhiteSpace(data.NetContentUnit) ? null : data.NetContentUnit.Trim().ToUpperInvariant();
        SearchText = Truncate(TextNormalization.ForSearch($"{name} {shortName} {sku}"), 400);
        return Result.Success();
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._/-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex SkuPattern();
}

/// <summary>Presentación (paquete, caja): <see cref="Factor"/> unidades base por presentación (RN-CAT-04).</summary>
[Audited("catalog")]
public sealed class ProductPackaging : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private ProductPackaging(Guid id, Guid companyId, Guid productId, string name)
        : base(id)
    {
        CompanyId = companyId;
        ProductId = productId;
        Name = name;
    }

    public Guid CompanyId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Name { get; private set; }

    public decimal Factor { get; private set; }

    public bool IsSellable { get; private set; }

    public bool IsPurchasable { get; private set; }

    public string AuditLabel => $"Presentación {Name} (x{Factor:0.####})";

    public static Result<ProductPackaging> Create(Guid id, Product product, string name, decimal factor, bool isSellable, bool isPurchasable)
    {
        ArgumentNullException.ThrowIfNull(product);
        var packaging = new ProductPackaging(id, product.CompanyId, product.Id, string.Empty);
        var result = packaging.Update(name, factor, isSellable, isPurchasable);
        return result.IsSuccess ? packaging : result.Error;
    }

    public Result Update(string name, decimal factor, bool isSellable, bool isPurchasable)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 60)
        {
            return CatalogErrors.InvalidName;
        }

        if (factor <= 0m || !Guard.HasAtMostDecimals(factor, RoundingPolicy.QuantityDecimals))
        {
            return CatalogErrors.InvalidFactor;
        }

        Name = trimmed;
        Factor = factor;
        IsSellable = isSellable;
        IsPurchasable = isPurchasable;
        return Result.Success();
    }
}

/// <summary>Código de barras de un producto o de una de sus presentaciones. Uno por producto puede ser el principal.</summary>
[Audited("catalog")]
public sealed class ProductBarcode : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private ProductBarcode(Guid id, Guid companyId, Guid productId, string code, string normalizedCode)
        : base(id)
    {
        CompanyId = companyId;
        ProductId = productId;
        Code = code;
        NormalizedCode = normalizedCode;
    }

    public Guid CompanyId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid? PackagingId { get; private set; }

    public string Code { get; private set; }

    public string NormalizedCode { get; private set; }

    public BarcodeType CodeType { get; private set; }

    public bool IsPrimary { get; private set; }

    public string AuditLabel => $"Código {Code}";

    public static ProductBarcode Create(Guid id, Product product, ProductPackaging? packaging, NormalizedBarcode barcode, bool isPrimary)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(barcode);
        if (packaging is not null && packaging.ProductId != product.Id)
        {
            throw new DomainException("La presentación pertenece a otro producto.");
        }

        return new ProductBarcode(id, product.CompanyId, product.Id, barcode.Code, barcode.NormalizedCode)
        {
            PackagingId = packaging?.Id,
            CodeType = barcode.Type,
            IsPrimary = isPrimary,
        };
    }

    public void SetPrimary(bool isPrimary) => IsPrimary = isPrimary;
}

/// <summary>Impuesto asignado a un producto. <see cref="FixedAmount"/>: valor propio por unidad (impuestos de valor fijo).</summary>
[Audited("catalog")]
public sealed class ProductTax : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private ProductTax(Guid id, Guid companyId, Guid productId, Guid taxId)
        : base(id)
    {
        CompanyId = companyId;
        ProductId = productId;
        TaxId = taxId;
    }

    public Guid CompanyId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid TaxId { get; private set; }

    public decimal? FixedAmount { get; private set; }

    public string AuditLabel => $"Impuesto del producto {ProductId:D}";

    public static Result<ProductTax> Create(Guid id, Product product, Tax tax, decimal? fixedAmount)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(tax);
        if (fixedAmount is not null && (tax.Calculation != TaxCalculation.FixedPerUnit || fixedAmount < 0))
        {
            return CatalogErrors.FixedAmountOnlyForFixedTaxes;
        }

        return new ProductTax(id, product.CompanyId, product.Id, tax.Id) { FixedAmount = fixedAmount };
    }

    public void ChangeFixedAmount(decimal? fixedAmount) => FixedAmount = fixedAmount;
}
