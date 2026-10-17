using Pos.Cloud.Licensing.Domain;

namespace Pos.Cloud.Licensing.Application;

/// <summary>
/// Pago en línea de las suscripciones (⚙️ sección <c>Payments</c>; Fase 12-C). Sin las llaves de Wompi el pago en línea queda
/// APAGADO: el portal dice "Pago en línea no configurado" y todo funciona como antes (renovación manual).
/// </summary>
public sealed class PaymentsOptions
{
    public const string SectionName = "Payments";

    /// <summary>
    /// Dirección pública del portal (con su ruta base si la tiene), p. ej. <c>https://businesspost.tutiendanueva.com</c>. Vacía = se
    /// toma de la petición (el proxy reenvía el esquema y el host).
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Días antes del vencimiento en que el check-in devuelve el enlace de pago (D12C-08: 15).</summary>
    public int LinkDaysBeforeExpiry { get; set; } = PaymentRules.DefaultLinkDaysBeforeExpiry;

    /// <summary>Cada cuánto la conciliación consulta en la pasarela las órdenes pendientes (D12C-10: 15 min).</summary>
    public int ReconcileMinutes { get; set; } = 15;

    /// <summary>Horas sin respuesta tras las cuales una orden pendiente vence (D12C-03: 24 h).</summary>
    public int PendingExpiryHours { get; set; } = 24;

    /// <summary>Órdenes pendientes que admite una suscripción en las últimas 24 h (evita llenar la tabla desde el enlace público).</summary>
    public int MaxPendingOrdersPerSubscription { get; set; } = 10;

    /// <summary>
    /// Días en que la conciliación sigue revisando las órdenes RECHAZADAS o VENCIDAS (W1: un pago aprobado tarde igual se aplica).
    /// </summary>
    public int RecheckClosedOrdersDays { get; set; } = 7;

    /// <summary>
    /// Antigüedad máxima del <c>timestamp</c> de un evento de la pasarela (antirrepetición). Cubre los reintentos de Wompi cuando la nube
    /// respondió error (W1).
    /// </summary>
    public int EventMaxAgeHours { get; set; } = 72;

    /// <summary>Cuánto puede adelantarse el <c>timestamp</c> de un evento a la hora del servidor (relojes desfasados).</summary>
    public int EventMaxFutureMinutes { get; set; } = 5;

    public WompiOptions Wompi { get; set; } = new();
}

/// <summary>
/// Llaves y direcciones de Wompi (D12C-11): SOLO en variables de entorno del servidor (<c>Payments__Wompi__…</c>) o, en desarrollo,
/// en <c>dotnet user-secrets</c>. Nunca en el repositorio ni en la BD.
/// </summary>
public sealed class WompiOptions
{
    public const string SandboxApi = "https://sandbox.wompi.co/v1";
    public const string ProductionApi = "https://production.wompi.co/v1";
    public const string DefaultCheckoutUrl = "https://checkout.wompi.co/p/";

    /// <summary><c>Sandbox</c> (llaves de prueba) o <c>Production</c>.</summary>
    public string Environment { get; set; } = "Sandbox";

    public string? PublicKey { get; set; }

    public string? PrivateKey { get; set; }

    public string? IntegritySecret { get; set; }

    public string? EventsSecret { get; set; }

    public string CheckoutUrl { get; set; } = DefaultCheckoutUrl;

    /// <summary>Solo para pruebas; vacío = según <see cref="Environment"/>.</summary>
    public string? ApiBaseUrl { get; set; }

    public bool IsProduction => string.Equals(Environment, "Production", StringComparison.OrdinalIgnoreCase);

    public bool IsSandbox => string.Equals(Environment, "Sandbox", StringComparison.OrdinalIgnoreCase);

    public string EffectiveApiBaseUrl => (string.IsNullOrWhiteSpace(ApiBaseUrl) ? IsProduction ? ProductionApi : SandboxApi : ApiBaseUrl).TrimEnd('/');

    /// <summary>Configurado = ambiente válido y las cuatro llaves presentes.</summary>
    public bool IsConfigured =>
        (IsProduction || IsSandbox)
        && !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(IntegritySecret) && !string.IsNullOrWhiteSpace(EventsSecret)
        && Uri.TryCreate(CheckoutUrl, UriKind.Absolute, out var checkout) && checkout.Scheme == Uri.UriSchemeHttps;
}

