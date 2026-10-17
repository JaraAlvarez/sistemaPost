using Pos.Modules.Cash.Domain;

namespace Pos.Modules.Cash.UnitTests;

/// <summary>Reglas del cobro con Sistecrédito (Fase 7-B, D7B-05): estados, uso único en la venta y revisión.</summary>
public class SistecreditoAttemptTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 15, 0, 0, TimeSpan.Zero);

    private static SistecreditoAttempt Pending(Guid saleId, decimal amount = 80_000m)
    {
        var attempt = SistecreditoAttempt.Create(Guid.NewGuid(), Company, saleId, null, amount, "cc", "1.020.304.050", SistecreditoEnvironment.Staging, Now).Value;
        attempt.Started("tx-1", "PendingForPaymentMethod", Now);
        return attempt;
    }

    [Fact]
    public void Validaciones_documento_enmascarado_y_factura_unica()
    {
        var attempt = Pending(Guid.NewGuid());
        attempt.DocumentType.ShouldBe("CC");
        attempt.DocumentNumber.ShouldBe("1020304050");
        attempt.MaskedDocument.ShouldBe("••••4050");
        attempt.Invoice.ShouldBe($"BP-{attempt.Id:N}");
        attempt.Status.ShouldBe(SistecreditoAttemptStatus.Pending);

        SistecreditoAttempt.Create(Guid.NewGuid(), Company, Guid.NewGuid(), null, 1_000.5m, "CC", "123456", SistecreditoEnvironment.Staging, Now)
            .Error.ShouldBe(SistecreditoErrors.InvalidAmount);
        SistecreditoAttempt.Create(Guid.NewGuid(), Company, Guid.NewGuid(), null, 0m, "CC", "123456", SistecreditoEnvironment.Staging, Now)
            .Error.ShouldBe(SistecreditoErrors.InvalidAmount);
        SistecreditoAttempt.Create(Guid.NewGuid(), Company, Guid.NewGuid(), null, 1_000m, "RC", "123456", SistecreditoEnvironment.Staging, Now)
            .Error.ShouldBe(SistecreditoErrors.InvalidDocument);
        SistecreditoAttempt.Create(Guid.NewGuid(), Company, Guid.NewGuid(), null, 1_000m, "CC", "12", SistecreditoEnvironment.Staging, Now)
            .Error.ShouldBe(SistecreditoErrors.InvalidDocument);
        SistecreditoProtocol.Mask("123").ShouldBe("•••");
    }

    [Fact]
    public void Aprobado_se_usa_una_sola_vez_en_su_venta_y_por_su_valor()
    {
        var sale = Guid.NewGuid();
        var attempt = Pending(sale);
        attempt.Use(sale, 80_000m, Now).Error.ShouldBe(SistecreditoErrors.NotApproved);

        attempt.Apply("Approved", "https://pay.test/x", 6, null, Now).ShouldBeTrue();
        attempt.Status.ShouldBe(SistecreditoAttemptStatus.Approved);
        attempt.Installments.ShouldBe(6);
        attempt.Use(Guid.NewGuid(), 80_000m, Now).Error.ShouldBe(SistecreditoErrors.OtherSale);
        attempt.Use(sale, 79_000m, Now).Error.Code.ShouldBe(SistecreditoErrors.AmountMismatch.Code);

        attempt.Use(sale, 80_000m, Now).IsSuccess.ShouldBeTrue();
        attempt.CompletedSaleId.ShouldBe(sale);
        attempt.Use(sale, 80_000m, Now).Error.ShouldBe(SistecreditoErrors.AlreadyUsed);
    }

    [Fact]
    public void Rechazado_vencido_o_cancelado_en_Sistecredito_termina_con_el_motivo()
    {
        var rejected = Pending(Guid.NewGuid());
        rejected.Apply("Rejected", null, null, "Cupo insuficiente", Now).ShouldBeTrue();
        rejected.Status.ShouldBe(SistecreditoAttemptStatus.Rejected);
        rejected.Message!.ShouldContain("Cupo insuficiente");

        var expired = Pending(Guid.NewGuid());
        expired.Apply("Expired", null, null, null, Now);
        expired.Status.ShouldBe(SistecreditoAttemptStatus.Expired);

        var abandoned = Pending(Guid.NewGuid());
        abandoned.Apply("Abandoned", null, null, null, Now);
        abandoned.Status.ShouldBe(SistecreditoAttemptStatus.Cancelled);

        // Un estado que no es final no cambia nada; una URL que no es http(s) no se guarda.
        var waiting = Pending(Guid.NewGuid());
        waiting.Apply("PendingForPaymentMethod", "javascript:alert(1)", null, null, Now).ShouldBeFalse();
        waiting.RedirectUrl.ShouldBeNull();
    }

    [Fact]
    public void Cancelado_en_la_caja_y_aprobado_despues_requiere_revision_y_se_cierra_con_nota()
    {
        var sale = Guid.NewGuid();
        var attempt = Pending(sale);
        attempt.CancelLocally("Cancelado en la caja.", Now).IsSuccess.ShouldBeTrue();
        attempt.CancelLocally("otra vez", Now).Error.ShouldBe(SistecreditoErrors.NotPending);

        attempt.Apply("Approved", null, null, null, Now.AddMinutes(2)).ShouldBeTrue();
        attempt.Status.ShouldBe(SistecreditoAttemptStatus.ReviewRequired);
        attempt.ReviewRequiredAt.ShouldNotBeNull();

        attempt.Resolve("ok", Guid.NewGuid(), Now).Error.ShouldBe(SistecreditoErrors.ReviewNoteRequired);
        attempt.Resolve("Reversado en el portal de Sistecrédito", Guid.NewGuid(), Now).IsSuccess.ShouldBeTrue();
        attempt.Use(sale, 80_000m, Now).Error.ShouldBe(SistecreditoErrors.NotApproved); // ya revisado: no se usa
    }

    [Fact]
    public void Encender_exige_llaves_y_el_medio_del_sistema_siempre_pide_referencia()
    {
        var settings = SistecreditoSettings.Create(Company);
        settings.Configure(null, null, SistecreditoEnvironment.Staging, enabled: true, Now).Error.ShouldBe(SistecreditoErrors.CredentialsRequired);
        settings.Configure([1, 2, 3], "abcd1234", SistecreditoEnvironment.Production, enabled: true, Now).IsSuccess.ShouldBeTrue();
        settings.KeyHint.ShouldBe("1234");
        settings.IsEnabled.ShouldBeTrue();

        var method = PaymentMethod.Create(Guid.NewGuid(), Company, PaymentMethod.SistecreditoCode, "Sistecrédito", PaymentMethodKind.Sistecredito, "ZZZ", false, 90, true).Value;
        method.RequiresReference.ShouldBeTrue();
        method.AffectsCashDrawer.ShouldBeFalse();
    }
}
