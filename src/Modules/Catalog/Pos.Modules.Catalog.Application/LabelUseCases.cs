using System.Globalization;
using FluentValidation;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Promotions.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Printing;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Catalog.Application;

/// <summary>
/// Lo que se dibuja en una etiqueta: nombre, precio grande (el de oferta si hay), precio normal tachado, texto de la oferta (con su último día),
/// precio por kilo o litro, código de barras (o el SKU en CODE128 si no tiene) y la fecha.
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

    public static readonly Error BranchRequired = Error.Validation(
        "CATALOG.LABELS_BRANCH_REQUIRED", "Elija la sucursal: los precios y las promociones dependen de ella.");

    public static readonly Error BranchNotFound = Error.NotFound("CATALOG.LABELS_BRANCH_NOT_FOUND", "La sucursal elegida no existe o está inactiva.");

    public static readonly Error BranchForbidden = Error.Forbidden(
        "CATALOG.LABELS_BRANCH_FORBIDDEN", "No tiene permiso para consultar los productos de esa sucursal: elija la suya o pida acceso a un administrador.");

    public static readonly Error PurchaseNotFound = Error.NotFound(
        "CATALOG.LABELS_PURCHASE_NOT_FOUND", "No se encontró una compra con ese número en la sucursal (use el número interno de la compra, no el de la factura).");

    public static readonly Error PurchaseNotPosted = Error.BusinessRule(
        "CATALOG.LABELS_PURCHASE_NOT_POSTED", "La compra todavía no está contabilizada (o fue anulada): solo se imprimen etiquetas de mercancía recibida.");

    public static readonly Error PurchaseForbidden = Error.Forbidden(
        "CATALOG.LABELS_PURCHASE_FORBIDDEN", "Para traer los productos de una compra necesita el permiso de consultar compras.");

    public static readonly Error TooManyLabels = Error.Validation(
        "CATALOG.LABELS_TOO_MANY",
        $"Máximo {LabelLayout.MaxLabels} etiquetas por archivo, {LabelTicketHandler.MaxTicketLabels} en la impresora de tiquetes y " +
        $"{LabelLayout.MaxCopies} copias por producto: divida la impresión.");

    public static readonly Error NothingToPrint = Error.Validation("CATALOG.LABELS_EMPTY", "No hay etiquetas para imprimir: agregue productos y cantidades.");

    public static Error BarcodeTooLong(IEnumerable<string> names) => Error.Validation(
        "CATALOG.LABELS_BARCODE_TOO_LONG",
        $"El código de barras de {string.Join(", ", names)} es demasiado largo para esta etiqueta y no se podría leer: elija la hoja de 14 " +
        "etiquetas o genere el código interno (EAN-13, más corto).");
}

/// <summary>
/// Sucursal de la etiqueta: la pedida (debe existir activa y el usuario debe poder consultar productos en ella), la del usuario o la del
/// equipo (igual que al leer un código en la caja).
/// </summary>
public sealed class LabelBranch(ICurrentUser currentUser, IInstallationContext installation, IWarehouseDirectory directory, IPermissionChecker permissions)
{
    public async Task<Result<Guid>> ResolveAsync(Guid? requested, CancellationToken cancellationToken)
    {
        if (requested is { } branchId)
        {
            if (!await directory.IsActiveBranchAsync(branchId, cancellationToken))
            {
                return LabelErrors.BranchNotFound;
            }

            return await permissions.HasPermissionAsync(CatalogPermissions.ProductView, branchId, cancellationToken)
                ? branchId
                : LabelErrors.BranchForbidden;
        }

        var resolved = currentUser.BranchId ?? installation.BranchId;
        return resolved is { } value ? value : LabelErrors.BranchRequired;
    }
}

/// <summary>Resultado de armar etiquetas: las que salen y cuántos productos se omitieron (descontinuados o inexistentes).</summary>
public sealed record LabelBuild(IReadOnlyList<LabelItemDto> Items, int Omitted);

/// <summary>
/// Arma las etiquetas de una lista de productos: precio al público vigente de la lista por defecto calculado como en la caja, la promoción que
/// rige hoy en la sucursal (sin las de días u horario), el precio por kilo o litro y el código de barras.
/// </summary>
public sealed class LabelBuilder(ICatalogQueries queries, ICatalogReader reader, IActivePromotions promotions, IClock clock)
{
    /// <summary>Máximo de productos distintos en una lista de etiquetas.</summary>
    public const int MaxProducts = 500;

