using System.Globalization;
using FluentValidation;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.Modules.Promotions.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Printing;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Catalog.Application;

/// <summary>
/// Lo que se dibuja en una etiqueta: nombre, precio grande (el de oferta si hay), precio normal tachado, texto de la oferta, precio por kilo
/// o litro, código de barras (o el SKU en CODE128 si no tiene) y la fecha.
/// </summary>
public sealed record LabelContent(
    string Name,
    string Sku,
    BarcodeDrawing? Barcode,
    string? PriceText,
    string? PriceUnit,
    string? RegularPriceText,
    string? OfferText,
    string? UnitMeasurePrice,
    string? DateText);

/// <summary>Archivo generado (PDF de etiquetas).</summary>
public sealed record LabelFile(string FileName, string ContentType, byte[] Content, int Labels, int Pages);

/// <summary>Dibuja las etiquetas en un PDF al tamaño exacto de la plantilla (implementación con PDFsharp en Infrastructure).</summary>
public interface ILabelRenderer
{
    byte[] RenderPdf(LabelTemplate template, IReadOnlyList<LabelContent> labels, string title);
}

public static class LabelErrors
{
    public static readonly Error InvalidSource = Error.Validation(
        "CATALOG.LABELS_INVALID_SOURCE",
        "Elija de dónde salen las etiquetas: productos, categoría, cambios de precio desde una fecha, compra o productos sin código de barras.");

    public static readonly Error MissingFilter = Error.Validation(
        "CATALOG.LABELS_MISSING_FILTER", "Falta el dato para buscar: los productos, la categoría, la fecha o el número de la compra.");

    public static readonly Error PurchaseNotFound = Error.NotFound("CATALOG.LABELS_PURCHASE_NOT_FOUND", "No se encontró una compra con ese número.");

    public static readonly Error PurchaseNotPosted = Error.BusinessRule(
        "CATALOG.LABELS_PURCHASE_NOT_POSTED", "La compra todavía no está contabilizada (o fue anulada): solo se imprimen etiquetas de mercancía recibida.");

    public static readonly Error TooManyLabels = Error.Validation(
        "CATALOG.LABELS_TOO_MANY",
        $"Máximo {LabelLayout.MaxLabels} etiquetas por archivo y {LabelLayout.MaxCopies} copias por producto: divida la impresión.");

    public static readonly Error NothingToPrint = Error.Validation("CATALOG.LABELS_EMPTY", "No hay etiquetas para imprimir: agregue productos y cantidades.");
}

/// <summary>
/// Arma las etiquetas de una lista de productos: precio al público vigente de la lista por defecto (con impuestos), la promoción que rige hoy
/// en la sucursal, el precio por kilo o litro y el código de barras.
/// </summary>
public sealed class LabelBuilder(ICatalogQueries queries, ICatalogReader reader, IActivePromotions promotions, IInstallationContext installation, IClock clock)
{
    /// <summary>Máximo de productos distintos en una lista de etiquetas.</summary>
    public const int MaxProducts = 500;

