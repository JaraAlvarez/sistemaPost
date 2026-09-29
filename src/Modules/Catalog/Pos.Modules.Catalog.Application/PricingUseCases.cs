using System.Globalization;
using FluentValidation;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Data;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.Modules.Inventory.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Catalog.Application;

/// <summary>
/// Fija precios con vigencia (RN-CAT-05): cierra el que cubre la fecha de inicio (o reemplaza uno programado para esa
/// misma fecha) y crea el nuevo; si hay otro programado después, el nuevo termina donde empieza ese. Revisa el precio
/// contra el costo promedio de la sucursal (RN-CAT-06): advierte o bloquea según <c>catalog.price_below_cost</c>.
/// </summary>
public sealed class PriceService(
    IInstallationContext installation,
    ICatalogStore store,
    IInventoryQueries inventory,
    ISettingsReader settings,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    IIdGenerator ids,
    IClock clock)
{
    /// <summary>Margen para aceptar un "desde" que llega unos segundos atrás (reloj del cliente).</summary>
    public static readonly TimeSpan PastTolerance = TimeSpan.FromMinutes(5);

    public async Task<Result<(ProductPrice Price, IReadOnlyList<string> Warnings)>> SetAsync(
        Product product, ProductPackaging? packaging, Guid? branchId, PriceList? priceList, decimal price, DateTimeOffset? validFrom,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        var now = clock.UtcNow;
        var from = validFrom ?? now;
        if (from < now - PastTolerance)
        {
            return CatalogErrors.PriceInThePast;
        }

        if (from < now)
        {
            from = now;
        }

        if (!ProductPrice.IsValidPrice(price))
        {
            return CatalogErrors.InvalidPrice;
        }

        if (packaging is { IsSellable: false })
        {
            return CatalogErrors.PackagingNotSellable;
        }

        var list = priceList ?? (await store.GetPriceListsAsync(cancellationToken)).SingleOrDefault(l => l.IsDefault);
        if (list is null)
        {
            return CatalogErrors.PriceListNotFound;
        }

        var warnings = new List<string>();
        var belowCost = await CheckCostAsync(product, packaging, branchId, list, price, cancellationToken);
        if (belowCost is not null)
        {
            var policy = await settings.GetAsync(CatalogSettings.PriceBelowCost, new SettingContext(product.CompanyId), cancellationToken);
            if (policy == CatalogSettings.Block)
            {
                return CatalogErrors.PriceBelowCost;
            }

            warnings.Add(belowCost);
        }

        var existing = (await store.GetPricesAsync(list.Id, product.Id, packaging?.Id, branchId, cancellationToken)).OrderBy(p => p.ValidFrom).ToList();
        var previous = existing.FirstOrDefault(p => p.IsValidAt(from));
        var plan = ValidityPlanner.PlanInsert<DateTimeOffset>([.. existing.Select(p => (p.ValidFrom, p.ValidTo))], from);
        ProductPrice result;
        if (plan.ReplaceIndex is { } replace)
        {
            result = existing[replace];
            result.Reprice(price);
        }
        else
        {
            if (plan.CloseIndex is { } close)
            {
                existing[close].CloseAt(from);

                // La restricción de exclusión exige cerrar la vigencia anterior ANTES de insertar la nueva.
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            result = ProductPrice.Create(ids.NewId(), list, product, packaging, branchId, price, from, plan.NewValidTo).Value;
            store.Add(result);
        }

        var target = packaging is null ? "Precio" : $"Precio de {packaging.Name}";
        var scope = branchId is null ? string.Empty : " (sucursal)";
        var before = previous is null ? "sin precio" : previous.Price.ToString("0.##", CultureInfo.InvariantCulture);
        var schedule = from > now ? string.Create(CultureInfo.InvariantCulture, $" desde {from:yyyy-MM-dd HH:mm} UTC") : string.Empty;
        await audit.WriteAsync(
            new AuditEntry(
                "catalog",
                from > now ? "PRODUCT_PRICE_SCHEDULED" : "PRODUCT_PRICE_CHANGED",
                nameof(Product),
                product.Id,
                product.AuditLabel,
                string.Create(CultureInfo.InvariantCulture, $"{target} en {list.Code}{scope}: {before} → {price:0.##}{schedule}"),
                OldValues: previous is null ? null : new Dictionary<string, object?> { ["price"] = previous.Price },
                NewValues: new Dictionary<string, object?> { ["price"] = price, ["valid_from"] = from }),
            cancellationToken);

        return (result, warnings);
    }

    /// <summary>Mensaje si el precio sin impuestos por unidad base queda por debajo del costo promedio de la sucursal.</summary>
    private async Task<string?> CheckCostAsync(
        Product product, ProductPackaging? packaging, Guid? branchId, PriceList list, decimal price, CancellationToken cancellationToken)
    {
        if (!product.IsStockable || (branchId ?? installation.BranchId) is not { } branch)
        {
            return null;
        }

        var cost = await inventory.GetAverageCostAsync(product.Id, branch, cancellationToken);
        if (cost is not > 0m)
        {
            return null;
        }

        var net = price;
        if (list.PricesIncludeTax)
        {
            var (vatRate, fixedAmounts) = await CurrentTaxesAsync(product, cancellationToken);
            net = (price - fixedAmounts * (packaging?.Factor ?? 1m)) / (1m + (vatRate / 100m));
        }

        var perBaseUnit = net / (packaging?.Factor ?? 1m);
        return perBaseUnit < cost
            ? string.Create(CultureInfo.InvariantCulture,
                $"CATALOG.PRICE_BELOW_COST: el precio sin impuestos por {product.BaseUnitCode} ({perBaseUnit:0.##}) es menor que el costo promedio ({cost:0.##}).")
            : null;
    }

    private async Task<(decimal VatRate, decimal FixedAmounts)> CurrentTaxesAsync(Product product, CancellationToken cancellationToken)
    {
        var today = clock.Today;
        var vat = 0m;
        var fixedAmounts = 0m;
        foreach (var productTax in await store.GetProductTaxesAsync(product.Id, cancellationToken))
        {
            var tax = await store.GetTaxAsync(productTax.TaxId, cancellationToken);
            if (tax is null)
            {
                continue;
            }

            var rate = (await store.GetTaxRatesAsync(tax.Id, cancellationToken)).FirstOrDefault(r => r.IsValidOn(today));
            if (tax.Calculation == TaxCalculation.Percentage && tax.IsVat)
            {
                vat += rate?.Rate ?? 0m;
            }
            else if (tax.Calculation == TaxCalculation.FixedPerUnit)
            {
                fixedAmounts += productTax.FixedAmount ?? rate?.FixedAmount ?? 0m;
            }
        }

        return (vat, fixedAmounts);
    }
}

internal static class PriceMapping
{
    public static PriceDto ToDto(this ProductPrice p, string listCode, DateTimeOffset now) => new(
        p.Id, p.PriceListId, listCode, p.PackagingId, p.BranchId, p.Price, p.ValidFrom, p.ValidTo,
        p.ValidFrom > now ? "SCHEDULED" : p.ValidTo is { } to && to <= now ? "EXPIRED" : "CURRENT");
}

/// <summary>
/// Fija o programa un precio. Sin lista: la predeterminada; sin presentación: unidad base (por kilo en peso); sin
/// sucursal: todas; sin fecha: desde ahora.
/// </summary>
public sealed record SetPriceCommand(Guid ProductId, decimal Price, Guid? PriceListId, Guid? PackagingId, Guid? BranchId, DateTimeOffset? ValidFrom)
    : ICommand<SetPriceResultDto>;

internal sealed class SetPriceValidator : AbstractValidator<SetPriceCommand>
{
    public SetPriceValidator() => RuleFor(x => x.Price).GreaterThanOrEqualTo(0m);
}

internal sealed class SetPriceHandler(ICatalogStore store, PriceService prices, IClock clock) : ICommandHandler<SetPriceCommand, SetPriceResultDto>
{
    public async Task<Result<SetPriceResultDto>> Handle(SetPriceCommand request, CancellationToken cancellationToken)
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

        PriceList? list = null;
        if (request.PriceListId is { } listId && (list = await store.GetPriceListAsync(listId, cancellationToken)) is null)
        {
            return CatalogErrors.PriceListNotFound;
        }

        var set = await prices.SetAsync(product, packaging, request.BranchId, list, request.Price, request.ValidFrom, cancellationToken);
        if (set.IsFailure)
        {
            return set.Error;
        }

        var code = list?.Code ?? (await store.GetPriceListsAsync(cancellationToken)).Single(l => l.Id == set.Value.Price.PriceListId).Code;
        return new SetPriceResultDto(set.Value.Price.ToDto(code, clock.UtcNow), set.Value.Warnings);
    }
}

