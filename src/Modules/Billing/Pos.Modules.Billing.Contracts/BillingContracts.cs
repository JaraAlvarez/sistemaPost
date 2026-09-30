using Pos.Application.Abstractions.Security;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Billing.Contracts;

/// <summary>Permisos del módulo Billing (docs/fases/fase-07-propuesta.md §7 y fase-11b-propuesta.md D11B-11).</summary>
public static class BillingPermissions
{
    public const string DocumentView = "billing.document.view";
    public const string DocumentManage = "billing.document.manage";
    public const string SettingsManage = "billing.settings.manage";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(DocumentView, "Consultar comprobantes y documentos fiscales con sus eventos", isSensitive: false),
        new(DocumentManage, "Reintentar el envío de documentos electrónicos", isSensitive: true),
        new(SettingsManage, "Configurar la facturación electrónica: modo, ambiente, credenciales del proveedor y rangos", isSensitive: true),
    ];
}

/// <summary>
/// Emisión pedida por ventas. <c>Source</c>: SALE, SALE_VOID o CUSTOMER_RETURN. <c>BuyerIdentificationType</c>: el tipo del
/// tercero (Consumidor final si no se identificó). <c>InvoiceRequested</c>: el cliente pidió factura (modo ON_REQUEST).
/// </summary>
public sealed record FiscalIssueRequest(
    string Source,
    Guid SourceId,
    string SourceNumber,
    Guid BranchId,
    Guid? PosTerminalId,
    DateOnly BusinessDate,
    string BuyerName,
    string BuyerIdentificationType,
    string BuyerIdentification,
    string? BuyerEmail,
    decimal Subtotal,
    decimal TaxTotal,
    decimal Total,
    Guid? RelatedSourceId = null,
    bool InvoiceRequested = false);

/// <summary>Documento soporte pedido por compras al contabilizar una compra a un proveedor no obligado a facturar.</summary>
public sealed record FiscalSupportDocumentRequest(
    Guid PurchaseId, string PurchaseNumber, Guid SupplierId, Guid BranchId, DateOnly BusinessDate, decimal Subtotal, decimal TaxTotal, decimal Total);

/// <summary>Documento visto por otros módulos (lo que se imprime en el tiquete).</summary>
public sealed record FiscalDocumentInfo(Guid Id, string DocumentType, string Status, string? FiscalNumber, string? Cufe, string? QrData);

/// <summary>
/// Facturación vista por ventas y compras (D7-12, D11B-02): emite el documento en la transacción del origen; nunca llama al
/// proveedor en línea (lo hace la cola en segundo plano).
/// </summary>
public interface IBillingService
{
    /// <summary>
    /// Comprobante de una venta, anulación o cambio. Modo OFF: comprobante interno. Modo EVERY_SALE (u ON_REQUEST si el cliente pidió
    /// factura): documento electrónico PENDING con <c>reference_code</c> = id del origen. Una nota crédito solo es electrónica si la
    /// factura que corrige lo es.
    /// </summary>
    Task<Result<FiscalDocumentInfo>> IssueAsync(FiscalIssueRequest request, CancellationToken cancellationToken = default);

