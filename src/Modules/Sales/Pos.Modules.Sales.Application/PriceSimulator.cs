using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Promotions.Contracts;
using Pos.Modules.Sales.Contracts;
using Pos.Modules.Sales.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Sales.Application;

/// <summary>Simulación de una venta con el mismo motor de la caja (el encargado prueba promociones antes de activarlas).</summary>
public sealed class PriceSimulator(ICatalogSaleItems catalog) : IPriceSimulator
{
    public async Task<Result<SimulationDto>> SimulateAsync(
        Guid branchId, IReadOnlyList<SimulationLineRequest> lines, IReadOnlyList<PromotionDefinition> promotions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(promotions);
        if (lines.Count is 0 or > 200 || lines.Any(l => l.Quantity <= 0m))
        {
            return SalesErrors.InvalidQuantity;
        }

        var items = new List<(Guid Key, CatalogSaleItem Item, decimal Quantity)>();
        foreach (var line in lines)
        {
            if (await catalog.GetAsync(line.ProductId, line.PackagingId, branchId, null, cancellationToken) is not { UnitPrice: not null } item)
            {
                return Error.BusinessRule(SalesErrors.ProductNotSellable.Code, $"El producto {line.ProductId} no existe o no tiene precio vigente.");
            }

            items.Add((Guid.NewGuid(), item, line.Quantity));
        }

        var priced = SaleCalculator.Calculate(
            [.. items.Select(i => new PricingLine(
                i.Key, i.Item.ProductId, i.Item.PackagingId, i.Item.CategoryId, i.Item.BrandId, i.Quantity, i.Item.Factor, i.Item.UnitPrice!.Value, i.Item.PriceIncludesTax,
                [.. i.Item.Taxes.Select(t => new PricingTax(t.TaxId, t.Code, t.Kind, t.Rate, t.FixedAmount))]))],
            PromotionRules.Map(promotions));
        var byKey = priced.Lines.ToDictionary(l => l.Key);
        return new SimulationDto(
            [.. items.Select(i => new SimulationLineDto(
                i.Item.ProductId, i.Item.Sku, i.Item.Name, i.Quantity, i.Item.UnitPrice!.Value, byKey[i.Key].Gross, byKey[i.Key].PromotionName,
                byKey[i.Key].PromotionDiscount, byKey[i.Key].Total))],
            priced.Gross, priced.PromotionTotal, priced.Total);
    }
}
