using System.Globalization;
using System.Security.Cryptography;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.Licensing.Domain;

/// <summary>Estado de una orden de pago en línea (docs/fases/fase-12c-propuesta.md D12C-03). En la BD: MAYÚSCULAS_CON_GUIONES.</summary>
public enum PaymentOrderStatus
{
    Pending,
    Approved,
    Declined,
    Voided,
    Error,
    Expired,

    /// <summary>"Requiere revisión" (D12C-06): valor distinto, suscripción suspendida o cancelada, o anulación después de renovar.</summary>
    ReviewRequired,
}

/// <summary>Resultado de aplicar una transacción confirmada de la pasarela a una orden.</summary>
public enum PaymentOutcome
{
    /// <summary>Nada cambió (orden ya renovada, ya en revisión, evento repetido o de otra referencia).</summary>
    Ignored,

    /// <summary>La transacción sigue pendiente en la pasarela (PSE, efectivo…).</summary>
    StillPending,

    /// <summary>Rechazada, anulada o con error en la pasarela: la orden queda cerrada sin renovar.</summary>
    Failed,

    /// <summary>Aprobada, con valor y moneda correctos: se renovó la suscripción (una sola vez).</summary>
    Renewed,

    /// <summary>Aprobada pero no se renueva sola: queda "Requiere revisión" con alerta en el tablero.</summary>
    ReviewRequired,
}

/// <summary>Estados de la transacción en la pasarela (Wompi): PENDING, APPROVED, DECLINED, VOIDED, ERROR.</summary>
public static class GatewayStatuses
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Declined = "DECLINED";
    public const string Voided = "VOIDED";
    public const string Error = "ERROR";
}

/// <summary>Transacción tal como la confirma la pasarela consultándola con la llave privada (nunca solo lo que dice el evento).</summary>
public sealed record GatewayTransaction(string Id, string Reference, long AmountInCents, string Currency, string Status, string? PaymentMethodType);

/// <summary>Reglas comerciales del pago en línea (§10 de la propuesta).</summary>
public static class PaymentRules
{
    public const string Currency = "COP";
    public const string Gateway = "WOMPI";

    /// <summary>Valor máximo de un precio (protege contra errores de digitación).</summary>
    public const decimal MaxAmount = 100_000_000m;

    /// <summary>Días antes del vencimiento en que el check-in devuelve el enlace de pago (D12C-08, ⚙️ <c>Payments:LinkDaysBeforeExpiry</c>).</summary>
    public const int DefaultLinkDaysBeforeExpiry = 15;

    /// <summary>Periodos que se pueden pagar de una vez: mensual 1, 3 o 6; anual 1 (respuesta 5).</summary>
    public static IReadOnlyList<int> AllowedPeriods(BillingPeriod period) => period == BillingPeriod.Monthly ? [1, 3, 6] : [1];

    public static bool IsAllowed(BillingPeriod period, int periods) => AllowedPeriods(period).Contains(periods);

    /// <summary>¿La suscripción admite pagar en línea? En prueba, vigente, en gracia o vencida; nunca suspendida ni cancelada (D12C-06).</summary>
    public static bool IsPayable(SubscriptionStatus status) =>
        status is SubscriptionStatus.Trial or SubscriptionStatus.Active or SubscriptionStatus.PastDue or SubscriptionStatus.Expired;

    /// <summary>
    /// ¿El check-in devuelve el enlace de pago? (D12C-08) En prueba o vigente cuando faltan <paramref name="daysBeforeExpiry"/> días o
    /// menos; siempre en gracia o vencida; nunca suspendida ni cancelada.
    /// </summary>
    public static bool ShouldOfferLink(SubscriptionStatus status, DateTimeOffset validUntil, DateTimeOffset now, int daysBeforeExpiry) => status switch
    {
        SubscriptionStatus.Trial or SubscriptionStatus.Active => validUntil - now <= TimeSpan.FromDays(daysBeforeExpiry),
        SubscriptionStatus.PastDue or SubscriptionStatus.Expired => true,
        _ => false,
    };

