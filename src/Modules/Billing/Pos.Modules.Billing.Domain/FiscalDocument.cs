using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Billing.Domain;

/// <summary>Documento de origen del comprobante.</summary>
public enum FiscalSource
{
    Sale,
    SaleVoid,
    CustomerReturn,

    /// <summary>Compra a un proveedor no obligado a facturar (documento soporte, Fase 11-B).</summary>
    Purchase,
}

public enum FiscalDocumentType
{
    /// <summary>Tiquete de venta impreso, no electrónico (decisión del propietario en la Fase 7; modo OFF).</summary>
    InternalReceipt,

    /// <summary>Documento equivalente electrónico POS (reservado: Factus no lo emite, H2).</summary>
    PosElectronic,

    /// <summary>Factura electrónica de venta (Factus, H2): consumidor final o cliente identificado.</summary>
    InvoiceElectronic,

    CreditNote,

    /// <summary>Documento soporte de adquisiciones a no obligados a facturar.</summary>
    SupportDocument,
}

public enum FiscalStatus
{
    /// <summary>Comprobante interno: no se envía a la DIAN.</summary>
    NotRequired,

    Pending,

    /// <summary>Reclamado por la cola de envío (el envío está en curso).</summary>
    Submitting,

    Accepted,
    Rejected,

    /// <summary>Sin Internet o proveedor caído: se envía al volver la conexión (D11B-04).</summary>
    Contingency,

    Error,

    /// <summary>Comprobante interno de una venta anulada.</summary>
    Voided,

    /// <summary>Documento electrónico que nunca fue aceptado y se canceló (la venta se anuló antes de enviarlo).</summary>
    Cancelled,
}

public static class BillingErrors
{
    public static readonly Error NotFound = Error.NotFound("BILLING.DOCUMENT_NOT_FOUND", "El documento no existe.");

    public static readonly Error NotRetryable = Error.BusinessRule(
        "BILLING.NOT_RETRYABLE", "Solo se reintenta un documento electrónico pendiente, con error, en contingencia o rechazado.");

    public static readonly Error InvalidStatus = Error.BusinessRule("BILLING.INVALID_STATUS", "El documento no está en un estado que permita esta acción.");

    public static readonly Error NotCorrectable = Error.BusinessRule(
        "BILLING.NOT_CORRECTABLE", "Solo se corrigen los datos del adquirente de un documento electrónico aún no aceptado (pendiente, con error o rechazado).");

    public static readonly Error InvalidBuyer = Error.Validation(
        "BILLING.INVALID_BUYER", "Los datos del adquirente están incompletos: tipo y número de identificación, nombre, tipo de persona y régimen.");

    public static readonly Error RangeNotFound = Error.NotFound("BILLING.RANGE_NOT_FOUND", "El rango de numeración no existe.");

    public static readonly Error InvalidAssignment = Error.Validation(
        "BILLING.INVALID_ASSIGNMENT", "Una caja solo se asigna junto con su sucursal.");

    public static readonly Error RangeNotUsable = Error.BusinessRule(
        "BILLING.RANGE_NOT_USABLE", "El rango está inactivo, agotado o vencido: sincronice los rangos del proveedor.");

    public static readonly Error CredentialsRequired = Error.BusinessRule(
        "BILLING.CREDENTIALS_REQUIRED", "Configure las credenciales del proveedor antes de encender la facturación electrónica.");

    public static readonly Error InvalidCredentials = Error.Validation(
        "BILLING.INVALID_CREDENTIALS", "Las credenciales del proveedor están incompletas: usuario, contraseña, client id y client secret.");
}

/// <summary>Código de referencia ante el proveedor (RN-FE-02): el id del documento de origen, sin guiones.</summary>
public static class FiscalReference
{
    public static string For(FiscalSource source, Guid sourceId) => source switch
    {
        FiscalSource.Sale => sourceId.ToString("N"),
        FiscalSource.SaleVoid => "NCA" + sourceId.ToString("N"),
        FiscalSource.CustomerReturn => "NCD" + sourceId.ToString("N"),
        FiscalSource.Purchase => "DS" + sourceId.ToString("N"),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Origen de documento desconocido."),
    };
}

