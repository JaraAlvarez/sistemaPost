using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Catalog.Application;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Catalog.Domain;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Catalog.Infrastructure;

/// <summary>
/// Mapeo del esquema catalog. Las relaciones se declaran para que EF ordene los INSERT (categoría → producto →
/// presentación → código/precio) y con claves compuestas (id, company_id) como en la BD.
/// </summary>
internal sealed class CatalogModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>(b =>
        {
            b.ToTable("categories", "catalog");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.HasOne<Category>().WithMany().HasForeignKey(x => new { x.ParentId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<Brand>(b =>
        {
            b.ToTable("brands", "catalog");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<Tax>(b =>
        {
            b.ToTable("taxes", "catalog");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
            b.Property(x => x.Calculation).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.Ignore(x => x.IsVat);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<TaxRate>(b =>
        {
            b.ToTable("tax_rates", "catalog");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasOne<Tax>().WithMany().HasForeignKey(x => new { x.TaxId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products", "catalog");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.SaleMode).HasUpperSnakeConversion();
            b.Property(x => x.ProductType).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Ignore(x => x.IsStockable);
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.HasOne<Category>().WithMany().HasForeignKey(x => new { x.CategoryId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Brand>().WithMany().HasForeignKey(x => new { x.BrandId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<ProductPackaging>(b =>
        {
            b.ToTable("product_packagings", "catalog");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasAlternateKey(x => new { x.Id, x.ProductId });
            b.HasOne<Product>().WithMany().HasForeignKey(x => new { x.ProductId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<ProductBarcode>(b =>
        {
            b.ToTable("product_barcodes", "catalog");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.CodeType).HasUpperSnakeConversion();
            b.HasOne<Product>().WithMany().HasForeignKey(x => new { x.ProductId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProductPackaging>().WithMany().HasForeignKey(x => new { x.PackagingId, x.ProductId })
                .HasPrincipalKey(x => new { x.Id, x.ProductId }).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<ProductTax>(b =>
        {
            b.ToTable("product_taxes", "catalog");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasOne<Product>().WithMany().HasForeignKey(x => new { x.ProductId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Tax>().WithMany().HasForeignKey(x => new { x.TaxId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<VariableBarcodeRule>(b =>
        {
            b.ToTable("variable_barcode_rules", "catalog");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Content).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<PriceList>(b =>
        {
            b.ToTable("price_lists", "catalog");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<ProductPrice>(b =>
        {
            b.ToTable("product_prices", "catalog");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasOne<PriceList>().WithMany().HasForeignKey(x => new { x.PriceListId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Product>().WithMany().HasForeignKey(x => new { x.ProductId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProductPackaging>().WithMany().HasForeignKey(x => new { x.PackagingId, x.ProductId })
                .HasPrincipalKey(x => new { x.Id, x.ProductId }).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<ImportBatchRecord>(b =>
        {
            b.ToTable("import_batches", "catalog");
            b.HasKey(x => x.Id);
            b.HasMany(x => x.Rows).WithOne().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ImportRowRecord>(b =>
        {
            b.ToTable("import_rows", "catalog");
            b.HasKey(x => x.Id);
            b.Property(x => x.Data).HasColumnType("jsonb");
            b.Property(x => x.Errors).HasColumnType("jsonb");
        });
    }
}

/// <summary>Lote de importación (estado local del nodo; no se audita fila por fila, sí el resultado).</summary>
internal sealed class ImportBatchRecord : ICompanyOwned
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public int TotalRows { get; set; }

    public int CreateRows { get; set; }

    public int UpdateRows { get; set; }

    public int UnchangedRows { get; set; }

    public int ErrorRows { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }

    public Guid? AppliedBy { get; set; }

    public List<ImportRowRecord> Rows { get; set; } = [];
}

internal sealed class ImportRowRecord
{
    public Guid Id { get; set; }

    public Guid BatchId { get; set; }

    public int RowNumber { get; set; }

    public string Action { get; set; } = string.Empty;

    public JsonDocument Data { get; set; } = JsonDocument.Parse("{}");

    /// <summary><c>{"key": "...", "errors": [...], "warnings": [...]}</c>.</summary>
    public JsonDocument Errors { get; set; } = JsonDocument.Parse("{}");
}

internal sealed class CatalogImportRepository(PosDbContext context, Pos.SharedKernel.Identifiers.IIdGenerator ids) : ICatalogImportRepository
{
    public void Add(ImportBatchData batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var record = new ImportBatchRecord
        {
            Id = batch.Id,
            CompanyId = batch.CompanyId,
            Kind = batch.Kind,
            FileName = batch.FileName.Length > 200 ? batch.FileName[..200] : batch.FileName,
            Status = batch.Status,
            CreatedAt = batch.CreatedAt,
            CreatedBy = batch.CreatedBy,
            Rows = [.. batch.Rows.Select(r => new ImportRowRecord
            {
                Id = ids.NewId(),
                BatchId = batch.Id,
                RowNumber = r.RowNumber,
                Action = r.Action,
                Data = JsonDocument.Parse(r.Data.GetRawText()),
                Errors = JsonSerializer.SerializeToDocument(new { key = r.Key, errors = r.Errors, warnings = r.Warnings }),
            })],
        };
        Count(record, batch);
        context.Add(record);
    }

    public async Task<ImportBatchData?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await context.Set<ImportBatchRecord>().Include(b => b.Rows).AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (record is null)
        {
            return null;
        }

        var batch = new ImportBatchData
        {
            Id = record.Id,
            CompanyId = record.CompanyId,
            Kind = record.Kind,
            FileName = record.FileName,
            Status = record.Status,
            CreatedAt = record.CreatedAt,
            CreatedBy = record.CreatedBy,
            AppliedAt = record.AppliedAt,
            AppliedBy = record.AppliedBy,
        };
        foreach (var row in record.Rows.OrderBy(r => r.RowNumber))
        {
            var meta = row.Errors.RootElement;
            batch.Rows.Add(new ImportRowData(
                row.RowNumber, row.Action, meta.TryGetProperty("key", out var key) ? key.GetString() ?? string.Empty : string.Empty,
                row.Data.RootElement.Clone(), Strings(meta, "errors"), Strings(meta, "warnings")));
        }

        return batch;
    }

    public async Task UpdateStatusAsync(ImportBatchData batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await context.Set<ImportBatchRecord>().Where(b => b.Id == batch.Id).ExecuteUpdateAsync(
            s => s.SetProperty(b => b.Status, batch.Status).SetProperty(b => b.AppliedAt, batch.AppliedAt).SetProperty(b => b.AppliedBy, batch.AppliedBy),
            cancellationToken);
    }

    private static void Count(ImportBatchRecord record, ImportBatchData batch)
    {
        record.TotalRows = batch.Rows.Count;
        record.CreateRows = batch.Rows.Count(r => r.Action == "CREATE");
        record.UpdateRows = batch.Rows.Count(r => r.Action == "UPDATE");
        record.UnchangedRows = batch.Rows.Count(r => r.Action == "UNCHANGED");
        record.ErrorRows = batch.Rows.Count(r => r.Action == "ERROR");
    }

    private static List<string> Strings(JsonElement element, string property) =>
        element.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
            : [];
}

internal sealed class CatalogStore(PosDbContext context) : ICatalogStore
{
    public void Add(Category category) => context.Add(category);

    public void Add(Brand brand) => context.Add(brand);

    public void Add(Tax tax) => context.Add(tax);

    public void Add(TaxRate rate) => context.Add(rate);

    public void Add(Product product) => context.Add(product);

    public void Add(ProductPackaging packaging) => context.Add(packaging);

    public void Add(ProductBarcode barcode) => context.Add(barcode);

    public void Add(ProductTax productTax) => context.Add(productTax);

    public void Add(PriceList priceList) => context.Add(priceList);

    public void Add(ProductPrice price) => context.Add(price);

    public void Add(VariableBarcodeRule rule) => context.Add(rule);

    public void Remove(object entity) => context.Remove(entity);

    public Task<Category?> GetCategoryAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<Category>().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken) =>
        await context.Set<Category>().ToListAsync(cancellationToken);

    public async Task<bool> CategoryInUseAsync(Guid id, CancellationToken cancellationToken) =>
        await context.Set<Category>().AnyAsync(c => c.ParentId == id, cancellationToken)
        || await context.Set<Product>().AnyAsync(p => p.CategoryId == id, cancellationToken);

    public Task<Brand?> GetBrandAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<Brand>().SingleOrDefaultAsync(b => b.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Brand>> GetBrandsAsync(CancellationToken cancellationToken) => await context.Set<Brand>().ToListAsync(cancellationToken);

    public Task<bool> BrandInUseAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<Product>().AnyAsync(p => p.BrandId == id, cancellationToken);

    public Task<Tax?> GetTaxAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<Tax>().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Tax>> GetTaxesAsync(CancellationToken cancellationToken) => await context.Set<Tax>().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TaxRate>> GetTaxRatesAsync(Guid taxId, CancellationToken cancellationToken) =>
        await context.Set<TaxRate>().Where(r => r.TaxId == taxId).ToListAsync(cancellationToken);

    public Task<PriceList?> GetPriceListAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<PriceList>().SingleOrDefaultAsync(l => l.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PriceList>> GetPriceListsAsync(CancellationToken cancellationToken) =>
        await context.Set<PriceList>().ToListAsync(cancellationToken);

    public Task<Product?> GetProductAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<Product>().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Product?> GetProductBySkuAsync(string sku, CancellationToken cancellationToken) =>
        context.Set<Product>().SingleOrDefaultAsync(p => p.Sku == sku, cancellationToken);

    public async Task<IReadOnlyList<ProductPackaging>> GetPackagingsAsync(Guid productId, CancellationToken cancellationToken) =>
        await context.Set<ProductPackaging>().Where(p => p.ProductId == productId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProductBarcode>> GetBarcodesAsync(Guid productId, CancellationToken cancellationToken) =>
        await context.Set<ProductBarcode>().Where(b => b.ProductId == productId).ToListAsync(cancellationToken);

    public Task<ProductBarcode?> FindBarcodeAsync(string normalizedCode, CancellationToken cancellationToken) =>
        context.Set<ProductBarcode>().FirstOrDefaultAsync(b => b.NormalizedCode == normalizedCode, cancellationToken);

    public async Task<IReadOnlyList<ProductBarcode>> GetBarcodesAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
        await context.Set<ProductBarcode>().Where(b => productIds.Contains(b.ProductId)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProductTax>> GetProductTaxesAsync(Guid productId, CancellationToken cancellationToken) =>
        await context.Set<ProductTax>().Where(t => t.ProductId == productId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProductPrice>> GetPricesAsync(
        Guid priceListId, Guid productId, Guid? packagingId, Guid? branchId, CancellationToken cancellationToken) =>
        await context.Set<ProductPrice>()
            .Where(p => p.PriceListId == priceListId && p.ProductId == productId && p.PackagingId == packagingId && p.BranchId == branchId)
            .ToListAsync(cancellationToken);

    public Task<ProductPrice?> GetPriceAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<ProductPrice>().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<VariableBarcodeRule>> GetBarcodeRulesAsync(CancellationToken cancellationToken) =>
        await context.Set<VariableBarcodeRule>().ToListAsync(cancellationToken);

    public async Task<UnitInfo?> GetUnitAsync(string code, CancellationToken cancellationToken) =>
        (await GetUnitsAsync(cancellationToken)).SingleOrDefault(u => u.Code == code);

    public async Task<IReadOnlyList<UnitInfo>> GetUnitsAsync(CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<UnitRow>($"""SELECT code, dimension FROM ref.units_of_measure""")
            .ToListAsync(cancellationToken))
        .Select(u => new UnitInfo(u.Code, u.Dimension)).ToList();

    public Task<bool> SkuExistsAsync(string sku, Guid? exceptProductId, CancellationToken cancellationToken) =>
        context.Set<Product>().AnyAsync(p => p.Sku == sku && p.Id != exceptProductId, cancellationToken);

    public Task<bool> PluExistsAsync(string plu, Guid? exceptProductId, CancellationToken cancellationToken) =>
        context.Set<Product>().AnyAsync(p => p.PluCode == plu && p.Id != exceptProductId, cancellationToken);

    public async Task<IReadOnlyList<Product>> GetProductsBySkusAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken) =>
        skus.Count == 0 ? [] : await context.Set<Product>().Where(p => skus.Contains(p.Sku)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Product>> GetProductsByIdsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
        productIds.Count == 0 ? [] : await context.Set<Product>().Where(p => productIds.Contains(p.Id)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProductBarcode>> FindBarcodesAsync(IReadOnlyCollection<string> normalizedCodes, CancellationToken cancellationToken) =>
        normalizedCodes.Count == 0 ? [] : await context.Set<ProductBarcode>().Where(b => normalizedCodes.Contains(b.NormalizedCode)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProductPackaging>> GetPackagingsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
        productIds.Count == 0 ? [] : await context.Set<ProductPackaging>().Where(p => productIds.Contains(p.ProductId)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProductTax>> GetProductTaxesAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
        productIds.Count == 0 ? [] : await context.Set<ProductTax>().Where(t => productIds.Contains(t.ProductId)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProductPrice>> GetPricesAsync(Guid priceListId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
        productIds.Count == 0
            ? []
            : await context.Set<ProductPrice>().Where(p => p.PriceListId == priceListId && productIds.Contains(p.ProductId)).ToListAsync(cancellationToken);

    public async Task<long> NextCodeSequenceAsync(Guid nodeId, string kind, CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<long>($"""
            INSERT INTO catalog.code_sequences (node_id, kind, last_value) VALUES ({nodeId}, {kind}, 1)
            ON CONFLICT (node_id, kind) DO UPDATE SET last_value = catalog.code_sequences.last_value + 1
            RETURNING last_value AS "Value"
            """).ToListAsync(cancellationToken)).Single();

    private sealed record UnitRow(string Code, string Dimension);
}

/// <summary>Restricciones de la BD → errores de negocio legibles (ninguna violación llega como 500).</summary>
internal sealed class CatalogConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_products__company_sku"] = CatalogErrors.SkuDuplicated,
        ["ux_products__company_plu"] = CatalogErrors.PluDuplicated,
        ["ux_product_barcodes__company_code"] = CatalogErrors.BarcodeDuplicated,
        ["ux_product_barcodes__product_primary"] = Error.Conflict("CATALOG.PRIMARY_BARCODE_DUPLICATED", "El producto ya tiene un código principal."),
        ["ux_categories__parent_name"] = CatalogErrors.CategoryNameDuplicated,
        ["ux_brands__company_name"] = CatalogErrors.BrandNameDuplicated,
        ["ux_taxes__company_code"] = CatalogErrors.TaxCodeDuplicated,
        ["ux_price_lists__company_code"] = CatalogErrors.PriceListCodeDuplicated,
        ["ux_price_lists__company_default"] = Error.Conflict("CATALOG.DEFAULT_PRICE_LIST_DUPLICATED", "Ya hay otra lista de precios predeterminada."),
        ["ux_product_packagings__product_name"] = CatalogErrors.PackagingNameDuplicated,
        ["ux_product_taxes__product_tax"] = CatalogErrors.ProductTaxDuplicated,
        ["ux_variable_barcode_rules__company_prefix"] = CatalogErrors.BarcodeRulePrefixDuplicated,
        ["ex_product_prices__no_overlap"] = CatalogErrors.PeriodOverlap,
        ["ex_tax_rates__no_overlap"] = CatalogErrors.PeriodOverlap,
        ["fk_products__base_unit"] = CatalogErrors.UnitNotFound,
        ["fk_products__net_content_unit"] = CatalogErrors.UnitNotFound,
        ["fk_products__category"] = CatalogErrors.CategoryNotFound,
        ["fk_products__brand"] = CatalogErrors.BrandNotFound,
        ["ck_code_sequences__last_value"] = CatalogErrors.InternalCodesExhausted,
        ["ck_products__plu"] = CatalogErrors.InvalidPlu,
        ["ck_products__sku"] = CatalogErrors.InvalidSku,
    };
}

public static class CatalogInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, CatalogModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, CatalogConstraintErrors>();
        services.AddScoped<ICatalogStore, CatalogStore>();
        services.AddScoped<ICatalogQueries, CatalogQueries>();
        services.AddScoped<ICatalogImportRepository, CatalogImportRepository>();
        services.AddScoped<ICatalogReader, CatalogReader>();
    }
}
