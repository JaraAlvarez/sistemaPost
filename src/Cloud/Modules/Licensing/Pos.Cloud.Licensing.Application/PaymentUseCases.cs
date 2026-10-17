using System.Globalization;
using System.Text.Json;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Licensing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Licensing.Application;

// Pago en línea de las suscripciones (docs/fases/fase-12c-propuesta.md). Todo queda en la auditoría de la nube (D12C-12).

// ─────────────────────────────── Configuración y precios ───────────────────────────────

/// <summary>¿Está configurado el pago en línea? (sin datos secretos; lo usan las pantallas para mostrar u ocultar los botones).</summary>
public sealed record GetPaymentSettingsQuery : IQuery<PaymentSettingsDto>;

internal sealed class GetPaymentSettingsHandler(IPaymentGateway gateway) : IQueryHandler<GetPaymentSettingsQuery, PaymentSettingsDto>
{
    public Task<Result<PaymentSettingsDto>> Handle(GetPaymentSettingsQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new PaymentSettingsDto(gateway.IsConfigured, gateway.Name, gateway.Environment)));
}

/// <summary>Precios vigentes por edición y periodicidad, más el historial completo (D12C-02).</summary>
[RequiresPermission(CloudPermissions.PaymentView)]
public sealed record ListPricesQuery : IQuery<PricesDto>;

internal sealed class ListPricesHandler(IPaymentReadModel read, IPaymentGateway gateway, IClock clock) : IQueryHandler<ListPricesQuery, PricesDto>
{
    public async Task<Result<PricesDto>> Handle(ListPricesQuery request, CancellationToken cancellationToken)
    {
        var history = await read.ListPricesAsync(cancellationToken);
        var now = clock.UtcNow;
        var current = history
            .Where(p => p.ValidFrom <= now)
            .GroupBy(p => (p.Edition, p.BillingPeriod))
            .Select(g => g.OrderByDescending(p => p.ValidFrom).ThenByDescending(p => p.Id).First())
            .OrderBy(p => p.Edition, StringComparer.Ordinal).ThenBy(p => p.BillingPeriod, StringComparer.Ordinal)
            .ToList();
        return new PricesDto(gateway.IsConfigured, current, history);
    }
}

/// <summary>
/// Fija un precio nuevo (fila nueva del historial; nunca se edita uno anterior). <c>ValidFrom</c> nulo = desde ya. Las órdenes
/// creadas antes conservan su valor congelado.
/// </summary>
[RequiresPermission(CloudPermissions.PriceManage)]
public sealed record SetPriceCommand(string Edition, string BillingPeriod, decimal AmountCop, DateTimeOffset? ValidFrom = null) : ICommand<Guid>;

