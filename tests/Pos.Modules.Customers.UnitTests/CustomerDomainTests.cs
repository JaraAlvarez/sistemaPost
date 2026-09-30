using Pos.Modules.Customers.Application;
using Pos.Modules.Customers.Domain;

namespace Pos.Modules.Customers.UnitTests;

public class ColombianCalendarTests
{
    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    public void Domingo_de_Pascua(int year, int month, int day) => ColombianCalendar.EasterSunday(year).ShouldBe(new DateOnly(year, month, day));

    [Fact]
    public void Festivos_de_2026_con_la_Ley_Emiliani()
    {
        ColombianCalendar.Holidays(2026).Order().ShouldBe(
        [
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 12), new DateOnly(2026, 3, 23), new DateOnly(2026, 4, 2), new DateOnly(2026, 4, 3),
            new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 18), new DateOnly(2026, 6, 8), new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 29),
            new DateOnly(2026, 7, 20), new DateOnly(2026, 8, 7), new DateOnly(2026, 8, 17), new DateOnly(2026, 10, 12), new DateOnly(2026, 11, 2),
            new DateOnly(2026, 11, 16), new DateOnly(2026, 12, 8), new DateOnly(2026, 12, 25),
        ]);
    }

    [Fact]
    public void Dias_habiles_saltan_fines_de_semana_y_festivos()
    {
        // Viernes 9 de enero de 2026 + 1 hábil: el lunes 12 es festivo (Reyes) → martes 13.
        ColombianCalendar.AddBusinessDays(new DateOnly(2026, 1, 9), 1).ShouldBe(new DateOnly(2026, 1, 13));
        // Lunes 30 de marzo de 2026 + 5 hábiles (31, 1, 6, 7 y 8): jueves y viernes santos no cuentan → miércoles 8 de abril.
        ColombianCalendar.AddBusinessDays(new DateOnly(2026, 3, 30), 5).ShouldBe(new DateOnly(2026, 4, 8));
        ColombianCalendar.AddBusinessDays(new DateOnly(2026, 3, 30), 0).ShouldBe(new DateOnly(2026, 3, 30));
        ColombianCalendar.IsBusinessDay(new DateOnly(2026, 7, 20)).ShouldBeFalse();
        ColombianCalendar.IsBusinessDay(new DateOnly(2026, 7, 21)).ShouldBeTrue();
        Should.Throw<ArgumentOutOfRangeException>(() => ColombianCalendar.AddBusinessDays(new DateOnly(2026, 1, 1), -1));
    }
}

