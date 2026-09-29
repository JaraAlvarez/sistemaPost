using FluentValidation;
using Pos.Application.Abstractions.Data;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Catalog.Application;

/// <summary>Datos de un producto recibidos por la API. SKU vacío = se genera con el prefijo del nodo (D4-07).</summary>
public sealed record ProductInput(
    string? Sku,
    string Name,
    string? ShortName,
    string? Description,
    Guid CategoryId,
    Guid? BrandId,
    string BaseUnitCode,
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

public sealed record ProductTaxInput(Guid TaxId, decimal? FixedAmount);

internal sealed class ProductInputValidator : AbstractValidator<ProductInput>
{
    public ProductInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Product.MaxNameLength);
        RuleFor(x => x.ShortName).MaximumLength(Product.MaxShortNameLength);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Sku).MaximumLength(40);
        RuleFor(x => x.CategoryId).NotEmpty();
        RuleFor(x => x.BaseUnitCode).NotEmpty().MaximumLength(10);
    }
}

/// <summary>
/// Reglas compartidas por la API y la importación: referencias (categoría, marca, unidades), unicidad de SKU y PLU,
/// códigos internos, códigos de barras e impuestos del producto.
/// </summary>
public sealed class ProductWriter(
    IInstallationContext installation,
    ICatalogStore store,
    IWarehouseDirectory directory,
    ISettingsReader settings,
    IUnitOfWork unitOfWork,
    IIdGenerator ids)
{
    public async Task<Result<ProductData>> ResolveAsync(ProductInput input, Guid? existingProductId, string? currentSku, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (await store.GetCategoryAsync(input.CategoryId, cancellationToken) is null)
        {
            return CatalogErrors.CategoryNotFound;
        }

        if (input.BrandId is { } brandId && await store.GetBrandAsync(brandId, cancellationToken) is null)
        {
            return CatalogErrors.BrandNotFound;
        }

        var unit = await store.GetUnitAsync(input.BaseUnitCode.Trim().ToUpperInvariant(), cancellationToken);
        if (unit is null || (input.NetContentUnit is { } contentUnit && await store.GetUnitAsync(contentUnit.Trim().ToUpperInvariant(), cancellationToken) is null))
        {
            return CatalogErrors.UnitNotFound;
        }

        var sku = string.IsNullOrWhiteSpace(input.Sku) ? currentSku ?? await NextSkuAsync(cancellationToken) : Product.NormalizeSku(input.Sku);
        if (await store.SkuExistsAsync(sku, existingProductId, cancellationToken))
        {
            return CatalogErrors.SkuDuplicated;
        }

        if (Product.NormalizePlu(input.PluCode) is { } plu && plu.All(char.IsAsciiDigit) && await store.PluExistsAsync(plu, existingProductId, cancellationToken))
        {
            return CatalogErrors.PluDuplicated;
        }

        return new ProductData(sku, input.Name, input.ShortName, input.Description, input.CategoryId, input.BrandId, unit.Code, unit.Dimension,
            input.SaleMode, input.ProductType, input.IsSoldByScale, input.AllowsDecimalQuantity, input.AllowsOpenPrice, input.TracksLots,
            input.TracksExpiry, input.PluCode, input.NetContent, input.NetContentUnit);
    }

    /// <summary>Agrega un código (escrito o generado) validando que no pertenezca a otro producto o presentación.</summary>
    public async Task<Result<ProductBarcode>> AddBarcodeAsync(
        Product product, ProductPackaging? packaging, string? code, BarcodeType? type, bool generate, bool isPrimary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        Result<NormalizedBarcode> normalized;
        if (generate)
        {
            var sequence = await store.NextCodeSequenceAsync(installation.NodeId, "BARCODE", cancellationToken);
            if (sequence > Barcodes.MaxInternalSequence)
            {
                return CatalogErrors.InternalCodesExhausted;
            }

            normalized = Barcodes.Normalize(Barcodes.Internal(await directory.GetLocalNodeNumberAsync(cancellationToken), sequence), BarcodeType.Internal);
        }
        else
        {
            normalized = Barcodes.Normalize(code, type);
        }

        if (normalized.IsFailure)
        {
            return normalized.Error;
        }

        if (await store.FindBarcodeAsync(normalized.Value.NormalizedCode, cancellationToken) is not null)
        {
            return CatalogErrors.BarcodeDuplicated;
        }

        var existing = await store.GetBarcodesAsync(product.Id, cancellationToken);
        var primary = isPrimary || existing.All(b => !b.IsPrimary) && packaging is null;
        if (primary && existing.Any(b => b.IsPrimary))
        {
            foreach (var other in existing.Where(b => b.IsPrimary))
            {
                other.SetPrimary(false);
            }

            // El índice único parcial exige quitar la marca a la anterior ANTES de insertar la nueva principal.
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var barcode = ProductBarcode.Create(ids.NewId(), product, packaging, normalized.Value, primary);
        store.Add(barcode);
        return barcode;
    }

    /// <summary>Reemplaza los impuestos del producto (máximo un IVA). Sin lista: el IVA por defecto de la empresa.</summary>
    public async Task<Result> SetTaxesAsync(Product product, IReadOnlyList<ProductTaxInput>? requested, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        var taxes = await store.GetTaxesAsync(cancellationToken);
        IReadOnlyList<ProductTaxInput> inputs;
        if (requested is null)
        {
            var code = await settings.GetAsync(CatalogSettings.DefaultVatCode, new SettingContext(product.CompanyId), cancellationToken);
            var fallback = taxes.FirstOrDefault(t => t.Code == code && t.Status == MasterStatus.Active);
            inputs = fallback is null ? [] : [new ProductTaxInput(fallback.Id, null)];
        }
        else
        {
            inputs = requested.DistinctBy(t => t.TaxId).ToList();
        }

        var chosen = new List<(Tax Tax, decimal? FixedAmount)>();
        foreach (var input in inputs)
        {
            var tax = taxes.SingleOrDefault(t => t.Id == input.TaxId);
            if (tax is null)
            {
                return CatalogErrors.TaxNotFound;
            }

            if (tax.Status != MasterStatus.Active)
            {
                return CatalogErrors.TaxInactive;
            }

            if (input.FixedAmount is not null && tax.Calculation != TaxCalculation.FixedPerUnit)
            {
                return CatalogErrors.FixedAmountOnlyForFixedTaxes;
            }

            chosen.Add((tax, input.FixedAmount));
        }

        if (chosen.Count(c => c.Tax.IsVat) > 1)
        {
            return CatalogErrors.OneVatPerProduct;
        }

        var current = await store.GetProductTaxesAsync(product.Id, cancellationToken);
        foreach (var removed in current.Where(c => chosen.All(x => x.Tax.Id != c.TaxId)))
        {
            store.Remove(removed);
        }

        foreach (var (tax, fixedAmount) in chosen)
        {
            var existing = current.SingleOrDefault(c => c.TaxId == tax.Id);
            if (existing is null)
            {
                var created = ProductTax.Create(ids.NewId(), product, tax, fixedAmount);
                if (created.IsFailure)
                {
                    return created.Error;
                }

                store.Add(created.Value);
            }
            else if (existing.FixedAmount != fixedAmount)
            {
                existing.ChangeFixedAmount(fixedAmount);
            }
        }

        return Result.Success();
    }

    private async Task<string> NextSkuAsync(CancellationToken cancellationToken)
    {
        var sequence = await store.NextCodeSequenceAsync(installation.NodeId, "SKU", cancellationToken);
        return Barcodes.InternalSku(await directory.GetLocalNodeNumberAsync(cancellationToken), sequence);
    }
}

// ─────────────────────────────── Consultas ───────────────────────────────

public sealed record SearchProductsQuery(string? Search, Guid? CategoryId, Guid? BrandId, ProductStatus? Status, int Page = 1, int PageSize = 50)
    : IQuery<PagedResult<ProductSummaryDto>>;

internal sealed class SearchProductsHandler(ICatalogQueries queries, IInstallationContext installation, IClock clock)
    : IQueryHandler<SearchProductsQuery, PagedResult<ProductSummaryDto>>
{
    public async Task<Result<PagedResult<ProductSummaryDto>>> Handle(SearchProductsQuery request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        return await queries.SearchProductsAsync(
            new ProductSearch(request.Search, request.CategoryId, request.BrandId, request.Status, page, pageSize, installation.BranchId),
            clock.UtcNow, cancellationToken);
    }
}

public sealed record GetProductQuery(Guid ProductId) : IQuery<ProductDetailDto>;

internal sealed class GetProductHandler(ICatalogQueries queries, IClock clock) : IQueryHandler<GetProductQuery, ProductDetailDto>
{
    public async Task<Result<ProductDetailDto>> Handle(GetProductQuery request, CancellationToken cancellationToken) =>
        await queries.GetProductAsync(request.ProductId, clock.UtcNow, cancellationToken) is { } product ? product : CatalogErrors.ProductNotFound;
}

// ─────────────────────────────── Crear y modificar ───────────────────────────────

/// <summary>
/// Crea un producto con su código de barras (escrito o generado), sus impuestos (por defecto el IVA de la empresa) y,
/// opcionalmente, su precio en la lista por defecto desde ahora.
/// </summary>
public sealed record CreateProductCommand(
    ProductInput Product, string? Barcode, bool GenerateBarcode, IReadOnlyList<ProductTaxInput>? Taxes, decimal? Price) : ICommand<ProductDetailDto>;

internal sealed class CreateProductValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductValidator()
    {
        RuleFor(x => x.Product).NotNull().SetValidator(new ProductInputValidator());
        RuleFor(x => x.Barcode).MaximumLength(50);
        RuleFor(x => x.Price).GreaterThanOrEqualTo(0m);
    }
}

