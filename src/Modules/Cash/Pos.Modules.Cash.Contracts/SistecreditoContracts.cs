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
/// <c>Usable</c>: aprobado y sin usar (aunque esté en REVIEW_REQUIRED): la caja lo agrega como pago de su venta.
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
    string? ReviewNote,
    bool Usable = false);

/// <summary>
/// Sistecrédito visto por ventas (D7B-05): al completar una venta pagada con Sistecrédito, el pago debe corresponder a un cobro APROBADO
/// de esa venta, por ese valor y sin usar. Se marca usado en la misma transacción de la venta.
/// </summary>
public interface ISistecreditoPayments
{
    Task<Result> UseApprovedAsync(Guid saleId, string transactionId, decimal amount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Paso previo a completar la venta, FUERA de su transacción: vuelve a consultar en Sistecrédito los cobros aprobados y sin usar de la
    /// venta (⚙️ máximo 5 s en total, mejor esfuerzo) y guarda en el momento los que ya no estén aprobados ("Requiere revisión").
    /// </summary>
    Task RecheckForSaleAsync(Guid saleId, CancellationToken cancellationToken = default);

    /// <summary>Cobros aprobados y sin usar de la venta (aunque estén en revisión): la venta no puede quedar por debajo de su valor.</summary>
    Task<IReadOnlyList<SistecreditoPendingUse>> ListUsableAsync(Guid saleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// «No usar este crédito»: con autorización de supervisor, el cobro aprobado de la venta queda en revisión y ya no se usa. Sin
    /// <c>transactionId</c>, todos los créditos usables de la venta o, con <c>keepUpTo</c>, solo los necesarios (los más grandes primero)
    /// para que lo aprobado que queda no supere ese valor (lo que falta por pagar). Repetirlo cuando ya no queda ninguno usable: OK.
    /// </summary>
    Task<Result> ReleaseAsync(
        Guid saleId, string? transactionId, Guid userId, Guid? authorizedBy, decimal? keepUpTo = null, CancellationToken cancellationToken = default);

    /// <summary>Después de completar la venta, FUERA de su transacción: cancela (o revisa, si ya se aprobaron) los cobros que seguían pendientes.</summary>
    Task SettleAfterSaleAsync(Guid saleId, CancellationToken cancellationToken = default);

    /// <summary>Cobros recién aprobados y sin usar de la venta: la venta no se completa sin incluirlos (el cliente ya pagó con ellos).</summary>
    Task<IReadOnlyList<SistecreditoPendingUse>> ListApprovedUnusedAsync(Guid saleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// La venta pagada con estos cobros se anuló: quedan "Requiere revisión" para que la administración reverse el pago en el portal de
    /// Sistecrédito (Sistecrédito no tiene cómo reversarlo desde la caja). Queda en la bitácora.
    /// </summary>
    Task SaleVoidedAsync(Guid saleId, string? saleNumber, IReadOnlyList<string> transactionIds, CancellationToken cancellationToken = default);

    /// <summary>Cobros aprobados sin venta que la administración debe revisar (aviso en la franja de avisos).</summary>
    Task<int> CountPendingReviewsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Cobro aprobado y sin usar de una venta: número de la transacción de Sistecrédito y valor.</summary>
public sealed record SistecreditoPendingUse(string TransactionId, decimal Amount);

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
