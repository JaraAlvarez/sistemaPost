using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Purchasing.Application;
using Pos.Modules.Purchasing.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Purchasing.Infrastructure;

/// <summary>
/// Mapeo del esquema purchasing. Las relaciones con clave compuesta (id, company_id) se declaran para que EF ordene los
/// INSERT (cuenta por pagar antes que la aplicación del pago de contado, proveedor antes que sus productos).
/// </summary>
internal sealed class PurchasingModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Supplier>(b =>
        {
            b.ToTable("suppliers", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.Ignore(x => x.AcceptsNewDocuments);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<SupplierProduct>(b =>
        {
            b.ToTable("supplier_products", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.SupplierId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<PurchaseOrder>(b =>
        {
            b.ToTable("purchase_orders", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.Ignore(x => x.IsReceivable);
            b.HasMany(x => x.Lines).WithOne().HasForeignKey("OrderId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Lines).HasField("_lines");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<PurchaseOrderLine>(b =>
        {
            b.ToTable("purchase_order_lines", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Ignore(x => x.PendingBaseQuantity);
        });

        modelBuilder.Entity<Purchase>(b =>
        {
            b.ToTable("purchases", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.PaymentMode).HasUpperSnakeConversion();
            b.Property(x => x.ProrationMethod).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => new { x.PurchaseOrderId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Lines).WithOne().HasForeignKey("PurchaseId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Lines).HasField("_lines");
            b.HasMany(x => x.Withholdings).WithOne().HasForeignKey("PurchaseId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Withholdings).HasField("_withholdings");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<PurchaseLine>(b =>
        {
            b.ToTable("purchase_lines", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Ignore(x => x.ReturnableBaseQuantity);
            b.HasMany(x => x.Taxes).WithOne().HasForeignKey("PurchaseLineId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Taxes).HasField("_taxes");
        });

        modelBuilder.Entity<PurchaseLineTax>(b =>
        {
            b.ToTable("purchase_line_taxes", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<PurchaseWithholding>(b =>
        {
            b.ToTable("purchase_withholdings", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasUpperSnakeConversion();
        });

        modelBuilder.Entity<AccountPayable>(b =>
        {
            b.ToTable("accounts_payable", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasAlternateKey(x => new { x.Id, x.CompanyId });
            b.Ignore(x => x.PaidAmount);
            b.HasOne<Purchase>().WithMany().HasForeignKey(x => new { x.PurchaseId, x.CompanyId })
                .HasPrincipalKey(x => new { x.Id, x.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Entries).WithOne().HasForeignKey("AccountId").IsRequired().OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Entries).HasField("_entries");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<PayableEntry>(b =>
        {
            b.ToTable("payable_entries", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.EntryType).HasUpperSnakeConversion();
        });

        modelBuilder.Entity<PayablePayment>(b =>
        {
            b.ToTable("payable_payments", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.HasMany(x => x.Allocations).WithOne().HasForeignKey("PaymentId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Allocations).HasField("_allocations");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<PaymentAllocation>(b =>
        {
            b.ToTable("payable_payment_allocations", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasOne<AccountPayable>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SupplierReturn>(b =>
        {
            b.ToTable("supplier_returns", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Property(x => x.Settlement).HasConversion(new UpperSnakeEnumConverter<ReturnSettlement>());
            b.HasMany(x => x.Lines).WithOne().HasForeignKey("ReturnId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Lines).HasField("_lines");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<SupplierReturnLine>(b =>
        {
            b.ToTable("supplier_return_lines", "purchasing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });
    }
}

internal sealed class PurchasingStore(PosDbContext context) : IPurchasingStore
{
    public void Add(object entity) => context.Add(entity);

    public Task<Supplier?> GetSupplierAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<Supplier>().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<Supplier?> GetSupplierByPartyAsync(Guid partyId, CancellationToken cancellationToken) =>
        context.Set<Supplier>().SingleOrDefaultAsync(s => s.PartyId == partyId, cancellationToken);

    public async Task<IReadOnlyList<SupplierProduct>> GetSupplierProductsAsync(Guid supplierId, CancellationToken cancellationToken) =>
        await context.Set<SupplierProduct>().Where(p => p.SupplierId == supplierId).ToListAsync(cancellationToken);

    public void Remove(SupplierProduct item) => context.Remove(item);

    public Task<PurchaseOrder?> GetOrderAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<PurchaseOrder>().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

    public Task<Purchase?> GetPurchaseAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<Purchase>().Include(p => p.Lines).ThenInclude(l => l.Taxes).Include(p => p.Withholdings).AsSplitQuery()
            .SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<bool> InvoiceExistsAsync(Guid supplierId, string invoiceNumber, Guid? exceptPurchaseId, CancellationToken cancellationToken) =>
        context.Set<Purchase>().AnyAsync(
            p => p.SupplierId == supplierId && p.SupplierInvoiceNumber == invoiceNumber && p.Status != PurchaseStatus.Voided && p.Id != exceptPurchaseId,
            cancellationToken);

    public Task<AccountPayable?> GetPayableAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<AccountPayable>().Include(a => a.Entries).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<AccountPayable?> GetPayableByPurchaseAsync(Guid purchaseId, CancellationToken cancellationToken) =>
        context.Set<AccountPayable>().Include(a => a.Entries).SingleOrDefaultAsync(a => a.PurchaseId == purchaseId, cancellationToken);

    public async Task<IReadOnlyList<AccountPayable>> GetPayablesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await context.Set<AccountPayable>().Include(a => a.Entries).Where(a => ids.Contains(a.Id)).ToListAsync(cancellationToken);

    public Task<PayablePayment?> GetPaymentAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<PayablePayment>().Include(p => p.Allocations).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<SupplierReturn?> GetReturnAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<SupplierReturn>().Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
}

/// <summary>Restricciones de la BD → errores de negocio legibles.</summary>
internal sealed class PurchasingConstraintErrors : IConstraintErrorProvider
{
    public IReadOnlyDictionary<string, Error> GetConstraintErrors() => new Dictionary<string, Error>
    {
        ["ux_suppliers__company_code"] = PurchasingErrors.SupplierDuplicated,
        ["ux_suppliers__company_party"] = PurchasingErrors.SupplierDuplicated,
        ["ux_supplier_products__supplier_product"] = PurchasingErrors.SupplierProductDuplicated,
        ["ux_supplier_products__supplier_code"] = PurchasingErrors.SupplierProductDuplicated,
        ["ux_purchases__supplier_invoice"] = PurchasingErrors.InvoiceDuplicated,
        ["ux_purchase_order_lines__product"] = PurchasingErrors.DuplicatedLine,
        ["ux_purchase_line_taxes__code"] = Error.Validation("PURCHASING.INVALID_TAX", "Un impuesto aparece dos veces en la misma línea."),
        ["ux_purchase_withholdings__kind"] = PurchasingErrors.InvalidWithholding,
        ["ux_payable_payment_allocations__account"] = PurchasingErrors.InvalidPayment,
        ["ux_supplier_return_lines__purchase_line"] = PurchasingErrors.DuplicatedLine,
        ["ck_purchase_lines__quantities"] = PurchasingErrors.ReturnExceedsPurchase,
        ["fk_suppliers__party"] = Error.NotFound("PARTIES.NOT_FOUND", "El tercero no existe."),
        ["ux_accounts_payable__purchase"] = Error.Conflict("PURCHASING.ALREADY_POSTED", "La compra ya fue contabilizada por otro usuario."),
    };
}

public static class PurchasingInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, PurchasingModelContributor>();
        services.AddSingleton<IConstraintErrorProvider, PurchasingConstraintErrors>();
        services.AddScoped<IPurchasingStore, PurchasingStore>();
        services.AddScoped<IPurchasingQueries, PurchasingQueries>();
    }
}