    public async Task<LabelBuild> BuildAsync(IReadOnlyList<(Guid ProductId, int Quantity)> requested, Guid branchId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var ids = requested.Select(r => r.ProductId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new LabelBuild([], 0);
        }

        var now = clock.UtcNow;
        var products = (await queries.GetLabelProductsAsync(ids, branchId, now, cancellationToken)).ToDictionary(p => p.Id);
        var taxes = await reader.GetTaxesAsync(ids, clock.Today, cancellationToken);
        var active = (await promotions.GetActiveAsync(branchId, now, cancellationToken)).Select(ToLabelPromotion).ToList();

        var items = new List<LabelItemDto>(requested.Count);
        var omitted = 0;
        foreach (var (productId, quantity) in requested)
        {
            if (products.GetValueOrDefault(productId) is not { Status: not ProductStatus.Discontinued } product)
            {
                omitted++;
                continue;
            }

            var productTaxes = (taxes.GetValueOrDefault(productId) ?? []).Select(t => new LabelTax(t.Rate, t.FixedAmount)).ToList();
            // La lista general siempre admite promociones (ScanUseCase: DEFAULT → true), aunque haya datos viejos en false.
            items.Add(ToItem(product, productTaxes, active, quantity, clock.BusinessTimeZone));
        }

        return new LabelBuild(items, omitted);
    }

    /// <summary>Etiqueta de un producto con sus impuestos y las promociones vigentes (puro: se prueba sin base de datos).</summary>
    public static LabelItemDto ToItem(
        LabelProductData product, IReadOnlyList<LabelTax> taxes, IReadOnlyList<LabelPromotion> promotions, int quantity, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(taxes);
        ArgumentNullException.ThrowIfNull(zone);
        var weighed = product.IsSoldByScale || product.SaleMode == SaleMode.Weight;
        decimal? price = product.ListPrice is { } list ? LabelPricing.RegularPrice(list, product.IncludesTax, taxes) : null;
        var unit = weighed ? (product.BaseUnitCode == "KG" ? "kg" : product.BaseUnitCode.ToLowerInvariant()) : null;
        var offer = LabelPricing.BestOffer(
            product.Id, product.CategoryId, product.BrandId, product.ListPrice, product.IncludesTax, taxes, promotions, unit ?? "und");
        DateOnly? until = offer?.ValidTo is { } to ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to.AddTicks(-1), zone).DateTime) : null;
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
            Math.Clamp(quantity, 1, LabelLayout.MaxCopies),
            until,
            product.Status == ProductStatus.Active);
    }

    /// <summary>Contenido dibujable de una etiqueta (con la fecha si se pide).</summary>
    public static LabelContent ToContent(LabelItemDto item, DateOnly? date)
    {
        ArgumentNullException.ThrowIfNull(item);
        var hasOffer = item.PromoPrice is { } promo && (item.Price is null || promo < item.Price);
        var shown = hasOffer ? item.PromoPrice : item.Price;
        var offer = item.PromoText is { } text
            ? item.PromoValidUntil is { } until ? $"{text} hasta {until.ToString("dd/MM", CultureInfo.InvariantCulture)}" : text
            : null;
        return new LabelContent(
            item.Name,
            item.Sku,
            BarcodeSymbology.Encode(item.Barcode ?? item.Sku),
            shown is { } value ? LabelPricing.Money(value) : null,
            item.PriceUnit,
            hasOffer && item.Price is { } regular ? LabelPricing.Money(regular) : null,
            offer,
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
        d.MinQuantity,
        d.ValidTo,
        d.HasSchedule);
}

// ─────────────────────────────── Armar la lista ───────────────────────────────

/// <summary>
/// Arma la lista de etiquetas para revisarla antes de imprimir. <c>Source</c>: PRODUCTS (con <c>ProductIds</c>), CATEGORY (con
/// <c>CategoryId</c>), PRICE_CHANGES (con <c>Since</c>), PURCHASE (con <c>PurchaseNumber</c>: número interno o Id; cantidad = unidades
/// recibidas; exige consultar compras) o WITHOUT_BARCODE. <c>BranchId</c>: sucursal de los precios (por defecto la del usuario o del equipo).
/// </summary>
public sealed record PreviewLabelsQuery(string Source, IReadOnlyList<Guid>? ProductIds = null, Guid? CategoryId = null, DateOnly? Since = null,
    string? PurchaseNumber = null, Guid? BranchId = null, LabelTemplateKind? Template = null) : IQuery<LabelPreviewDto>;

