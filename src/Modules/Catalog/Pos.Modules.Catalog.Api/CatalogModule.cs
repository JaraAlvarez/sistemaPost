using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Infrastructure;
using Pos.Modules.Catalog.Application;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.Modules.Catalog.Infrastructure;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Catalog.Api;

/// <summary>Módulo Catalog: categorías, marcas, impuestos, productos, códigos, báscula, precios con vigencia e importación.</summary>
public sealed class CatalogModule : IModule
{
    public string Name => "catalog";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRequestHandlersFrom(typeof(ICatalogStore).Assembly);
        CatalogInfrastructureRegistration.Register(services);
        services.AddScoped<ProductWriter>();
        services.AddScoped<ICatalogSaleItems, CatalogSaleItems>();
        services.AddScoped<PriceService>();
        services.AddScoped<CatalogImportService>();
        services.AddScoped<ICompanyInitializer, CatalogInitializer>();
        services.AddSingleton<ISettingDefinitionProvider, CatalogSettingsProvider>();
        services.AddSingleton<IPermissionCatalogProvider, CatalogPermissionCatalog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/catalog").WithTags("Catálogo");
        MapMasters(group);
        MapProducts(group.MapGroup("/products"));
        MapImports(group.MapGroup("/imports"));

        group.MapGet("/scan/{code}", async (string code, Guid? branchId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ScanCodeQuery(code, branchId), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductView)
            .WithSummary("Resuelve un código leído en la caja: código de barras, etiqueta de báscula (peso o precio) o SKU");
    }

