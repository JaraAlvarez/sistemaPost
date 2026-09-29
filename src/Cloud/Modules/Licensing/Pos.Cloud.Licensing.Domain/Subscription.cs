using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;

namespace Pos.Cloud.Licensing.Domain;

/// <summary>Quién hace el cambio (usuario del portal o el usuario técnico) y cuándo.</summary>
public sealed record ChangeContext(Guid ActorId, DateTimeOffset Now, IIdGenerator Ids);

/// <summary>Evento de la suscripción (solo inserción, L-09): el historial explica cualquier reclamo de un cliente.</summary>
public sealed class SubscriptionEvent : Entity<Guid>
{
    internal SubscriptionEvent(Guid id, Guid subscriptionId, SubscriptionEventType type, DateTimeOffset occurredAt, Guid actorId)
        : base(id)
    {
        SubscriptionId = subscriptionId;
        Type = type;
        OccurredAt = occurredAt;
        ActorId = actorId;
    }

    public Guid SubscriptionId { get; private set; }

    public SubscriptionEventType Type { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid ActorId { get; private set; }

    public string? PaymentReference { get; private set; }

    public DateTimeOffset? PeriodStart { get; private set; }

    public DateTimeOffset? PeriodEnd { get; private set; }

    public string? OldValue { get; private set; }

    public string? NewValue { get; private set; }

    public string? Reason { get; private set; }

    internal SubscriptionEvent With(
        string? paymentReference = null, DateTimeOffset? periodStart = null, DateTimeOffset? periodEnd = null, string? oldValue = null,
        string? newValue = null, string? reason = null)
    {
        PaymentReference = paymentReference;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        OldValue = oldValue;
        NewValue = newValue;
        Reason = reason;
        return this;
    }
}

/// <summary>
/// Suscripción de una empresa: edición, periodicidad, vigencia, prueba y gracia (docs/fases/fase-12a-propuesta.md §4, §5.4).
/// Estados guardados por acciones del portal (crear, renovar, suspender, reactivar, cancelar) y transiciones por el paso del
/// tiempo (<see cref="Refresh"/>): vigente → en gracia (PAST_DUE) al pasar <see cref="ValidUntil"/> → vencida (EXPIRED) al pasar
/// <see cref="GraceUntil"/>. Cada cambio deja un <see cref="SubscriptionEvent"/>.
/// </summary>
[Audited("licensing")]
public sealed class Subscription : AggregateRoot<Guid>, IHasAuditLabel
{
    public const int MaxTrialDays = 90;
    public const int MaxGraceDays = 90;
    public const int MaxGraceExtension = 30;
    public const int MaxPeriods = 24;

    private readonly List<SubscriptionEvent> _events = [];

    private Subscription(Guid id, Guid organizationId, LicenseEdition edition, BillingPeriod billingPeriod, int graceDays)
        : base(id)
    {
        OrganizationId = organizationId;
        Edition = edition;
        BillingPeriod = billingPeriod;
        GraceDays = graceDays;
    }

    public Guid OrganizationId { get; private set; }

    public LicenseEdition Edition { get; private set; }