internal sealed class SetPriceHandler(IPaymentStore store, PortalChange portal, IAuditWriter audit) : ICommandHandler<SetPriceCommand, Guid>
{
    public async Task<Result<Guid>> Handle(SetPriceCommand request, CancellationToken cancellationToken)
    {
        if (!LicensingCodes.TryParseEdition(request.Edition, out var edition))
        {
            return LicenseErrorsFor.Field("edition", "Edición inválida: SINGLE (Caja Única) o MULTI (Multicaja).");
        }

        if (!Enum.TryParse<BillingPeriod>(request.BillingPeriod, ignoreCase: true, out var period) || !Enum.IsDefined(period))
        {
            return LicenseErrorsFor.Field("billingPeriod", "Periodicidad inválida: MONTHLY o ANNUAL.");
        }

        var change = portal.Create();
        var previous = PaymentRules.Current(await store.GetPricesAsync(cancellationToken), edition, period, change.Now);
        var price = SubscriptionPrice.Create(change.Ids.NewId(), edition, period, request.AmountCop, request.ValidFrom ?? change.Now);
        if (price.IsFailure)
        {
            return price.Error;
        }

        store.Add(price.Value);
        await audit.WriteAsync(
            new AuditEntry("licensing", "PAYMENT_PRICE_SET", nameof(SubscriptionPrice), price.Value.Id, price.Value.AuditLabel,
                $"{portal.ActorName} fijó el precio {edition.DisplayName()} {PaymentText.Period(period)} en {PaymentText.Money(request.AmountCop)} (IVA incluido) " +
                $"desde {PaymentText.Day(price.Value.ValidFrom)}; antes {(previous is null ? "sin precio" : PaymentText.Money(previous.AmountCop))}.",
                OldValues: previous is null ? null : new Dictionary<string, object?> { ["amount_cop"] = previous.AmountCop.ToString(CultureInfo.InvariantCulture) },
                NewValues: new Dictionary<string, object?> { ["amount_cop"] = request.AmountCop.ToString(CultureInfo.InvariantCulture) },
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return price.Value.Id;
    }
}

// ─────────────────────────────── Enlaces de pago ───────────────────────────────

/// <summary>El enlace de pago vigente de la suscripción (lo crea si no tiene): "Copiar enlace de pago" y QR (D12C-07).</summary>
[RequiresPermission(CloudPermissions.PaymentView)]
public sealed record EnsurePaymentLinkCommand(Guid SubscriptionId) : ICommand<PaymentLinkDto>;

internal sealed class EnsurePaymentLinkHandler(PaymentLinks links) : ICommandHandler<EnsurePaymentLinkCommand, PaymentLinkDto>
{
    public async Task<Result<PaymentLinkDto>> Handle(EnsurePaymentLinkCommand request, CancellationToken cancellationToken) =>
        await links.EnsureAsync(request.SubscriptionId, cancellationToken) is { } link ? PaymentLinks.ToDto(link) : LicensingErrors.SubscriptionNotFound;
}

/// <summary>Regenera el enlace (p. ej. se compartió por error): el anterior deja de funcionar.</summary>
[RequiresPermission(CloudPermissions.PaymentManage)]
public sealed record RegeneratePaymentLinkCommand(Guid SubscriptionId) : ICommand<PaymentLinkDto>;

internal sealed class RegeneratePaymentLinkHandler(
    IPaymentStore store, ILicensingStore licensing, PortalChange portal, IAuditWriter audit) : ICommandHandler<RegeneratePaymentLinkCommand, PaymentLinkDto>
{
    public async Task<Result<PaymentLinkDto>> Handle(RegeneratePaymentLinkCommand request, CancellationToken cancellationToken)
    {
        var subscription = await licensing.GetSubscriptionAsync(request.SubscriptionId, cancellationToken);
        if (subscription is null)
        {
            return LicensingErrors.SubscriptionNotFound;
        }

        var change = portal.Create();
        if (await store.GetActiveLinkAsync(subscription.Id, cancellationToken) is { } old)
        {
            old.Revoke(change.Now);

            // Solo puede haber un enlace vigente por suscripción: el anterior se revoca antes de insertar el nuevo.
            await licensing.FlushAsync(cancellationToken);
        }

        var link = PaymentLink.Create(change.Ids.NewId(), subscription.Id, change.Now);
        store.Add(link);
        var organization = (await licensing.GetOrganizationAsync(subscription.OrganizationId, cancellationToken))!;
        await audit.WriteAsync(
            new AuditEntry("licensing", "PAYMENT_LINK_REGENERATED", nameof(Subscription), subscription.Id, organization.AuditLabel,
                $"{portal.ActorName} regeneró el enlace de pago de {organization.LegalName}: el anterior dejó de funcionar.", Severity: AuditSeverity.Warning),
            cancellationToken);
        return PaymentLinks.ToDto(link);
    }
}

/// <summary>Enlaces de pago: el vigente de cada suscripción y la dirección que devuelve el check-in del POS.</summary>
public sealed class PaymentLinks(IPaymentStore store, ILicensingStore licensing, IPaymentGateway gateway, PaymentsOptions options, IIdGenerator ids, IClock clock)
{
    public const string PagePath = "pagar";

    /// <summary>El enlace vigente de la suscripción; si no tiene, lo crea (en la transacción en curso).</summary>
    public async Task<PaymentLink?> EnsureAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        if (await store.GetActiveLinkAsync(subscriptionId, cancellationToken) is { } existing)
        {
            return existing;
        }

        if (await licensing.GetSubscriptionAsync(subscriptionId, cancellationToken) is null)
        {
            return null;
        }

        return await store.EnsureActiveLinkAsync(PaymentLink.Create(ids.NewId(), subscriptionId, clock.UtcNow), cancellationToken);
    }

    /// <summary>
    /// Enlace para el check-in (D12C-08): solo con el pago en línea configurado y si la suscripción está por vencer (⚙️ 15 días), en
    /// gracia o vencida. Nunca para una suspendida o cancelada.
    /// </summary>
    public async Task<string?> ForCheckinAsync(Subscription subscription, string? requestBaseUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var baseUrl = BaseUrl(requestBaseUrl);
        if (!gateway.IsConfigured || baseUrl is null
            || !PaymentRules.ShouldOfferLink(subscription.Status, subscription.ValidUntil, clock.UtcNow, Math.Max(0, options.LinkDaysBeforeExpiry)))
        {
            return null;
        }

        var link = await EnsureAsync(subscription.Id, cancellationToken);
        return link is null ? null : Url(baseUrl, $"{PagePath}/{link.Code}");
    }

    /// <summary>Dirección pública: <c>Payments:PublicBaseUrl</c> o, si falta, la de la petición.</summary>
    public string? BaseUrl(string? requestBaseUrl) =>
        (string.IsNullOrWhiteSpace(options.PublicBaseUrl) ? requestBaseUrl : options.PublicBaseUrl) is { Length: > 0 } value
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? value.TrimEnd('/')
            : null;

    public static string Url(string baseUrl, string relative) => $"{baseUrl.TrimEnd('/')}/{relative.TrimStart('/')}";

    internal static PaymentLinkDto ToDto(PaymentLink link) => new(link.SubscriptionId, link.Code, $"{PagePath}/{link.Code}", link.CreatedAt);
}

// ─────────────────────────────── Página pública y orden ───────────────────────────────

/// <summary>Datos de la página pública /pagar/&lt;código&gt; (anónima: el código de 128 bits es la autorización).</summary>
public sealed record GetPaymentPageQuery(string Code) : IQuery<PaymentPageDto>;

internal sealed class GetPaymentPageHandler(IPaymentStore store, ILicensingStore licensing, IPaymentGateway gateway, IClock clock)
    : IQueryHandler<GetPaymentPageQuery, PaymentPageDto>
{
    public async Task<Result<PaymentPageDto>> Handle(GetPaymentPageQuery request, CancellationToken cancellationToken)
    {
        if (!PaymentRules.IsLinkCode(request.Code) || await store.GetLinkByCodeAsync(request.Code, cancellationToken) is not { IsActive: true } link
            || await licensing.GetSubscriptionAsync(link.SubscriptionId, cancellationToken) is not { } subscription)
        {
            return PaymentErrors.LinkNotFound;
        }

        var organization = (await licensing.GetOrganizationAsync(subscription.OrganizationId, cancellationToken))!;
        var now = clock.UtcNow;
        var status = subscription.StatusAt(now);
        var price = PaymentRules.Current(await store.GetPricesAsync(cancellationToken), subscription.Edition, subscription.BillingPeriod, now);
        string? message = null;
        if (!gateway.IsConfigured)
        {
            message = $"{PaymentErrors.NotConfiguredMessage} Comuníquese con BusinessPost para renovar.";
        }
        else if (!PaymentRules.IsPayable(status))
        {
            message = "Comuníquese con BusinessPost para renovar su suscripción.";
        }
        else if (price is null)
        {
            message = PaymentErrors.PriceNotConfigured.Message;
        }

        var options = price is null
            ? []
            : PaymentRules.AllowedPeriods(subscription.BillingPeriod)
                .Select(n => new PaymentOptionDto(n, price.AmountCop * n, $"{PaymentText.Periods(subscription.BillingPeriod, n)} — {PaymentText.Money(price.AmountCop * n)}"))
                .ToList();
        return new PaymentPageDto(
            gateway.IsConfigured, organization.LegalName, subscription.Edition.DisplayName(), PaymentText.Period(subscription.BillingPeriod),
            subscription.ValidUntil, status.DisplayName(), message is null, message, message is null ? options : []);
    }
}

/// <summary>
/// Crea la orden desde el enlace (valor congelado) y devuelve la redirección al checkout firmada (D12C-03). Suspendida o
/// cancelada: no crea orden ("Comuníquese con BusinessPost"); sin precio: "Precio no configurado". <c>BaseUrl</c>: dirección
/// pública de la petición (la página de regreso es <c>&lt;base&gt;/pagar/resultado/&lt;referencia&gt;</c>).
/// </summary>
public sealed record CreatePaymentOrderCommand(string Code, int Periods, string? BaseUrl) : ICommand<CheckoutDto>;

internal sealed class CreatePaymentOrderHandler(
    IPaymentStore store, ILicensingStore licensing, IPaymentGateway gateway, PaymentLinks links, PaymentsOptions options, IAuditWriter audit,
    IIdGenerator ids, IClock clock) : ICommandHandler<CreatePaymentOrderCommand, CheckoutDto>
{
    public async Task<Result<CheckoutDto>> Handle(CreatePaymentOrderCommand request, CancellationToken cancellationToken)
    {
        if (!gateway.IsConfigured)
        {
            return PaymentErrors.NotConfigured;
        }

        if (links.BaseUrl(request.BaseUrl) is not { } baseUrl)
        {
            return LicenseErrorsFor.Field("baseUrl", "No se pudo determinar la dirección pública del portal (Payments:PublicBaseUrl).");
        }

        if (!PaymentRules.IsLinkCode(request.Code) || await store.GetLinkByCodeAsync(request.Code, cancellationToken) is not { IsActive: true } link
            || await licensing.GetSubscriptionAsync(link.SubscriptionId, cancellationToken) is not { } subscription)
        {
            return PaymentErrors.LinkNotFound;
        }

        var now = clock.UtcNow;
        if (!PaymentRules.IsPayable(subscription.StatusAt(now)))
        {
            return PaymentErrors.SubscriptionNotPayable;
        }

        var price = PaymentRules.Current(await store.GetPricesAsync(cancellationToken), subscription.Edition, subscription.BillingPeriod, now);
        if (price is null)
        {
            return PaymentErrors.PriceNotConfigured;
        }

        if (!PaymentRules.IsAllowed(subscription.BillingPeriod, request.Periods))
        {
            return PaymentErrors.InvalidPeriods;
        }

        if (await store.CountRecentPendingOrdersAsync(subscription.Id, now.AddHours(-24), cancellationToken) >= Math.Max(1, options.MaxPendingOrdersPerSubscription))
        {
            return PaymentErrors.TooManyOrders;
        }

        var reference = PaymentRules.Reference(clock.ToBusinessTime(now).Year, await store.NextOrderNumberAsync(cancellationToken));
        var created = PaymentOrder.Create(ids.NewId(), reference, subscription, price, request.Periods, now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var order = created.Value;
        store.Add(order);
        var organization = (await licensing.GetOrganizationAsync(subscription.OrganizationId, cancellationToken))!;
        await audit.WriteAsync(
            new AuditEntry("licensing", "PAYMENT_ORDER_CREATED", nameof(PaymentOrder), order.Id, order.AuditLabel,
                $"Orden de pago {order.Reference} de {organization.LegalName}: {PaymentText.Periods(order.BillingPeriod, order.Periods)} de {order.Edition.DisplayName()} " +
                $"por {PaymentText.Money(order.AmountCop)} (IVA incluido), creada desde el enlace de pago."),
            cancellationToken);

        // La página de regreso lleva un token aleatorio de la orden: la referencia sola es un consecutivo y se podría enumerar.
        var redirect = PaymentLinks.Url(baseUrl, $"{PaymentLinks.PagePath}/resultado/{order.Reference}/{order.ResultToken}");
        var expiresAt = now.AddHours(Math.Max(1, options.PendingExpiryHours));
        return new CheckoutDto(order.Reference,
            gateway.BuildCheckoutUrl(new CheckoutRequest(order.Reference, order.AmountInCents, order.Currency, redirect, expiresAt)));
    }
}

/// <summary>
/// Página de regreso del checkout: muestra el estado leyendo NUESTRA orden (D12C-04: nunca renueva desde aquí). Exige la referencia Y el
/// token aleatorio de la orden (la referencia es un consecutivo): sin token válido solo dice que el pago se confirmará, sin estado ni valor.
/// </summary>
public sealed record GetPaymentResultQuery(string? TransactionId, string? Reference, string? Token = null) : IQuery<PaymentResultDto>;

internal sealed class GetPaymentResultHandler(IPaymentReadModel read) : IQueryHandler<GetPaymentResultQuery, PaymentResultDto>
{
    public const string WithoutTokenText =
        "Si completó el pago, la pasarela lo confirmará y la suscripción se renovará sola. Puede cerrar esta página.";

    public async Task<Result<PaymentResultDto>> Handle(GetPaymentResultQuery request, CancellationToken cancellationToken)
    {
        if (!PaymentRules.IsReference(request.Reference) || !PaymentRules.IsResultToken(request.Token))
        {
            return new PaymentResultDto(false, null, "UNKNOWN", WithoutTokenText, null, true);
        }

        var order = await read.FindOrderForResultAsync(request.Reference!, request.Token!, cancellationToken);
        if (order is null)
        {
            return new PaymentResultDto(false, null, "UNKNOWN", WithoutTokenText, null, true);
        }

        var (text, final) = order.Status switch
        {
            "APPROVED" => ("Pago aprobado: su suscripción ya quedó renovada. La tienda lo verá en su próximo reporte o con «Ya pagué, verificar».", true),
            "PENDING" => ("Pago en proceso: la pasarela aún no lo confirma (PSE o efectivo pueden tardar). Puede cerrar esta página.", false),
            "DECLINED" => ("El pago fue rechazado. Puede intentar de nuevo desde el enlace de pago.", true),
            "VOIDED" => ("El pago fue anulado.", true),
            "ERROR" => ("La pasarela reportó un error con el pago. Intente de nuevo desde el enlace de pago.", true),
            "EXPIRED" => ("El intento de pago venció sin respuesta. Si pagó, BusinessPost lo revisará.", true),
            _ => ("Pago recibido: BusinessPost lo está revisando y le confirmará la renovación.", true),
        };
        return new PaymentResultDto(true, order.Reference, order.Status, text, order.AmountCop, final);
    }
}

// ─────────────────────────────── Eventos de la pasarela ───────────────────────────────

public sealed record PaymentEventReceipt(string Result);

/// <summary>
/// Evento de Wompi (webhook, D12C-04/05/06/13). Verifica la firma y registra el evento (también los rechazados, con auditoría);
/// CONFIRMA la transacción consultándola con la llave privada y aplica la misma renovación que la manual, una sola vez. Los
/// eventos repetidos no cambian nada. Firma inválida → 401 (el registro y la auditoría se confirman igual).
/// </summary>
public sealed record HandlePaymentEventCommand(string Body, string? ChecksumHeader, System.Net.IPAddress? SourceIp = null) : ICommand<Outcome<PaymentEventReceipt>>;

internal sealed class HandlePaymentEventHandler(
    IPaymentStore store, IPaymentGateway gateway, PaymentApplier applier, PaymentsOptions options, IAuditWriter audit, IIdGenerator ids, IClock clock)
    : ICommandHandler<HandlePaymentEventCommand, Outcome<PaymentEventReceipt>>
{
    /// <summary>Ventana en la que se audita a lo sumo un evento rechazado por IP (el resto solo queda en el registro de eventos).</summary>
    public static readonly TimeSpan RejectedAuditWindow = TimeSpan.FromMinutes(10);

    public async Task<Result<Outcome<PaymentEventReceipt>>> Handle(HandlePaymentEventCommand request, CancellationToken cancellationToken)
    {
        if (!gateway.IsConfigured)
        {
            return Outcome.Fail<PaymentEventReceipt>(PaymentErrors.NotConfigured);
        }

        var now = clock.UtcNow;
        var parsed = gateway.ParseEvent(request.Body, request.ChecksumHeader);

        // Antirrepetición: el timestamp va firmado; fuera de la ventana se trata como un evento no válido.
        if (parsed.SignatureValid && !WompiProtocol.IsFresh(parsed.Timestamp, now, TimeSpan.FromHours(Math.Max(1, options.EventMaxAgeHours)),
                TimeSpan.FromMinutes(Math.Max(0, options.EventMaxFutureMinutes))))
        {
            parsed = parsed with { SignatureValid = false, RejectionReason = "El evento está fuera de la ventana de tiempo admitida (posible repetición)." };
        }

        if (!parsed.SignatureValid)
        {
            // Firma inválida: solo metadatos (nunca el cuerpo, que puede venir de cualquiera) y a lo sumo una auditoría por IP cada 10 min.
            var audited = !await store.HasRecentRejectedEventAsync(request.SourceIp, now - RejectedAuditWindow, cancellationToken);
            var rejected = PaymentEvent.Webhook(
                ids.NewId(), now, false, parsed.EventType, parsed.Transaction, parsed.Environment, PaymentText.RejectedMetadata(request.Body),
                parsed.RejectionReason, request.SourceIp);
            store.Add(rejected);
            if (audited)
            {
                await audit.WriteAsync(
                    new AuditEntry("licensing", "PAYMENT_EVENT_REJECTED", nameof(PaymentEvent), rejected.Id, "Evento de la pasarela",
                        $"Evento de Wompi rechazado desde {request.SourceIp?.ToString() ?? "IP desconocida"}: {parsed.RejectionReason} " +
                        $"(dice ser la transacción {rejected.TransactionId ?? "—"}, referencia {rejected.Reference ?? "—"}) " +
                        $"(los siguientes de esa IP en {RejectedAuditWindow.TotalMinutes:0} min solo quedan en el registro de eventos).",
                        Severity: AuditSeverity.Warning),
                    cancellationToken);
            }

            return Outcome.Fail<PaymentEventReceipt>(PaymentErrors.InvalidSignature);
        }

        var received = PaymentEvent.Webhook(
            ids.NewId(), now, true, parsed.EventType, parsed.Transaction, parsed.Environment, PaymentText.Payload(request.Body), null, request.SourceIp);
        store.Add(received);

        await audit.WriteAsync(
            new AuditEntry("licensing", "PAYMENT_EVENT_RECEIVED", nameof(PaymentEvent), received.Id, "Evento de la pasarela",
                $"Evento {parsed.EventType} de Wompi con firma válida: transacción {parsed.Transaction?.Id ?? "—"} {parsed.Transaction?.Status ?? string.Empty} " +
                $"de la referencia {parsed.Transaction?.Reference ?? "—"}."),
            cancellationToken);

        if (parsed.EventType != WompiProtocol.TransactionUpdated || parsed.Transaction is not { } notified || !PaymentRules.IsReference(notified.Reference))
        {
            return Outcome.Ok(new PaymentEventReceipt("IGNORED"));
        }

        // La única fuente de verdad es la transacción consultada con la llave privada (no lo que dice el evento). Si la pasarela no responde
        // se contesta 503 para que Wompi REINTENTE (W1); el evento queda registrado igual (Outcome confirma la transacción).
        var confirmed = await gateway.GetTransactionAsync(notified.Id, cancellationToken);
        if (confirmed is null)
        {
            return Outcome.Fail<PaymentEventReceipt>(PaymentErrors.GatewayUnavailable);
        }

        var order = await store.LockOrderAsync(notified.Reference, cancellationToken);
        if (order is null)
        {
            await audit.WriteAsync(
                new AuditEntry("licensing", "PAYMENT_EVENT_UNKNOWN_ORDER", nameof(PaymentEvent), received.Id, "Evento de la pasarela",
                    $"Evento de Wompi para la referencia {notified.Reference}, que no corresponde a ninguna orden.", Severity: AuditSeverity.Warning),
                cancellationToken);
            return Outcome.Ok(new PaymentEventReceipt("UNKNOWN_ORDER"));
        }

        received.LinkTo(order.Id);
        var outcome = await applier.ApplyAsync(order, confirmed, "evento de Wompi", cancellationToken);
        return Outcome.Ok(new PaymentEventReceipt(outcome switch
        {
            PaymentOutcome.Renewed => "RENEWED",
            PaymentOutcome.ReviewRequired => "REVIEW_REQUIRED",
            PaymentOutcome.StillPending => "PENDING",
            PaymentOutcome.Failed => "FAILED",
            PaymentOutcome.Reverted => "REVERTED",
            _ => "IGNORED",
        }));
    }
}

/// <summary>Aplica una transacción confirmada a la orden y deja la auditoría (lo usan el evento, la conciliación y "Consultar ahora").</summary>
public sealed class PaymentApplier(ILicensingStore licensing, LicensingOptions options, IAuditWriter audit, IIdGenerator ids, IClock clock)
{
    public async Task<PaymentOutcome> ApplyAsync(PaymentOrder order, GatewayTransaction transaction, string origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(transaction);
        var subscription = (await licensing.GetSubscriptionAsync(order.SubscriptionId, cancellationToken))!;
        var organization = (await licensing.GetOrganizationAsync(order.OrganizationId, cancellationToken))!;

        // Renovación automática: la hace el usuario técnico (como los vencimientos), con la referencia de Wompi.
        var change = new ChangeContext(SystemActor.Id, clock.UtcNow, ids);
        var outcome = order.Process(transaction, subscription, options.GraceDays, change);
        var paid = $"{PaymentText.Money(order.AmountCop)} (Wompi {transaction.Id}{(transaction.PaymentMethodType is { } m ? $", {m}" : string.Empty)})";
        switch (outcome)
        {
            case PaymentOutcome.Renewed:
                await audit.WriteAsync(
                    new AuditEntry("licensing", "PAYMENT_APPROVED", nameof(PaymentOrder), order.Id, order.AuditLabel,
                        $"Pago en línea {order.Reference} de {organization.LegalName} aprobado por {paid}, confirmado por {origin}."),
                    cancellationToken);
                await audit.WriteAsync(
                    new AuditEntry("licensing", "SUBSCRIPTION_RENEWED", nameof(Subscription), subscription.Id, organization.AuditLabel,
                        $"Renovación automática de {organization.LegalName} por el pago en línea {order.Reference} " +
                        $"({PaymentText.Periods(order.BillingPeriod, order.Periods)}): vigente hasta {PaymentText.Day(subscription.ValidUntil)}."),
                    cancellationToken);
                break;
            case PaymentOutcome.ReviewRequired:
                await audit.WriteAsync(
                    new AuditEntry("licensing", "PAYMENT_REVIEW_REQUIRED", nameof(PaymentOrder), order.Id, order.AuditLabel,
                        $"Pago en línea {order.Reference} de {organization.LegalName} ({paid}) requiere revisión: {order.ReviewReason}", Severity: AuditSeverity.Critical),
                    cancellationToken);
                break;
            case PaymentOutcome.Reverted:
                await audit.WriteAsync(
                    new AuditEntry("licensing", "PAYMENT_VOIDED_REVERTED", nameof(PaymentOrder), order.Id, order.AuditLabel,
                        $"La pasarela anuló el pago en línea {order.Reference} de {organization.LegalName} ({paid}) después de renovar: se devolvió la " +
                        $"vigencia que había dado; ahora vence {PaymentText.Day(subscription.ValidUntil)} ({origin}).",
                        Severity: AuditSeverity.Critical),
                    cancellationToken);
                break;
            case PaymentOutcome.Failed:
                await audit.WriteAsync(
                    new AuditEntry("licensing", "PAYMENT_FAILED", nameof(PaymentOrder), order.Id, order.AuditLabel,
                        $"Pago en línea {order.Reference} de {organization.LegalName}: la pasarela lo reportó {transaction.Status} ({origin})."),
                    cancellationToken);
                break;
        }

        return outcome;
    }
}

// ─────────────────────────────── Conciliación y portal ───────────────────────────────

/// <summary>
/// Órdenes para la conciliación en segundo plano (D12C-10): las pendientes y, durante ⚙️ 7 días, las rechazadas o vencidas (W1: un pago
/// aprobado tarde, o cuyo evento se perdió, igual se aplica). Solo la usa el host.
/// </summary>
public sealed record ListPendingPaymentOrdersQuery(int Limit = 200) : IQuery<IReadOnlyList<Guid>>;

internal sealed class ListPendingPaymentOrdersHandler(IPaymentStore store, PaymentsOptions options, IClock clock)
    : IQueryHandler<ListPendingPaymentOrdersQuery, IReadOnlyList<Guid>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(ListPendingPaymentOrdersQuery request, CancellationToken cancellationToken) =>
        Result.Success(await store.GetOrderIdsToReconcileAsync(
            Math.Clamp(request.Limit, 1, 1000), clock.UtcNow.AddDays(-Math.Max(0, options.RecheckClosedOrdersDays)), cancellationToken));
}

/// <summary>Concilia una orden (tarea en segundo plano): consulta la pasarela por la referencia, aplica y vence las viejas (⚙️ 24 h).</summary>
public sealed record ReconcilePaymentOrderCommand(Guid OrderId) : ICommand<string>;

internal sealed class ReconcilePaymentOrderHandler(PaymentReconciler reconciler) : ICommandHandler<ReconcilePaymentOrderCommand, string>
{
    public Task<Result<string>> Handle(ReconcilePaymentOrderCommand request, CancellationToken cancellationToken) =>
        reconciler.RunAsync(request.OrderId, "conciliación automática", cancellationToken);
}

/// <summary>"Consultar ahora" desde la pantalla Pagos: misma conciliación, a pedido.</summary>
[RequiresPermission(CloudPermissions.PaymentView)]
public sealed record CheckPaymentOrderCommand(Guid OrderId) : ICommand<string>;

internal sealed class CheckPaymentOrderHandler(PaymentReconciler reconciler, PortalChange portal) : ICommandHandler<CheckPaymentOrderCommand, string>
{
    public Task<Result<string>> Handle(CheckPaymentOrderCommand request, CancellationToken cancellationToken) =>
        reconciler.RunAsync(request.OrderId, $"consulta de {portal.ActorName}", cancellationToken);
}

public sealed class PaymentReconciler(
    IPaymentStore store, IPaymentGateway gateway, PaymentApplier applier, PaymentsOptions options, IAuditWriter audit, IIdGenerator ids, IClock clock)
{
    public async Task<Result<string>> RunAsync(Guid orderId, string origin, CancellationToken cancellationToken)
    {
        if (!gateway.IsConfigured)
        {
            return PaymentErrors.NotConfigured;
        }

        var order = await store.GetOrderAsync(orderId, cancellationToken);
        if (order is null)
        {
            return PaymentErrors.OrderNotFound;
        }

        if (order.IsApplied || order.Status == PaymentOrderStatus.ReviewRequired)
        {
            return "La orden ya está cerrada; no se consultó la pasarela.";
        }

        var transactions = await gateway.FindByReferenceAsync(order.Reference, cancellationToken);
        if (transactions is null)
        {
            return PaymentErrors.GatewayUnavailable;
        }

        // Si hay varias (reintentos en el checkout), manda la aprobada; si no, la pendiente; si no, la última reportada.
        var transaction = transactions.FirstOrDefault(t => t.Status == GatewayStatuses.Approved)
                          ?? transactions.FirstOrDefault(t => t.Status == GatewayStatuses.Pending)
                          ?? (transactions.Count > 0 ? transactions[^1] : null);
        var now = clock.UtcNow;
        order = (await store.LockOrderAsync(order.Reference, cancellationToken))!;
        var wasPending = order.Status == PaymentOrderStatus.Pending;
        var outcome = PaymentOutcome.StillPending;
        if (transaction is not null)
        {
            outcome = await applier.ApplyAsync(order, transaction, origin, cancellationToken);

            // Las rechazadas o vencidas se revisan cada ciclo durante días: su consulta solo se registra si cambió algo.
            if (wasPending || outcome != PaymentOutcome.Ignored)
            {
                var query = PaymentEvent.Query(ids.NewId(), now, transaction, null, origin);
                query.LinkTo(order.Id);
                store.Add(query);
            }
        }

        // W2: solo vence si la pasarela NO la reporta pendiente (PSE o efectivo pueden tardar más del plazo).
        if (transaction is null && order.Expire(now, TimeSpan.FromHours(Math.Max(1, options.PendingExpiryHours))))
        {
            await audit.WriteAsync(
                new AuditEntry("licensing", "PAYMENT_ORDER_EXPIRED", nameof(PaymentOrder), order.Id, order.AuditLabel,
                    $"Orden de pago {order.Reference} vencida: sin pago confirmado en {options.PendingExpiryHours} h."),
                cancellationToken);
            return "La orden venció sin pago confirmado.";
        }

        return transaction is null
            ? "La pasarela no tiene transacciones para esta referencia (el cliente no completó el pago)."
            : $"Transacción {transaction.Id}: {transaction.Status} → {PaymentText.Outcome(outcome)}.";
    }
}

/// <summary>"Marcar revisado" con la nota de lo que se hizo (renovación manual, devolución en el panel de Wompi…).</summary>
[RequiresPermission(CloudPermissions.PaymentManage)]
public sealed record MarkPaymentReviewedCommand(Guid OrderId, string Note) : ICommand;

internal sealed class MarkPaymentReviewedHandler(IPaymentStore store, PortalChange portal, IAuditWriter audit) : ICommandHandler<MarkPaymentReviewedCommand>
{
    public async Task<Result> Handle(MarkPaymentReviewedCommand request, CancellationToken cancellationToken)
    {
        var order = await store.GetOrderAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            return PaymentErrors.OrderNotFound;
        }