    public async Task<IReadOnlyList<LabelItemDto>> BuildAsync(IReadOnlyList<(Guid ProductId, int Quantity)> requested, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var ids = requested.Select(r => r.ProductId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var now = clock.UtcNow;
        var products = (await queries.GetLabelProductsAsync(ids, installation.BranchId, now, cancellationToken)).ToDictionary(p => p.Id);
        var taxes = await reader.GetTaxesAsync(ids, clock.Today, cancellationToken);
        var active = installation.BranchId is { } branchId
            ? (await promotions.GetActiveAsync(branchId, now, cancellationToken)).Select(ToLabelPromotion).ToList()
            : [];

        var items = new List<LabelItemDto>(requested.Count);
        foreach (var (productId, quantity) in requested)
        {
            if (products.GetValueOrDefault(productId) is not { } product)
            {
                continue;
            }

            var productTaxes = taxes.GetValueOrDefault(productId) ?? [];
            items.Add(ToItem(product, productTaxes, product.AllowsPromotions ? active : [], quantity));
        }

        return items;
    }

    /// <summary>Etiqueta de un producto con sus impuestos y las promociones vigentes (puro: se prueba sin base de datos).</summary>
    public static LabelItemDto ToItem(LabelProductData product, IReadOnlyList<CatalogTaxInfo> taxes, IReadOnlyList<LabelPromotion> promotions, int quantity)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(taxes);
        var weighed = product.IsSoldByScale || product.SaleMode == SaleMode.Weight;
        decimal? price = product.ListPrice is { } list
            ? LabelPricing.ConsumerPrice(list, product.IncludesTax, taxes.Sum(t => t.Rate ?? 0m), taxes.Sum(t => t.FixedAmount ?? 0m))
            : null;
        var offer = LabelPricing.BestOffer(product.Id, product.CategoryId, product.BrandId, price, promotions);
        var unit = weighed ? (product.BaseUnitCode == "KG" ? "kg" : product.BaseUnitCode.ToLowerInvariant()) : null;
        return new LabelItemDto(
            product.Id,
            product.Sku,
            product.Name,
            product.Barcode,
            product.Barcode is not null,
            price,
            unit,
            offer?.Price,
            offer?.Text,
            weighed ? null : LabelPricing.UnitMeasurePrice(offer?.Price ?? price, product.NetContent, product.NetContentUnit),
            weighed,
            Math.Clamp(quantity, 1, LabelLayout.MaxCopies));
    }

