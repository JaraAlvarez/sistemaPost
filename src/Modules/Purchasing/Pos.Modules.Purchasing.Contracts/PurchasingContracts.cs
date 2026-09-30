using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Purchasing.Contracts;

/// <summary>Permisos del módulo Purchasing (docs/fases/fase-05-propuesta.md §7).</summary>
public static class PurchasingPermissions
{
    public const string SupplierManage = "purchasing.supplier.manage";
    public const string OrderManage = "purchasing.order.manage";
    public const string OrderApprove = "purchasing.order.approve";
    public const string PurchaseView = "purchasing.purchase.view";
    public const string PurchaseManage = "purchasing.purchase.manage";
    public const string PurchasePost = "purchasing.purchase.post";
    public const string PurchaseVoid = "purchasing.purchase.void";
    public const string PayableView = "purchasing.payable.view";
    public const string PayablePay = "purchasing.payable.pay";
    public const string ReturnManage = "purchasing.return.manage";

    /// <summary>Registrar, modificar y verificar cuentas bancarias de proveedores (Fase 8, RN-PUR-09): solo propietario y administrador.</summary>
    public const string SupplierBankManage = "purchasing.supplier.bank_manage";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(SupplierManage, "Crear y modificar proveedores y los productos que suministran", isSensitive: false),
        new(OrderManage, "Crear, enviar y cerrar órdenes de compra", isSensitive: false),
        new(OrderApprove, "Aprobar órdenes de compra", isSensitive: true),
        new(PurchaseView, "Consultar proveedores, órdenes, compras y devoluciones", isSensitive: false),
        new(PurchaseManage, "Registrar compras en borrador (factura, líneas, lotes, cargos y retenciones)", isSensitive: false),
        new(PurchasePost, "Contabilizar compras (entran al inventario y a la cartera)", isSensitive: true),
        new(PurchaseVoid, "Anular compras contabilizadas", isSensitive: true),
        new(PayableView, "Consultar cuentas por pagar, pagos y cartera por edades", isSensitive: true),
        new(PayablePay, "Registrar y anular pagos a proveedores", isSensitive: true),
        new(ReturnManage, "Registrar, contabilizar y liquidar devoluciones a proveedor", isSensitive: true),
        new(SupplierBankManage, "Registrar, modificar y verificar cuentas bancarias de proveedores (admite autorización de supervisor)", isSensitive: true),
    ];
}

public sealed record SupplierDto(
    Guid Id,
    Guid PartyId,
    string Code,
    string Name,
    string Identification,
    int PaymentTermDays,
    Guid? PreferredPaymentMethodId,
    decimal? CreditLimit,
    bool IssuesInvoices,
    string? Notes,
    string Status,
    decimal OpenBalance);

public sealed record SupplierProductDto(
    Guid Id,
    Guid SupplierId,
    Guid ProductId,
    string Sku,
    string ProductName,
    Guid? PackagingId,
    string? SupplierCode,
    decimal? LastCost,
    DateTimeOffset? LastPurchaseAt,
    int? LeadTimeDays,
    bool IsPreferred);

public sealed record OrderLineDto(
    Guid Id,
    int LineNumber,
    Guid ProductId,
    string Sku,
    string ProductName,
    Guid? PackagingId,
    decimal Factor,
    decimal Quantity,
    decimal BaseQuantity,
    decimal UnitCost,
    decimal ReceivedBaseQuantity,
    decimal PendingBaseQuantity);

public sealed record PurchaseOrderDto(
    Guid Id,
    string Number,
    Guid SupplierId,
    string SupplierName,
    Guid WarehouseId,
    DateOnly OrderDate,
    DateOnly? ExpectedDate,
    string Status,
    string? Notes,
    decimal Total,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? SentAt,
    IReadOnlyList<OrderLineDto> Lines);

public sealed record PurchaseLineTaxDto(string TaxCode, bool IsVat, decimal? Rate, decimal? FixedAmount, decimal Base, decimal Amount, bool IsDeductible);

public sealed record PurchaseLineDto(
    Guid Id,
    int LineNumber,
    Guid ProductId,
    string Sku,
    string ProductName,
    Guid? PackagingId,
    decimal Factor,
    decimal Quantity,
    decimal BaseQuantity,
    decimal UnitCost,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal ChargesAmount,
    decimal TaxAmount,
    decimal NonDeductibleTax,
    decimal LineTotal,
    decimal NetUnitCost,
    string? LotNumber,
    DateOnly? ExpiryDate,
    Guid? OrderLineId,
    decimal ReturnedBaseQuantity,
    IReadOnlyList<PurchaseLineTaxDto> Taxes);

