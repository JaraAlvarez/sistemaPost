using Pos.SharedKernel.Results;

namespace Pos.Modules.Cash.Contracts;

/// <summary>
/// Configuración de Sistecrédito vista por la administración (D7B-02). Nunca trae las llaves: solo si hay, cuándo se cambiaron y los
/// últimos 4 caracteres de la llave de suscripción. <c>Environment</c>: STAGING (pruebas) o PRODUCTION.
/// </summary>
public sealed record SistecreditoSettingsDto(
    bool HasCredentials,
    bool IsEnabled,
    string Environment,
    DateTimeOffset? CredentialsUpdatedAt,
    string? KeyHint,
    DateTimeOffset? LastVerifiedAt,
    bool? LastVerificationOk,
    string? LastVerificationMessage,
    bool PaymentMethodActive,
    Guid? PaymentMethodId,
    int PendingReviews);

/// <summary>
/// Resultado de "Verificar conexión". <c>WorkingEnvironment</c>: si las llaves no sirven en el ambiente elegido pero sí en el otro,
/// cuál es (STAGING o PRODUCTION).
/// </summary>
public sealed record SistecreditoVerificationDto(bool Ok, string Message, string? WorkingEnvironment);

/// <summary>
/// Un cobro con Sistecrédito. <c>Status</c>: PENDING, APPROVED, REJECTED, EXPIRED, CANCELLED o REVIEW_REQUIRED. El documento va
/// enmascarado (solo los últimos 4). <c>Warning</c>: aviso temporal (p. ej. sin conexión con Sistecrédito; se sigue intentando).
/// </summary>
public sealed record SistecreditoPaymentDto(
    Guid Id,
    Guid SaleId,
    decimal Amount,
    string DocumentType,
    string MaskedDocument,
    string Environment,
    string? TransactionId,
    string Status,
    string? ProviderStatus,
    string? RedirectUrl,
    int? Installments,
    string? Message,
    string? Warning,
    DateTimeOffset StartedAt,
    DateTimeOffset? ApprovedAt,
    Guid? CompletedSaleId,
    DateTimeOffset? ReviewRequiredAt,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote);

/// <summary>
/// Sistecrédito visto por ventas (D7B-05): al completar una venta pagada con Sistecrédito, el pago debe corresponder a un cobro APROBADO
/// de esa venta, por ese valor y sin usar. Se marca usado en la misma transacción de la venta.
/// </summary>
public interface ISistecreditoPayments
{
    Task<Result> UseApprovedAsync(Guid saleId, string transactionId, decimal amount, CancellationToken cancellationToken = default);

    /// <summary>Cobros aprobados sin venta que la administración debe revisar (aviso en la franja de avisos).</summary>
    Task<int> CountPendingReviewsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Venta en curso vista por la caja para cobrarla con Sistecrédito. <c>Status</c>: OPEN, ON_HOLD, COMPLETED…</summary>
public sealed record SaleChargeInfo(
    Guid SaleId, Guid PosTerminalId, string Status, decimal AmountDue, string? CustomerIdentificationType, string? CustomerIdentification);

/// <summary>
/// Contrato invertido (como <see cref="IOpenSalesProbe"/>): la caja pregunta por la venta que va a cobrar con Sistecrédito. Lo implementa
/// el módulo de ventas; así la caja no depende de él.
/// </summary>
public interface ISaleChargeProbe
{
    Task<SaleChargeInfo?> GetAsync(Guid saleId, CancellationToken cancellationToken = default);
}