    /// <summary>Referencia única de la orden: <c>BP-&lt;año&gt;-&lt;consecutivo de 6 dígitos&gt;</c> (D12C-03).</summary>
    public static string Reference(int year, long number) =>
        string.Create(CultureInfo.InvariantCulture, $"BP-{year:0000}-{number:000000}");

    public static bool IsReference(string? text) =>
        text is { Length: >= 14 and <= 20 } && text.StartsWith("BP-", StringComparison.Ordinal) && text[7] == '-'
        && text.AsSpan(3, 4).ContainsOnlyDigits() && text.AsSpan(8).ContainsOnlyDigits();

    /// <summary>Referencia del pago en el historial de la suscripción: la misma renovación que la manual, con los datos de Wompi.</summary>
    public static string RenewalReference(string transactionId, string reference) => $"WOMPI {transactionId} {reference}";

    /// <summary>Precio vigente: el de mayor "vigente desde" que ya empezó (en empate, el registrado después).</summary>
    public static SubscriptionPrice? Current(IEnumerable<SubscriptionPrice> prices, LicenseEdition edition, BillingPeriod period, DateTimeOffset now) =>
        prices
            .Where(p => p.Edition == edition && p.BillingPeriod == period && p.ValidFrom <= now)
            .OrderByDescending(p => p.ValidFrom)
            .ThenByDescending(p => p.Id)
            .FirstOrDefault();

    /// <summary>Código del enlace de pago: 128 bits aleatorios en base64url (22 caracteres, D12C-07).</summary>
    public static string NewLinkCode() => Base64Url(RandomNumberGenerator.GetBytes(16));

    public static bool IsLinkCode(string? code) =>
        code is { Length: 22 } && code.AsSpan().ContainsOnly("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool ContainsOnlyDigits(this ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return span.Length > 0;
    }

