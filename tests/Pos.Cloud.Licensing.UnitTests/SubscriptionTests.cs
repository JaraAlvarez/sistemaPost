using Pos.Cloud.Licensing.Domain;
using Pos.Licensing.Contracts;
using static Pos.Cloud.Licensing.UnitTests.TestSupport;

namespace Pos.Cloud.Licensing.UnitTests;

public class SubscriptionTests
{
    private const string Reason = "Solicitud del cliente";

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public void La_prueba_exige_entre_1_y_90_dias(int days) =>
        Subscription.StartTrial(Guid.CreateVersion7(), Guid.CreateVersion7(), LicenseEdition.SingleTerminal, BillingPeriod.Monthly, days, 7, Now)
            .Error.ShouldBe(LicensingErrors.InvalidTrialDays);

    [Theory]
    [InlineData(-1)]
    [InlineData(91)]
    public void La_gracia_inicial_va_de_0_a_90_dias(int grace)
    {
        Subscription.StartTrial(Guid.CreateVersion7(), Guid.CreateVersion7(), LicenseEdition.SingleTerminal, BillingPeriod.Monthly, 15, grace, Now)
            .Error.ShouldBe(LicensingErrors.InvalidGraceDays);
        Subscription.StartPaid(Guid.CreateVersion7(), Guid.CreateVersion7(), LicenseEdition.SingleTerminal, BillingPeriod.Monthly, "PAGO-1", 1, grace, Now)
            .Error.ShouldBe(LicensingErrors.InvalidGraceDays);
    }

    [Fact]
    public void La_prueba_vence_en_los_dias_indicados_y_deja_el_evento_de_creacion()
    {
        var subscription = Trial(days: 15, grace: 7);

        subscription.Status.ShouldBe(SubscriptionStatus.Trial);
        subscription.IsPaid.ShouldBeFalse();
        subscription.TrialEndsAt.ShouldBe(Start.AddDays(15));
        subscription.ValidUntil.ShouldBe(Start.AddDays(15));
        subscription.GraceUntil.ShouldBe(Start.AddDays(22));
        subscription.AllowsActivation.ShouldBeTrue();
        subscription.AuditLabel.ShouldBe("Suscripción Multicaja (En prueba)");
        var created = subscription.Events.ShouldHaveSingleItem();
        created.Type.ShouldBe(SubscriptionEventType.Created);
        created.SubscriptionId.ShouldBe(subscription.Id);
        created.ActorId.ShouldBe(Actor);
        created.OccurredAt.ShouldBe(Start);
        created.PeriodEnd.ShouldBe(Start.AddDays(15));
        created.NewValue.ShouldBe(SubscriptionStatuses.Trial);
        created.Reason.ShouldBe("Prueba de 15 días");
    }