internal sealed class PreviewLabelsValidator : AbstractValidator<PreviewLabelsQuery>
{
    public PreviewLabelsValidator()
    {
        RuleFor(x => x.Source).NotEmpty().MaximumLength(30);
        RuleFor(x => x.ProductIds!.Count).LessThanOrEqualTo(LabelBuilder.MaxProducts).When(x => x.ProductIds is not null)
            .WithMessage($"Máximo {LabelBuilder.MaxProducts} productos por lista.");
        RuleFor(x => x.PurchaseNumber).MaximumLength(60);
    }
}

internal sealed class PreviewLabelsHandler(
    ICatalogQueries queries, IPurchaseReceipts purchases, IPermissionChecker permissions, LabelBuilder builder, LabelBranch branches, IClock clock)
    : IQueryHandler<PreviewLabelsQuery, LabelPreviewDto>
{
    public async Task<Result<LabelPreviewDto>> Handle(PreviewLabelsQuery request, CancellationToken cancellationToken)
    {
        var branch = await branches.ResolveAsync(request.BranchId, cancellationToken);
        if (branch.IsFailure)
        {
            return branch.Error;
        }

        var branchId = branch.Value;

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
                    new LabelFilter(source, request.CategoryId, since, clock.UtcNow, branchId), LabelBuilder.MaxProducts + 1, cancellationToken);
                requested = [.. ids.Select(id => (id, 1))];
                break;

            case LabelSources.Purchase:
                if (!await permissions.HasPermissionAsync(PurchasingPermissions.PurchaseView, branchId, cancellationToken))
                {
                    return LabelErrors.PurchaseForbidden;
                }

                var purchase = await purchases.FindAsync(request.PurchaseNumber!, branchId, cancellationToken);
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
        var build = await builder.BuildAsync([.. requested.Take(LabelBuilder.MaxProducts)], branchId, cancellationToken);
        var items = build.Items.ToList();
        if (source == LabelSources.Purchase)
        {
            // Lo que se vende por peso lleva una sola etiqueta de góndola (no una por kilo).
            items = [.. items.Select(i => i.IsWeighed ? i with { Quantity = 1 } : i)];
        }

        var warnings = Warnings(items, build.Omitted, truncated);
        if (request.Template is { } kind)
        {
            warnings.AddRange(BarcodeWarnings(LabelLayout.Get(kind), items));
        }

        return new LabelPreviewDto(source, reference, items, warnings, truncated);
    }

    /// <summary>Códigos que no caben legibles en la plantilla (el PDF los rechazaría) o EAN por debajo del tamaño nominal.</summary>
    internal static IEnumerable<string> BarcodeWarnings(LabelTemplate template, IReadOnlyList<LabelItemDto> items)
    {
        var fits = items.Select(i => (i.Name, Barcode: BarcodeSymbology.Encode(i.Barcode ?? i.Sku)))
            .Where(x => x.Barcode is not null)
            .Select(x => (x.Name, Fit: LabelLayout.FitBarcode(template, x.Barcode!)))
            .ToList();
        var tooLong = fits.Where(f => f.Fit is null).Select(f => f.Name).Distinct().ToList();
        if (tooLong.Count > 0)
        {
            yield return LabelErrors.BarcodeTooLong(tooLong.Take(5)).Message;
        }

        var small = fits.Count(f => f.Fit is { BelowNominal: true });
        if (small > 0)
        {
            yield return $"En {template.Name} el código EAN de {small} producto(s) sale más pequeño de lo recomendado (barras de 0,25 mm): algunos " +
                "lectores lo leen mal. Si pasa, use el adhesivo de 50 × 30 mm o la hoja A4.";
        }
    }

    private static List<string> Warnings(List<LabelItemDto> items, int omitted, bool truncated)
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

        if (omitted > 0)
        {
            warnings.Add($"{omitted} producto(s) no se incluyeron porque están descontinuados o ya no existen.");
        }

        var inactive = items.Count(i => !i.IsActive);
        if (inactive > 0)
        {
            warnings.Add($"{inactive} producto(s) están inactivos (no se venden en la caja): revise si de verdad quiere su etiqueta.");
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
public sealed record RenderLabelsQuery(LabelTemplateKind Template, IReadOnlyList<LabelPrintItem> Items, bool ShowDate = true, Guid? BranchId = null)
    : IQuery<LabelFile>;

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

internal sealed class RenderLabelsHandler(LabelBuilder builder, LabelBranch branches, ILabelRenderer renderer, IClock clock)
    : IQueryHandler<RenderLabelsQuery, LabelFile>
{
    public async Task<Result<LabelFile>> Handle(RenderLabelsQuery request, CancellationToken cancellationToken)
    {
        var branch = await branches.ResolveAsync(request.BranchId, cancellationToken);
        if (branch.IsFailure)
        {
            return branch.Error;
        }

        var branchId = branch.Value;

        var contents = await LabelPrinting.ContentsAsync(builder, request.Items, branchId, request.ShowDate ? clock.Today : null, LabelLayout.MaxLabels,
            cancellationToken);
        if (contents.IsFailure)
        {
            return contents.Error;
        }

        var template = LabelLayout.Get(request.Template);
        var tooLong = contents.Value.Where(c => c.Barcode is { } b && LabelLayout.FitBarcode(template, b) is null)
            .Select(c => c.Name).Distinct().ToList();
        if (tooLong.Count > 0)
        {
            return LabelErrors.BarcodeTooLong(tooLong.Take(5));
        }

        var pdf = renderer.RenderPdf(template, contents.Value, "Etiquetas");
        var stamp = clock.ToBusinessTime(clock.UtcNow).ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
        return new LabelFile($"etiquetas_{stamp}.pdf", "application/pdf", pdf, contents.Value.Count, LabelLayout.PageCount(template, contents.Value.Count));
    }
}

/// <summary>
/// Etiquetas sencillas para la impresora de tiquetes de la caja (ESC/POS, papel continuo): nombre, precio y código de barras (EAN-13 o
/// CODE128 ajustado al ancho del papel), una tras otra con una línea para cortar a mano. La interfaz se lo entrega al agente de la caja.
/// </summary>
public sealed record LabelTicketQuery(IReadOnlyList<LabelPrintItem> Items, Guid? BranchId = null) : IQuery<TicketDocument>;

internal sealed class LabelTicketValidator : AbstractValidator<LabelTicketQuery>
{
    public LabelTicketValidator() => RuleFor(x => x.Items).NotNull().SetValidator(new LabelItemsValidator());
}

internal sealed class LabelTicketHandler(LabelBuilder builder, LabelBranch branches, IClock clock) : IQueryHandler<LabelTicketQuery, TicketDocument>
{
    /// <summary>En papel de tiquete se limita la cantidad (cada etiqueta gasta unos 4 cm).</summary>
    public const int MaxTicketLabels = 200;

    public async Task<Result<TicketDocument>> Handle(LabelTicketQuery request, CancellationToken cancellationToken)
    {
        var branch = await branches.ResolveAsync(request.BranchId, cancellationToken);
        if (branch.IsFailure)
        {
            return branch.Error;
        }

        var branchId = branch.Value;

        var contents = await LabelPrinting.ContentsAsync(builder, request.Items, branchId, clock.Today, MaxTicketLabels, cancellationToken);
        return contents.IsSuccess ? LabelPrinting.Ticket(contents.Value) : contents.Error;
    }
}

internal static class LabelPrinting
{
    public static async Task<Result<IReadOnlyList<LabelContent>>> ContentsAsync(
        LabelBuilder builder, IReadOnlyList<LabelPrintItem> items, Guid branchId, DateOnly? date, int max, CancellationToken cancellationToken)
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

        var built = await builder.BuildAsync(requested, branchId, cancellationToken);
        if (built.Items.Count == 0)
        {
            return CatalogErrors.ProductNotFound;
        }

        return built.Items.SelectMany(i => Enumerable.Repeat(LabelBuilder.ToContent(i, date), i.Quantity)).ToList();
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
                elements.Add(new BarcodeElement(barcode.Text, barcode.Symbol switch
                {
                    BarcodeSymbol.Ean13 => BarcodeElement.Ean13,
                    BarcodeSymbol.Ean8 => BarcodeElement.Ean8,
                    _ => BarcodeElement.Code128,
                }));
            }

            elements.Add(new SeparatorLine('-'));
        }

        elements.Add(new FeedElement(2));
        return new TicketDocument("Etiquetas", elements, Cut: true, OpenDrawer: false);
    }
}

