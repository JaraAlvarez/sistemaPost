using System.Globalization;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Messaging;
using Pos.Cloud.Abstractions;
using Pos.Cloud.Licensing.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Cloud.Licensing.Application;

/// <summary>Contexto común de los cambios hechos desde el portal.</summary>
public sealed class PortalChange(IPortalUserContext user, IIdGenerator ids, IClock clock)
{
    public ChangeContext Create() => new(user.UserId ?? SystemActor.Id, clock.UtcNow, ids);

    public string ActorName => user.DisplayName ?? SystemActor.DisplayName;
}

[RequiresPermission(CloudPermissions.LicensingView)]
public sealed record ListSubscriptionsQuery(string? Status = null) : IQuery<IReadOnlyList<SubscriptionRowDto>>;

internal sealed class ListSubscriptionsHandler(ILicensingReadModel read, IClock clock) : IQueryHandler<ListSubscriptionsQuery, IReadOnlyList<SubscriptionRowDto>>
{
    public async Task<Result<IReadOnlyList<SubscriptionRowDto>>> Handle(ListSubscriptionsQuery request, CancellationToken cancellationToken)
    {
        var rows = await read.ListSubscriptionsAsync(clock.UtcNow, cancellationToken);
        return string.IsNullOrWhiteSpace(request.Status)
            ? Result.Success(rows)
            : Result.Success<IReadOnlyList<SubscriptionRowDto>>([.. rows.Where(r => string.Equals(r.Status, request.Status, StringComparison.OrdinalIgnoreCase))]);
    }
}

/// <summary>
/// Crea la suscripción de una empresa: de prueba (<paramref name="TrialDays"/>, por defecto los configurados) o pagada desde el
/// inicio (con la referencia del pago). Edición: <c>SINGLE</c> o <c>MULTI</c>; periodicidad: <c>MONTHLY</c> o <c>ANNUAL</c>.
/// </summary>
[RequiresPermission(CloudPermissions.SubscriptionManage)]
public sealed record CreateSubscriptionCommand(
    Guid OrganizationId, string Edition, string BillingPeriod, bool Trial, int? TrialDays = null, string? PaymentReference = null, int Periods = 1)
    : ICommand<Guid>;

internal sealed class CreateSubscriptionHandler(ILicensingStore store, LicensingOptions options, PortalChange portal, IAuditWriter audit)
    : ICommandHandler<CreateSubscriptionCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateSubscriptionCommand request, CancellationToken cancellationToken)
    {
        var organization = await store.GetOrganizationAsync(request.OrganizationId, cancellationToken);
        if (organization is null)
        {
            return LicensingErrors.OrganizationNotFound;
        }

        if (!LicensingCodes.TryParseEdition(request.Edition, out var edition))
        {
            return LicenseErrorsFor.Field("edition", "Edición inválida: SINGLE (Caja Única) o MULTI (Multicaja).");
        }

        if (!Enum.TryParse<BillingPeriod>(request.BillingPeriod, ignoreCase: true, out var period) || !Enum.IsDefined(period))
        {
            return LicenseErrorsFor.Field("billingPeriod", "Periodicidad inválida: MONTHLY o ANNUAL.");
        }

        if (await store.GetCurrentSubscriptionAsync(organization.Id, cancellationToken) is not null)
        {
            return LicensingErrors.SubscriptionExists;
        }

        var change = portal.Create();
        var created = request.Trial
            ? Subscription.StartTrial(change.Ids.NewId(), organization.Id, edition, period, request.TrialDays ?? options.TrialDays, options.GraceDays, change)
            : Subscription.StartPaid(
                change.Ids.NewId(), organization.Id, edition, period, request.PaymentReference ?? string.Empty, request.Periods, options.GraceDays, change);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var subscription = created.Value;
        store.Add(subscription);
        await audit.WriteAsync(
            new AuditEntry("licensing", "SUBSCRIPTION_CREATED", nameof(Subscription), subscription.Id, organization.AuditLabel,
                $"{portal.ActorName} creó la suscripción {edition.DisplayName()} de {organization.LegalName}: " +
                (request.Trial ? $"prueba hasta {subscription.ValidUntil:yyyy-MM-dd}." : $"pagada ({request.PaymentReference}) hasta {subscription.ValidUntil:yyyy-MM-dd}.")),
            cancellationToken);
        return subscription.Id;
    }
}