    [Theory]
    [InlineData("ab", 1)]
    [InlineData("PAGO-1", 0)]
    [InlineData("PAGO-1", 25)]
    public void La_suscripcion_pagada_valida_la_referencia_y_los_periodos(string reference, int periods)
    {
        var result = Subscription.StartPaid(
            Guid.CreateVersion7(), Guid.CreateVersion7(), LicenseEdition.MultiTerminal, BillingPeriod.Monthly, reference, periods, 7, Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(reference.Length < 3 ? LicensingErrors.PaymentReferenceRequired : LicensingErrors.InvalidPeriods);
    }

    [Fact]
    public void La_suscripcion_pagada_mensual_empieza_ahora_y_registra_creacion_y_renovacion()
    {
        var subscription = Paid(BillingPeriod.Monthly, periods: 3);

        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        subscription.IsPaid.ShouldBeTrue();
        subscription.CurrentPeriodStart.ShouldBe(Start);
        subscription.CurrentPeriodEnd.ShouldBe(Start.AddMonths(3));
        subscription.ValidUntil.ShouldBe(Start.AddMonths(3));
        subscription.Events.Select(e => e.Type).ShouldBe([SubscriptionEventType.Created, SubscriptionEventType.Renewed]);
        var renewed = subscription.Events.Last();
        renewed.PaymentReference.ShouldBe("PAGO-001");
        renewed.PeriodStart.ShouldBe(Start);
        renewed.PeriodEnd.ShouldBe(Start.AddMonths(3));
        renewed.NewValue.ShouldBe(SubscriptionStatuses.Active);
    }

    [Fact]
    public void La_suscripcion_anual_suma_anios() =>
        Paid(BillingPeriod.Annual, periods: 2).CurrentPeriodEnd.ShouldBe(Start.AddYears(2));

    [Fact]
    public void Renovar_una_prueba_empieza_el_periodo_ahora()
    {
        var subscription = Trial();
        var later = Start.AddDays(5);

        subscription.Renew(" PAGO-777 ", 1, 10, At(later)).IsSuccess.ShouldBeTrue();

        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        subscription.CurrentPeriodStart.ShouldBe(later);
        subscription.CurrentPeriodEnd.ShouldBe(later.AddMonths(1));
        subscription.GraceDays.ShouldBe(10);
        var renewed = subscription.Events.Last();
        renewed.PaymentReference.ShouldBe("PAGO-777");
        renewed.OldValue.ShouldBe(SubscriptionStatuses.Trial);
    }

    [Fact]
    public void Renovar_una_vigente_conserva_el_ciclo()
    {
        var subscription = Paid();
        var end = subscription.CurrentPeriodEnd!.Value;

        subscription.Renew("PAGO-002", 1, 7, At(Start.AddDays(10))).IsSuccess.ShouldBeTrue();

        subscription.CurrentPeriodStart.ShouldBe(end);
        subscription.CurrentPeriodEnd.ShouldBe(end.AddMonths(1));
    }

    [Fact]
    public void Renovar_en_gracia_conserva_el_ciclo_y_vuelve_a_vigente()
    {
        var subscription = Paid();
        var end = subscription.CurrentPeriodEnd!.Value;

        subscription.Renew("PAGO-002", 1, 7, At(end.AddDays(2))).IsSuccess.ShouldBeTrue();

        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        subscription.CurrentPeriodStart.ShouldBe(end);
        subscription.Events.Select(e => e.Type).ShouldContain(SubscriptionEventType.StatusChanged);
    }

    [Fact]
    public void Renovar_una_vencida_empieza_el_periodo_ahora()
    {
        var subscription = Paid();
        var later = subscription.CurrentPeriodEnd!.Value.AddDays(30);

        subscription.Renew("PAGO-002", 2, 7, At(later)).IsSuccess.ShouldBeTrue();

        subscription.CurrentPeriodStart.ShouldBe(later);
        subscription.CurrentPeriodEnd.ShouldBe(later.AddMonths(2));
    }

    [Fact]
    public void Renovar_valida_estado_referencia_periodos_y_gracia()
    {
        var subscription = Paid();

        subscription.Renew("x", 1, 7, Now).Error.ShouldBe(LicensingErrors.PaymentReferenceRequired);
        subscription.Renew("PAGO-002", 30, 7, Now).Error.ShouldBe(LicensingErrors.InvalidPeriods);
        subscription.Renew("PAGO-002", 1, 91, Now).Error.ShouldBe(LicensingErrors.InvalidGraceDays);
        subscription.Suspend(Reason, Now).IsSuccess.ShouldBeTrue();
        subscription.Renew("PAGO-002", 1, 7, Now).Error.ShouldBe(LicensingErrors.InvalidTransition);
    }

    [Fact]
    public void Con_el_tiempo_pasa_a_gracia_y_luego_a_vencida_con_sus_eventos()
    {
        var subscription = Paid(grace: 7);
        var end = subscription.ValidUntil;

        subscription.Refresh(At(end)).ShouldBeFalse();
        subscription.Refresh(At(end.AddSeconds(1))).ShouldBeTrue();
        subscription.Status.ShouldBe(SubscriptionStatus.PastDue);
        subscription.AllowsActivation.ShouldBeTrue();
        subscription.Refresh(At(end.AddDays(7))).ShouldBeFalse();
        subscription.Refresh(At(end.AddDays(8))).ShouldBeTrue();
        subscription.Status.ShouldBe(SubscriptionStatus.Expired);
        subscription.AllowsActivation.ShouldBeFalse();

        var changes = subscription.Events.Where(e => e.Type == SubscriptionEventType.StatusChanged).ToList();
        changes.Select(e => (e.OldValue, e.NewValue)).ShouldBe(
        [
            (SubscriptionStatuses.Active, SubscriptionStatuses.PastDue),
            (SubscriptionStatuses.PastDue, SubscriptionStatuses.Expired),
        ]);
    }

    [Fact]
    public void Una_prueba_vencida_tambien_pasa_por_gracia()
    {
        var subscription = Trial(days: 10, grace: 5);

        subscription.StatusAt(Start.AddDays(9)).ShouldBe(SubscriptionStatus.Trial);
        subscription.StatusAt(Start.AddDays(12)).ShouldBe(SubscriptionStatus.PastDue);
        subscription.StatusAt(Start.AddDays(16)).ShouldBe(SubscriptionStatus.Expired);
        subscription.Status.ShouldBe(SubscriptionStatus.Trial);
    }

    [Fact]
    public void Suspender_y_reactivar_vuelve_al_estado_por_fechas()
    {
        var subscription = Paid();

        subscription.Suspend("  x  ", Now).Error.ShouldBe(LicensingErrors.ReasonRequired);
        subscription.Suspend(" Falta de pago ", Now).IsSuccess.ShouldBeTrue();
        subscription.Status.ShouldBe(SubscriptionStatus.Suspended);
        subscription.SuspendedReason.ShouldBe("Falta de pago");
        subscription.AllowsActivation.ShouldBeFalse();
        subscription.Suspend(Reason, Now).Error.ShouldBe(LicensingErrors.InvalidTransition);
        subscription.Refresh(At(Start.AddYears(1))).ShouldBeFalse();
        subscription.StatusAt(Start.AddYears(1)).ShouldBe(SubscriptionStatus.Suspended);

        subscription.Reactivate("x", Now).Error.ShouldBe(LicensingErrors.ReasonRequired);
        subscription.Reactivate("Pago recibido", At(Start.AddDays(3))).IsSuccess.ShouldBeTrue();

        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        subscription.SuspendedReason.ShouldBeNull();
        var reactivated = subscription.Events.Last();
        reactivated.Type.ShouldBe(SubscriptionEventType.Reactivated);
        reactivated.OldValue.ShouldBe(SubscriptionStatuses.Suspended);
        reactivated.NewValue.ShouldBe(SubscriptionStatuses.Active);
        reactivated.Reason.ShouldBe("Pago recibido");
        subscription.Reactivate(Reason, Now).Error.ShouldBe(LicensingErrors.InvalidTransition);
    }

    [Fact]
    public void Reactivar_despues_del_vencimiento_la_deja_vencida()
    {
        var subscription = Paid();
        subscription.Suspend(Reason, Now);

        subscription.Reactivate(Reason, At(Start.AddYears(1))).IsSuccess.ShouldBeTrue();

        subscription.Status.ShouldBe(SubscriptionStatus.Expired);
    }

    [Fact]
    public void Cancelar_es_definitivo()
    {
        var subscription = Paid();
        subscription.Suspend(Reason, Now);

        subscription.Cancel("no", Now).Error.ShouldBe(LicensingErrors.ReasonRequired);
        subscription.Cancel("Cierre del negocio", At(Start.AddDays(1))).IsSuccess.ShouldBeTrue();

        subscription.Status.ShouldBe(SubscriptionStatus.Cancelled);
        subscription.CancelledAt.ShouldBe(Start.AddDays(1));
        subscription.SuspendedReason.ShouldBeNull();
        subscription.Events.Last().OldValue.ShouldBe(SubscriptionStatuses.Suspended);
        subscription.Cancel(Reason, Now).Error.ShouldBe(LicensingErrors.InvalidTransition);
        subscription.Suspend(Reason, Now).Error.ShouldBe(LicensingErrors.InvalidTransition);
        subscription.Renew("PAGO-9", 1, 7, Now).Error.ShouldBe(LicensingErrors.InvalidTransition);
        subscription.ChangeEdition(LicenseEdition.SingleTerminal, Reason, Now).Error.ShouldBe(LicensingErrors.InvalidTransition);
        subscription.ExtendGrace(5, Reason, Now).Error.ShouldBe(LicensingErrors.InvalidTransition);
        subscription.Refresh(At(Start.AddYears(2))).ShouldBeFalse();
        subscription.AuditLabel.ShouldBe("Suscripción Multicaja (Cancelada)");
    }

    [Fact]
    public void Cambiar_la_edicion_deja_el_valor_anterior_y_el_nuevo()
    {
        var subscription = Paid();

        subscription.ChangeEdition(LicenseEdition.MultiTerminal, Reason, Now).Error.ShouldBe(LicensingErrors.SameEdition);
        subscription.ChangeEdition(LicenseEdition.SingleTerminal, "", Now).Error.ShouldBe(LicensingErrors.ReasonRequired);
        subscription.ChangeEdition(LicenseEdition.SingleTerminal, Reason, Now).IsSuccess.ShouldBeTrue();

        subscription.Edition.ShouldBe(LicenseEdition.SingleTerminal);
        var changed = subscription.Events.Last();
        changed.Type.ShouldBe(SubscriptionEventType.EditionChanged);
        changed.OldValue.ShouldBe(LicenseEditions.MultiTerminal);
        changed.NewValue.ShouldBe(LicenseEditions.SingleTerminal);
    }

    [Fact]
    public void Extender_la_gracia_devuelve_una_vencida_a_gracia()
    {
        var subscription = Paid(grace: 7);
        var later = At(subscription.ValidUntil.AddDays(10));
        subscription.Refresh(later).ShouldBeTrue();
        subscription.Status.ShouldBe(SubscriptionStatus.Expired);

        subscription.ExtendGrace(5, "El pago está en camino", later).IsSuccess.ShouldBeTrue();

        subscription.GraceDays.ShouldBe(12);
        subscription.Status.ShouldBe(SubscriptionStatus.PastDue);
        var events = subscription.Events.TakeLast(2).ToList();
        events[0].Type.ShouldBe(SubscriptionEventType.GraceExtended);
        events[0].OldValue.ShouldBe("7");
        events[0].NewValue.ShouldBe("12");
        events[1].Type.ShouldBe(SubscriptionEventType.StatusChanged);
        events[1].NewValue.ShouldBe(SubscriptionStatuses.PastDue);
    }

    [Fact]
    public void Extender_la_gracia_valida_dias_total_y_motivo()
    {
        var subscription = Trial(grace: 80);

        subscription.ExtendGrace(0, Reason, Now).Error.ShouldBe(LicensingErrors.InvalidGraceDays);
        subscription.ExtendGrace(31, Reason, Now).Error.ShouldBe(LicensingErrors.InvalidGraceDays);
        subscription.ExtendGrace(11, Reason, Now).Error.ShouldBe(LicensingErrors.InvalidGraceDays);
        subscription.ExtendGrace(10, "x", Now).Error.ShouldBe(LicensingErrors.ReasonRequired);
        subscription.ExtendGrace(10, Reason, Now).IsSuccess.ShouldBeTrue();
        subscription.GraceDays.ShouldBe(Subscription.MaxGraceDays);
    }

    [Fact]
    public void Las_operaciones_exigen_el_contexto_del_cambio()
    {
        var subscription = Paid();

        Should.Throw<ArgumentNullException>(() => subscription.Refresh(null!));
        Should.Throw<ArgumentNullException>(() => subscription.Renew("PAGO-1", 1, 7, null!));
        Should.Throw<ArgumentNullException>(() => subscription.Suspend(Reason, null!));
        Should.Throw<ArgumentNullException>(() => subscription.Reactivate(Reason, null!));
        Should.Throw<ArgumentNullException>(() => subscription.Cancel(Reason, null!));
        Should.Throw<ArgumentNullException>(() => subscription.ExtendGrace(1, Reason, null!));
        Should.Throw<ArgumentNullException>(() => subscription.ChangeEdition(LicenseEdition.SingleTerminal, Reason, null!));
        Should.Throw<ArgumentNullException>(() =>
            Subscription.StartTrial(Guid.Empty, Guid.Empty, LicenseEdition.MultiTerminal, BillingPeriod.Monthly, 1, 1, null!));
        Should.Throw<ArgumentNullException>(() =>
            Subscription.StartPaid(Guid.Empty, Guid.Empty, LicenseEdition.MultiTerminal, BillingPeriod.Monthly, "PAGO", 1, 1, null!));
    }
}