// ─────────────────────────────── Códigos internos ───────────────────────────────

/// <summary>
/// Genera el código interno EAN-13 (prefijo 29 + nodo + consecutivo, saltando los números que ya existan) a los productos que no tienen código de
/// barras propio, para que la etiqueta lleve un código que la caja lee. Responde producto por producto: los que ya tienen código, los
/// descontinuados o los que no existen se informan sin detener el resto. Queda en la auditoría.
/// </summary>
public sealed record GenerateLabelCodesCommand(IReadOnlyList<Guid> ProductIds) : ICommand<IReadOnlyList<GeneratedLabelCodeDto>>;

internal sealed class GenerateLabelCodesValidator : AbstractValidator<GenerateLabelCodesCommand>
{
    public GenerateLabelCodesValidator() => RuleFor(x => x.ProductIds).NotNull().Must(ids => ids.Count is >= 1 and <= LabelBuilder.MaxProducts)
        .WithMessage($"Elija de 1 a {LabelBuilder.MaxProducts} productos.");
}

internal sealed class GenerateLabelCodesHandler(ICatalogStore store, ProductWriter writer, IAuditWriter audit, IIdGenerator ids)
    : ICommandHandler<GenerateLabelCodesCommand, IReadOnlyList<GeneratedLabelCodeDto>>
{
    public async Task<Result<IReadOnlyList<GeneratedLabelCodeDto>>> Handle(GenerateLabelCodesCommand request, CancellationToken cancellationToken)
    {
        var requested = request.ProductIds.Distinct().ToList();
        var products = (await store.GetProductsByIdsAsync(requested, cancellationToken)).ToDictionary(p => p.Id);
        var barcodes = (await store.GetBarcodesAsync(requested, cancellationToken)).ToLookup(b => b.ProductId);
        var results = new Dictionary<Guid, GeneratedLabelCodeDto>();
        var targets = new List<Product>();
        foreach (var id in requested)
        {
            if (products.GetValueOrDefault(id) is not { } product)
            {
                results[id] = new GeneratedLabelCodeDto(id, string.Empty, string.Empty, null, false, "El producto no existe.");
            }
            else if (product.Status == ProductStatus.Discontinued)
            {
                results[id] = new GeneratedLabelCodeDto(id, product.Sku, product.Name, null, false, "El producto está descontinuado.");
            }
            else if (barcodes[id].FirstOrDefault(b => b.PackagingId is null) is { } existing)
            {
                results[id] = new GeneratedLabelCodeDto(id, product.Sku, product.Name, existing.Code, false, "Ya tenía código de barras.");
            }
            else
            {
                targets.Add(product);
            }
        }

        if (targets.Count > 0)
        {
            // Una sola reserva para todo el lote (saltando números ya usados), sin consultas por producto.
            var codes = await writer.ReserveInternalCodesAsync(targets.Count, cancellationToken);
            if (codes.IsFailure)
            {
                return codes.Error;
            }

            for (var i = 0; i < targets.Count; i++)
            {
                var product = targets[i];
                // Sin código propio de la unidad: es principal solo si ninguna presentación tiene la marca (no se le quita a otra).
                var primary = barcodes[product.Id].All(b => !b.IsPrimary);
                var barcode = ProductBarcode.Create(ids.NewId(), product, null, codes.Value[i], primary);
                store.Add(barcode);
                results[product.Id] = new GeneratedLabelCodeDto(product.Id, product.Sku, product.Name, barcode.Code, true);
            }

            var generated = targets.Select(t => results[t.Id]).ToList();
            var detail = string.Join(", ", generated.Take(20).Select(g => $"{g.Sku} → {g.Barcode}"));
            await audit.WriteAsync(
                new AuditEntry("catalog", "PRODUCT_INTERNAL_CODES_GENERATED", "Product", generated.Count == 1 ? generated[0].ProductId : null,
                    generated.Count == 1 ? $"Producto {generated[0].Sku} · {generated[0].Name}" : $"{generated.Count} productos",
                    $"Se generaron {generated.Count} código(s) interno(s) para etiquetas: {detail}{(generated.Count > 20 ? "…" : string.Empty)}.",
                    NewValues: new Dictionary<string, object?> { ["codes"] = string.Join(", ", generated.Select(g => $"{g.Sku}={g.Barcode}")) }),
                cancellationToken);
        }

        return requested.Select(id => results[id]).ToList();
    }
}
