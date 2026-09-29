using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Catalog.Application;

/// <summary>
/// Resuelve un código leído en la caja (propuesta §5.1): código de barras (UPC con o sin 0), etiqueta de báscula (peso o
/// precio) o SKU. Devuelve producto, presentación, cantidad, precio vigente de la sucursal, impuestos y si se puede vender
/// (RN-CAT-02) con los motivos. La venta en sí llega en la Fase 7.
/// </summary>
public sealed record ScanCodeQuery(string Code, Guid? BranchId = null) : IQuery<ScanResultDto>;

internal sealed class ScanCodeHandler(
    ICatalogStore store,
    ICatalogQueries queries,
    ICurrentUser currentUser,
    IInstallationContext installation,
    IClock clock) : IQueryHandler<ScanCodeQuery, ScanResultDto>
{
    public const int ScaleQuantityDecimals = 3;

    public async Task<Result<ScanResultDto>> Handle(ScanCodeQuery request, CancellationToken cancellationToken)
    {
        var code = Barcodes.NormalizeForLookup(request.Code ?? string.Empty);
        if (code.Length == 0)
        {
            return CatalogErrors.CodeNotFound;
        }

        Guid productId;
        Guid? packagingId = null;
        VariableBarcodeReading? reading = null;
        var source = "BARCODE";
        if (await queries.FindByBarcodeAsync(code, cancellationToken) is { } byBarcode)
        {
            (productId, packagingId) = byBarcode;
        }
        else if ((reading = (await store.GetBarcodeRulesAsync(cancellationToken)).Select(r => r.Read(code)).FirstOrDefault(r => r is not null)) is not null
            && await queries.FindByPluAsync(reading.Plu, cancellationToken) is { } byPlu)
        {
            productId = byPlu;
            source = reading.Content == VariableBarcodeContent.Weight ? "SCALE_WEIGHT" : "SCALE_PRICE";
        }
        else if (await queries.FindBySkuAsync(Product.NormalizeSku(request.Code), cancellationToken) is { } bySku)
        {
            productId = bySku;
            reading = null;
            source = "SKU";
        }
        else
        {
            return CatalogErrors.CodeNotFound;
        }

        var product = await store.GetProductAsync(productId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.CodeNotFound;
        }

        var packaging = packagingId is { } pid ? (await store.GetPackagingsAsync(productId, cancellationToken)).SingleOrDefault(p => p.Id == pid) : null;
        var now = clock.UtcNow;
        var branchId = request.BranchId ?? currentUser.BranchId ?? installation.BranchId;
        var price = await queries.GetEffectivePriceAsync(productId, packaging?.Id, branchId, now, cancellationToken);
        var taxes = await queries.GetTaxLinesAsync(productId, clock.BusinessDateOf(now), cancellationToken);

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

        if (price is null)
        {
            reasons.Add("El producto no tiene precio vigente en la lista predeterminada.");
        }

        return new ScanResultDto(
            product.Id, product.Sku, product.Name, product.ShortName, packaging?.Id, packaging?.Name, packaging?.Factor ?? 1m, product.BaseUnitCode,
            product.SaleMode.Db(), source, quantity, price?.Price, amount, price?.IncludesTax ?? true, [.. taxes.Select(t => t.Line)],
            reasons.Count == 0, reasons);
    }
}