public class CustomerDomainTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 15, 0, 0, TimeSpan.Zero);

    private static PrivacyPolicy Policy(int version = 1) =>
        PrivacyPolicy.Create(Guid.NewGuid(), Company, version, CustomersInitializer.TemplateText, CustomersInitializer.TemplateNotice).Value;

    private static ConsentContext Context(int minutes = 0) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(minutes));

    private static CustomerConsent Consent(Guid party, ConsentPurpose purpose, bool granted, int minutes, IReadOnlyList<string>? channels = null, PrivacyPolicy? policy = null) =>
        CustomerConsent.Create(Guid.NewGuid(), Company, party, new ConsentInput(purpose, granted, ConsentChannel.PosVerbal, channels, null), policy ?? Policy(),
            Context(minutes)).Value;

    [Fact]
    public void Grupo_con_codigo_valido_y_el_de_por_defecto_no_se_inactiva()
    {
        var group = CustomerGroup.Create(Guid.NewGuid(), Company, " empleados ", "Empleados", Guid.NewGuid()).Value;
        group.Code.ShouldBe("EMPLEADOS");
        group.AuditLabel.ShouldContain("EMPLEADOS");
        group.Update("Empleados 2026", null, isActive: false).IsSuccess.ShouldBeTrue();
        group.Status.ShouldBe(MasterStatus.Inactive);
        group.PriceListId.ShouldBeNull();
        CustomerGroup.Create(Guid.NewGuid(), Company, "x", "Nombre", null).Error.ShouldBe(CustomerErrors.InvalidGroup);
        CustomerGroup.Create(Guid.NewGuid(), Company, "OK", "N", null).Error.ShouldBe(CustomerErrors.InvalidGroup);
        var general = CustomerGroup.Create(Guid.NewGuid(), Company, CustomerGroup.DefaultCode, "General", null, isDefault: true).Value;
        general.Update("General", null, isActive: false).Error.ShouldBe(CustomerErrors.DefaultGroupRequired);
    }

    [Fact]
    public void Cliente_con_la_clave_del_tercero_bloqueo_con_motivo_y_lista_propia()
    {
        var party = Guid.NewGuid();
        var customer = Customer.Create(party, Company, Guid.NewGuid(), CustomerOrigin.PosQuick, Guid.NewGuid());
        customer.Id.ShouldBe(party);
        customer.PartyId.ShouldBe(party);
        customer.CreditStatus.ShouldBe(CreditStatus.None);
        customer.LoyaltyStatus.ShouldBe(LoyaltyStatus.None);
        customer.AuditLabel.ShouldContain(party.ToString());

        var list = Guid.NewGuid();
        customer.AssignPricing(Guid.NewGuid(), list).IsSuccess.ShouldBeTrue();
        customer.PriceListId.ShouldBe(list);
        customer.ChangeStatus(CustomerStatus.Blocked, "no").Error.ShouldBe(CustomerErrors.ReasonRequired);
        customer.ChangeStatus(CustomerStatus.Blocked, " Cheque devuelto ").IsSuccess.ShouldBeTrue();
        customer.BlockReason.ShouldBe("Cheque devuelto");
        customer.ChangeStatus(CustomerStatus.Active, null).IsSuccess.ShouldBeTrue();
        customer.BlockReason.ShouldBeNull();
        customer.SetAlwaysRequestsInvoice(true);
        customer.AlwaysRequestsInvoice.ShouldBeTrue();
    }

    [Fact]
    public void El_estado_vigente_es_el_ultimo_registro_de_cada_finalidad()
    {
        var party = Guid.NewGuid();
        var customer = Customer.Create(party, Company, Guid.NewGuid(), CustomerOrigin.PosQuick, null);
        customer.ApplyConsents([]);
        customer.ServiceConsent.ShouldBeFalse();
        customer.ConsentPolicyVersion.ShouldBeNull();

        var v2 = Policy(2);
        customer.ApplyConsents(
        [
            Consent(party, ConsentPurpose.Service, true, 0),
            Consent(party, ConsentPurpose.Marketing, true, 1, ["whatsapp", "SMS", "sms"]),
            Consent(party, ConsentPurpose.Marketing, false, 2, policy: v2),
            Consent(party, ConsentPurpose.Marketing, true, 3, ["EMAIL"], v2),
        ]);
        customer.ServiceConsent.ShouldBeTrue();
        customer.MarketingConsent.ShouldBeTrue();
        customer.MarketingChannels.ShouldBe("EMAIL");
        customer.ConsentPolicyVersion.ShouldBe(2);
        customer.ConsentUpdatedAt.ShouldBe(Now.AddMinutes(3));

        customer.Anonymize(Now);
        customer.Status.ShouldBe(CustomerStatus.Inactive);
        customer.ServiceConsent.ShouldBeFalse();
        customer.MarketingChannels.ShouldBeNull();
        customer.AssignPricing(Guid.NewGuid(), null).Error.ShouldBe(CustomerErrors.Anonymized);
        customer.ChangeStatus(CustomerStatus.Active, null).Error.ShouldBe(CustomerErrors.Anonymized);
        customer.ChangeStatus(CustomerStatus.Inactive, null).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Autorizacion_valida_guarda_la_prueba_y_las_invalidas_se_rechazan()
    {
        var party = Guid.NewGuid();
        var policy = Policy();
        var context = Context();
        var consent = CustomerConsent.Create(Guid.NewGuid(), Company, party,
            new ConsentInput(ConsentPurpose.Marketing, true, ConsentChannel.PosSigned, ["sms", "EMAIL"], " Formato 0042 "), policy, context).Value;
        consent.MarketingChannels.ShouldBe("EMAIL;SMS");
        consent.Evidence.ShouldBe("Formato 0042");
        consent.PolicyId.ShouldBe(policy.Id);
        consent.PolicyVersion.ShouldBe(1);
        consent.UserId.ShouldBe(context.UserId);
        consent.BranchId.ShouldBe(context.BranchId);
        consent.PosTerminalId.ShouldBe(context.PosTerminalId);
        consent.NodeId.ShouldBe(context.NodeId);
        consent.Channel.ShouldBe(ConsentChannel.PosSigned);

        CustomerConsent.Create(Guid.NewGuid(), Company, party, new ConsentInput(ConsentPurpose.Service, true, ConsentChannel.PosVerbal, ["SMS"], null), policy, context)
            .Error.ShouldBe(CustomerErrors.InvalidConsent);
        CustomerConsent.Create(Guid.NewGuid(), Company, party, new ConsentInput(ConsentPurpose.Marketing, true, ConsentChannel.PosVerbal, ["FAX"], null), policy, context)
            .Error.ShouldBe(CustomerErrors.InvalidConsent);
        CustomerConsent.Create(Guid.NewGuid(), Company, party, new ConsentInput(ConsentPurpose.Marketing, false, ConsentChannel.PosVerbal, ["SMS"], null), policy, context)
            .Error.ShouldBe(CustomerErrors.InvalidConsent);
        CustomerConsent.Create(Guid.NewGuid(), Company, party, new ConsentInput((ConsentPurpose)9, true, ConsentChannel.PosVerbal, null, null), policy, context)
            .Error.ShouldBe(CustomerErrors.InvalidConsent);
        CustomerConsent.Create(Guid.NewGuid(), Company, party, new ConsentInput(ConsentPurpose.Service, true, ConsentChannel.PosVerbal, null, new string('x', 201)), policy,
            context).Error.ShouldBe(CustomerErrors.InvalidConsent);
        policy.Activate(Guid.NewGuid(), Now);
        policy.Retire();
        CustomerConsent.Create(Guid.NewGuid(), Company, party, new ConsentInput(ConsentPurpose.Service, true, ConsentChannel.PosVerbal, null, null), policy, context)
            .Error.ShouldBe(CustomerErrors.InvalidConsent);
        Should.Throw<ArgumentNullException>(() => CustomerConsent.Create(Guid.NewGuid(), Company, party, null!, policy, context));
    }

    [Fact]
    public void Politica_con_huella_y_solo_se_activa_la_pendiente()
    {
        var policy = Policy();
        policy.Status.ShouldBe(PolicyStatus.PendingReview);
        policy.TextHash.Length.ShouldBe(64);
        policy.AuditLabel.ShouldBe("Política de datos v1");
        var user = Guid.NewGuid();
        policy.Activate(user, Now).IsSuccess.ShouldBeTrue();
        policy.ActivatedBy.ShouldBe(user);
        policy.Activate(user, Now).IsFailure.ShouldBeTrue();
        PrivacyPolicy.Create(Guid.NewGuid(), Company, 1, "corta", CustomersInitializer.TemplateNotice).Error.ShouldBe(CustomerErrors.InvalidPolicy);
        PrivacyPolicy.Create(Guid.NewGuid(), Company, 0, CustomersInitializer.TemplateText, CustomersInitializer.TemplateNotice).Error.ShouldBe(CustomerErrors.InvalidPolicy);
        PrivacyPolicy.Create(Guid.NewGuid(), Company, 1, CustomersInitializer.TemplateText, "aviso").Error.ShouldBe(CustomerErrors.InvalidPolicy);
    }

    [Fact]
    public void Solicitudes_del_titular_vencen_en_10_o_15_dias_habiles_y_se_cierran_una_vez()
    {
        var party = Guid.NewGuid();
        var received = new DateOnly(2026, 3, 30);
        var query = DataRequest.Create(Guid.NewGuid(), Company, party, DataRequestType.Query, ConsentChannel.Email, "Copia de mis datos", received).Value;
        query.DueOn.ShouldBe(ColombianCalendar.AddBusinessDays(received, 10));
        query.AuditLabel.ShouldContain("Query");
        var complaint = DataRequest.Create(Guid.NewGuid(), Company, party, DataRequestType.Delete, ConsentChannel.PaperForm, "Borrar mis datos", received).Value;
        complaint.DueOn.ShouldBe(ColombianCalendar.AddBusinessDays(received, 15));
        DataRequest.TermDays(DataRequestType.Complaint).ShouldBe(15);

        query.IsOverdue(query.DueOn).ShouldBeFalse();
        query.IsOverdue(query.DueOn.AddDays(1)).ShouldBeTrue();
        query.Close(true, "ok", Guid.NewGuid(), Now).Error.ShouldBe(CustomerErrors.ReasonRequired);
        query.Close(false, "Solicitud sin soporte", Guid.NewGuid(), Now).IsSuccess.ShouldBeTrue();
        query.Status.ShouldBe(DataRequestStatus.Rejected);
        query.IsOverdue(query.DueOn.AddDays(30)).ShouldBeFalse();
        query.Close(true, "Otra respuesta", Guid.NewGuid(), Now).Error.ShouldBe(CustomerErrors.RequestClosed);
        DataRequest.Create(Guid.NewGuid(), Company, party, DataRequestType.Query, ConsentChannel.Email, "x", received).Error.ShouldBe(CustomerErrors.InvalidRequest);
        DataRequest.Create(Guid.NewGuid(), Company, party, (DataRequestType)8, ConsentChannel.Email, "Detalle válido", received).Error
            .ShouldBe(CustomerErrors.InvalidRequest);
    }

    [Fact]
    public async Task Credito_y_puntos_nulos_siempre_rechazan()
    {
        (await new NullCustomerCreditGate().AuthorizeAsync(Guid.NewGuid(), 10m, TestContext.Current.CancellationToken)).Error.Code.ShouldBe("CUSTOMERS.CREDIT_NOT_AVAILABLE");
        (await new NullLoyaltyProgram().RedeemAsync(Guid.NewGuid(), 10m, TestContext.Current.CancellationToken)).Error.Code.ShouldBe("CUSTOMERS.LOYALTY_NOT_AVAILABLE");
    }
}