    /// <summary>Contenido dibujable de una etiqueta (con la fecha si se pide).</summary>
    public static LabelContent ToContent(LabelItemDto item, DateOnly? date)
    {
        ArgumentNullException.ThrowIfNull(item);
        var hasOffer = item.PromoPrice is { } promo && (item.Price is null || promo < item.Price);
        var shown = hasOffer ? item.PromoPrice : item.Price;
        return new LabelContent(
            item.Name,
            item.Sku,
            BarcodeSymbology.Encode(item.Barcode ?? item.Sku),
            shown is { } value ? LabelPricing.Money(value) : null,
            item.PriceUnit,
            hasOffer && item.Price is { } regular ? LabelPricing.Money(regular) : null,
            item.PromoText,
            item.UnitMeasurePrice,
            date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
    }

    private static LabelPromotion ToLabelPromotion(PromotionDefinition d) => new(
        d.Name,
        d.Kind,
        [.. d.Targets.Select(t => new LabelPromotionTarget(t.ProductId, t.PackagingId, t.CategoryIds, t.BrandId))],
        d.BuyQuantity,
        d.PayQuantity,
        d.Price,
        d.Percent,
        d.MinQuantity);
}

// ─────────────────────────────── Armar la lista ───────────────────────────────

/// <summary>
/// Arma la lista de etiquetas para revisarla antes de imprimir. <c>Source</c>: PRODUCTS (con <c>ProductIds</c>), CATEGORY (con
/// <c>CategoryId</c>), PRICE_CHANGES (con <c>Since</c>), PURCHASE (con <c>PurchaseNumber</c>; cantidad = unidades recibidas) o WITHOUT_BARCODE.
/// </summary>
public sealed record PreviewLabelsQuery(string Source, IReadOnlyList<Guid>? ProductIds = null, Guid? CategoryId = null, DateOnly? Since = null,
    string? PurchaseNumber = null) : IQuery<LabelPreviewDto>;

internal sealed class PreviewLabelsHandler(
    ICatalogQueries queries, IPurchaseReceipts purchases, LabelBuilder builder, IInstallationContext installation, IClock clock)
    : IQueryHandler<PreviewLabelsQuery, LabelPreviewDto>
{
    public async Task<Result<LabelPreviewDto>> Handle(PreviewLabelsQuery request, CancellationToken cancellationToken)
    {
        var source = (request.Source ?? string.Empty).Trim().ToUpperInvariant();
        List<(Guid ProductId, int Quantity)> requested;
        string? reference = null;
        switch (source)
        {
            case LabelSources.Products:
                if (request.ProductIds is not { Count: > 0 })
                {
                    return LabelErrors.MissingFilter;
                }

                requested = [.. request.ProductIds.Distinct().Select(id => (id, 1))];
                break;

            case LabelSources.Category when request.CategoryId is null:
            case LabelSources.PriceChanges when request.Since is null:
            case LabelSources.Purchase when string.IsNullOrWhiteSpace(request.PurchaseNumber):
                return LabelErrors.MissingFilter;

            case LabelSources.Category or LabelSources.PriceChanges or LabelSources.WithoutBarcode:
                DateTimeOffset? since = null;
                if (request.Since is { } day)
                {
                    var local = day.ToDateTime(TimeOnly.MinValue);
                    since = new DateTimeOffset(local, clock.BusinessTimeZone.GetUtcOffset(local));
                    reference = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                var ids = await queries.FindLabelProductIdsAsync(
                    new LabelFilter(source, request.CategoryId, since, clock.UtcNow, installation.BranchId), LabelBuilder.MaxProducts + 1, cancellationToken);
                requested = [.. ids.Select(id => (id, 1))];
                break;

            case LabelSources.Purchase:
                var purchase = await purchases.FindAsync(request.PurchaseNumber!, cancellationToken);
                if (purchase is null)
                {
                    return LabelErrors.PurchaseNotFound;
                }

                if (purchase.Status != "POSTED")
                {
                    return LabelErrors.PurchaseNotPosted;
                }

                reference = purchase.Number;
                requested = [.. purchase.Lines.GroupBy(l => l.ProductId)
                    .Select(g => (g.Key, (int)Math.Min(LabelLayout.MaxCopies, Math.Ceiling(g.Sum(l => l.BaseQuantity)))))
                    .Where(x => x.Item2 > 0)];
                break;

            default:
                return LabelErrors.InvalidSource;
        }

        var truncated = requested.Count > LabelBuilder.MaxProducts;
        var items = (await builder.BuildAsync([.. requested.Take(LabelBuilder.MaxProducts)], cancellationToken)).ToList();
        if (source == LabelSources.Purchase)
        {
            // Lo que se vende por peso lleva una sola etiqueta de góndola (no una por kilo).
            items = [.. items.Select(i => i.IsWeighed ? i with { Quantity = 1 } : i)];
        }

        return new LabelPreviewDto(source, reference, items, Warnings(items, truncated), truncated);
    }

    private static List<string> Warnings(List<LabelItemDto> items, bool truncated)
    {
        var warnings = new List<string>();
        if (items.Count == 0)
        {
            warnings.Add("No se encontraron productos con ese criterio.");
        }

        if (truncated)
        {
            warnings.Add($"Se muestran los primeros {LabelBuilder.MaxProducts} productos; imprima por partes (por ejemplo, por categoría).");
        }

        var withoutPrice = items.Count(i => i.Price is null);
        if (withoutPrice > 0)
        {
            warnings.Add($"{withoutPrice} producto(s) no tienen precio vigente: la etiqueta saldrá sin precio.");
        }

        var withoutBarcode = items.Count(i => !i.HasBarcode);
        if (withoutBarcode > 0)
        {
            warnings.Add($"{withoutBarcode} producto(s) no tienen código de barras: la etiqueta lleva el SKU (también se puede leer en la caja). " +
                "Si prefiere, genere un código interno.");
        }

        return warnings;
    }
}

// ─────────────────────────────── Imprimir ───────────────────────────────

/// <summary>PDF de etiquetas con la plantilla elegida; las cantidades repiten la etiqueta de cada producto.</summary>
public sealed record RenderLabelsQuery(LabelTemplateKind Template, IReadOnlyList<LabelPrintItem> Items, bool ShowDate = true) : IQuery<LabelFile>;

internal sealed class LabelItemsValidator : AbstractValidator<IReadOnlyList<LabelPrintItem>>
{
    public LabelItemsValidator()
    {
        RuleFor(x => x.Count).InclusiveBetween(1, LabelBuilder.MaxProducts).WithMessage(LabelErrors.NothingToPrint.Message);
        RuleForEach(x => x).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotEmpty();
            item.RuleFor(i => i.Quantity).InclusiveBetween(0, LabelLayout.MaxCopies);
        });
    }
}