    private static bool ContainsOnly(this ReadOnlySpan<char> span, string allowed)
    {
        foreach (var c in span)
        {
            if (!allowed.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Errores del pago en línea con código estable.</summary>
public static class PaymentErrors
{
    public const string NotConfiguredMessage = "Pago en línea no configurado.";

    public static readonly Error NotConfigured = Error.BusinessRule("PAYMENTS.NOT_CONFIGURED", NotConfiguredMessage);

    public static readonly Error LinkNotFound = Error.NotFound(
        "PAYMENTS.LINK_NOT_FOUND", "El enlace de pago no existe o fue reemplazado. Pida a BusinessPost el enlace vigente.");

    public static readonly Error SubscriptionNotPayable = Error.BusinessRule(
        "PAYMENTS.SUBSCRIPTION_NOT_PAYABLE", "Esta suscripción no admite pagos en línea en su estado actual. Comuníquese con BusinessPost.");

    public static readonly Error PriceNotConfigured = Error.BusinessRule(
        "PAYMENTS.PRICE_NOT_CONFIGURED", "Precio no configurado: comuníquese con BusinessPost.");

    public static readonly Error InvalidPeriods = Error.Validation("PAYMENTS.INVALID_PERIODS", "Periodos inválidos: mensual 1, 3 o 6; anual 1.");

    public static readonly Error InvalidPrice = Error.Validation(
        "PAYMENTS.INVALID_PRICE", "El valor debe ser mayor que cero, en pesos con IVA incluido, sin centavos y de máximo $100.000.000.");

    public static readonly Error InvalidSignature = Error.Unauthorized("PAYMENTS.INVALID_SIGNATURE", "La firma del evento de la pasarela no es válida.");

    public static readonly Error OrderNotFound = Error.NotFound("PAYMENTS.ORDER_NOT_FOUND", "La orden de pago no existe.");

    public static readonly Error NotUnderReview = Error.BusinessRule(
        "PAYMENTS.NOT_UNDER_REVIEW", "Solo se marcan como revisados los pagos que requieren revisión y aún no se han revisado.");

    public static readonly Error NoteRequired = Error.Validation("PAYMENTS.NOTE_REQUIRED", "Escriba la nota de la revisión (de 5 a 300 caracteres).");

    public static readonly Error TooManyOrders = Error.Conflict(
        "PAYMENTS.TOO_MANY_ORDERS", "Hay demasiados intentos de pago pendientes para esta suscripción. Espere unos minutos e intente de nuevo.");

    public static readonly Error GatewayUnavailable = Error.Unexpected(
        "PAYMENTS.GATEWAY_UNAVAILABLE", "No se pudo consultar la pasarela de pagos en este momento. Intente más tarde.");
}

/// <summary>
/// Precio de una edición y periodicidad, en pesos con IVA incluido, vigente desde una fecha (D12C-02). Historial de SOLO INSERCIÓN:
/// un cambio de precio es una fila nueva; las órdenes ya creadas conservan su valor congelado.
/// </summary>
public sealed class SubscriptionPrice : Entity<Guid>, IHasAuditLabel
{
    private SubscriptionPrice(Guid id, LicenseEdition edition, BillingPeriod billingPeriod, decimal amountCop, DateTimeOffset validFrom)
        : base(id)
    {
        Edition = edition;
        BillingPeriod = billingPeriod;
        AmountCop = amountCop;
        ValidFrom = validFrom;
    }

    public LicenseEdition Edition { get; private set; }

    public BillingPeriod BillingPeriod { get; private set; }

    /// <summary>Valor en pesos colombianos, IVA incluido, sin centavos.</summary>
    public decimal AmountCop { get; private set; }

    public DateTimeOffset ValidFrom { get; private set; }

    public string AuditLabel => $"Precio {Edition.DisplayName()} {(BillingPeriod == BillingPeriod.Monthly ? "mensual" : "anual")}";

    public static Result<SubscriptionPrice> Create(Guid id, LicenseEdition edition, BillingPeriod billingPeriod, decimal amountCop, DateTimeOffset validFrom)
    {
        if (amountCop <= 0 || amountCop > PaymentRules.MaxAmount || amountCop != decimal.Truncate(amountCop))
        {
            return PaymentErrors.InvalidPrice;
        }

        return new SubscriptionPrice(id, edition, billingPeriod, amountCop, validFrom);
    }
}

/// <summary>
/// Enlace público de pago de una suscripción (D12C-07): <c>/pagar/&lt;código&gt;</c> con 128 bits aleatorios. Se puede regenerar: el
/// anterior se revoca y deja de funcionar.
/// </summary>
public sealed class PaymentLink : Entity<Guid>
{
    private PaymentLink(Guid id, Guid subscriptionId, string code, DateTimeOffset createdAt)
        : base(id)
    {
        SubscriptionId = subscriptionId;
        Code = code;
        CreatedAt = createdAt;
    }

    public Guid SubscriptionId { get; private set; }

    public string Code { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    public static PaymentLink Create(Guid id, Guid subscriptionId, DateTimeOffset now) => new(id, subscriptionId, PaymentRules.NewLinkCode(), now);

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}

/// <summary>
/// Orden de pago (D12C-03): el valor queda CONGELADO al crearla. La única fuente de verdad es la transacción confirmada con la
/// llave privada (D12C-04); <see cref="Process"/> aplica la MISMA renovación que la manual (<see cref="Subscription.Renew"/>) y
/// una orden renueva una sola vez (D12C-05, además una restricción única en la BD).
/// </summary>
public sealed class PaymentOrder : AggregateRoot<Guid>, IHasAuditLabel
{
    /// <summary>Para EF Core.</summary>
    private PaymentOrder(Guid id)
        : base(id)
    {
        Reference = string.Empty;
        Currency = PaymentRules.Currency;
        Gateway = PaymentRules.Gateway;
    }

    private PaymentOrder(Guid id, string reference, Guid subscriptionId, Guid organizationId, LicenseEdition edition, BillingPeriod billingPeriod,
        int periods, decimal amountCop, DateTimeOffset now)
        : base(id)
    {
        Reference = reference;
        SubscriptionId = subscriptionId;
        OrganizationId = organizationId;
        Edition = edition;
        BillingPeriod = billingPeriod;
        Periods = periods;
        AmountCop = amountCop;
        AmountInCents = (long)(amountCop * 100);
        Currency = PaymentRules.Currency;
        Gateway = PaymentRules.Gateway;
        Status = PaymentOrderStatus.Pending;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public string Reference { get; private set; }

    public Guid SubscriptionId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public LicenseEdition Edition { get; private set; }

    public BillingPeriod BillingPeriod { get; private set; }

    public int Periods { get; private set; }

    public decimal AmountCop { get; private set; }

    public long AmountInCents { get; private set; }

    public string Currency { get; private set; }

    public PaymentOrderStatus Status { get; private set; }

    public string Gateway { get; private set; }

    public string? GatewayTransactionId { get; private set; }

    public string? PaymentMethodType { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Cuándo se aplicó la renovación (nulo = la orden no ha renovado).</summary>
    public DateTimeOffset? AppliedAt { get; private set; }

    /// <summary>Evento RENEWED de la suscripción que produjo esta orden (único en la BD).</summary>
    public Guid? RenewalEventId { get; private set; }

    public string? ReviewReason { get; private set; }

    public Guid? ReviewedBy { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public string? ReviewNote { get; private set; }

    public bool IsApplied => AppliedAt is not null;

    /// <summary>Requiere revisión y nadie la ha marcado como revisada (alerta del tablero).</summary>
    public bool NeedsReview => Status == PaymentOrderStatus.ReviewRequired && ReviewedAt is null;

    public string AuditLabel => $"Pago {Reference}";

    /// <summary>Crea la orden con el precio vigente × los periodos elegidos. Valida estado de la suscripción y periodos.</summary>
    public static Result<PaymentOrder> Create(Guid id, string reference, Subscription subscription, SubscriptionPrice? price, int periods, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (!PaymentRules.IsPayable(subscription.StatusAt(now)))
        {
            return PaymentErrors.SubscriptionNotPayable;
        }

        if (price is null || price.Edition != subscription.Edition || price.BillingPeriod != subscription.BillingPeriod)
        {
            return PaymentErrors.PriceNotConfigured;
        }

        if (!PaymentRules.IsAllowed(subscription.BillingPeriod, periods))
        {
            return PaymentErrors.InvalidPeriods;
        }

        if (!PaymentRules.IsReference(reference))
        {
            throw new ArgumentException("Referencia de pago inválida.", nameof(reference));
        }

        return new PaymentOrder(id, reference, subscription.Id, subscription.OrganizationId, subscription.Edition, subscription.BillingPeriod,
            periods, price.AmountCop * periods, now);
    }

    /// <summary>
    /// Aplica la transacción CONFIRMADA de la pasarela (D12C-04/05/06). Idempotente: una orden ya renovada no vuelve a renovar, y
    /// los eventos repetidos no cambian nada. Si se aprueba con valor y moneda correctos y la suscripción admite renovación, ejecuta
    /// <see cref="Subscription.Renew"/> con la referencia <c>WOMPI &lt;transacción&gt; &lt;referencia&gt;</c>.
    /// </summary>
    public PaymentOutcome Process(GatewayTransaction transaction, Subscription subscription, int defaultGraceDays, ChangeContext change)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(change);
        if (!string.Equals(transaction.Reference, Reference, StringComparison.Ordinal) || subscription.Id != SubscriptionId
            || string.IsNullOrWhiteSpace(transaction.Id) || transaction.Id.Length > 60)
        {
            return PaymentOutcome.Ignored;
        }

        var status = (transaction.Status ?? string.Empty).ToUpperInvariant();
        if (IsApplied)
        {
            // Ya renovó: solo una anulación posterior de ESA transacción pide revisión (la devolución se hace en el panel de Wompi).
            if (status == GatewayStatuses.Voided && transaction.Id == GatewayTransactionId && Status != PaymentOrderStatus.ReviewRequired)
            {
                RequireReview("La pasarela anuló el pago después de renovar la suscripción: revise la vigencia.", change.Now);
                return PaymentOutcome.ReviewRequired;
            }

            return PaymentOutcome.Ignored;
        }

        if (Status == PaymentOrderStatus.ReviewRequired)
        {
            return PaymentOutcome.Ignored;
        }

        switch (status)
        {
            case GatewayStatuses.Pending:
                if (Status != PaymentOrderStatus.Pending || GatewayTransactionId == transaction.Id)
                {
                    return Status == PaymentOrderStatus.Pending ? PaymentOutcome.StillPending : PaymentOutcome.Ignored;
                }

                Record(transaction, change.Now);
                return PaymentOutcome.StillPending;

            case GatewayStatuses.Declined or GatewayStatuses.Voided or GatewayStatuses.Error:
                var failed = status switch
                {
                    GatewayStatuses.Declined => PaymentOrderStatus.Declined,
                    GatewayStatuses.Voided => PaymentOrderStatus.Voided,
                    _ => PaymentOrderStatus.Error,
                };
                if (Status == failed && GatewayTransactionId == transaction.Id)
                {
                    return PaymentOutcome.Ignored;
                }

                Record(transaction, change.Now);
                Status = failed;
                return PaymentOutcome.Failed;

            case GatewayStatuses.Approved:
                Record(transaction, change.Now);
                return Approve(transaction, subscription, defaultGraceDays, change);

            default:
                return PaymentOutcome.Ignored;
        }
    }

    /// <summary>Sin respuesta de la pasarela en el plazo (⚙️ 24 h): la orden vence. Un pago aprobado después igual se aplica.</summary>
    public bool Expire(DateTimeOffset now, TimeSpan maxAge)
    {
        if (Status != PaymentOrderStatus.Pending || now - CreatedAt < maxAge)
        {
            return false;
        }

        Status = PaymentOrderStatus.Expired;
        UpdatedAt = now;
        return true;
    }

    /// <summary>"Marcar revisado" con la nota de lo que se hizo (renovar a mano, devolver el dinero…).</summary>
    public Result MarkReviewed(Guid actorId, string note, DateTimeOffset now)
    {
        if (!NeedsReview)
        {
            return PaymentErrors.NotUnderReview;
        }

        var cleaned = (note ?? string.Empty).Trim();
        if (cleaned.Length is < 5 or > 300)
        {
            return PaymentErrors.NoteRequired;
        }

        ReviewedBy = actorId;
        ReviewedAt = now;
        ReviewNote = cleaned;
        UpdatedAt = now;
        return Result.Success();
    }

    private PaymentOutcome Approve(GatewayTransaction transaction, Subscription subscription, int defaultGraceDays, ChangeContext change)
    {
        if (transaction.AmountInCents != AmountInCents || !string.Equals(transaction.Currency, Currency, StringComparison.OrdinalIgnoreCase))
        {
            RequireReview(string.Create(CultureInfo.InvariantCulture,
                $"El valor pagado ({transaction.AmountInCents / 100m:0.##} {transaction.Currency}) no coincide con la orden ({AmountCop:0.##} {Currency})."), change.Now);
            return PaymentOutcome.ReviewRequired;
        }

        var current = subscription.StatusAt(change.Now);
        if (current is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled)
        {
            RequireReview($"Pago aprobado, pero la suscripción está {current.DisplayName().ToLowerInvariant()}: no se renueva sola.", change.Now);
            return PaymentOutcome.ReviewRequired;
        }

        if (subscription.Edition != Edition || subscription.BillingPeriod != BillingPeriod)
        {
            RequireReview("Pago aprobado, pero la edición o la periodicidad cambiaron después de crear la orden.", change.Now);
            return PaymentOutcome.ReviewRequired;
        }

        var renewed = subscription.Renew(PaymentRules.RenewalReference(transaction.Id, Reference), Periods, defaultGraceDays, change);
        if (renewed.IsFailure)
        {
            RequireReview($"Pago aprobado, pero la renovación no se pudo aplicar: {renewed.Error.Message}", change.Now);
            return PaymentOutcome.ReviewRequired;
        }

        Status = PaymentOrderStatus.Approved;
        AppliedAt = change.Now;
        RenewalEventId = subscription.Events.Last(e => e.Type == SubscriptionEventType.Renewed).Id;
        return PaymentOutcome.Renewed;
    }

    private void Record(GatewayTransaction transaction, DateTimeOffset now)
    {
        GatewayTransactionId = transaction.Id;
        PaymentMethodType = transaction.PaymentMethodType is { Length: > 30 } method ? method[..30] : transaction.PaymentMethodType;
        UpdatedAt = now;
    }

    private void RequireReview(string reason, DateTimeOffset now)
    {
        Status = PaymentOrderStatus.ReviewRequired;
        ReviewReason = reason.Length > 300 ? reason[..300] : reason;
        UpdatedAt = now;
    }
}

/// <summary>
/// Registro crudo de lo recibido de la pasarela (SOLO INSERCIÓN): eventos del webhook (con su firma válida o no) y consultas de
/// conciliación. Explica cualquier reclamo ("yo pagué y no se renovó").
/// </summary>
public sealed class PaymentEvent : Entity<Guid>
{
    public const string WebhookSource = "WEBHOOK";
    public const string QuerySource = "QUERY";

    /// <summary>Máximo guardado del cuerpo recibido (lo demás se recorta).</summary>
    public const int MaxPayloadChars = 16_000;

    private PaymentEvent(Guid id, DateTimeOffset receivedAt, string source)
        : base(id)
    {
        ReceivedAt = receivedAt;
        Source = source;
        Gateway = PaymentRules.Gateway;
    }

    public DateTimeOffset ReceivedAt { get; private set; }

    public string Gateway { get; private set; }

    public string Source { get; private set; }

    public string? EventType { get; private set; }

    /// <summary>Firma del evento: válida o no (webhook); nulo para las consultas hechas por la nube.</summary>
    public bool? SignatureValid { get; private set; }

    public string? TransactionId { get; private set; }

    public string? TransactionStatus { get; private set; }

    public string? Reference { get; private set; }

    public long? AmountInCents { get; private set; }

    public string? Currency { get; private set; }

    public string? Environment { get; private set; }

    public Guid? OrderId { get; private set; }

    /// <summary>JSON recibido (recortado si es muy grande).</summary>
    public string? Payload { get; private set; }

    public string? RejectionReason { get; private set; }

    public static PaymentEvent Webhook(
        Guid id, DateTimeOffset now, bool signatureValid, string? eventType, GatewayTransaction? transaction, string? environment, string? payload,
        string? rejectionReason) =>
        new PaymentEvent(id, now, WebhookSource) { SignatureValid = signatureValid, EventType = Cut(eventType, 60), Environment = Cut(environment, 10) }
            .With(transaction, payload, rejectionReason);

    public static PaymentEvent Query(Guid id, DateTimeOffset now, GatewayTransaction? transaction, string? payload, string? note) =>
        new PaymentEvent(id, now, QuerySource) { EventType = "transaction.query" }.With(transaction, payload, note);

    public void LinkTo(Guid orderId) => OrderId ??= orderId;

    private PaymentEvent With(GatewayTransaction? transaction, string? payload, string? rejectionReason)
    {
        TransactionId = Cut(transaction?.Id, 60);
        TransactionStatus = Cut(transaction?.Status, 20);
        Reference = Cut(transaction?.Reference, 40);
        AmountInCents = transaction?.AmountInCents;
        Currency = Cut(transaction?.Currency, 3);
        Payload = payload;
        RejectionReason = Cut(rejectionReason, 300);
        return this;
    }

    private static string? Cut(string? value, int max) => value is { Length: > 0 } v ? (v.Length > max ? v[..max] : v) : null;
}