        var change = portal.Create();
        var reviewed = order.MarkReviewed(change.ActorId, request.Note, change.Now);
        if (reviewed.IsFailure)
        {
            return reviewed;
        }

        await audit.WriteAsync(
            new AuditEntry("licensing", "PAYMENT_REVIEWED", nameof(PaymentOrder), order.Id, order.AuditLabel,
                $"{portal.ActorName} marcó como revisado el pago {order.Reference}: {order.ReviewNote}", Severity: AuditSeverity.Warning),
            cancellationToken);
        return Result.Success();
    }
}

[RequiresPermission(CloudPermissions.PaymentView)]
public sealed record ListPaymentOrdersQuery(PaymentOrderFilter Filter) : IQuery<IReadOnlyList<PaymentOrderRowDto>>;

internal sealed class ListPaymentOrdersHandler(IPaymentReadModel read) : IQueryHandler<ListPaymentOrdersQuery, IReadOnlyList<PaymentOrderRowDto>>
{
    public async Task<Result<IReadOnlyList<PaymentOrderRowDto>>> Handle(ListPaymentOrdersQuery request, CancellationToken cancellationToken) =>
        Result.Success(await read.ListOrdersAsync(request.Filter ?? new PaymentOrderFilter(), cancellationToken));
}

[RequiresPermission(CloudPermissions.PaymentView)]
public sealed record GetPaymentOrderQuery(Guid OrderId) : IQuery<PaymentOrderDetailDto>;