internal sealed class RenderLabelsValidator : AbstractValidator<RenderLabelsQuery>
{
    public RenderLabelsValidator()
    {
        RuleFor(x => x.Template).IsInEnum();
        RuleFor(x => x.Items).NotNull().SetValidator(new LabelItemsValidator());
    }
}

internal sealed class RenderLabelsHandler(LabelBuilder builder, ILabelRenderer renderer, IClock clock) : IQueryHandler<RenderLabelsQuery, LabelFile>
{
    public async Task<Result<LabelFile>> Handle(RenderLabelsQuery request, CancellationToken cancellationToken)
    {
        var contents = await LabelPrinting.ContentsAsync(builder, request.Items, request.ShowDate ? clock.Today : null, LabelLayout.MaxLabels, cancellationToken);
        if (contents.IsFailure)
        {
            return contents.Error;
        }

        var template = LabelLayout.Get(request.Template);
        var pdf = renderer.RenderPdf(template, contents.Value, "Etiquetas");
        var stamp = clock.ToBusinessTime(clock.UtcNow).ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
        return new LabelFile($"etiquetas_{stamp}.pdf", "application/pdf", pdf, contents.Value.Count, LabelLayout.PageCount(template, contents.Value.Count));
    }
}

/// <summary>
/// Etiquetas sencillas para la impresora de tiquetes de la caja (ESC/POS, papel continuo): nombre, precio y código de barras CODE128, una
/// tras otra con una línea para cortar a mano. La interfaz se lo entrega al agente de la caja.
/// </summary>
public sealed record LabelTicketQuery(IReadOnlyList<LabelPrintItem> Items) : IQuery<TicketDocument>;

internal sealed class LabelTicketValidator : AbstractValidator<LabelTicketQuery>
{
    public LabelTicketValidator() => RuleFor(x => x.Items).NotNull().SetValidator(new LabelItemsValidator());
}

internal sealed class LabelTicketHandler(LabelBuilder builder, IClock clock) : IQueryHandler<LabelTicketQuery, TicketDocument>
{
    /// <summary>En papel de tiquete se limita la cantidad (cada etiqueta gasta unos 4 cm).</summary>
    public const int MaxTicketLabels = 200;

    public async Task<Result<TicketDocument>> Handle(LabelTicketQuery request, CancellationToken cancellationToken)
    {
        var contents = await LabelPrinting.ContentsAsync(builder, request.Items, clock.Today, MaxTicketLabels, cancellationToken);
        return contents.IsSuccess ? LabelPrinting.Ticket(contents.Value) : contents.Error;
    }
}

internal static class LabelPrinting
{
    public static async Task<Result<IReadOnlyList<LabelContent>>> ContentsAsync(
        LabelBuilder builder, IReadOnlyList<LabelPrintItem> items, DateOnly? date, int max, CancellationToken cancellationToken)
    {
        var requested = items.Where(i => i.Quantity > 0).GroupBy(i => i.ProductId).Select(g => (g.Key, g.Sum(i => i.Quantity))).ToList();
        if (requested.Count == 0)
        {
            return LabelErrors.NothingToPrint;
        }

        if (requested.Any(r => r.Item2 > LabelLayout.MaxCopies) || requested.Sum(r => r.Item2) > max)
        {
            return LabelErrors.TooManyLabels;
        }

        var built = await builder.BuildAsync(requested, cancellationToken);
        if (built.Count == 0)
        {
            return CatalogErrors.ProductNotFound;
        }

        return built.SelectMany(i => Enumerable.Repeat(LabelBuilder.ToContent(i, date), i.Quantity)).ToList();
    }

