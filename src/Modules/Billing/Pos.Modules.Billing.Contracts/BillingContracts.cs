using Pos.Application.Abstractions.Security;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Billing.Contracts;

/// <summary>Permisos del módulo Billing (docs/fases/fase-07-propuesta.md §7).</summary>
public static class BillingPermissions
{
    public const string DocumentView = "billing.document.view";
    public const string DocumentManage = "billing.document.manage";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(DocumentView, "Consultar comprobantes y documentos fiscales con sus eventos", isSensitive: false),
        new(DocumentManage, "Reintentar el envío de documentos electrónicos", isSensitive: true),
    ];
}

/// <summary>
/// Emisión pedida por ventas. <c>Source</c>: SALE, SALE_VOID o CUSTOMER_RETURN. <c>BuyerIdentificationType</c>: el tipo del
/// tercero (Consumidor final si no se identificó).
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
    Guid? RelatedSourceId = null);

/// <summary>Documento visto por otros módulos (lo que se imprime en el tiquete).</summary>
public sealed record FiscalDocumentInfo(Guid Id, string DocumentType, string Status, string? FiscalNumber, string? Cufe, string? QrData);

/// <summary>
/// Facturación vista por ventas (D7-12): emite el comprobante en la transacción de la venta; nunca llama al proveedor en
/// línea (lo hace un trabajador en segundo plano cuando la facturación electrónica esté activa, Fase 11-B).
/// </summary>
public interface IBillingService
{
    Task<Result<FiscalDocumentInfo>> IssueAsync(FiscalIssueRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Anula el comprobante de una venta anulada. Si el documento electrónico ya fue aceptado emite una nota crédito
    /// pendiente y la devuelve.
    /// </summary>
    Task<Result<FiscalDocumentInfo?>> VoidForSourceAsync(Guid sourceId, string reason, CancellationToken cancellationToken = default);

    Task<FiscalDocumentInfo?> GetForSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);
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
    IReadOnlyList<FiscalDocumentEventDto> Events);