/// <summary>Cancela un precio programado que aún no rige; la vigencia del anterior se extiende hasta donde llegaba este.</summary>
public sealed record CancelScheduledPriceCommand(Guid ProductId, Guid PriceId) : ICommand;

internal sealed class CancelScheduledPriceHandler(ICatalogStore store, IUnitOfWork unitOfWork, IClock clock) : ICommandHandler<CancelScheduledPriceCommand>
{
    public async Task<Result> Handle(CancelScheduledPriceCommand request, CancellationToken cancellationToken)
    {
        var price = await store.GetPriceAsync(request.PriceId, cancellationToken);
        if (price is null || price.ProductId != request.ProductId)
        {
            return CatalogErrors.PriceNotFound;
        }

        if (!price.IsScheduled(clock.UtcNow))
        {
            return CatalogErrors.PriceNotScheduled;
        }

        var siblings = await store.GetPricesAsync(price.PriceListId, price.ProductId, price.PackagingId, price.BranchId, cancellationToken);
        var previous = siblings.SingleOrDefault(p => p.Id != price.Id && p.ValidTo == price.ValidFrom);
        store.Remove(price);
        if (previous is not null)
        {
            // Primero se borra (lógicamente) el programado; luego el anterior recupera su vigencia (sin solapamiento).
            await unitOfWork.SaveChangesAsync(cancellationToken);
            previous.CloseAt(price.ValidTo);
        }

        return Result.Success();
    }
}

public sealed record GetPriceHistoryQuery(Guid ProductId) : IQuery<IReadOnlyList<PriceDto>>;

internal sealed class GetPriceHistoryHandler(ICatalogQueries queries, IClock clock) : IQueryHandler<GetPriceHistoryQuery, IReadOnlyList<PriceDto>>
{
    public async Task<Result<IReadOnlyList<PriceDto>>> Handle(GetPriceHistoryQuery request, CancellationToken cancellationToken) =>
        Result.Success(await queries.GetPriceHistoryAsync(request.ProductId, clock.UtcNow, cancellationToken));
}