    public BillingPeriod BillingPeriod { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    public DateTimeOffset? TrialEndsAt { get; private set; }

    public DateTimeOffset? CurrentPeriodStart { get; private set; }

    public DateTimeOffset? CurrentPeriodEnd { get; private set; }

    public int GraceDays { get; private set; }

    public string? SuspendedReason { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>Eventos nuevos de esta unidad de trabajo (el historial completo se consulta aparte).</summary>
    public IReadOnlyCollection<SubscriptionEvent> Events => _events.AsReadOnly();

    /// <summary>Fin del periodo pagado o, si nunca se pagó, fin de la prueba. Es el <c>valid_until</c> del token.</summary>
    public DateTimeOffset ValidUntil => CurrentPeriodEnd ?? TrialEndsAt!.Value;

    public DateTimeOffset GraceUntil => ValidUntil.AddDays(GraceDays);

    public bool IsPaid => CurrentPeriodEnd is not null;

    public string AuditLabel => $"Suscripción {Edition.DisplayName()} ({Status.DisplayName()})";

    /// <summary>¿Admite activar instalaciones nuevas? Vigente, en prueba o en gracia.</summary>
    public bool AllowsActivation => Status is SubscriptionStatus.Trial or SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    /// <summary>Suscripción de prueba de <paramref name="trialDays"/> días.</summary>
    public static Result<Subscription> StartTrial(
        Guid id, Guid organizationId, LicenseEdition edition, BillingPeriod billingPeriod, int trialDays, int graceDays, ChangeContext change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (trialDays is < 1 or > MaxTrialDays)
        {
            return LicensingErrors.InvalidTrialDays;
        }

        if (graceDays is < 0 or > MaxGraceDays)
        {
            return LicensingErrors.InvalidGraceDays;
        }

        var subscription = new Subscription(id, organizationId, edition, billingPeriod, graceDays)
        {
            Status = SubscriptionStatus.Trial,
            TrialEndsAt = change.Now.AddDays(trialDays),
        };
        subscription.Record(SubscriptionEventType.Created, change)
            .With(periodEnd: subscription.TrialEndsAt, newValue: SubscriptionStatus.Trial.ToCode(), reason: $"Prueba de {trialDays} días");
        return subscription;
    }

    /// <summary>Suscripción pagada desde el inicio (registra también la renovación con la referencia del pago).</summary>
    public static Result<Subscription> StartPaid(
        Guid id, Guid organizationId, LicenseEdition edition, BillingPeriod billingPeriod, string paymentReference, int periods, int graceDays,
        ChangeContext change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (graceDays is < 0 or > MaxGraceDays)
        {
            return LicensingErrors.InvalidGraceDays;
        }

        var subscription = new Subscription(id, organizationId, edition, billingPeriod, graceDays) { Status = SubscriptionStatus.Active };
        if (!TryValidateRenewal(paymentReference, periods, out var reference, out var error))
        {
            return error;
        }

        subscription.Record(SubscriptionEventType.Created, change).With(newValue: SubscriptionStatus.Active.ToCode());
        subscription.ApplyRenewal(reference, periods, graceDays, change);
        return subscription;
    }

    /// <summary>
    /// Registra un pago y extiende la vigencia (L-10, renovación manual). Activa o en gracia: continúa desde el fin del periodo
    /// actual (se conserva el ciclo). En prueba o vencida: el periodo empieza ahora. La gracia vuelve al valor por defecto.
    /// </summary>
    public Result Renew(string paymentReference, int periods, int defaultGraceDays, ChangeContext change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Refresh(change);
        if (Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled)
        {
            return LicensingErrors.InvalidTransition;
        }

        if (!TryValidateRenewal(paymentReference, periods, out var reference, out var error))
        {
            return error;
        }

        if (defaultGraceDays is < 0 or > MaxGraceDays)
        {
            return LicensingErrors.InvalidGraceDays;
        }

        ApplyRenewal(reference, periods, defaultGraceDays, change);
        return Result.Success();
    }

    public Result ChangeEdition(LicenseEdition edition, string reason, ChangeContext change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Status == SubscriptionStatus.Cancelled)
        {
            return LicensingErrors.InvalidTransition;
        }

        if (edition == Edition)
        {
            return LicensingErrors.SameEdition;
        }

        if (!TryReason(reason, out var cleaned))
        {
            return LicensingErrors.ReasonRequired;
        }

        var old = Edition;
        Edition = edition;
        Record(SubscriptionEventType.EditionChanged, change).With(oldValue: old.ToCode(), newValue: edition.ToCode(), reason: cleaned);
        return Result.Success();
    }

    /// <summary>Suspende (falta de pago, fraude…). El POS lo recibe en su siguiente check-in y pasa a restringido.</summary>
    public Result Suspend(string reason, ChangeContext change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled)
        {
            return LicensingErrors.InvalidTransition;
        }

        if (!TryReason(reason, out var cleaned))
        {
            return LicensingErrors.ReasonRequired;
        }

        var old = Status;
        Status = SubscriptionStatus.Suspended;
        SuspendedReason = cleaned;
        Record(SubscriptionEventType.Suspended, change).With(oldValue: old.ToCode(), newValue: Status.ToCode(), reason: cleaned);
        return Result.Success();
    }

    /// <summary>Levanta la suspensión: vuelve al estado que corresponde por fechas (vigente, en gracia o vencida).</summary>
    public Result Reactivate(string reason, ChangeContext change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Status != SubscriptionStatus.Suspended)
        {
            return LicensingErrors.InvalidTransition;
        }

        if (!TryReason(reason, out var cleaned))
        {
            return LicensingErrors.ReasonRequired;
        }

        Status = StatusByDate(change.Now);
        SuspendedReason = null;
        Record(SubscriptionEventType.Reactivated, change)
            .With(oldValue: SubscriptionStatus.Suspended.ToCode(), newValue: Status.ToCode(), reason: cleaned);
        return Result.Success();
    }