internal sealed class GetPaymentOrderHandler(IPaymentReadModel read) : IQueryHandler<GetPaymentOrderQuery, PaymentOrderDetailDto>
{
    public async Task<Result<PaymentOrderDetailDto>> Handle(GetPaymentOrderQuery request, CancellationToken cancellationToken) =>
        await read.GetOrderAsync(request.OrderId, cancellationToken) is { } detail ? detail : PaymentErrors.OrderNotFound;
}

/// <summary>Alerta del tablero: pagos que requieren revisión (D12C-06) y pendientes.</summary>
[RequiresPermission(CloudPermissions.DashboardView)]
public sealed record GetPaymentAlertsQuery : IQuery<PaymentAlertsDto>;

internal sealed class GetPaymentAlertsHandler(IPaymentReadModel read, IPaymentGateway gateway) : IQueryHandler<GetPaymentAlertsQuery, PaymentAlertsDto>
{
    public async Task<Result<PaymentAlertsDto>> Handle(GetPaymentAlertsQuery request, CancellationToken cancellationToken)
    {
        var (review, pending) = await read.CountAlertsAsync(cancellationToken);
        var toReview = review == 0
            ? []
            : (await read.ListOrdersAsync(new PaymentOrderFilter(Status: "REVIEW_REQUIRED"), cancellationToken)).Where(o => o.ReviewedAt is null).Take(20).ToList();
        return new PaymentAlertsDto(gateway.IsConfigured, review, pending, toReview);
    }
}