    public static TicketDocument Ticket(IReadOnlyList<LabelContent> labels)
    {
        var elements = new List<TicketElement>();
        foreach (var label in labels)
        {
            elements.Add(new TextLine(label.Name, TicketAlign.Center, Bold: true));
            if (label.PriceText is { } price)
            {
                elements.Add(new TextLine(label.PriceUnit is { } unit ? $"{price} /{unit}" : price, TicketAlign.Center, Bold: true, DoubleSize: true));
            }

            if (label.OfferText is { } offer)
            {
                elements.Add(new TextLine(label.RegularPriceText is { } regular ? $"{offer} · Antes {regular}" : offer, TicketAlign.Center));
            }

            if (label.UnitMeasurePrice is { } perUnit)
            {
                elements.Add(new TextLine(perUnit, TicketAlign.Center));
            }

            if (label.Barcode is { } barcode)
            {
                elements.Add(new BarcodeElement(barcode.Text));
            }

            elements.Add(new SeparatorLine('-'));
        }

        elements.Add(new FeedElement(2));
        return new TicketDocument("Etiquetas", elements, Cut: true, OpenDrawer: false);
    }
}

// ─────────────────────────────── Códigos internos ───────────────────────────────

/// <summary>
/// Genera el código interno EAN-13 (prefijo 29 + nodo + consecutivo) a los productos que no tienen código de barras propio, para que la
/// etiqueta lleve un código que la caja lee. Los que ya tienen código no cambian. Queda en la auditoría.
/// </summary>
public sealed record GenerateLabelCodesCommand(IReadOnlyList<Guid> ProductIds) : ICommand<IReadOnlyList<GeneratedLabelCodeDto>>;

internal sealed class GenerateLabelCodesValidator : AbstractValidator<GenerateLabelCodesCommand>
{
    public GenerateLabelCodesValidator() => RuleFor(x => x.ProductIds).NotNull().Must(ids => ids.Count is >= 1 and <= LabelBuilder.MaxProducts)
        .WithMessage($"Elija de 1 a {LabelBuilder.MaxProducts} productos.");
}

internal sealed class GenerateLabelCodesHandler(ICatalogStore store, ProductWriter writer, IAuditWriter audit)
    : ICommandHandler<GenerateLabelCodesCommand, IReadOnlyList<GeneratedLabelCodeDto>>
{
    public async Task<Result<IReadOnlyList<GeneratedLabelCodeDto>>> Handle(GenerateLabelCodesCommand request, CancellationToken cancellationToken)
    {
        var generated = new List<GeneratedLabelCodeDto>();
        foreach (var productId in request.ProductIds.Distinct())
        {
            var product = await store.GetProductAsync(productId, cancellationToken);
            if (product is null || product.Status == ProductStatus.Discontinued)
            {
                continue;
            }

            if ((await store.GetBarcodesAsync(product.Id, cancellationToken)).Any(b => b.PackagingId is null))
            {
                continue;
            }

            var barcode = await writer.AddBarcodeAsync(product, null, null, BarcodeType.Internal, generate: true, isPrimary: true, cancellationToken);
            if (barcode.IsFailure)
            {
                return barcode.Error;
            }

            generated.Add(new GeneratedLabelCodeDto(product.Id, product.Sku, product.Name, barcode.Value.Code));
        }

        if (generated.Count > 0)
        {
            var detail = string.Join(", ", generated.Take(20).Select(g => $"{g.Sku} → {g.Barcode}"));
            await audit.WriteAsync(
                new AuditEntry("catalog", "PRODUCT_INTERNAL_CODES_GENERATED", "Product", generated.Count == 1 ? generated[0].ProductId : null,
                    generated.Count == 1 ? $"Producto {generated[0].Sku} · {generated[0].Name}" : $"{generated.Count} productos",
                    $"Se generaron {generated.Count} código(s) interno(s) para etiquetas: {detail}{(generated.Count > 20 ? "…" : string.Empty)}.",
                    NewValues: new Dictionary<string, object?> { ["codes"] = string.Join(", ", generated.Select(g => $"{g.Sku}={g.Barcode}")) }),
                cancellationToken);
        }

        return generated;
    }
}