internal sealed class CreateProductHandler(
    IInstallationContext installation,
    ICatalogStore store,
    ICatalogQueries queries,
    ProductWriter writer,
    PriceService prices,
    IUnitOfWork unitOfWork,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<CreateProductCommand, ProductDetailDto>
{
    public async Task<Result<ProductDetailDto>> Handle(CreateProductCommand request, CancellationToken cancellationToken)
    {
        var companyId = installation.RequireCompany();
        if (companyId.IsFailure)
        {
            return companyId.Error;
        }

        var data = await writer.ResolveAsync(request.Product, null, null, cancellationToken);
        if (data.IsFailure)
        {
            return data.Error;
        }

        var product = Product.Create(ids.NewId(), companyId.Value, data.Value);
        if (product.IsFailure)
        {
            return product.Error;
        }

        store.Add(product.Value);
        if (!string.IsNullOrWhiteSpace(request.Barcode) || request.GenerateBarcode)
        {
            var barcode = await writer.AddBarcodeAsync(product.Value, null, request.Barcode, null, request.GenerateBarcode, isPrimary: true, cancellationToken);
            if (barcode.IsFailure)
            {
                return barcode.Error;
            }
        }

        var taxes = await writer.SetTaxesAsync(product.Value, request.Taxes, cancellationToken);
        if (taxes.IsFailure)
        {
            return taxes.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (request.Price is { } price)
        {
            var set = await prices.SetAsync(product.Value, null, null, null, price, null, cancellationToken);
            if (set.IsFailure)
            {
                return set.Error;
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return (await queries.GetProductAsync(product.Value.Id, clock.UtcNow, cancellationToken))!;
    }
}

public sealed record UpdateProductCommand(Guid ProductId, ProductInput Product) : ICommand<ProductDetailDto>;

internal sealed class UpdateProductValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductValidator() => RuleFor(x => x.Product).NotNull().SetValidator(new ProductInputValidator());
}

internal sealed class UpdateProductHandler(
    ICatalogStore store, ICatalogQueries queries, ProductWriter writer, IInventoryQueries inventory, IUnitOfWork unitOfWork, IClock clock)
    : ICommandHandler<UpdateProductCommand, ProductDetailDto>
{
    public async Task<Result<ProductDetailDto>> Handle(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        var product = await store.GetProductAsync(request.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        var data = await writer.ResolveAsync(request.Product, product.Id, product.Sku, cancellationToken);
        if (data.IsFailure)
        {
            return data.Error;
        }

        var updated = product.Update(data.Value, await inventory.HasMovementsAsync(product.Id, cancellationToken));
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (await queries.GetProductAsync(product.Id, clock.UtcNow, cancellationToken))!;
    }
}

public sealed record ChangeProductStatusCommand(Guid ProductId, ProductStatus Status) : ICommand;

internal sealed class ChangeProductStatusHandler(ICatalogStore store, IInventoryQueries inventory, ISettingsReader settings)
    : ICommandHandler<ChangeProductStatusCommand>
{
    public async Task<Result> Handle(ChangeProductStatusCommand request, CancellationToken cancellationToken)
    {
        var product = await store.GetProductAsync(request.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        var block = await settings.GetAsync(CatalogSettings.BlockDiscontinueWithStock, new SettingContext(product.CompanyId), cancellationToken);
        var onHand = request.Status == ProductStatus.Discontinued ? await inventory.GetOnHandAsync(product.Id, cancellationToken) : 0m;
        return product.ChangeStatus(request.Status, onHand, block);
    }
}

// ─────────────────────────────── Presentaciones ───────────────────────────────

public sealed record PackagingInput(string Name, decimal Factor, bool IsSellable, bool IsPurchasable);

public sealed record AddPackagingCommand(Guid ProductId, PackagingInput Packaging, string? Barcode, decimal? Price) : ICommand<ProductDetailDto>;

internal sealed class AddPackagingHandler(
    ICatalogStore store, ICatalogQueries queries, ProductWriter writer, PriceService prices, IUnitOfWork unitOfWork, IIdGenerator ids, IClock clock)
    : ICommandHandler<AddPackagingCommand, ProductDetailDto>
{
    public async Task<Result<ProductDetailDto>> Handle(AddPackagingCommand request, CancellationToken cancellationToken)
    {
        var product = await store.GetProductAsync(request.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        var input = request.Packaging;
        var packaging = ProductPackaging.Create(ids.NewId(), product, input.Name, input.Factor, input.IsSellable, input.IsPurchasable);
        if (packaging.IsFailure)
        {
            return packaging.Error;
        }

        store.Add(packaging.Value);
        if (!string.IsNullOrWhiteSpace(request.Barcode))
        {
            var barcode = await writer.AddBarcodeAsync(product, packaging.Value, request.Barcode, null, generate: false, isPrimary: false, cancellationToken);
            if (barcode.IsFailure)
            {
                return barcode.Error;
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (request.Price is { } price)
        {
            var set = await prices.SetAsync(product, packaging.Value, null, null, price, null, cancellationToken);
            if (set.IsFailure)
            {
                return set.Error;
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return (await queries.GetProductAsync(product.Id, clock.UtcNow, cancellationToken))!;
    }
}

public sealed record UpdatePackagingCommand(Guid ProductId, Guid PackagingId, PackagingInput Packaging) : ICommand<PackagingDto>;

internal sealed class UpdatePackagingHandler(ICatalogStore store) : ICommandHandler<UpdatePackagingCommand, PackagingDto>
{
    public async Task<Result<PackagingDto>> Handle(UpdatePackagingCommand request, CancellationToken cancellationToken)
    {
        var packaging = (await store.GetPackagingsAsync(request.ProductId, cancellationToken)).SingleOrDefault(p => p.Id == request.PackagingId);
        if (packaging is null)
        {
            return CatalogErrors.PackagingNotFound;
        }

        var input = request.Packaging;
        var updated = packaging.Update(input.Name, input.Factor, input.IsSellable, input.IsPurchasable);
        return updated.IsSuccess
            ? new PackagingDto(packaging.Id, packaging.Name, packaging.Factor, packaging.IsSellable, packaging.IsPurchasable)
            : updated.Error;
    }
}

/// <summary>Elimina la presentación con sus códigos; sus precios vigentes se cierran y los programados se cancelan.</summary>
public sealed record RemovePackagingCommand(Guid ProductId, Guid PackagingId) : ICommand;

internal sealed class RemovePackagingHandler(ICatalogStore store, IClock clock) : ICommandHandler<RemovePackagingCommand>
{
    public async Task<Result> Handle(RemovePackagingCommand request, CancellationToken cancellationToken)
    {
        var packaging = (await store.GetPackagingsAsync(request.ProductId, cancellationToken)).SingleOrDefault(p => p.Id == request.PackagingId);
        if (packaging is null)
        {
            return CatalogErrors.PackagingNotFound;
        }

        foreach (var barcode in (await store.GetBarcodesAsync(request.ProductId, cancellationToken)).Where(b => b.PackagingId == packaging.Id))
        {
            store.Remove(barcode);
        }

        var now = clock.UtcNow;
        foreach (var list in await store.GetPriceListsAsync(cancellationToken))
        {
            foreach (var price in await store.GetPricesAsync(list.Id, request.ProductId, packaging.Id, null, cancellationToken))
            {
                ClosePrice(store, price, now);
            }
        }

        store.Remove(packaging);
        return Result.Success();
    }

    internal static void ClosePrice(ICatalogStore store, ProductPrice price, DateTimeOffset now)
    {
        if (price.IsScheduled(now))
        {
            store.Remove(price);
        }
        else if (price.ValidTo is null || price.ValidTo > now)
        {
            price.CloseAt(now);
        }
    }
}

// ─────────────────────────────── Códigos de barras ───────────────────────────────

public sealed record AddBarcodeCommand(Guid ProductId, string? Code, Guid? PackagingId, BarcodeType? Type, bool Generate, bool IsPrimary)
    : ICommand<BarcodeDto>;

internal sealed class AddBarcodeHandler(ICatalogStore store, ProductWriter writer) : ICommandHandler<AddBarcodeCommand, BarcodeDto>
{
    public async Task<Result<BarcodeDto>> Handle(AddBarcodeCommand request, CancellationToken cancellationToken)
    {
        var product = await store.GetProductAsync(request.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        ProductPackaging? packaging = null;
        if (request.PackagingId is { } packagingId
            && (packaging = (await store.GetPackagingsAsync(product.Id, cancellationToken)).SingleOrDefault(p => p.Id == packagingId)) is null)
        {
            return CatalogErrors.PackagingNotFound;
        }

        var barcode = await writer.AddBarcodeAsync(product, packaging, request.Code, request.Type, request.Generate, request.IsPrimary, cancellationToken);
        return barcode.IsSuccess
            ? new BarcodeDto(barcode.Value.Id, barcode.Value.Code, barcode.Value.NormalizedCode, barcode.Value.CodeType.Db(), barcode.Value.PackagingId,
                barcode.Value.IsPrimary)
            : barcode.Error;
    }
}

public sealed record RemoveBarcodeCommand(Guid ProductId, Guid BarcodeId) : ICommand;

internal sealed class RemoveBarcodeHandler(ICatalogStore store) : ICommandHandler<RemoveBarcodeCommand>
{
    public async Task<Result> Handle(RemoveBarcodeCommand request, CancellationToken cancellationToken)
    {
        var barcode = (await store.GetBarcodesAsync(request.ProductId, cancellationToken)).SingleOrDefault(b => b.Id == request.BarcodeId);
        if (barcode is null)
        {
            return CatalogErrors.BarcodeNotFound;
        }

        store.Remove(barcode);
        return Result.Success();
    }
}

// ─────────────────────────────── Impuestos del producto ───────────────────────────────

public sealed record SetProductTaxesCommand(Guid ProductId, IReadOnlyList<ProductTaxInput> Taxes) : ICommand<ProductDetailDto>;

internal sealed class SetProductTaxesHandler(ICatalogStore store, ICatalogQueries queries, ProductWriter writer, IUnitOfWork unitOfWork, IClock clock)
    : ICommandHandler<SetProductTaxesCommand, ProductDetailDto>
{
    public async Task<Result<ProductDetailDto>> Handle(SetProductTaxesCommand request, CancellationToken cancellationToken)
    {
        var product = await store.GetProductAsync(request.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        var set = await writer.SetTaxesAsync(product, request.Taxes ?? [], cancellationToken);
        if (set.IsFailure)
        {
            return set.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (await queries.GetProductAsync(product.Id, clock.UtcNow, cancellationToken))!;
    }
}