// ─────────────────────────────── Cliente: "Mi suscripción" ───────────────────────────────

/// <summary>Las empresas de la cuenta del cliente con su vencimiento e historial de pagos (D12C-09).</summary>
[RequiresPermission(CloudPermissions.SubscriptionSelfView)]
public sealed record GetMySubscriptionsQuery : IQuery<CustomerSubscriptionsDto>;

internal sealed class GetMySubscriptionsHandler(IPortalUserContext user, IPaymentStore store, IPaymentReadModel read, IPaymentGateway gateway, IClock clock)
    : IQueryHandler<GetMySubscriptionsQuery, CustomerSubscriptionsDto>
{
    public async Task<Result<CustomerSubscriptionsDto>> Handle(GetMySubscriptionsQuery request, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId || await store.GetCustomerAccountAsync(userId, cancellationToken) is not { } account)
        {
            return Result.Success(new CustomerSubscriptionsDto(gateway.IsConfigured, []));
        }

        var organizations = await read.CustomerSubscriptionsAsync(account, clock.UtcNow, cancellationToken);
        return new CustomerSubscriptionsDto(gateway.IsConfigured, [.. organizations.Select(o => o with { CanPay = gateway.IsConfigured && o.CanPay })]);
    }
}

/// <summary>Enlace de pago de una empresa del cliente (solo de su cuenta) para el botón "Pagar".</summary>
[RequiresPermission(CloudPermissions.SubscriptionSelfView)]
public sealed record EnsureMyPaymentLinkCommand(Guid OrganizationId) : ICommand<PaymentLinkDto>;