public sealed record WithholdingDto(string Kind, decimal Base, decimal? Rate, decimal Amount);

/// <summary>
/// Alerta al contabilizar. <c>Kind</c>: PRICE_BELOW_COST (el precio de venta sin impuestos quedó por debajo del nuevo
/// costo) o COST_VARIATION (el costo varió más del umbral ⚙️ frente a la última compra al proveedor).
/// </summary>
public sealed record PurchaseAlertDto(Guid ProductId, string Sku, string ProductName, string Kind, string Message, decimal NewCost, decimal? Reference);

public sealed record PurchaseDto(
    Guid Id,
    string Number,
    Guid SupplierId,
    string SupplierName,
    Guid WarehouseId,
    Guid? PurchaseOrderId,
    string SupplierInvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly BusinessDate,
    DateOnly DueDate,
    string PaymentMode,
    Guid? PaymentMethodId,
    string? PaymentReference,
    bool RequiresSupportDocument,
    decimal? InvoiceTotal,
    string ProrationMethod,
    decimal ChargesTotal,
    string? ChargesNotes,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal DeductibleTaxTotal,
    decimal WithholdingTotal,
    decimal Total,
    decimal PayableTotal,
    string Status,
    string? Notes,
    DateTimeOffset? PostedAt,
    DateTimeOffset? VoidedAt,
    string? VoidReason,
    IReadOnlyList<PurchaseLineDto> Lines,
    IReadOnlyList<WithholdingDto> Withholdings,
    IReadOnlyList<PurchaseAlertDto> Alerts);

// ─────────────────────────────── Fase 8 (8.4): mejoras de proveedores ───────────────────────────────

/// <summary>
/// Ficha resumen del proveedor en el período (compras contabilizadas por fecha de factura, devoluciones contabilizadas o
/// liquidadas por fecha del documento) y su cartera a la fecha. <c>PrimaryBankAccountStatus</c>: estado de la cuenta
/// principal activa (null si no tiene).
/// </summary>
public sealed record SupplierSummaryDto(
    Guid SupplierId,
    string Code,
    string Name,
    string Status,
    DateOnly From,
    DateOnly To,
    decimal PurchasedTotal,
    int PurchaseCount,
    DateOnly? LastPurchaseDate,
    decimal? LastPurchaseTotal,
    decimal ReturnsTotal,
    int ReturnCount,
    decimal Balance,
    decimal OverdueBalance,
    int OverdueCount,
    int ActiveProducts,
    int PaymentTermDays,
    decimal? MinimumOrderAmount,
    string? PrimaryBankAccountStatus);

/// <summary>"¿Quién me vende esto?": proveedores del producto (último costo solo con inventory.cost.view).</summary>
public sealed record ProductSupplierDto(
    Guid SupplierId,
    string SupplierCode,
    string SupplierName,
    string SupplierStatus,
    string? SupplierProductCode,
    Guid? PackagingId,
    decimal? LastCost,
    DateTimeOffset? LastPurchaseAt,
    int? LeadTimeDays,
    bool IsPreferred);

/// <summary>Costo neto por unidad base de un producto en cada compra contabilizada.</summary>
public sealed record CostHistoryEntryDto(
    Guid PurchaseId,
    string PurchaseNumber,
    string SupplierInvoiceNumber,
    DateOnly InvoiceDate,
    Guid SupplierId,
    string SupplierName,
    Guid? PackagingId,
    decimal Factor,
    decimal Quantity,
    decimal BaseQuantity,
    decimal UnitCost,
    decimal DiscountAmount,
    decimal NetUnitCost);

/// <summary>Entrada de la agenda. <c>DayOfWeek</c>: 1 = lunes … 7 = domingo; <c>Kind</c>: VISIT, ORDER o DELIVERY; sin sucursal = todas.</summary>
public sealed record ScheduleEntryDto(Guid Id, int DayOfWeek, string DayName, string Kind, Guid? BranchId, string? Notes, DateOnly NextDate);

public sealed record SupplierScheduleDto(Guid SupplierId, decimal? MinimumOrderAmount, string? OrderCutoffNote, IReadOnlyList<ScheduleEntryDto> Entries);

