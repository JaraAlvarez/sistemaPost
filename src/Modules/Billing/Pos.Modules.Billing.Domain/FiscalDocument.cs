using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Billing.Domain;

/// <summary>Documento de origen del comprobante.</summary>
public enum FiscalSource
{
    Sale,
    SaleVoid,
    CustomerReturn,
}

public enum FiscalDocumentType
{
    /// <summary>Tiquete de venta impreso, no electrónico (decisión del propietario en la Fase 7).</summary>
    InternalReceipt,

    /// <summary>Documento equivalente electrónico POS (Fase 11-B).</summary>
    PosElectronic,

    InvoiceElectronic,

    CreditNote,
}

public enum FiscalStatus
{
    /// <summary>Comprobante interno: no se envía a la DIAN.</summary>
    NotRequired,

    Pending,
    Submitting,
    Accepted,
    Rejected,
    Contingency,
    Error,
    Voided,
}

public static class BillingErrors
{
    public static readonly Error NotFound = Error.NotFound("BILLING.DOCUMENT_NOT_FOUND", "El documento no existe.");

    public static readonly Error NotRetryable = Error.BusinessRule(
        "BILLING.NOT_RETRYABLE", "Solo se reintenta un documento electrónico pendiente, con error o rechazado.");

    public static readonly Error InvalidStatus = Error.BusinessRule("BILLING.INVALID_STATUS", "El documento no está en un estado que permita esta acción.");
}

/// <summary>Evento de un documento (solo inserción): emisión, envío, respuesta, reintento, anulación.</summary>
public sealed class FiscalDocumentEvent : Entity<Guid>
{
    private FiscalDocumentEvent(Guid id)
        : base(id)
    {
    }

    public string EventType { get; private set; } = string.Empty;

    public string? Detail { get; private set; }

    public string? ProviderCode { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid? UserId { get; private set; }

    internal static FiscalDocumentEvent Create(Guid id, string type, string? detail, string? providerCode, DateTimeOffset now, Guid? userId) =>
        new(id) { EventType = type, Detail = detail is { Length: > 1000 } ? detail[..1000] : detail, ProviderCode = providerCode, OccurredAt = now, UserId = userId };
}

/// <summary>Datos de emisión: documento de origen, comprador y totales (snapshot).</summary>
public sealed record FiscalIssue(
    FiscalSource Source,
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
    Guid? RelatedDocumentId = null);

/// <summary>
/// Comprobante o documento fiscal de una venta, anulación o cambio (D7-12). La venta nunca lo espera: hoy es un
/// comprobante interno (<c>NOT_REQUIRED</c>); con la facturación electrónica (11-B) nace <c>PENDING</c> y el proveedor
/// (Factus) le asigna número fiscal, CUFE/CUDE y QR.
/// </summary>
public sealed class FiscalDocument : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private readonly List<FiscalDocumentEvent> _events = [];

    private FiscalDocument(Guid id, Guid companyId)
        : base(id)
    {
        CompanyId = companyId;
    }

    public Guid CompanyId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid? PosTerminalId { get; private set; }

    public FiscalSource Source { get; private set; }

    public Guid SourceId { get; private set; }

    public string SourceNumber { get; private set; } = string.Empty;

    public FiscalDocumentType DocumentType { get; private set; }

    public FiscalStatus Status { get; private set; }

    public string? Provider { get; private set; }

    public string? FiscalNumber { get; private set; }

    public string? Cufe { get; private set; }

    public string? QrData { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public Guid? RelatedDocumentId { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public string BuyerName { get; private set; } = string.Empty;

    public string BuyerIdentificationType { get; private set; } = string.Empty;

    public string BuyerIdentification { get; private set; } = string.Empty;

    public string? BuyerEmail { get; private set; }

    public decimal Subtotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    public decimal Total { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public IReadOnlyList<FiscalDocumentEvent> Events => _events;

    public string AuditLabel => $"Documento {DocumentType} de {SourceNumber}";

    public bool IsElectronic => DocumentType != FiscalDocumentType.InternalReceipt;

    public static FiscalDocument Issue(Guid id, Guid companyId, FiscalIssue issue, bool electronic, DateTimeOffset now, Guid? userId, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(newId);
        var type = !electronic
            ? FiscalDocumentType.InternalReceipt
            : issue.Source == FiscalSource.Sale ? FiscalDocumentType.PosElectronic : FiscalDocumentType.CreditNote;
        var document = new FiscalDocument(id, companyId)
        {
            BranchId = issue.BranchId,
            PosTerminalId = issue.PosTerminalId,
            Source = issue.Source,
            SourceId = issue.SourceId,
            SourceNumber = issue.SourceNumber,
            DocumentType = type,
            Status = electronic ? FiscalStatus.Pending : FiscalStatus.NotRequired,
            RelatedDocumentId = issue.RelatedDocumentId,
            BusinessDate = issue.BusinessDate,
            BuyerName = issue.BuyerName,
            BuyerIdentificationType = issue.BuyerIdentificationType,
            BuyerIdentification = issue.BuyerIdentification,
            BuyerEmail = issue.BuyerEmail,
            Subtotal = issue.Subtotal,
            TaxTotal = issue.TaxTotal,
            Total = issue.Total,
            IssuedAt = now,
            NextAttemptAt = electronic ? now : null,
        };
        document._events.Add(FiscalDocumentEvent.Create(newId(), "ISSUED", electronic ? "Pendiente de envío al proveedor." : "Comprobante interno.", null, now, userId));
        return document;
    }

    /// <summary>
    /// Anula el comprobante de una venta anulada. Un documento electrónico aceptado no se anula: requiere nota crédito
    /// (la emite quien llama); devuelve <c>false</c> en ese caso.
    /// </summary>
    public bool Void(string reason, DateTimeOffset now, Guid? userId, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(newId);
        if (Status is FiscalStatus.Accepted or FiscalStatus.Submitting or FiscalStatus.Contingency)
        {
            return false;
        }

        Status = FiscalStatus.Voided;
        NextAttemptAt = null;
        _events.Add(FiscalDocumentEvent.Create(newId(), "VOIDED", reason, null, now, userId));
        return true;
    }

    /// <summary>Resultado de un envío al proveedor (11-B): aceptado, rechazado o error con reintento.</summary>
    public void RecordAttempt(FiscalStatus result, string? fiscalNumber, string? cufe, string? qr, string? providerCode, string? detail, string provider,
        DateTimeOffset now, DateTimeOffset? nextAttempt, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(newId);
        Attempts++;
        Provider = provider;
        Status = result;
        FiscalNumber = fiscalNumber ?? FiscalNumber;
        Cufe = cufe ?? Cufe;
        QrData = qr ?? QrData;
        NextAttemptAt = result is FiscalStatus.Pending or FiscalStatus.Error ? nextAttempt : null;
        _events.Add(FiscalDocumentEvent.Create(newId(), "ATTEMPT", detail, providerCode, now, null));
    }

    /// <summary>Reintento manual de un documento electrónico pendiente, con error o rechazado.</summary>
    public Result Retry(DateTimeOffset now, Guid userId, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(newId);
        if (!IsElectronic || Status is not (FiscalStatus.Pending or FiscalStatus.Error or FiscalStatus.Rejected))
        {
            return BillingErrors.NotRetryable;
        }

        Status = FiscalStatus.Pending;
        NextAttemptAt = now;
        _events.Add(FiscalDocumentEvent.Create(newId(), "RETRY_REQUESTED", null, null, now, userId));
        return Result.Success();
    }
}