internal sealed class EnsureMyPaymentLinkHandler(IPortalUserContext user, IPaymentStore store, ILicensingStore licensing, PaymentLinks links)
    : ICommandHandler<EnsureMyPaymentLinkCommand, PaymentLinkDto>
{
    private static readonly Error Forbidden = Error.Forbidden("PAYMENTS.FORBIDDEN", "No tiene acceso a esa empresa.");

    public async Task<Result<PaymentLinkDto>> Handle(EnsureMyPaymentLinkCommand request, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId || await store.GetCustomerAccountAsync(userId, cancellationToken) is not { } account
            || await licensing.GetOrganizationAsync(request.OrganizationId, cancellationToken) is not { } organization || organization.AccountId != account)
        {
            return Forbidden;
        }

        if (await licensing.GetCurrentSubscriptionAsync(organization.Id, cancellationToken) is not { } subscription)
        {
            return LicensingErrors.SubscriptionNotFound;
        }

        return await links.EnsureAsync(subscription.Id, cancellationToken) is { } link ? PaymentLinks.ToDto(link) : LicensingErrors.SubscriptionNotFound;
    }
}

/// <summary>Textos comunes del pago en línea.</summary>
public static class PaymentText
{
    private static readonly CultureInfo Colombia = CultureInfo.GetCultureInfo("es-CO");