/// <summary>Evento recibido en el webhook, ya verificado (o no) con el secreto de eventos.</summary>
/// <param name="SignatureValid">La firma (checksum) coincide.</param>
/// <param name="EventType">P. ej. <c>transaction.updated</c>.</param>
/// <param name="Transaction">Datos de la transacción que trae el evento (se confirman después consultando la pasarela).</param>
/// <param name="Environment"><c>test</c> o <c>prod</c>.</param>
/// <param name="RejectionReason">Por qué no es válido (sin datos secretos).</param>
/// <param name="Timestamp">Momento del evento según la pasarela (<c>timestamp</c>, firmado): antirrepetición.</param>
public sealed record GatewayEvent(
    bool SignatureValid, string? EventType, GatewayTransaction? Transaction, string? Environment, string? RejectionReason, DateTimeOffset? Timestamp = null);

/// <summary>
/// Datos para armar la redirección al checkout alojado por la pasarela. <c>ExpiresAt</c>: después de esa hora la pasarela ya no acepta el
/// pago (va firmado en la integridad).
/// </summary>
public sealed record CheckoutRequest(string Reference, long AmountInCents, string Currency, string RedirectUrl, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Pasarela de pagos (D12C-01). Hoy solo Wompi (<c>WompiGateway</c>); cambiar de pasarela es escribir otro adaptador. Nunca se
/// ven ni se guardan datos de tarjetas o cuentas: el cliente paga en la página de la pasarela.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Nombre guardado en las órdenes (<c>WOMPI</c>).</summary>
    string Name { get; }

    bool IsConfigured { get; }

    /// <summary><c>Sandbox</c> o <c>Production</c> (para mostrar en el portal).</summary>
    string Environment { get; }

    /// <summary>Origen del checkout (para la política de seguridad de contenido de las páginas /pagar).</summary>
    string? CheckoutOrigin { get; }

    /// <summary>URL del checkout con la firma de integridad (referencia + valor + moneda).</summary>
    string BuildCheckoutUrl(CheckoutRequest request);

    /// <summary>Verifica la firma del evento (cuerpo JSON y cabecera del checksum) y extrae la transacción.</summary>
    GatewayEvent ParseEvent(string body, string? checksumHeader);

    /// <summary>Consulta la transacción con la llave privada. Nulo si no existe o la pasarela no responde.</summary>
    Task<GatewayTransaction?> GetTransactionAsync(string transactionId, CancellationToken cancellationToken);

    /// <summary>Transacciones de una referencia (conciliación). Nulo si la pasarela no responde.</summary>
    Task<IReadOnlyList<GatewayTransaction>?> FindByReferenceAsync(string reference, CancellationToken cancellationToken);
}

/// <summary>Persistencia del pago en línea (EF Core).</summary>
public interface IPaymentStore
{
    void Add(SubscriptionPrice price);

    void Add(PaymentLink link);

    void Add(PaymentOrder order);

    void Add(PaymentEvent paymentEvent);

    Task<IReadOnlyList<SubscriptionPrice>> GetPricesAsync(CancellationToken cancellationToken);

    Task<PaymentLink?> GetActiveLinkAsync(Guid subscriptionId, CancellationToken cancellationToken);

    Task<PaymentLink?> GetLinkByCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>
    /// Inserta <paramref name="candidate"/> si la suscripción no tiene enlace vigente (sin fallar si otro proceso lo creó al mismo
    /// tiempo: dos check-ins simultáneos no deben romperse) y devuelve el enlace vigente.
    /// </summary>
    Task<PaymentLink> EnsureActiveLinkAsync(PaymentLink candidate, CancellationToken cancellationToken);

    Task<PaymentOrder?> GetOrderAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Bloquea la fila de la orden hasta el fin de la transacción y la lee de nuevo (evento y conciliación a la vez).</summary>
    Task<PaymentOrder?> LockOrderAsync(string reference, CancellationToken cancellationToken);

    /// <summary>
    /// Órdenes a conciliar: las pendientes y las rechazadas o vencidas creadas desde <paramref name="recheckSince"/> (W1). Primero las
    /// pendientes.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOrderIdsToReconcileAsync(int limit, DateTimeOffset recheckSince, CancellationToken cancellationToken);

    /// <summary>¿Hubo un evento con firma inválida de esa IP desde <paramref name="since"/>? (limita la auditoría Warning por IP).</summary>
    Task<bool> HasRecentRejectedEventAsync(System.Net.IPAddress? ip, DateTimeOffset since, CancellationToken cancellationToken);