/// <summary>Cuenta bancaria del proveedor. <c>Status</c>: PENDING_VERIFICATION, VERIFIED o INACTIVE.</summary>
public sealed record SupplierBankAccountDto(
    Guid Id,
    Guid SupplierId,
    string BankCode,
    string BankName,
    string AccountType,
    string AccountNumber,
    string MaskedNumber,
    string HolderName,
    string HolderIdentificationType,
    string HolderIdentificationNumber,
    string Status,
    bool IsPrimary,
    DateTimeOffset ChangedAt,
    Guid ChangedBy,
    DateTimeOffset? VerifiedAt,
    Guid? VerifiedBy);

public sealed record BankDto(string Code, string Name);

/// <summary>Retención sugerida del proveedor. <c>Kind</c>: RETEFUENTE, RETEIVA o RETEICA; tarifa en %.</summary>
public sealed record SupplierWithholdingDefaultDto(string Kind, decimal Rate, string? Concept);

/// <summary>Vencimientos para el tablero: cuentas vencidas y las que vencen en los próximos <c>Days</c> días.</summary>
public sealed record DuePayablesDto(
    DateOnly AsOf, int Days, decimal OverdueTotal, decimal DueSoonTotal, IReadOnlyList<PayableDto> Overdue, IReadOnlyList<PayableDto> DueSoon);

/// <summary>Advertencia que no bloquea la operación (p. ej. PURCHASING.BANK_ACCOUNT_UNVERIFIED en un pago).</summary>
public sealed record OperationWarningDto(string Code, string Message);

/// <summary>Documento en un listado. <c>Kind</c>: ORDER, PURCHASE, RETURN o PAYMENT.</summary>
public sealed record PurchasingDocumentDto(
    Guid Id, string Kind, string Number, Guid SupplierId, string SupplierName, string Status, DateOnly Date, decimal Total, string? Reference, DateTimeOffset CreatedAt);

public sealed record PayableEntryDto(Guid Id, string EntryType, decimal Amount, decimal BalanceAfter, string SourceType, Guid SourceId, string? SourceNumber, DateTimeOffset OccurredAt);

public sealed record PayableDto(
    Guid Id,
    Guid SupplierId,
    string SupplierName,
    Guid PurchaseId,
    string DocumentNumber,
    DateOnly IssueDate,
    DateOnly DueDate,
    decimal OriginalAmount,
    decimal Balance,
    string Status,
    int DaysOverdue,
    string AgingBucket,
    IReadOnlyList<PayableEntryDto> Entries);

public sealed record AgingRowDto(
    Guid SupplierId, string SupplierCode, string SupplierName, decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90, decimal Total);

public sealed record AgingReportDto(DateOnly AsOf, IReadOnlyList<AgingRowDto> Rows, AgingRowDto Totals);

public sealed record SupplierStatementDto(Guid SupplierId, string SupplierName, decimal Balance, IReadOnlyList<PayableDto> Accounts, IReadOnlyList<PurchasingDocumentDto> Payments);

public sealed record AllocationDto(Guid AccountId, string DocumentNumber, decimal Amount);

public sealed record PaymentDto(
    Guid Id,
    string Number,
    Guid SupplierId,
    string SupplierName,
    DateOnly PaymentDate,
    Guid PaymentMethodId,
    string? Reference,
    decimal Amount,
    string Status,
    string? Notes,
    string? VoidReason,
    Guid? CashSessionId,
    IReadOnlyList<AllocationDto> Allocations,
    IReadOnlyList<OperationWarningDto>? Warnings = null);

public sealed record SupplierReturnLineDto(
    Guid Id, Guid PurchaseLineId, Guid ProductId, string Sku, string ProductName, decimal BaseQuantity, decimal UnitCost, decimal Total, decimal CreditAmount, Guid? LotId);

public sealed record SupplierReturnDto(
    Guid Id,
    string Number,
    Guid PurchaseId,
    Guid SupplierId,
    string SupplierName,
    Guid WarehouseId,
    DateOnly BusinessDate,
    string Reason,
    string Status,
    decimal Total,
    decimal CreditTotal,
    string? Settlement,
    string? SettlementReference,
    DateTimeOffset? PostedAt,
    DateTimeOffset? SettledAt,
    IReadOnlyList<SupplierReturnLineDto> Lines);
