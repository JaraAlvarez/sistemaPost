using Pos.SharedKernel;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Purchasing.Domain;

public enum SupplierStatus
{
    Active,

    /// <summary>No admite órdenes ni compras nuevas (sí pagos y devoluciones de lo ya comprado).</summary>
    Blocked,

    Inactive,
}

public sealed record SupplierData(
    string Code, int PaymentTermDays, Guid? PreferredPaymentMethodId, decimal? CreditLimit, bool IssuesInvoices, string? Notes);

/// <summary>
/// Rol proveedor de un tercero (D5-01): condiciones de pago y si factura electrónicamente. Si no factura (campesino,
/// persona natural no obligada), sus compras quedan marcadas para documento soporte (D5-12, Fase 11-B).
/// </summary>
[Audited("purchasing")]
public sealed class Supplier : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private Supplier(Guid id, Guid companyId, Guid partyId)
        : base(id)
    {
        CompanyId = companyId;
        PartyId = partyId;
    }

    public Guid CompanyId { get; private set; }

    public Guid PartyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public int PaymentTermDays { get; private set; }

    public Guid? PreferredPaymentMethodId { get; private set; }

    public decimal? CreditLimit { get; private set; }

    public bool IssuesInvoices { get; private set; } = true;

    public string? Notes { get; private set; }

    public SupplierStatus Status { get; private set; } = SupplierStatus.Active;

    public bool AcceptsNewDocuments => Status == SupplierStatus.Active;

    public string AuditLabel => $"Proveedor {Code}";

    public static Result<Supplier> Create(Guid id, Guid companyId, Guid partyId, SupplierData data)
    {
        var supplier = new Supplier(id, companyId, partyId);
        var result = supplier.Update(data);
        return result.IsSuccess ? supplier : result.Error;
    }

    /// <summary>Código por defecto: el número de identificación del tercero (sin DV).</summary>
    public static string DefaultCode(string identificationNumber)
    {
        var code = new string([.. (identificationNumber ?? string.Empty).ToUpperInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);
        return code.Length > 20 ? code[..20] : code;
    }

    public Result Update(SupplierData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var code = (data.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length is 0 or > 20 || !code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '_' or '-')
            || data.PaymentTermDays is < 0 or > 365 || data.CreditLimit < 0m || data.Notes?.Trim().Length > 500)
        {
            return PurchasingErrors.InvalidSupplier;
        }

        Code = code;
        PaymentTermDays = data.PaymentTermDays;
        PreferredPaymentMethodId = data.PreferredPaymentMethodId;
        CreditLimit = data.CreditLimit;
        IssuesInvoices = data.IssuesInvoices;
        Notes = string.IsNullOrWhiteSpace(data.Notes) ? null : data.Notes.Trim();
        return Result.Success();
    }

    public void SetStatus(SupplierStatus status) => Status = status;

    /// <summary>Pedido mínimo del proveedor (en pesos, antes de impuestos) y nota de la hora de corte (Fase 8, D8-14).</summary>
    public decimal? MinimumOrderAmount { get; private set; }

    public string? OrderCutoffNote { get; private set; }

    public Result SetOrderingTerms(decimal? minimumOrderAmount, string? orderCutoffNote)
    {
        if (minimumOrderAmount < 0m || !Guard.HasAtMostDecimals(minimumOrderAmount ?? 0m, 2) || orderCutoffNote?.Trim().Length > 200)
        {
            return PurchasingErrors.InvalidOrderingTerms;
        }

        MinimumOrderAmount = minimumOrderAmount;
        OrderCutoffNote = string.IsNullOrWhiteSpace(orderCutoffNote) ? null : orderCutoffNote.Trim();
        return Result.Success();
    }
}

/// <summary>Producto que suministra un proveedor: su código, presentación habitual y último costo por unidad base.</summary>
[Audited("purchasing")]
public sealed class SupplierProduct : AggregateRoot<Guid>, ICompanyOwned, ISoftDeletable, ISyncVersioned, IHasAuditLabel
{
    private SupplierProduct(Guid id, Guid companyId, Guid supplierId, Guid productId)
        : base(id)
    {
        CompanyId = companyId;
        SupplierId = supplierId;
        ProductId = productId;
    }

    public Guid CompanyId { get; private set; }

    public Guid SupplierId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid? PackagingId { get; private set; }

    public string? SupplierCode { get; private set; }

    public decimal? LastCost { get; private set; }

    public DateTimeOffset? LastPurchaseAt { get; private set; }

    public int? LeadTimeDays { get; private set; }

    public bool IsPreferred { get; private set; }

    public string AuditLabel => $"Producto de proveedor {SupplierCode ?? ProductId.ToString("D")}";

    public static Result<SupplierProduct> Create(
        Guid id, Guid companyId, Guid supplierId, Guid productId, Guid? packagingId, string? supplierCode, int? leadTimeDays, bool isPreferred)
    {
        var item = new SupplierProduct(id, companyId, supplierId, productId);
        var result = item.Update(packagingId, supplierCode, leadTimeDays, isPreferred);
        return result.IsSuccess ? item : result.Error;
    }

    public Result Update(Guid? packagingId, string? supplierCode, int? leadTimeDays, bool isPreferred)
    {
        var code = string.IsNullOrWhiteSpace(supplierCode) ? null : supplierCode.Trim().ToUpperInvariant();
        if (code is { Length: > 40 } || leadTimeDays is < 0 or > 365)
        {
            return PurchasingErrors.InvalidSupplierProduct;
        }

        PackagingId = packagingId;
        SupplierCode = code;
        LeadTimeDays = leadTimeDays;
        IsPreferred = isPreferred;
        return Result.Success();
    }

    /// <summary>Último costo neto por unidad base (lo actualiza la compra contabilizada).</summary>
    public void RecordPurchase(decimal netUnitCost, DateTimeOffset at)
    {
        LastCost = netUnitCost;
        LastPurchaseAt = at;
    }
}
