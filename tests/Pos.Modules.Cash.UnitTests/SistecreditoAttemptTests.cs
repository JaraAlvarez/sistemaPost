using System.Globalization;
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
        using var culture = new CultureScope("es-CO"); // los valores del mensaje se escriben con la cultura de Colombia
        var attempt = Pending(Guid.NewGuid());
        attempt.DocumentType.ShouldBe("CC");
        attempt.DocumentNumber.ShouldBe("1020304050");
        attempt.MaskedDocument.ShouldBe("••••4050");
        attempt.Invoice.ShouldBe($"BP-{attempt.Id:N}");
        attempt.Status.ShouldBe(SistecreditoAttemptStatus.Pending);

        var cents = SistecreditoAttempt.Create(Guid.NewGuid(), Company, Guid.NewGuid(), null, 1_000.5m, "CC", "123456", SistecreditoEnvironment.Staging, Now).Error;
        cents.Code.ShouldBe(SistecreditoErrors.InvalidAmount.Code);
        cents.Type.ShouldBe(SistecreditoErrors.InvalidAmount.Type);
        cents.Message.ShouldContain("pesos enteros");
        cents.Message.ShouldContain("cobre 1.000 con Sistecrédito");
        cents.Message.ShouldContain("0,50 restantes con otro medio de pago");
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
    public void Con_centavos_el_mensaje_dice_cuanto_cobrar_con_Sistecredito_y_cuanto_con_otro_medio()
    {
        using var culture = new CultureScope("es-CO");
        // Revisión B5: la caja proponía lo que falta con centavos y Sistecrédito solo cobra pesos enteros.
        var error = SistecreditoAttempt.Create(Guid.NewGuid(), Company, Guid.NewGuid(), null, 9_050.50m, "CC", "123456", SistecreditoEnvironment.Staging, Now).Error;
        error.Code.ShouldBe(SistecreditoErrors.InvalidAmount.Code);
        error.Message.ShouldContain("pesos enteros");
        error.Message.ShouldContain("9.050");
        error.Message.ShouldContain("otro medio de pago");
        SistecreditoAttempt.Create(Guid.NewGuid(), Company, Guid.NewGuid(), null, 0.5m, "CC", "123456", SistecreditoEnvironment.Staging, Now)
            .Error.ShouldBe(SistecreditoErrors.InvalidAmount);
    }

    [Fact]
    public void Requiere_revision_aprobado_y_sin_usar_completa_su_venta_y_sale_de_la_revision()
    {
        // Revisión M1: un cobro en "Requiere revisión" (aprobado, sin usar) es plata ya cobrada: completa la venta.
        var sale = Guid.NewGuid();
        var attempt = Pending(sale);
        attempt.Apply("Approved", null, null, null, Now);
        attempt.RequireReview("Sin venta completada.", Now.AddMinutes(20));
        attempt.Status.ShouldBe(SistecreditoAttemptStatus.ReviewRequired);
        attempt.IsUsable.ShouldBeTrue();

        attempt.Use(sale, 80_000m, Now.AddMinutes(21)).IsSuccess.ShouldBeTrue();
        attempt.Status.ShouldBe(SistecreditoAttemptStatus.Approved);
        attempt.IsUsable.ShouldBeFalse();
    }

    [Fact]
    public void Aprobado_que_Sistecredito_ya_no_reporta_aprobado_queda_en_revision_y_no_se_usa()
    {
        // Revisión B4: al volver a consultar un aprobado, si se reversó no completa la venta.
        var sale = Guid.NewGuid();
        var attempt = Pending(sale);
        attempt.Apply("Approved", null, null, null, Now);
        attempt.Apply("PendingForPaymentMethod", null, null, null, Now).ShouldBeFalse(); // un estado intermedio no cambia nada
        attempt.IsUsable.ShouldBeTrue();

        attempt.Apply("Cancelled", null, null, null, Now.AddMinutes(1)).ShouldBeTrue();
        attempt.Status.ShouldBe(SistecreditoAttemptStatus.ReviewRequired);
        attempt.Message!.ShouldContain("ya no reporta aprobado");
        attempt.IsUsable.ShouldBeFalse();
        attempt.Use(sale, 80_000m, Now).Error.ShouldBe(SistecreditoErrors.NoLongerApproved);
    }

    [Fact]
    public void Venta_anulada_deja_el_cobro_en_revision_para_reversarlo_en_el_portal()
    {
        // Revisión M3.
        var sale = Guid.NewGuid();
        var attempt = Pending(sale);
        attempt.SaleVoided(Now).ShouldBeFalse(); // sin usar no aplica
        attempt.Apply("Approved", null, null, null, Now);
        attempt.Use(sale, 80_000m, Now).IsSuccess.ShouldBeTrue();

        attempt.SaleVoided(Now.AddHours(1)).ShouldBeTrue();
        attempt.Status.ShouldBe(SistecreditoAttemptStatus.ReviewRequired);
        attempt.Message.ShouldBe("Venta anulada: reverse el pago en el portal de Sistecrédito");
        attempt.ReviewRequiredAt.ShouldBe(Now.AddHours(1));
        attempt.SaleVoided(Now.AddHours(2)).ShouldBeFalse(); // una sola vez
        attempt.Resolve("Reversado en el portal de Sistecrédito", Guid.NewGuid(), Now.AddHours(3)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Pendiente_viejo_vence_y_708_vence_o_revisa_segun_si_se_aprobo()
    {
        // Revisión M4: nada se queda atascado en la revisión automática.
        var stale = Pending(Guid.NewGuid());
        stale.Expire("Vencido.", Now.AddHours(25)).ShouldBeTrue();
        stale.Status.ShouldBe(SistecreditoAttemptStatus.Expired);
        stale.Expire("otra vez", Now).ShouldBeFalse();

        var missing = Pending(Guid.NewGuid());
        missing.NotFoundAtProvider(Now.AddHours(1));
        missing.Status.ShouldBe(SistecreditoAttemptStatus.Expired);
        missing.ProviderStatus.ShouldBe(SistecreditoProtocol.NotFoundStatus);

        var approved = Pending(Guid.NewGuid());
        approved.Apply("Approved", null, null, null, Now);
        approved.NotFoundAtProvider(Now.AddMinutes(5));
        approved.Status.ShouldBe(SistecreditoAttemptStatus.ReviewRequired);
        approved.IsUsable.ShouldBeFalse();
    }

    [Fact]
    public void Vencido_en_la_caja_que_el_cliente_aprueba_despues_queda_en_revision_y_usable()
    {
        // Revisión N3: un pendiente que se dio por vencido y luego aparece aprobado no se pierde.
        var sale = Guid.NewGuid();
        var attempt = Pending(sale);
        attempt.Expire("Vencido.", Now.AddHours(25)).ShouldBeTrue();
        attempt.Apply("Approved", null, null, null, Now.AddHours(26)).ShouldBeTrue();
        attempt.Status.ShouldBe(SistecreditoAttemptStatus.ReviewRequired);
        attempt.ApprovedAt.ShouldBe(Now.AddHours(26));
        attempt.IsUsable.ShouldBeTrue();
    }

    [Fact]
    public void Si_no_se_alcanza_a_crear_en_Sistecredito_el_registro_local_queda_cancelado_con_el_motivo()
    {
        // Revisión M5: el intento se guarda antes de llamar a Sistecrédito; si la creación falla queda cancelado.
        var attempt = SistecreditoAttempt.Create(Guid.NewGuid(), Company, Guid.NewGuid(), null, 10_000m, "CC", "123456", SistecreditoEnvironment.Staging, Now).Value;
        attempt.StartFailed("Sin conexión.", Now);
        attempt.Status.ShouldBe(SistecreditoAttemptStatus.Cancelled);
        attempt.TransactionId.ShouldBeNull();
        attempt.Message!.ShouldStartWith("No se pudo crear el cobro en Sistecrédito");
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

    [Fact]
    public void Una_respuesta_Approved_vieja_no_revive_un_cobro_que_Sistecredito_dejo_de_aprobar()
    {
        // Revisión TP5.
        var sale = Guid.NewGuid();
        var attempt = Pending(sale);
        attempt.Apply("Approved", null, null, null, Now);
        attempt.Apply("Cancelled", null, null, null, Now.AddMinutes(1)); // reversado en Sistecrédito
        attempt.IsUsable.ShouldBeFalse();

        attempt.Apply("Approved", null, null, null, Now.AddMinutes(2)).ShouldBeFalse(); // respuesta vieja que llega tarde
        attempt.ProviderStatus.ShouldBe("Cancelled");
        attempt.IsUsable.ShouldBeFalse();
        attempt.Use(sale, 80_000m, Now).Error.ShouldBe(SistecreditoErrors.NoLongerApproved);
    }

    [Fact]
    public void No_usar_este_credito_lo_deja_en_revision_y_ya_no_se_usa()
    {
        // Revisión TP2.
        var sale = Guid.NewGuid();
        var attempt = Pending(sale);
        attempt.Release(sale, Guid.NewGuid(), Now).Error.ShouldBe(SistecreditoErrors.NotApproved); // pendiente: no aplica
        attempt.Apply("Approved", null, null, null, Now);
        attempt.Release(Guid.NewGuid(), Guid.NewGuid(), Now).Error.ShouldBe(SistecreditoErrors.OtherSale);

        attempt.Release(sale, Guid.NewGuid(), Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();
        attempt.Status.ShouldBe(SistecreditoAttemptStatus.ReviewRequired);
        attempt.Message.ShouldBe(SistecreditoAttempt.ReleasedMessage);
        attempt.IsUsable.ShouldBeFalse();
        attempt.Use(sale, 80_000m, Now).IsFailure.ShouldBeTrue();
        attempt.Resolve("Reversado en el portal de Sistecrédito", Guid.NewGuid(), Now.AddHours(1)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Un_cobro_que_no_se_pudo_guardar_espera_cada_vez_mas_y_vuelve_a_cero_al_consultarse_bien()
    {
        // Revisión TP4.
        var attempt = Pending(Guid.NewGuid());
        attempt.CheckFailed(Now);
        attempt.CheckFailures.ShouldBe(1);
        attempt.NextCheckAt.ShouldBe(Now.AddMinutes(2));
        attempt.CheckFailed(Now);
        attempt.NextCheckAt.ShouldBe(Now.AddMinutes(4));
        for (var i = 0; i < 10; i++)
        {
            attempt.CheckFailed(Now);
        }

        attempt.NextCheckAt.ShouldBe(Now.AddMinutes(60));
        attempt.Checked(Now);
        attempt.CheckFailures.ShouldBe(0);
        attempt.NextCheckAt.ShouldBeNull();
    }

    [Fact]
    public void No_usar_es_idempotente()
    {
        // Revisión SC4.

        var sale = Guid.NewGuid();
        var approved = Pending(sale);
        approved.Apply("Approved", null, null, null, Now);
        var user = Guid.NewGuid();
        approved.Release(sale, user, Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();
        approved.Release(sale, Guid.NewGuid(), Now.AddMinutes(2)).IsSuccess.ShouldBeTrue(); // repetir: OK sin cambios
        approved.ReleasedBy.ShouldBe(user);
        approved.ReleasedAt.ShouldBe(Now.AddMinutes(1));
    }

    [Theory]
    [InlineData("Abandoned", SistecreditoAttemptStatus.Cancelled)]
    [InlineData("Cancelled", SistecreditoAttemptStatus.Cancelled)]
    [InlineData("Expired", SistecreditoAttemptStatus.Expired)]
    [InlineData("Rejected", SistecreditoAttemptStatus.Rejected)]
    [InlineData("Failed", SistecreditoAttemptStatus.Rejected)]
    public void Aprobado_despues_de_un_fallo_de_Sistecredito_nunca_se_ignora_queda_en_revision(string failure, SistecreditoAttemptStatus failed)
    {
        // Revisión S5-1: no hay certeza de que esos estados sean irreversibles; si el cliente igual aprueba, quedó cobrado.
        var sale = Guid.NewGuid();
        var attempt = Pending(sale);
        attempt.Apply(failure, null, null, null, Now).ShouldBeTrue();
        attempt.Status.ShouldBe(failed);

        attempt.Apply("Approved", null, 3, null, Now.AddMinutes(30)).ShouldBeTrue();
        attempt.Status.ShouldBe(SistecreditoAttemptStatus.ReviewRequired);
        attempt.ApprovedAt.ShouldBe(Now.AddMinutes(30));
        attempt.ReviewRequiredAt.ShouldNotBeNull();
        attempt.IsUsable.ShouldBeTrue(); // la plata ya se cobró: puede completar su venta o reversarse
        attempt.Use(sale, 80_000m, Now.AddMinutes(31)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(19, 5)]
    [InlineData(20, 15)]
    [InlineData(74, 15)]
    [InlineData(75, 60)]
    [InlineData(239, 60)]
    [InlineData(240, 180)]
    [InlineData(1_400, 180)]
    public void Los_terminados_sin_aprobar_se_consultan_con_espera_creciente(int minutesSinceFinished, int gapMinutes) =>
        SistecreditoProtocol.FailedRecheckGap(TimeSpan.FromMinutes(minutesSinceFinished)).ShouldBe(TimeSpan.FromMinutes(gapMinutes)); // SX1

    [Fact]
    public void Un_cobro_ya_en_revision_conserva_su_hora_y_su_motivo_y_lo_nuevo_queda_aparte()
    {
        // SQ1.
        var sale = Guid.NewGuid();
        var attempt = Pending(sale);
        attempt.Apply("Approved", null, null, null, Now);
        attempt.Release(sale, Guid.NewGuid(), Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();
        attempt.Apply("Cancelled", null, null, null, Now.AddHours(2)); // reversado en el portal
        attempt.Status.ShouldBe(SistecreditoAttemptStatus.ReviewRequired);
        attempt.ReviewRequiredAt.ShouldBe(Now.AddMinutes(1));
        attempt.Message.ShouldBe(SistecreditoAttempt.ReleasedMessage);
        attempt.ReviewUpdate!.ShouldContain("ya no reporta aprobado");
    }

    [Fact]
    public void Un_terminado_sin_aprobar_guarda_cuando_volver_a_consultarlo()
    {
        // SQ3: la espera queda en NextCheckAt (la base de datos la filtra).
        var rejected = Pending(Guid.NewGuid());
        rejected.Apply("Rejected", null, null, null, Now);
        rejected.Checked(Now.AddMinutes(1));
        rejected.NextCheckAt.ShouldBe(Now.AddMinutes(6));
        rejected.Checked(Now.AddHours(5));
        rejected.NextCheckAt.ShouldBe(Now.AddHours(8));

        var pending = Pending(Guid.NewGuid());
        pending.Checked(Now);
        pending.NextCheckAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(80_000, new[] { 50_000, 40_000 }, new[] { 40_000 })] // con uno alcanza: el más chico que cubre el exceso (10.000)
    [InlineData(4_500, new[] { 4_000, 3_000 }, new[] { 3_000 })]
    [InlineData(10_000, new[] { 6_000, 5_000, 4_000 }, new[] { 5_000 })] // exceso 5.000 → 5.000 (no 6.000)
    [InlineData(1_000, new[] { 6_000, 5_000, 4_000 }, new[] { 6_000, 5_000, 4_000 })] // exceso 14.000: ninguno solo; hace falta los tres
    [InlineData(5_000, new[] { 6_000, 5_000, 4_000, 1_000 }, new[] { 6_000, 5_000 })] // exceso 11.000: dos créditos, la menor suma (11.000)
    [InlineData(20_000, new[] { 6_000, 5_000 }, new int[0])] // ya cabe: nada
    public void No_usar_solo_los_necesarios_libera_lo_minimo(int keepUpTo, int[] amounts, int[] expected)
    {
        // SX5.
        var values = amounts.Select(a => (decimal)a).ToList();
        SistecreditoProtocol.ChooseToRelease(values, keepUpTo).Select(i => (int)values[i]).Order().ShouldBe(expected.Order());
    }

    /// <summary>Fija la cultura mientras dura la prueba (los mensajes formatean valores con la cultura actual).</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = new CultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _culture;
    }
}