    private static void MapMasters(RouteGroupBuilder group)
    {
        group.MapGet("/units", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListUnitsQuery(), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductView);

        group.MapGet("/categories", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListCategoriesQuery(), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductView);
        group.MapPost("/categories", async (CategoryRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateCategoryCommand(r.Name, r.ParentId, r.SortOrder), ct)).ToCreatedResult(c => $"/api/v1/catalog/categories/{c.Id}"))
            .RequirePermission(CatalogPermissions.MasterManage);
        group.MapPut("/categories/{categoryId:guid}", async (Guid categoryId, CategoryRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateCategoryCommand(categoryId, r.Name, r.ParentId, r.SortOrder), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.MasterManage)
            .WithSummary("Renombra y, si cambia el padre, mueve la categoría con su subárbol");
        group.MapDelete("/categories/{categoryId:guid}", async (Guid categoryId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeleteCategoryCommand(categoryId), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.MasterManage);

        group.MapGet("/brands", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListBrandsQuery(), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductView);
        group.MapPost("/brands", async (BrandRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateBrandCommand(r.Name), ct)).ToCreatedResult(b => $"/api/v1/catalog/brands/{b.Id}"))
            .RequirePermission(CatalogPermissions.MasterManage);
        group.MapPut("/brands/{brandId:guid}", async (Guid brandId, BrandRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateBrandCommand(brandId, r.Name, r.IsActive), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.MasterManage);
        group.MapDelete("/brands/{brandId:guid}", async (Guid brandId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeleteBrandCommand(brandId), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.MasterManage);

        group.MapGet("/taxes", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListTaxesQuery(), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductView);
        group.MapPost("/taxes", async (CreateTaxCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(t => $"/api/v1/catalog/taxes/{t.Id}"))
            .RequirePermission(CatalogPermissions.TaxManage);
        group.MapPut("/taxes/{taxId:guid}", async (Guid taxId, UpdateTaxRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateTaxCommand(taxId, r.Name, r.DianCode, r.IsActive), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.TaxManage);
        group.MapPost("/taxes/{taxId:guid}/rates", async (Guid taxId, TaxRateRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetTaxRateCommand(taxId, r.Rate, r.FixedAmount, r.ValidFrom), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.TaxManage)
            .WithSummary("Programa una tarifa desde una fecha (cierra la vigente en esa fecha)");

        group.MapGet("/price-lists", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListPriceListsQuery(), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductView);
        group.MapPost("/price-lists", async (CreatePriceListCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(l => $"/api/v1/catalog/price-lists/{l.Id}"))
            .RequirePermission(CatalogPermissions.MasterManage);
        group.MapPut("/price-lists/{priceListId:guid}", async (Guid priceListId, UpdatePriceListRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdatePriceListCommand(
                    priceListId, r.Name, r.PricesIncludeTax, r.IsDefault, r.IsActive, r.AdjustmentPercent, r.RoundingIncrement, r.AllowsPromotions), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.MasterManage);

        group.MapGet("/barcode-rules", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListBarcodeRulesQuery(), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductView);
        group.MapPost("/barcode-rules", async (BarcodeRuleInput rule, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SaveBarcodeRuleCommand(null, rule), ct)).ToCreatedResult(r => $"/api/v1/catalog/barcode-rules/{r.Id}"))
            .RequirePermission(CatalogPermissions.MasterManage);
        group.MapPut("/barcode-rules/{ruleId:guid}", async (Guid ruleId, BarcodeRuleInput rule, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SaveBarcodeRuleCommand(ruleId, rule), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.MasterManage);
    }

    private static void MapProducts(RouteGroupBuilder group)
    {
        group.MapGet("/", async (string? search, Guid? categoryId, Guid? brandId, string? status, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
            {
                ProductStatus? parsed = null;
                if (status is not null)
                {
                    if (!Enum.TryParse<ProductStatus>(status.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var value))
                    {
                        return Error.Validation("CATALOG.INVALID_STATUS", "Estado inválido: ACTIVE, INACTIVE o DISCONTINUED.").ToProblem();
                    }

                    parsed = value;
                }

                return (await d.Send(new SearchProductsQuery(search, categoryId, brandId, parsed, page ?? 1, pageSize ?? 50), ct)).ToHttpResult();
            })
            .RequirePermission(CatalogPermissions.ProductView)
            .WithSummary("Busca por palabras sin tildes, SKU o código de barras exacto (paginado)");

        group.MapGet("/{productId:guid}", async (Guid productId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetProductQuery(productId), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductView);

        group.MapPost("/", async (CreateProductCommand command, IDispatcher d, CancellationToken ct) =>
                (await d.Send(command, ct)).ToCreatedResult(p => $"/api/v1/catalog/products/{p.Id}"))
            .RequirePermission(CatalogPermissions.ProductManage)
            .WithSummary("Crea el producto con su código (escrito o generado), impuestos y precio opcional");

        group.MapPut("/{productId:guid}", async (Guid productId, ProductInput product, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateProductCommand(productId, product), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductManage);

        group.MapPost("/{productId:guid}/status", async (Guid productId, ProductStatusRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeProductStatusCommand(productId, r.Status), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductManage);

        group.MapPost("/{productId:guid}/packagings", async (Guid productId, PackagingRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddPackagingCommand(productId, new PackagingInput(r.Name, r.Factor, r.IsSellable, r.IsPurchasable), r.Barcode, r.Price), ct))
                .ToCreatedResult(p => $"/api/v1/catalog/products/{p.Id}"))
            .RequirePermission(CatalogPermissions.ProductManage);
        group.MapPut("/{productId:guid}/packagings/{packagingId:guid}", async (Guid productId, Guid packagingId, PackagingInput r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdatePackagingCommand(productId, packagingId, r), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductManage);
        group.MapDelete("/{productId:guid}/packagings/{packagingId:guid}", async (Guid productId, Guid packagingId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RemovePackagingCommand(productId, packagingId), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductManage);

        group.MapPost("/{productId:guid}/barcodes", async (Guid productId, BarcodeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddBarcodeCommand(productId, r.Code, r.PackagingId, r.Type, r.Generate, r.IsPrimary), ct))
                .ToCreatedResult(b => $"/api/v1/catalog/products/{productId}"))
            .RequirePermission(CatalogPermissions.ProductManage)
            .WithSummary("Agrega un código de fábrica o genera uno interno EAN-13 (prefijo 29 + nodo)");
        group.MapDelete("/{productId:guid}/barcodes/{barcodeId:guid}", async (Guid productId, Guid barcodeId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RemoveBarcodeCommand(productId, barcodeId), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductManage);

        group.MapPut("/{productId:guid}/taxes", async (Guid productId, IReadOnlyList<ProductTaxInput> taxes, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetProductTaxesCommand(productId, taxes), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductManage);

        group.MapGet("/{productId:guid}/prices", async (Guid productId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetPriceHistoryQuery(productId), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductView)
            .WithSummary("Historial completo de precios (vigentes, programados y vencidos)");
        group.MapPost("/{productId:guid}/prices", async (Guid productId, PriceRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetPriceCommand(productId, r.Price, r.PriceListId, r.PackagingId, r.BranchId, r.ValidFrom), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.PriceManage)
            .WithSummary("Fija el precio desde ahora o lo programa (nunca sobrescribe: cierra la vigencia anterior)");
        group.MapDelete("/{productId:guid}/prices/{priceId:guid}", async (Guid productId, Guid priceId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelScheduledPriceCommand(productId, priceId), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.PriceManage)
            .WithSummary("Cancela un precio programado que aún no rige");
    }

    private static void MapImports(RouteGroupBuilder group)
    {
        group.MapGet("/templates/{kind}", (string kind) =>
                ParseKind(kind) is { } parsed
                    ? Results.File(
                        Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ImportColumns.Template(parsed))).ToArray(),
                        "text/csv; charset=utf-8",
                        parsed == ImportKind.Products ? "plantilla-productos.csv" : "plantilla-precios.csv")
                    : InvalidKind().ToProblem())
            .RequirePermission(CatalogPermissions.ImportRun)
            .WithSummary("Plantilla CSV (se abre en Excel) con las columnas y un ejemplo");

        group.MapPost("/", async (string kind, HttpRequest http, IDispatcher d, CancellationToken ct) =>
            {
                if (ParseKind(kind) is not { } parsed)
                {
                    return InvalidKind().ToProblem();
                }

                // El formulario se lee aquí (no por enlace de parámetros) para que la seguridad del endpoint se verifique antes.
                if (await UploadedFile.ReadAsync(http, ct) is not { } file)
                {
                    return UploadedFile.Missing().ToProblem();
                }

                await using var stream = file.OpenReadStream();
                return (await d.Send(new UploadImportCommand(parsed, file.FileName, stream), ct)).ToCreatedResult(b => $"/api/v1/catalog/imports/{b.Id}");
            })
            .DisableAntiforgery()
            .RequirePermission(CatalogPermissions.ImportRun)
            .WithSummary("Sube un .xlsx o .csv (products o prices) y devuelve la vista previa por fila; no cambia nada");

        group.MapGet("/{importId:guid}", async (Guid importId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetImportQuery(importId), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ImportRun);
        group.MapPost("/{importId:guid}/apply", async (Guid importId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ApplyImportCommand(importId), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ImportRun)
            .WithSummary("Aplica la importación en una transacción (solo si ninguna fila tiene errores)");
        group.MapPost("/{importId:guid}/discard", async (Guid importId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DiscardImportCommand(importId), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ImportRun);
    }

    private static ImportKind? ParseKind(string kind) => kind?.Trim().ToUpperInvariant() switch
    {
        "PRODUCTS" or "PRODUCTOS" => ImportKind.Products,
        "PRICES" or "PRECIOS" => ImportKind.Prices,
        _ => null,
    };

    private static Error InvalidKind() => Error.Validation("CATALOG.INVALID_IMPORT_KIND", "Tipo de importación inválido: products o prices.");
}

public sealed record CategoryRequest(string Name, Guid? ParentId, int SortOrder);

public sealed record BrandRequest(string Name, bool IsActive = true);

public sealed record UpdateTaxRequest(string Name, string? DianCode, bool IsActive);

public sealed record TaxRateRequest(decimal? Rate, decimal? FixedAmount, DateOnly ValidFrom);

public sealed record UpdatePriceListRequest(
    string Name, bool PricesIncludeTax, bool IsDefault, bool IsActive, decimal? AdjustmentPercent = null, decimal? RoundingIncrement = null,
    bool? AllowsPromotions = null);

public sealed record ProductStatusRequest(ProductStatus Status);

public sealed record PackagingRequest(string Name, decimal Factor, bool IsSellable = true, bool IsPurchasable = true, string? Barcode = null, decimal? Price = null);

public sealed record BarcodeRequest(string? Code, Guid? PackagingId, BarcodeType? Type, bool Generate = false, bool IsPrimary = false);

public sealed record PriceRequest(decimal Price, Guid? PriceListId, Guid? PackagingId, Guid? BranchId, DateTimeOffset? ValidFrom);
