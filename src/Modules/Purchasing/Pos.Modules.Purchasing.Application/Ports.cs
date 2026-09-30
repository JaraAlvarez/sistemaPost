using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Purchasing.Contracts;
using Pos.Modules.Purchasing.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Purchasing.Application;

/// <summary>Maestros y documentos de compras (EF Core).</summary>
public interface IPurchasingStore
{
    void Add(object entity);

    Task<Supplier?> GetSupplierAsync(Guid id, CancellationToken cancellationToken);

    Task<Supplier?> GetSupplierByPartyAsync(Guid partyId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SupplierProduct>> GetSupplierProductsAsync(Guid supplierId, CancellationToken cancellationToken);

    void Remove(SupplierProduct item);

    Task<PurchaseOrder?> GetOrderAsync(Guid id, CancellationToken cancellationToken);

    Task<Purchase?> GetPurchaseAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Compra no anulada del proveedor con ese número de factura (RN-PUR-02).</summary>
    Task<bool> InvoiceExistsAsync(Guid supplierId, string invoiceNumber, Guid? exceptPurchaseId, CancellationToken cancellationToken);

    Task<AccountPayable?> GetPayableAsync(Guid id, CancellationToken cancellationToken);

    Task<AccountPayable?> GetPayableByPurchaseAsync(Guid purchaseId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountPayable>> GetPayablesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    Task<PayablePayment?> GetPaymentAsync(Guid id, CancellationToken cancellationToken);

    Task<SupplierReturn?> GetReturnAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SupplierSchedule>> GetSchedulesAsync(Guid supplierId, CancellationToken cancellationToken);

    void Remove(SupplierSchedule entry);

    Task<IReadOnlyList<SupplierWithholdingDefault>> GetWithholdingDefaultsAsync(Guid supplierId, CancellationToken cancellationToken);

    void Remove(SupplierWithholdingDefault item);

    Task<IReadOnlyList<SupplierBankAccount>> GetBankAccountsAsync(Guid supplierId, CancellationToken cancellationToken);
}

/// <summary>Consultas de pantalla (listados, cartera, estado de cuenta).</summary>
public interface IPurchasingQueries
{
    Task<IReadOnlyList<SupplierDto>> ListSuppliersAsync(string? search, bool includeInactive, CancellationToken cancellationToken);

    Task<SupplierDto?> GetSupplierAsync(Guid supplierId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PurchasingDocumentDto>> ListDocumentsAsync(DocumentFilter filter, CancellationToken cancellationToken);

    Task<IReadOnlyList<PayableDto>> ListPayablesAsync(Guid? supplierId, bool openOnly, DateOnly asOf, bool withEntries, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, string>> SupplierNamesAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken);

    /// <summary>Compras y devoluciones del proveedor en el período, su última compra y los productos activos (Fase 8).</summary>
    Task<SupplierActivity> GetSupplierActivityAsync(Guid supplierId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>Proveedores que venden el producto (con el último costo; el caso de uso lo oculta sin permiso).</summary>
    Task<IReadOnlyList<ProductSupplierDto>> ListProductSuppliersAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>Costo neto por compra contabilizada (más reciente primero).</summary>
    Task<IReadOnlyList<CostHistoryEntryDto>> ListCostHistoryAsync(
        Guid productId, Guid? supplierId, DateOnly? from, DateOnly? to, int limit, CancellationToken cancellationToken);

    /// <summary>Catálogo de bancos (ref.banks).</summary>
    Task<IReadOnlyList<BankDto>> ListBanksAsync(bool includeInactive, CancellationToken cancellationToken);
}

/// <summary>Actividad del proveedor en un período (compras por fecha de factura, devoluciones por fecha del documento).</summary>
public sealed record SupplierActivity(
    decimal PurchasedTotal, int PurchaseCount, DateOnly? LastPurchaseDate, decimal? LastPurchaseTotal, decimal ReturnsTotal, int ReturnCount, int ActiveProducts);

/// <summary><c>Kind</c>: ORDER, PURCHASE, RETURN o PAYMENT.</summary>
public sealed record DocumentFilter(string Kind, Guid BranchId, Guid? SupplierId, string? Status, bool? RequiresSupportDocument, int Limit = 500);

/// <summary>Configuraciones de compras. REGLA: el valor por defecto de una clave publicada no se cambia.</summary>
public static class PurchasingSettings
{
    /// <summary>D5-03 / pregunta 2: el IVA de las compras es descontable (la empresa es responsable de IVA) y no es costo.</summary>
    public static readonly SettingDefinition<bool> VatDeductible = new(
        "purchasing.vat_deductible",
        true,
        SettingScope.Company,
        "El IVA de las compras es descontable (no suma al costo). Desactívelo si la empresa no es responsable de IVA.");

    /// <summary>Diferencia máxima entre el total calculado y el total de la factura para contabilizar.</summary>
    public static readonly SettingDefinition<decimal> InvoiceTotalTolerance = new(
        "purchasing.invoice_total_tolerance",
        0m,
        SettingScope.Company,
        "Diferencia máxima (en pesos) entre el total calculado de la compra y el total de la factura.",
        v => v is >= 0m and <= 10_000m ? null : "Entre 0 y 10.000.");

    /// <summary>RN-PUR-04: porcentaje que se puede recibir por encima de lo pedido.</summary>
    public static readonly SettingDefinition<decimal> ReceiptTolerancePercent = new(
        "purchasing.receipt_tolerance_percent",
        0m,
        SettingScope.Company,
        "Porcentaje que se puede recibir por encima de lo pedido en una orden de compra.",
        v => v is >= 0m and <= 100m ? null : "Entre 0 y 100.");

    /// <summary>Alerta cuando el costo neto varía más de este porcentaje frente a la última compra al mismo proveedor.</summary>
    public static readonly SettingDefinition<decimal> CostVariationAlertPercent = new(
        "purchasing.cost_variation_alert_percent",
        20m,
        SettingScope.Company,
        "Variación del costo (en %) frente a la última compra al proveedor que genera una alerta al contabilizar.",
        v => v is > 0m and <= 1000m ? null : "Mayor que 0 y hasta 1000.");

    public static IEnumerable<SettingDefinition> All => [VatDeductible, InvoiceTotalTolerance, ReceiptTolerancePercent, CostVariationAlertPercent];
}

public sealed class PurchasingSettingsProvider : ISettingDefinitionProvider
{
    public IEnumerable<SettingDefinition> GetDefinitions() => PurchasingSettings.All;
}

public sealed class PurchasingPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => PurchasingPermissions.All;
}

internal static class PurchasingContext
{
    public static readonly Error SetupRequired =
        Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");

    public static Result<(Guid CompanyId, Guid BranchId)> RequireLocal(this IInstallationContext installation) =>
        installation.CompanyId is { } company && installation.BranchId is { } branch ? (company, branch) : SetupRequired;

    public static string Db<TEnum>(this TEnum value)
        where TEnum : struct, Enum
    {
        var name = value.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }
}