[RequiresPermission(CloudPermissions.SubscriptionManage)]
public sealed record RenewSubscriptionCommand(Guid SubscriptionId, string PaymentReference, int Periods = 1) : ICommand;

internal sealed class RenewSubscriptionHandler(ILicensingStore store, LicensingOptions options, PortalChange portal, IAuditWriter audit)
    : ICommandHandler<RenewSubscriptionCommand>
{
    public Task<Result> Handle(RenewSubscriptionCommand request, CancellationToken cancellationToken) =>
        SubscriptionAction.RunAsync(store, portal, audit, request.SubscriptionId, "SUBSCRIPTION_RENEWED", AuditSeverity.Info,
            (s, change) => s.Renew(request.PaymentReference, request.Periods, options.GraceDays, change),
            (s, org) => $"{portal.ActorName} registró el pago {request.PaymentReference} de {org.LegalName}: vigente hasta {s.ValidUntil:yyyy-MM-dd}.",
            cancellationToken);
}

[RequiresPermission(CloudPermissions.SubscriptionManage)]
public sealed record SuspendSubscriptionCommand(Guid SubscriptionId, string Reason) : ICommand;

internal sealed class SuspendSubscriptionHandler(ILicensingStore store, PortalChange portal, IAuditWriter audit) : ICommandHandler<SuspendSubscriptionCommand>
{
    public Task<Result> Handle(SuspendSubscriptionCommand request, CancellationToken cancellationToken) =>
        SubscriptionAction.RunAsync(store, portal, audit, request.SubscriptionId, "SUBSCRIPTION_SUSPENDED", AuditSeverity.Critical,
            (s, change) => s.Suspend(request.Reason, change),
            (_, org) => $"{portal.ActorName} suspendió la suscripción de {org.LegalName}: {request.Reason.Trim()}. Sus instalaciones lo reciben en el siguiente check-in.",
            cancellationToken);
}

[RequiresPermission(CloudPermissions.SubscriptionSupport)]
public sealed record ReactivateSubscriptionCommand(Guid SubscriptionId, string Reason) : ICommand;

internal sealed class ReactivateSubscriptionHandler(ILicensingStore store, PortalChange portal, IAuditWriter audit) : ICommandHandler<ReactivateSubscriptionCommand>
{
    public Task<Result> Handle(ReactivateSubscriptionCommand request, CancellationToken cancellationToken) =>
        SubscriptionAction.RunAsync(store, portal, audit, request.SubscriptionId, "SUBSCRIPTION_REACTIVATED", AuditSeverity.Warning,
            (s, change) => s.Reactivate(request.Reason, change),
            (s, org) => $"{portal.ActorName} reactivó la suscripción de {org.LegalName} ({s.Status.DisplayName()}): {request.Reason.Trim()}.",
            cancellationToken);
}

/// <summary>Cancela la suscripción y revoca su licencia vigente.</summary>
[RequiresPermission(CloudPermissions.SubscriptionManage)]
public sealed record CancelSubscriptionCommand(Guid SubscriptionId, string Reason) : ICommand;

internal sealed class CancelSubscriptionHandler(ILicensingStore store, PortalChange portal, IAuditWriter audit) : ICommandHandler<CancelSubscriptionCommand>
{
    public async Task<Result> Handle(CancelSubscriptionCommand request, CancellationToken cancellationToken)
    {
        var result = await SubscriptionAction.RunAsync(store, portal, audit, request.SubscriptionId, "SUBSCRIPTION_CANCELLED", AuditSeverity.Critical,
            (s, change) => s.Cancel(request.Reason, change),
            (_, org) => $"{portal.ActorName} canceló la suscripción de {org.LegalName}: {request.Reason.Trim()}.",
            cancellationToken);
        if (result.IsFailure)
        {
            return result;
        }

        var subscription = (await store.GetSubscriptionAsync(request.SubscriptionId, cancellationToken))!;
        if (await store.GetActiveLicenseAsync(subscription.OrganizationId, cancellationToken) is { } license && license.SubscriptionId == subscription.Id)
        {
            var revoked = license.Revoke($"Suscripción cancelada: {request.Reason.Trim()}", portal.Create().Now);
            if (revoked.IsFailure)
            {
                return revoked;
            }
        }

        return Result.Success();
    }
}

[RequiresPermission(CloudPermissions.SubscriptionSupport)]
public sealed record ExtendGraceCommand(Guid SubscriptionId, int Days, string Reason) : ICommand;