/// <summary>
/// Datos fiscales del adquirente guardados en el documento cuando el supervisor los corrige tras un rechazo (la venta es inmutable).
/// </summary>
public sealed record FiscalBuyerData(
    string PersonType, string? CheckDigit, string TaxRegime, IReadOnlyList<string> Responsibilities, string? Address, string? MunicipalityCode, string? Phone);

/// <summary>Corrección de los datos del adquirente (D11B-10).</summary>
public sealed record FiscalBuyerCorrection(string Name, string IdentificationType, string IdentificationNumber, string? Email, FiscalBuyerData Fiscal);

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
        new(id)
        {
            EventType = type, Detail = detail is { Length: > 1000 } ? detail[..1000] : detail,
            ProviderCode = providerCode is { Length: > 30 } ? providerCode[..30] : providerCode, OccurredAt = now, UserId = userId,
        };
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
/// Comprobante o documento fiscal de una venta, anulación, cambio o compra (D7-12, Fase 11-B). La venta nunca lo espera: en modo OFF
/// es un comprobante interno (<c>NOT_REQUIRED</c>); con la facturación electrónica nace <c>PENDING</c> en la transacción de la venta
/// y la cola de envío lo manda al proveedor, que le asigna número fiscal, CUFE/CUDE y QR.
/// </summary>
public sealed class FiscalDocument : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    private const int MaxRejectionLength = 2000;
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

    public string? ReferenceCode { get; private set; }

    public Guid? NumberingRangeId { get; private set; }

    public string? Provider { get; private set; }

    public string? ProviderDocumentId { get; private set; }

    public string? ProviderStatus { get; private set; }

    public string? FiscalNumber { get; private set; }

    public string? Cufe { get; private set; }

    public string? QrData { get; private set; }

    public string? PdfUrl { get; private set; }

    public string? RejectionMessage { get; private set; }

    public DateTimeOffset? ValidatedAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public Guid? RelatedDocumentId { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public string BuyerName { get; private set; } = string.Empty;

    public string BuyerIdentificationType { get; private set; } = string.Empty;

    public string BuyerIdentification { get; private set; } = string.Empty;

    public string? BuyerEmail { get; private set; }

    /// <summary>Datos fiscales corregidos del adquirente (si no, los de la venta).</summary>
    public FiscalBuyerData? BuyerFiscal { get; private set; }

    public decimal Subtotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    public decimal Total { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public IReadOnlyList<FiscalDocumentEvent> Events => _events;

    public string AuditLabel => $"Documento {DocumentType} de {SourceNumber}";

    public bool IsElectronic => DocumentType != FiscalDocumentType.InternalReceipt;

    /// <summary>Aún no aceptado ni cancelado: espera en la cola o requiere atención.</summary>
    public bool IsOpen => Status is FiscalStatus.Pending or FiscalStatus.Submitting or FiscalStatus.Error or FiscalStatus.Contingency or FiscalStatus.Rejected;

    /// <summary>Tipo de documento electrónico que corresponde a cada origen.</summary>
    public static FiscalDocumentType ElectronicTypeFor(FiscalSource source) => source switch
    {
        FiscalSource.Sale => FiscalDocumentType.InvoiceElectronic,
        FiscalSource.Purchase => FiscalDocumentType.SupportDocument,
        _ => FiscalDocumentType.CreditNote,
    };

    public static FiscalDocument Issue(Guid id, Guid companyId, FiscalIssue issue, bool electronic, DateTimeOffset now, Guid? userId, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(newId);
        if (!electronic && issue.Source == FiscalSource.Purchase)
        {
            throw new DomainException("Una compra solo genera documento soporte electrónico.");
        }

        var document = new FiscalDocument(id, companyId)
        {
            BranchId = issue.BranchId,
            PosTerminalId = issue.PosTerminalId,
            Source = issue.Source,
            SourceId = issue.SourceId,
            SourceNumber = issue.SourceNumber,
            DocumentType = electronic ? ElectronicTypeFor(issue.Source) : FiscalDocumentType.InternalReceipt,
            Status = electronic ? FiscalStatus.Pending : FiscalStatus.NotRequired,
            ReferenceCode = electronic ? FiscalReference.For(issue.Source, issue.SourceId) : null,
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
    /// Anula el comprobante de una venta anulada (D11B-07): el interno queda <c>VOIDED</c>; el electrónico que aún no fue aceptado
    /// (pendiente, con error, en contingencia o rechazado) se CANCELA antes de enviarlo. Un documento aceptado o en envío no se
    /// anula: requiere nota crédito (la emite quien llama); devuelve <c>false</c> en ese caso.
    /// </summary>
    public bool Void(string reason, DateTimeOffset now, Guid? userId, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(newId);
        if (Status is FiscalStatus.Accepted or FiscalStatus.Submitting)
        {
            return false;
        }

        if (Status is FiscalStatus.Voided or FiscalStatus.Cancelled)
        {
            return true;
        }

        Status = IsElectronic ? FiscalStatus.Cancelled : FiscalStatus.Voided;
        NextAttemptAt = null;
        _events.Add(FiscalDocumentEvent.Create(newId(), IsElectronic ? "CANCELLED" : "VOIDED", reason, null, now, userId));
        return true;
    }

    /// <summary>
    /// La cola reclama el documento para enviarlo (<c>SUBMITTING</c>) por un tiempo; si el envío no termina (el proceso se cayó), vuelve
    /// a quedar disponible al vencer. Devuelve si el reclamo anterior había quedado abandonado (hay que consultar el estado antes de
    /// reenviar).
    /// </summary>
    public Result<bool> Claim(DateTimeOffset now, TimeSpan lease)
    {
        var abandoned = Status == FiscalStatus.Submitting;
        if (!IsElectronic || Status is not (FiscalStatus.Pending or FiscalStatus.Error or FiscalStatus.Contingency or FiscalStatus.Submitting)
            || (abandoned && NextAttemptAt > now))
        {
            return BillingErrors.InvalidStatus;
        }

        Status = FiscalStatus.Submitting;
        NextAttemptAt = now + lease;
        return abandoned;
    }

    /// <summary>Rango de numeración con que se envía.</summary>
    public void UseRange(Guid rangeId) => NumberingRangeId = rangeId;

    /// <summary>
    /// El envío no se hizo y el documento vuelve a esperar (sin rango vigente, factura relacionada aún sin aceptar…). Devuelve si es la
    /// primera vez que ocurre por ese motivo (para alertar una sola vez).
    /// </summary>
    public bool Defer(string reason, string detail, DateTimeOffset now, DateTimeOffset nextAttempt, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(newId);
        var first = _events.All(e => e.EventType != reason);
        Status = FiscalStatus.Pending;
        NextAttemptAt = nextAttempt;
        if (first || _events[^1].EventType != reason)
        {
            _events.Add(FiscalDocumentEvent.Create(newId(), reason, detail, null, now, null));
        }

        return first;
    }

    /// <summary>
    /// Registra la respuesta del proveedor: aceptado (número, CUFE, QR), rechazado (con el mensaje), contingencia (sin conexión),
    /// error (reintento con espera) o sin configurar (sigue pendiente). Devuelve el estado resultante.
    /// </summary>
    public FiscalStatus RecordResult(FiscalProviderResult result, string provider, DateTimeOffset now, DateTimeOffset nextAttempt, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(newId);
        var status = result.Outcome switch
        {
            FiscalOutcome.Accepted when string.IsNullOrWhiteSpace(result.FiscalNumber ?? FiscalNumber) || string.IsNullOrWhiteSpace(result.Cufe ?? Cufe) =>
                FiscalStatus.Error,
            FiscalOutcome.Accepted => FiscalStatus.Accepted,
            FiscalOutcome.Rejected => FiscalStatus.Rejected,
            FiscalOutcome.Unavailable => FiscalStatus.Contingency,
            FiscalOutcome.NotConfigured or FiscalOutcome.NotFound => FiscalStatus.Pending,
            _ => FiscalStatus.Error,
        };
        var detail = status == FiscalStatus.Error && result.Outcome == FiscalOutcome.Accepted
            ? "El proveedor respondió aceptado sin número fiscal o sin CUFE: se consultará de nuevo."
            : result.Message;
        ProviderDocumentId = result.ProviderDocumentId ?? ProviderDocumentId;
        ProviderStatus = result.ProviderStatus is { Length: > 40 } ps ? ps[..40] : result.ProviderStatus ?? ProviderStatus;
        PdfUrl = result.PdfUrl ?? PdfUrl;
        if (status == FiscalStatus.Accepted)
        {
            ValidatedAt = result.ValidatedAt ?? now;
            RejectionMessage = null;
        }
        else if (status == FiscalStatus.Rejected)
        {
            var message = string.IsNullOrWhiteSpace(result.Message) ? "Rechazado por el proveedor (sin detalle)." : result.Message;
            RejectionMessage = message.Length > MaxRejectionLength ? message[..MaxRejectionLength] : message;
        }

        RecordAttempt(status, result.FiscalNumber, result.Cufe, result.QrData, result.Code, detail, provider, now, nextAttempt, newId);
        return status;
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
        if (result == FiscalStatus.Accepted)
        {
            ValidatedAt ??= now;
        }
        else if (result == FiscalStatus.Rejected)
        {
            RejectionMessage ??= detail ?? "Rechazado por el proveedor (sin detalle).";
        }

        NextAttemptAt = result is FiscalStatus.Pending or FiscalStatus.Error or FiscalStatus.Contingency ? nextAttempt : null;
        _events.Add(FiscalDocumentEvent.Create(newId(), "ATTEMPT", detail, providerCode, now, null));
    }

    /// <summary>Reintento manual de un documento electrónico pendiente, con error, en contingencia o rechazado.</summary>
    public Result Retry(DateTimeOffset now, Guid userId, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(newId);
        if (!IsElectronic || Status is not (FiscalStatus.Pending or FiscalStatus.Error or FiscalStatus.Rejected or FiscalStatus.Contingency))
        {
            return BillingErrors.NotRetryable;
        }

        Status = FiscalStatus.Pending;
        NextAttemptAt = now;
        _events.Add(FiscalDocumentEvent.Create(newId(), "RETRY_REQUESTED", null, null, now, userId));
        return Result.Success();
    }

    /// <summary>
    /// Corrige los datos del adquirente de un documento electrónico aún no aceptado (típicamente tras un rechazo por NIT, nombre o
    /// régimen) y lo deja pendiente para reenviarlo. La venta no cambia: la corrección queda en el documento y en sus eventos.
    /// </summary>
    public Result CorrectBuyer(FiscalBuyerCorrection correction, DateTimeOffset now, Guid userId, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(correction);
        ArgumentNullException.ThrowIfNull(newId);
        if (!IsElectronic || Status is not (FiscalStatus.Pending or FiscalStatus.Error or FiscalStatus.Rejected or FiscalStatus.Contingency))
        {
            return BillingErrors.NotCorrectable;
        }

        if (string.IsNullOrWhiteSpace(correction.Name) || string.IsNullOrWhiteSpace(correction.IdentificationType)
            || string.IsNullOrWhiteSpace(correction.IdentificationNumber) || correction.Fiscal is null
            || string.IsNullOrWhiteSpace(correction.Fiscal.PersonType) || string.IsNullOrWhiteSpace(correction.Fiscal.TaxRegime)
            || correction.Name.Trim().Length > 200 || correction.IdentificationNumber.Trim().Length > 30 || correction.IdentificationType.Trim().Length > 5
            || correction.Email is { Length: > 254 })
        {
            return BillingErrors.InvalidBuyer;
        }

        var before = $"{BuyerIdentificationType} {BuyerIdentification} · {BuyerName}";
        BuyerName = correction.Name.Trim();
        BuyerIdentificationType = correction.IdentificationType.Trim().ToUpperInvariant();
        BuyerIdentification = correction.IdentificationNumber.Trim();
        BuyerEmail = string.IsNullOrWhiteSpace(correction.Email) ? null : correction.Email.Trim();
        BuyerFiscal = correction.Fiscal;
        Status = FiscalStatus.Pending;
        NextAttemptAt = now;
        _events.Add(FiscalDocumentEvent.Create(
            newId(), "BUYER_CORRECTED", $"Antes: {before}. Ahora: {BuyerIdentificationType} {BuyerIdentification} · {BuyerName}.", null, now, userId));
        return Result.Success();
    }
}