    /// <summary>Documento soporte PENDING de la compra; <c>null</c> si la facturación electrónica está apagada.</summary>
    Task<Result<FiscalDocumentInfo?>> IssueSupportDocumentAsync(FiscalSupportDocumentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Anula el comprobante de una venta anulada: el interno queda anulado; la factura aún no aceptada se cancela; si ya fue aceptada
    /// (o está en envío) emite una nota crédito pendiente y la devuelve.
    /// </summary>
    Task<Result<FiscalDocumentInfo?>> VoidForSourceAsync(Guid sourceId, string reason, CancellationToken cancellationToken = default);

    Task<FiscalDocumentInfo?> GetForSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Documento de un origen distinto de la venta (para el tiquete): <c>SALE_VOID</c> (nota crédito de la anulación, con el id de la venta)
    /// o <c>CUSTOMER_RETURN</c> (nota crédito del cambio o del reintegro, con el id del cambio).
    /// </summary>
    Task<FiscalDocumentInfo?> GetBySourceAsync(Guid sourceId, string source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Para el tiquete (D11B-03): despierta la cola y espera hasta <paramref name="timeout"/> (⚙️ <c>billing.ticket_wait_seconds</c>, 3 s,
    /// si es nulo) a que el documento tenga número fiscal, CUFE y QR, o quede en un estado final. Devuelve el documento como esté al
    /// terminar la espera. Debe llamarse DESPUÉS de confirmar la transacción de la venta (la cola solo ve lo confirmado).
    /// </summary>
    Task<FiscalDocumentInfo?> WaitForFiscalDataAsync(Guid documentId, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}

public sealed record FiscalDocumentEventDto(string EventType, string? Detail, string? ProviderCode, DateTimeOffset OccurredAt);

public sealed record FiscalDocumentDto(
    Guid Id,
    string Source,
    Guid SourceId,
    string SourceNumber,
    string DocumentType,
    string Status,
    string? Provider,
    string? FiscalNumber,
    string? Cufe,
    int Attempts,
    DateOnly BusinessDate,
    string BuyerName,
    string BuyerIdentification,
    decimal Subtotal,
    decimal TaxTotal,
    decimal Total,
    DateTimeOffset IssuedAt,
    IReadOnlyList<FiscalDocumentEventDto> Events,
    string? ReferenceCode,
    string BuyerIdentificationType,
    string? BuyerEmail,
    string? QrData,
    string? PdfUrl,
    Guid? NumberingRangeId,
    string? ProviderDocumentId,
    string? ProviderStatus,
    string? RejectionMessage,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? NextAttemptAt,
    Guid? RelatedDocumentId,
    Guid BranchId,
    Guid? PosTerminalId);

/// <summary>Configuración de la facturación electrónica. Las credenciales NUNCA se devuelven: solo si están configuradas.</summary>
public sealed record BillingSettingsDto(
    string Provider,
    string ActiveAdapter,
    string Mode,
    string Environment,
    bool HasCredentials,
    DateTimeOffset? CredentialsUpdatedAt,
    DateTimeOffset? LastSyncAt,
    string? LastSyncError);

public sealed record FiscalRangeDto(
    Guid Id,
    string Provider,
    string ProviderRangeId,
    string DocumentType,
    string Prefix,
    long From,
    long To,
    long Current,
    long Remaining,
    decimal UsagePercent,
    string? ResolutionNumber,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsActive,
    bool IsUsable,
    bool NeedsAlert,
    Guid? BranchId,
    Guid? PosTerminalId,
    DateTimeOffset SyncedAt);

/// <summary>Conciliación de un día: ventas completadas/anuladas contra los documentos por estado (D11B, §6 flujo 6).</summary>
public sealed record FiscalReconciliationDayDto(
    DateOnly BusinessDate,
    int SalesCompleted,
    int SalesVoided,
    decimal SalesTotal,
    int InternalReceipts,
    int Accepted,
    int Pending,
    int Contingency,
    int Rejected,
    int Error,
    int Cancelled,
    int CreditNotes,
    int SalesWithoutDocument,
    decimal AcceptedTotal);

public sealed record FiscalRangeAlertDto(Guid RangeId, string Prefix, string DocumentType, decimal UsagePercent, DateOnly? ValidTo, string Message);

/// <summary>Alertas de facturación electrónica para el tablero (D11B-04, D11B-05, D11B-10).</summary>
public sealed record FiscalAlertsDto(
    string Mode,
    int PendingOverdue,
    int Rejected,
    int WithoutRange,
    IReadOnlyList<FiscalRangeAlertDto> Ranges,
    DateTimeOffset? OldestPendingAt)
{
    public int Count => PendingOverdue + Rejected + WithoutRange + Ranges.Count;
}