    public static string Money(decimal value) => value.ToString("$ #,##0", Colombia);

    public static string Day(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, BusinessTimeZones.Colombia).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Period(BillingPeriod period) => period == BillingPeriod.Monthly ? "mensual" : "anual";

    public static string Periods(BillingPeriod period, int periods) => period == BillingPeriod.Monthly
        ? periods == 1 ? "1 mes" : string.Create(CultureInfo.InvariantCulture, $"{periods} meses")
        : periods == 1 ? "1 año" : string.Create(CultureInfo.InvariantCulture, $"{periods} años");

    public static string Outcome(PaymentOutcome outcome) => outcome switch
    {
        PaymentOutcome.Renewed => "suscripción renovada",
        PaymentOutcome.ReviewRequired => "requiere revisión",
        PaymentOutcome.Failed => "pago no aprobado",
        PaymentOutcome.StillPending => "sigue pendiente",
        _ => "sin cambios",
    };

    /// <summary>
    /// Registro de un evento con firma inválida: SOLO metadatos (longitud y SHA-256 del cuerpo), nunca el contenido, que puede mandarlo
    /// cualquiera.
    /// </summary>
    public static string RejectedMetadata(string? body)
    {
        var text = body ?? string.Empty;
        return JsonSerializer.Serialize(new { rechazado = true, longitud = text.Length, sha256 = WompiProtocol.Sha256Hex(text) });
    }

    /// <summary>Cuerpo recibido para el registro: el JSON tal cual si es válido y no muy grande; si no, un resumen recortado.</summary>
    public static string Payload(string? body)
    {
        var text = body ?? string.Empty;
        if (text.Length <= PaymentEvent.MaxPayloadChars)
        {
            try
            {
                using var _ = JsonDocument.Parse(text);
                return text;
            }
            catch (JsonException)
            {
                // Se guarda recortado como texto dentro de un objeto JSON.
            }
        }

        return JsonSerializer.Serialize(new { recortado = true, longitud = text.Length, inicio = text.Length > 2000 ? text[..2000] : text });
    }
}