    Task<int> CountRecentPendingOrdersAsync(Guid subscriptionId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Siguiente número del consecutivo de las referencias <c>BP-&lt;año&gt;-&lt;n&gt;</c>.</summary>
    Task<long> NextOrderNumberAsync(CancellationToken cancellationToken);

    /// <summary>Cuenta del cliente (rol CUSTOMER) del usuario del portal; nulo si no tiene.</summary>
    Task<Guid?> GetCustomerAccountAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed record PriceDto(Guid Id, string Edition, string BillingPeriod, decimal AmountCop, DateTimeOffset ValidFrom, DateTimeOffset CreatedAt, string CreatedByName);

public sealed record PricesDto(bool PaymentsConfigured, IReadOnlyList<PriceDto> Current, IReadOnlyList<PriceDto> History);

public sealed record PaymentOrderRowDto(
    Guid Id,
    string Reference,
    Guid SubscriptionId,
    Guid OrganizationId,
    string OrganizationName,
    string Nit,
    string Edition,
    string BillingPeriod,
    int Periods,
    decimal AmountCop,
    string Status,
    string? GatewayTransactionId,
    string? PaymentMethodType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? AppliedAt,
    string? ReviewReason,
    DateTimeOffset? ReviewedAt,
    string? ReviewedByName,
    string? ReviewNote);

public sealed record PaymentEventDto(
    Guid Id,
    DateTimeOffset ReceivedAt,
    string Source,
    string? EventType,
    bool? SignatureValid,
    string? TransactionId,
    string? TransactionStatus,
    string? Reference,
    long? AmountInCents,
    string? Environment,
    string? RejectionReason,
    string? Payload);

public sealed record PaymentOrderDetailDto(PaymentOrderRowDto Order, IReadOnlyList<PaymentEventDto> Events);

public sealed record PaymentOrderFilter(string? Status = null, DateTimeOffset? From = null, DateTimeOffset? To = null, string? Search = null, Guid? OrganizationId = null);

public sealed record PaymentSettingsDto(bool Configured, string Gateway, string Environment);

public sealed record PaymentLinkDto(Guid SubscriptionId, string Code, string Path, DateTimeOffset CreatedAt);

public sealed record PaymentAlertsDto(bool Configured, int ReviewRequired, int Pending, IReadOnlyList<PaymentOrderRowDto> ToReview);

/// <summary>Opción de pago de la página pública: periodos y valor (pesos, IVA incluido).</summary>
public sealed record PaymentOptionDto(int Periods, decimal AmountCop, string Label);

/// <summary>
/// Página pública /pagar/&lt;código&gt; (D12C-07): SOLO el nombre de la empresa, la edición, el vencimiento y el valor. Nunca el
/// NIT, la clave, las ventas ni nada más.
/// </summary>
public sealed record PaymentPageDto(
    bool Configured,
    string OrganizationName,
    string EditionName,
    string BillingPeriodName,
    DateTimeOffset ValidUntil,
    string StatusName,
    bool CanPay,
    string? Message,
    IReadOnlyList<PaymentOptionDto> Options);

public sealed record CheckoutDto(string Reference, string CheckoutUrl);

/// <summary>Página de regreso /pagar/resultado: solo lee NUESTRA orden (nunca renueva desde aquí).</summary>
public sealed record PaymentResultDto(bool Found, string? Reference, string Status, string StatusText, decimal? AmountCop, bool Final);

/// <summary>"Mi suscripción" del cliente (rol CUSTOMER).</summary>
public sealed record CustomerSubscriptionDto(
    Guid OrganizationId, string OrganizationName, SubscriptionSummaryDto? Subscription, bool CanPay, IReadOnlyList<PaymentOrderRowDto> Payments);

public sealed record CustomerSubscriptionsDto(bool Configured, IReadOnlyList<CustomerSubscriptionDto> Organizations);

/// <summary>Lecturas del pago en línea para el portal.</summary>
public interface IPaymentReadModel
{
    Task<IReadOnlyList<PriceDto>> ListPricesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PaymentOrderRowDto>> ListOrdersAsync(PaymentOrderFilter filter, CancellationToken cancellationToken);

    Task<PaymentOrderDetailDto?> GetOrderAsync(Guid id, CancellationToken cancellationToken);

    Task<PaymentOrderRowDto?> FindOrderByTransactionAsync(string transactionId, CancellationToken cancellationToken);

    Task<PaymentOrderRowDto?> FindOrderByReferenceAsync(string reference, CancellationToken cancellationToken);

    /// <summary>La orden SOLO si el token de su página de resultado coincide (anti enumeración).</summary>
    Task<PaymentOrderRowDto?> FindOrderForResultAsync(string reference, string token, CancellationToken cancellationToken);

    Task<(int ReviewRequired, int Pending)> CountAlertsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomerSubscriptionDto>> CustomerSubscriptionsAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken);
}