internal sealed class ExtendGraceHandler(ILicensingStore store, PortalChange portal, IAuditWriter audit) : ICommandHandler<ExtendGraceCommand>
{
    public Task<Result> Handle(ExtendGraceCommand request, CancellationToken cancellationToken) =>
        SubscriptionAction.RunAsync(store, portal, audit, request.SubscriptionId, "SUBSCRIPTION_GRACE_EXTENDED", AuditSeverity.Warning,
            (s, change) => s.ExtendGrace(request.Days, request.Reason, change),
            (s, org) => string.Create(CultureInfo.InvariantCulture,
                $"{portal.ActorName} extendió {request.Days} días la gracia de {org.LegalName} (hasta {s.GraceUntil:yyyy-MM-dd}): {request.Reason.Trim()}."),
            cancellationToken);
}

[RequiresPermission(CloudPermissions.SubscriptionManage)]
public sealed record ChangeEditionCommand(Guid SubscriptionId, string Edition, string Reason) : ICommand;

internal sealed class ChangeEditionHandler(ILicensingStore store, PortalChange portal, IAuditWriter audit) : ICommandHandler<ChangeEditionCommand>
{
    public async Task<Result> Handle(ChangeEditionCommand request, CancellationToken cancellationToken)
    {
        if (!LicensingCodes.TryParseEdition(request.Edition, out var edition))
        {
            return LicenseErrorsFor.Field("edition", "Edición inválida: SINGLE (Caja Única) o MULTI (Multicaja).");
        }

        var subscription = await store.GetSubscriptionAsync(request.SubscriptionId, cancellationToken);
        if (subscription is not null && edition == LicenseEdition.SingleTerminal
            && await store.HasActiveStoreServersAsync(subscription.OrganizationId, cancellationToken))
        {
            return LicensingErrors.EditionHasStoreServers;
        }

        return await SubscriptionAction.RunAsync(store, portal, audit, request.SubscriptionId, "SUBSCRIPTION_EDITION_CHANGED", AuditSeverity.Warning,
            (s, change) => s.ChangeEdition(edition, request.Reason, change),
            (_, org) => $"{portal.ActorName} cambió la edición de {org.LegalName} a {edition.DisplayName()}: {request.Reason.Trim()}.",
            cancellationToken);
    }
}

/// <summary>
/// Actualiza el estado guardado de las suscripciones según las fechas (vigente → en gracia → vencida) y deja su evento. Lo
/// ejecuta un proceso en segundo plano del host (cada hora) para que el tablero y el historial estén al día.
/// </summary>
public sealed record RefreshSubscriptionStatusesCommand : ICommand<int>;

internal sealed class RefreshSubscriptionStatusesHandler(ILicensingStore store, IIdGenerator ids, IClock clock) : ICommandHandler<RefreshSubscriptionStatusesCommand, int>
{
    public async Task<Result<int>> Handle(RefreshSubscriptionStatusesCommand request, CancellationToken cancellationToken)
    {
        var change = new ChangeContext(SystemActor.Id, clock.UtcNow, ids);
        var changed = 0;
        foreach (var subscription in await store.GetSubscriptionsToRefreshAsync(cancellationToken))
        {
            changed += subscription.Refresh(change) ? 1 : 0;
        }

        return changed;
    }
}

internal static class SubscriptionAction
{
    public static async Task<Result> RunAsync(
        ILicensingStore store,
        PortalChange portal,
        IAuditWriter audit,
        Guid subscriptionId,
        string action,
        AuditSeverity severity,
        Func<Subscription, ChangeContext, Result> apply,
        Func<Subscription, Organization, string> summary,
        CancellationToken cancellationToken)
    {
        var subscription = await store.GetSubscriptionAsync(subscriptionId, cancellationToken);
        if (subscription is null)
        {
            return LicensingErrors.SubscriptionNotFound;
        }

        var result = apply(subscription, portal.Create());
        if (result.IsFailure)
        {
            return result;
        }

        var organization = (await store.GetOrganizationAsync(subscription.OrganizationId, cancellationToken))!;
        await audit.WriteAsync(
            new AuditEntry("licensing", action, nameof(Subscription), subscription.Id, organization.AuditLabel, summary(subscription, organization), Severity: severity),
            cancellationToken);
        return Result.Success();
    }
}

internal static class LicenseErrorsFor
{
    public static Error Field(string field, string message) => LicenseApiErrors.InvalidField(field, message);
}
