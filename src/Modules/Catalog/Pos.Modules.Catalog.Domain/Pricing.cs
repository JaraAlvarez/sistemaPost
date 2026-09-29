using System.Text.RegularExpressions;
using Pos.SharedKernel;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Catalog.Domain;

/// <summary>Lista de precios. Una por empresa es la predeterminada; por defecto los precios incluyen impuestos (D9).</summary>
[Audited("catalog")]
public sealed partial class PriceList : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    public const string GeneralCode = "GENERAL";

    private PriceList(Guid id, Guid companyId, string code, string name)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        Name = name;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public bool IsDefault { get; private set; }

    public bool PricesIncludeTax { get; private set; } = true;

    public MasterStatus Status { get; private set; } = MasterStatus.Active;

    public string AuditLabel => $"Lista de precios {Code} · {Name}";

    public static Result<PriceList> Create(Guid id, Guid companyId, string code, string name, bool pricesIncludeTax, bool isDefault)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(normalized))
        {
            return Error.Validation("CATALOG.INVALID_PRICE_LIST_CODE", "El código de la lista tiene de 2 a 20 mayúsculas, dígitos o _.");
        }

        var list = new PriceList(id, companyId, normalized, string.Empty) { IsDefault = isDefault };
        var updated = list.Update(name, pricesIncludeTax);
        return updated.IsSuccess ? list : updated.Error;
    }

    public Result Update(string name, bool pricesIncludeTax)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 80)
        {
            return CatalogErrors.InvalidName;
        }

        Name = trimmed;
        PricesIncludeTax = pricesIncludeTax;
        return Result.Success();
    }

    public void MakeDefault()
    {
        IsDefault = true;
        Status = MasterStatus.Active;
    }

    public void UnsetDefault() => IsDefault = false;

    public Result Deactivate()
    {
        if (IsDefault)
        {
            return CatalogErrors.DefaultPriceListRequired;
        }

        Status = MasterStatus.Inactive;
        return Result.Success();
    }

    public void Activate() => Status = MasterStatus.Active;

    [GeneratedRegex("^[A-Z0-9_]{2,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

/// <summary>
/// Precio con vigencia <c>[desde, hasta)</c> (RN-CAT-05): nunca se sobrescribe, se cierra y se crea otro. Sin
/// presentación = precio de la unidad base (por kilo en productos de peso); sin sucursal = todas las sucursales.
/// </summary>
[Audited("catalog")]
public sealed class ProductPrice : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    public const int PriceDecimals = 2;

    private ProductPrice(Guid id, Guid companyId, Guid priceListId, Guid productId, DateTimeOffset validFrom)
        : base(id)
    {
        CompanyId = companyId;
        PriceListId = priceListId;
        ProductId = productId;
        ValidFrom = validFrom;
    }

    public Guid CompanyId { get; private set; }

    public Guid PriceListId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid? PackagingId { get; private set; }

    public Guid? BranchId { get; private set; }

    public decimal Price { get; private set; }

    public DateTimeOffset ValidFrom { get; private set; }

    public DateTimeOffset? ValidTo { get; private set; }

    public string AuditLabel => $"Precio del producto {ProductId:D}";

    public static Result<ProductPrice> Create(
        Guid id, PriceList list, Product product, ProductPackaging? packaging, Guid? branchId, decimal price, DateTimeOffset validFrom,
        DateTimeOffset? validTo)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(product);
        if (!IsValidPrice(price))
        {
            return CatalogErrors.InvalidPrice;
        }

        if (packaging is not null && packaging.ProductId != product.Id)
        {
            return CatalogErrors.PackagingNotFound;
        }

        return new ProductPrice(id, product.CompanyId, list.Id, product.Id, validFrom)
        {
            PackagingId = packaging?.Id,
            BranchId = branchId,
            Price = price,
            ValidTo = validTo,
        };
    }

    public static bool IsValidPrice(decimal price) => price >= 0m && Guard.HasAtMostDecimals(price, PriceDecimals);

    public bool IsValidAt(DateTimeOffset instant) => ValidFrom <= instant && (ValidTo is null || instant < ValidTo);

    public bool IsScheduled(DateTimeOffset now) => ValidFrom > now;

    public void CloseAt(DateTimeOffset? validTo) => ValidTo = validTo;

    /// <summary>Solo para un precio programado (aún no vigente) que se reemplaza en la misma fecha.</summary>
    public void Reprice(decimal price) => Price = price;
}