    /// <summary>Cancela definitivamente (la empresa puede tener después una suscripción nueva).</summary>
    public Result Cancel(string reason, ChangeContext change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Status == SubscriptionStatus.Cancelled)
        {
            return LicensingErrors.InvalidTransition;
        }

        if (!TryReason(reason, out var cleaned))
        {
            return LicensingErrors.ReasonRequired;
        }

        var old = Status;
        Status = SubscriptionStatus.Cancelled;
        CancelledAt = change.Now;
        SuspendedReason = null;
        Record(SubscriptionEventType.Cancelled, change).With(oldValue: old.ToCode(), newValue: Status.ToCode(), reason: cleaned);
        return Result.Success();
    }

    /// <summary>Extiende la gracia del periodo actual (p. ej. el pago está en camino). La renovación la devuelve al valor normal.</summary>
    public Result ExtendGrace(int days, string reason, ChangeContext change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Status == SubscriptionStatus.Cancelled)
        {
            return LicensingErrors.InvalidTransition;
        }

        if (days is < 1 or > MaxGraceExtension || GraceDays + days > MaxGraceDays)
        {
            return LicensingErrors.InvalidGraceDays;
        }

        if (!TryReason(reason, out var cleaned))
        {
            return LicensingErrors.ReasonRequired;
        }

        var old = GraceDays;
        GraceDays += days;
        Record(SubscriptionEventType.GraceExtended, change)
            .With(oldValue: old.ToString(System.Globalization.CultureInfo.InvariantCulture), newValue: GraceDays.ToString(System.Globalization.CultureInfo.InvariantCulture), reason: cleaned);
        Refresh(change);
        return Result.Success();
    }

    /// <summary>
    /// Aplica las transiciones por fecha (vigente → en gracia → vencida, y de vuelta si cambió la gracia). Las suspendidas y
    /// canceladas no cambian solas. Devuelve <c>true</c> si cambió el estado.
    /// </summary>
    public bool Refresh(ChangeContext change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled)
        {
            return false;
        }

        var target = StatusByDate(change.Now);
        if (target == Status)
        {
            return false;
        }

        var old = Status;
        Status = target;
        Record(SubscriptionEventType.StatusChanged, change).With(oldValue: old.ToCode(), newValue: target.ToCode());
        return true;
    }

    /// <summary>Estado que corresponde por fechas, sin modificar nada (tableros y consultas).</summary>
    public SubscriptionStatus StatusAt(DateTimeOffset instant) =>
        Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled ? Status : StatusByDate(instant);

    private SubscriptionStatus StatusByDate(DateTimeOffset instant) =>
        instant <= ValidUntil
            ? IsPaid ? SubscriptionStatus.Active : SubscriptionStatus.Trial
            : instant <= GraceUntil ? SubscriptionStatus.PastDue : SubscriptionStatus.Expired;

    private void ApplyRenewal(string reference, int periods, int graceDays, ChangeContext change)
    {
        var continues = IsPaid && Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue;
        var start = continues ? CurrentPeriodEnd!.Value : change.Now;
        var end = BillingPeriod == BillingPeriod.Monthly ? start.AddMonths(periods) : start.AddYears(periods);
        var old = Status;
        CurrentPeriodStart = start;
        CurrentPeriodEnd = end;
        GraceDays = graceDays;
        Status = SubscriptionStatus.Active;
        Record(SubscriptionEventType.Renewed, change)
            .With(paymentReference: reference, periodStart: start, periodEnd: end, oldValue: old.ToCode(), newValue: Status.ToCode());
    }

    private static bool TryValidateRenewal(string paymentReference, int periods, out string reference, out Error error)
    {
        reference = (paymentReference ?? string.Empty).Trim();
        error = Error.None;
        if (reference.Length is < 3 or > 100)
        {
            error = LicensingErrors.PaymentReferenceRequired;
        }
        else if (periods is < 1 or > MaxPeriods)
        {
            error = LicensingErrors.InvalidPeriods;
        }

        return error == Error.None;
    }

    internal static bool TryReason(string? reason, out string cleaned)
    {
        cleaned = (reason ?? string.Empty).Trim();
        return cleaned.Length is >= 5 and <= 300;
    }

    private SubscriptionEvent Record(SubscriptionEventType type, ChangeContext change)
    {
        var entry = new SubscriptionEvent(change.Ids.NewId(), Id, type, change.Now, change.ActorId);
        _events.Add(entry);
        return entry;
    }
}
