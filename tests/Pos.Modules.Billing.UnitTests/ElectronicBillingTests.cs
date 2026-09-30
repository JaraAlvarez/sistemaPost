using Pos.Modules.Billing.Domain;
using Pos.SharedKernel.Domain;

namespace Pos.Modules.Billing.UnitTests;

/// <summary>Fase 11-B: estados del documento electrónico, cola, corrección del adquirente y referencias.</summary>
public class ElectronicDocumentTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.FromHours(-5));

    private static FiscalDocument Electronic(FiscalSource source = FiscalSource.Sale) => FiscalDocument.Issue(
        Guid.NewGuid(), Company,
        new FiscalIssue(source, Guid.NewGuid(), "C1-000001", Branch, null, new DateOnly(2026, 9, 30), "Consumidor final", "CC", "222222222222", null,
            10_000m, 1_900m, 11_900m),
        electronic: true, Now, User, Guid.NewGuid);

    private static FiscalProviderResult Accepted(string number = "SETP990000001") =>
        new(FiscalOutcome.Accepted, "prov-1", "VALIDATED", number, 990_000_001, "cufe-abc", "https://qr", "https://pdf", "200", "Validado", Now.AddSeconds(2));

    [Fact]
    public void El_codigo_de_referencia_es_el_id_del_origen_por_tipo()
    {
        var id = Guid.NewGuid();
        FiscalReference.For(FiscalSource.Sale, id).ShouldBe(id.ToString("N"));
        FiscalReference.For(FiscalSource.SaleVoid, id).ShouldBe("NCA" + id.ToString("N"));
        FiscalReference.For(FiscalSource.CustomerReturn, id).ShouldBe("NCD" + id.ToString("N"));
        FiscalReference.For(FiscalSource.Purchase, id).ShouldBe("DS" + id.ToString("N"));
        Should.Throw<ArgumentOutOfRangeException>(() => FiscalReference.For((FiscalSource)99, id));

        FiscalDocument.ElectronicTypeFor(FiscalSource.Sale).ShouldBe(FiscalDocumentType.InvoiceElectronic);
        FiscalDocument.ElectronicTypeFor(FiscalSource.SaleVoid).ShouldBe(FiscalDocumentType.CreditNote);
        FiscalDocument.ElectronicTypeFor(FiscalSource.Purchase).ShouldBe(FiscalDocumentType.SupportDocument);
    }

    [Fact]
    public void La_compra_solo_genera_documento_soporte_electronico()
    {
        var support = Electronic(FiscalSource.Purchase);
        (support.DocumentType, support.Status, support.ReferenceCode!.StartsWith("DS", StringComparison.Ordinal)).ShouldBe(
            (FiscalDocumentType.SupportDocument, FiscalStatus.Pending, true));

        Should.Throw<DomainException>(() => FiscalDocument.Issue(
            Guid.NewGuid(), Company,
            new FiscalIssue(FiscalSource.Purchase, Guid.NewGuid(), "CO-1", Branch, null, new DateOnly(2026, 9, 30), "Campesino", "CC", "123", null, 1m, 0m, 1m),
            electronic: false, Now, User, Guid.NewGuid));
    }

    [Fact]
    public void La_cola_reclama_el_documento_y_retoma_un_reclamo_abandonado()
    {
        var document = Electronic();
        document.IsOpen.ShouldBeTrue();

        var claimed = document.Claim(Now, TimeSpan.FromMinutes(5));
        (claimed.IsSuccess, claimed.Value, document.Status, document.NextAttemptAt).ShouldBe((true, false, FiscalStatus.Submitting, (DateTimeOffset?)Now.AddMinutes(5)));

        // Mientras el reclamo está vigente nadie más lo toma; vencido, se retoma avisando que hay que consultar el estado.
        document.Claim(Now.AddMinutes(1), TimeSpan.FromMinutes(5)).Error.ShouldBe(BillingErrors.InvalidStatus);
        var retaken = document.Claim(Now.AddMinutes(6), TimeSpan.FromMinutes(5));
        (retaken.IsSuccess, retaken.Value).ShouldBe((true, true));

        document.RecordResult(Accepted(), "FAKE", Now, Now, Guid.NewGuid);
        document.Claim(Now.AddHours(1), TimeSpan.FromMinutes(5)).IsFailure.ShouldBeTrue();
        document.IsOpen.ShouldBeFalse();

        var internalReceipt = FiscalDocument.Issue(
            Guid.NewGuid(), Company,
            new FiscalIssue(FiscalSource.Sale, Guid.NewGuid(), "C1-2", Branch, null, new DateOnly(2026, 9, 30), "Consumidor final", "CC", "222222222222", null, 1m, 0m, 1m),
            electronic: false, Now, User, Guid.NewGuid);
        internalReceipt.Claim(Now, TimeSpan.FromMinutes(5)).IsFailure.ShouldBeTrue();
        internalReceipt.ReferenceCode.ShouldBeNull();
    }

    [Fact]
    public void Aplazar_sin_rango_alerta_una_sola_vez()
    {
        var document = Electronic();
        document.Claim(Now, TimeSpan.FromMinutes(5));

        document.Defer("NO_RANGE", "Sin rango", Now, Now.AddMinutes(15), Guid.NewGuid).ShouldBeTrue();
        (document.Status, document.NextAttemptAt).ShouldBe((FiscalStatus.Pending, (DateTimeOffset?)Now.AddMinutes(15)));
        document.Defer("NO_RANGE", "Sin rango", Now, Now.AddMinutes(30), Guid.NewGuid).ShouldBeFalse();
        document.Events.Count(e => e.EventType == "NO_RANGE").ShouldBe(1);

        document.Defer("WAITING_INVOICE", "Factura pendiente", Now, Now, Guid.NewGuid).ShouldBeTrue();
        document.Defer("NO_RANGE", "Sin rango", Now, Now, Guid.NewGuid).ShouldBeFalse();
        document.Events.Count(e => e.EventType == "NO_RANGE").ShouldBe(2);
        Should.Throw<ArgumentNullException>(() => document.Defer("X", "x", Now, Now, null!));
    }

    [Fact]
    public void Aceptado_guarda_numero_CUFE_QR_y_datos_del_proveedor()
    {
        var document = Electronic();
        document.UseRange(Guid.Empty);

        document.RecordResult(Accepted(), "FAKE", Now, Now.AddMinutes(1), Guid.NewGuid).ShouldBe(FiscalStatus.Accepted);

        (document.FiscalNumber, document.Cufe, document.QrData, document.PdfUrl).ShouldBe(("SETP990000001", "cufe-abc", "https://qr", "https://pdf"));
        (document.ProviderDocumentId, document.ProviderStatus, document.ValidatedAt, document.NextAttemptAt, document.Provider).ShouldBe(
            ("prov-1", "VALIDATED", (DateTimeOffset?)Now.AddSeconds(2), (DateTimeOffset?)null, "FAKE"));
        (document.Attempts, document.RejectionMessage, document.NumberingRangeId).ShouldBe((1, (string?)null, (Guid?)Guid.Empty));
        document.Events[^1].ProviderCode.ShouldBe("200");
    }

    [Fact]
    public void Aceptado_sin_numero_o_sin_CUFE_queda_con_error_para_consultar_de_nuevo()
    {
        var document = Electronic();

        var status = document.RecordResult(new FiscalProviderResult(FiscalOutcome.Accepted, Cufe: "x", ValidatedAt: Now), "FAKE", Now, Now.AddMinutes(2), Guid.NewGuid);

        (status, document.NextAttemptAt).ShouldBe((FiscalStatus.Error, (DateTimeOffset?)Now.AddMinutes(2)));
        document.Events[^1].Detail!.ShouldContain("sin número fiscal");
        Should.Throw<ArgumentNullException>(() => document.RecordResult(null!, "FAKE", Now, Now, Guid.NewGuid));
        Should.Throw<ArgumentNullException>(() => document.RecordResult(Accepted(), "FAKE", Now, Now, null!));
    }

    [Theory]
    [InlineData(FiscalOutcome.Unavailable, FiscalStatus.Contingency)]
    [InlineData(FiscalOutcome.Failed, FiscalStatus.Error)]
    [InlineData(FiscalOutcome.NotConfigured, FiscalStatus.Pending)]
    [InlineData(FiscalOutcome.NotFound, FiscalStatus.Pending)]
    public void Sin_conexion_o_con_error_se_reintenta_con_espera(FiscalOutcome outcome, FiscalStatus expected)
    {
        var document = Electronic();

        document.RecordResult(new FiscalProviderResult(outcome, ProviderStatus: new string('s', 60), Message: "Falla"), "FAKE", Now, Now.AddMinutes(4), Guid.NewGuid)
            .ShouldBe(expected);

        (document.Status, document.NextAttemptAt, document.ProviderStatus!.Length).ShouldBe((expected, (DateTimeOffset?)Now.AddMinutes(4), 40));
    }

    [Fact]
    public void Rechazo_guarda_el_mensaje_y_la_correccion_del_adquirente_lo_deja_listo_para_reenviar()
    {
        var document = Electronic();
        document.RecordResult(new FiscalProviderResult(FiscalOutcome.Rejected, Code: "FAK24", Message: new string('m', 2_500)), "FAKE", Now, Now, Guid.NewGuid)
            .ShouldBe(FiscalStatus.Rejected);
        (document.RejectionMessage!.Length, document.NextAttemptAt).ShouldBe((2_000, (DateTimeOffset?)null));

        var other = Electronic();
        other.RecordResult(new FiscalProviderResult(FiscalOutcome.Rejected), "FAKE", Now, Now, Guid.NewGuid);
        other.RejectionMessage.ShouldBe("Rechazado por el proveedor (sin detalle).");

        var fiscal = new FiscalBuyerData("LEGAL", "8", "48", ["O-13"], "Calle 5", "05001", "3001234567");
        var corrected = document.CorrectBuyer(new FiscalBuyerCorrection(" Tienda La 14 SAS ", "nit", " 900123456 ", " compras@la14.co ", fiscal), Now, User, Guid.NewGuid);

        corrected.IsSuccess.ShouldBeTrue();
        (document.BuyerName, document.BuyerIdentificationType, document.BuyerIdentification, document.BuyerEmail, document.BuyerFiscal).ShouldBe(
            ("Tienda La 14 SAS", "NIT", "900123456", (string?)"compras@la14.co", fiscal));
        (document.Status, document.NextAttemptAt).ShouldBe((FiscalStatus.Pending, (DateTimeOffset?)Now));
        var evt = document.Events[^1];
        (evt.EventType, evt.UserId).ShouldBe(("BUYER_CORRECTED", (Guid?)User));
        evt.Detail!.ShouldContain("Antes: CC 222222222222");

        // Correo vacío: se quita.
        document.CorrectBuyer(new FiscalBuyerCorrection("Tienda", "NIT", "900123456", " ", fiscal), Now, User, Guid.NewGuid).IsSuccess.ShouldBeTrue();
        document.BuyerEmail.ShouldBeNull();
    }

    [Fact]
    public void La_correccion_valida_los_datos_y_el_estado()
    {
        var fiscal = new FiscalBuyerData("NATURAL", null, "49", [], null, null, null);
        var document = Electronic();

        document.CorrectBuyer(new FiscalBuyerCorrection("", "CC", "1", null, fiscal), Now, User, Guid.NewGuid).Error.ShouldBe(BillingErrors.InvalidBuyer);
        document.CorrectBuyer(new FiscalBuyerCorrection("Ana", "CC", "", null, fiscal), Now, User, Guid.NewGuid).Error.ShouldBe(BillingErrors.InvalidBuyer);
        document.CorrectBuyer(new FiscalBuyerCorrection("Ana", "", "1", null, fiscal), Now, User, Guid.NewGuid).Error.ShouldBe(BillingErrors.InvalidBuyer);
        document.CorrectBuyer(new FiscalBuyerCorrection("Ana", "CC", "1", null, null!), Now, User, Guid.NewGuid).Error.ShouldBe(BillingErrors.InvalidBuyer);
        document.CorrectBuyer(new FiscalBuyerCorrection("Ana", "CC", "1", null, fiscal with { PersonType = "" }), Now, User, Guid.NewGuid).Error
            .ShouldBe(BillingErrors.InvalidBuyer);
        document.CorrectBuyer(new FiscalBuyerCorrection("Ana", "CC", "1", null, fiscal with { TaxRegime = " " }), Now, User, Guid.NewGuid).Error
            .ShouldBe(BillingErrors.InvalidBuyer);
        document.CorrectBuyer(new FiscalBuyerCorrection(new string('a', 201), "CC", "1", null, fiscal), Now, User, Guid.NewGuid).Error.ShouldBe(BillingErrors.InvalidBuyer);
        document.CorrectBuyer(new FiscalBuyerCorrection("Ana", "CC", new string('1', 31), null, fiscal), Now, User, Guid.NewGuid).Error.ShouldBe(BillingErrors.InvalidBuyer);
        document.CorrectBuyer(new FiscalBuyerCorrection("Ana", "CCCCCC", "1", null, fiscal), Now, User, Guid.NewGuid).Error.ShouldBe(BillingErrors.InvalidBuyer);
        document.CorrectBuyer(new FiscalBuyerCorrection("Ana", "CC", "1", new string('e', 255), fiscal), Now, User, Guid.NewGuid).Error.ShouldBe(BillingErrors.InvalidBuyer);
        Should.Throw<ArgumentNullException>(() => document.CorrectBuyer(null!, Now, User, Guid.NewGuid));
        Should.Throw<ArgumentNullException>(() => document.CorrectBuyer(new FiscalBuyerCorrection("Ana", "CC", "1", null, fiscal), Now, User, null!));

        document.RecordResult(Accepted(), "FAKE", Now, Now, Guid.NewGuid);
        document.CorrectBuyer(new FiscalBuyerCorrection("Ana", "CC", "1", null, fiscal), Now, User, Guid.NewGuid).Error.ShouldBe(BillingErrors.NotCorrectable);
    }

    [Fact]
    public void El_modelo_neutro_oculta_las_credenciales_y_reconoce_al_consumidor_final()
    {
        var credentials = new FiscalCredentials("usuario@tienda.co", "clave-secreta", "client-id-1", "secreto-2");
        credentials.ToString().ShouldNotContain("clave-secreta");
        credentials.ToString().ShouldNotContain("secreto-2");
        credentials.ToString().ShouldNotContain("client-id-1");
        new FiscalConnection(FiscalEnvironment.Sandbox, credentials).ToString().ShouldNotContain("clave-secreta");

        var party = new FiscalParty("CC", "13", FiscalParty.FinalConsumerIdentification, null, "Consumidor final", "NATURAL", "49", ["R-99-PN"], null, null, null, null);
        party.IsFinalConsumer.ShouldBeTrue();
        (party with { IdentificationNumber = "1020" }).IsFinalConsumer.ShouldBeFalse();

        var line = new FiscalLine(1, FiscalLineKind.Product, "A", "Arroz", "UND", 1m, 3_000m, true, 3_000m, 0m, 2_521m,
            [new FiscalTax("IVA19", "VAT", 19m, null, 2_521m, 479m, false, false)], 3_000m);
        line.TaxTotal.ShouldBe(479m);

        var result = FiscalProviderResult.NotConfigured("sin proveedor");
        (result.Outcome, result.Code, result.Message).ShouldBe((FiscalOutcome.NotConfigured, "NOT_CONFIGURED", "sin proveedor"));
        new FiscalRangeSyncResult(FiscalOutcome.Accepted, []).Message.ShouldBeNull();
    }

    [Fact]
    public void Errores_nuevos_con_codigo_estable()
    {
        BillingErrors.NotCorrectable.Code.ShouldBe("BILLING.NOT_CORRECTABLE");
        BillingErrors.InvalidBuyer.Code.ShouldBe("BILLING.INVALID_BUYER");
        BillingErrors.RangeNotFound.Code.ShouldBe("BILLING.RANGE_NOT_FOUND");
        BillingErrors.InvalidAssignment.Code.ShouldBe("BILLING.INVALID_ASSIGNMENT");
        BillingErrors.RangeNotUsable.Code.ShouldBe("BILLING.RANGE_NOT_USABLE");
        BillingErrors.CredentialsRequired.Code.ShouldBe("BILLING.CREDENTIALS_REQUIRED");
        BillingErrors.InvalidCredentials.Code.ShouldBe("BILLING.INVALID_CREDENTIALS");
    }
}

/// <summary>Rangos de numeración, configuración del proveedor y espera entre reintentos.</summary>
public class NumberingAndSettingsTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly Guid Terminal = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 30);

    private static FiscalProviderRange Data(
        string id = "R1", FiscalDocumentType type = FiscalDocumentType.InvoiceElectronic, long from = 1, long to = 100, long current = 0,
        DateOnly? validFrom = null, DateOnly? validTo = null, bool active = true) =>
        new(id, type, "SETP", from, to, current, "18760000001", validFrom, validTo, active);

    private static FiscalNumberingRange Range(FiscalProviderRange? data = null) =>
        FiscalNumberingRange.Create(Guid.NewGuid(), Company, "FAKE", data ?? Data(), Now);

    [Fact]
    public void El_rango_se_crea_y_se_actualiza_sin_retroceder_el_consecutivo()
    {
        var range = Range(Data(current: 10, validFrom: Today.AddDays(-10), validTo: Today.AddYears(1)));
        (range.CompanyId, range.Provider, range.ProviderRangeId, range.DocumentType, range.Prefix, range.RangeFrom, range.RangeTo, range.CurrentNumber).ShouldBe(
            (Company, "FAKE", "R1", FiscalDocumentType.InvoiceElectronic, "SETP", 1L, 100L, 10L));
        (range.ResolutionNumber, range.ValidFrom, range.ValidTo, range.IsActive, range.SyncedAt).ShouldBe(
            ((string?)"18760000001", (DateOnly?)Today.AddDays(-10), (DateOnly?)Today.AddYears(1), true, Now));
        (range.UsagePercent, range.Remaining, range.AuditLabel).ShouldBe((10m, 90L, "Rango SETP 1-100 (InvoiceElectronic)"));

        range.Refresh(Data(current: 5), Now.AddDays(1));
        range.CurrentNumber.ShouldBe(10L);
        range.Refresh(Data(current: 500), Now.AddDays(1));
        range.CurrentNumber.ShouldBe(100L);

        Should.Throw<DomainException>(() => range.Refresh(Data(from: 0), Now));
        Should.Throw<DomainException>(() => range.Refresh(Data(from: 10, to: 5), Now));
        Should.Throw<DomainException>(() => range.Refresh(Data() with { Prefix = null! }, Now));
        Should.Throw<DomainException>(() => range.Refresh(Data() with { Prefix = "PREFIJOLARGO" }, Now));
        Should.Throw<ArgumentNullException>(() => range.Refresh(null!, Now));
        Should.Throw<ArgumentNullException>(() => FiscalNumberingRange.Create(Guid.NewGuid(), Company, "FAKE", null!, Now));
        Should.Throw<ArgumentException>(() => FiscalNumberingRange.Create(Guid.NewGuid(), Company, " ", Data(), Now));
    }

    [Fact]
    public void Se_asigna_a_una_sucursal_y_opcionalmente_a_una_caja()
    {
        var range = Range();
        range.Assign(null, Terminal).Error.ShouldBe(BillingErrors.InvalidAssignment);
        range.Assign(Branch, Terminal).IsSuccess.ShouldBeTrue();
        (range.BranchId, range.PosTerminalId).ShouldBe(((Guid?)Branch, (Guid?)Terminal));
        range.Assign(null, null).IsSuccess.ShouldBeTrue();
        range.BranchId.ShouldBeNull();
    }

    [Fact]
    public void Vigencia_agotamiento_y_alertas_del_rango()
    {
        var range = Range(Data(current: 0, validFrom: Today.AddDays(-1), validTo: Today.AddDays(60)));
        range.IsUsableOn(Today).ShouldBeTrue();
        range.IsUsableOn(Today.AddDays(-2)).ShouldBeFalse();
        range.IsUsableOn(Today.AddDays(61)).ShouldBeFalse();
        range.DaysToExpire(Today).ShouldBe(60);
        range.NeedsAlert(Today, 90m, 30).ShouldBeFalse();
        range.NeedsAlert(Today.AddDays(31), 90m, 30).ShouldBeTrue();

        // Cruza el 90 % una sola vez.
        range.Advance(89, 90m).ShouldBeFalse();
        range.Advance(90, 90m).ShouldBeTrue();
        range.Advance(95, 90m).ShouldBeFalse();
        range.NeedsAlert(Today, 90m, 30).ShouldBeTrue();
        range.Advance(10, 90m).ShouldBeFalse();
        range.CurrentNumber.ShouldBe(95L);
        range.Advance(100, 90m);
        range.IsUsableOn(Today).ShouldBeFalse();

        var open = Range(Data(validTo: null));
        (open.DaysToExpire(Today), open.NeedsAlert(Today, 90m, 30)).ShouldBe(((int?)null, false));
        var inactive = Range(Data(active: false));
        (inactive.IsUsableOn(Today), inactive.NeedsAlert(Today, 90m, 30)).ShouldBe((false, false));
    }

    [Fact]
    public void Se_elige_primero_el_rango_de_la_caja_y_luego_el_de_la_sucursal()
    {
        var branchWide = Range(Data("R1", validTo: Today.AddYears(1)));
        branchWide.Assign(Branch, null);
        var branchSooner = Range(Data("R2", validTo: Today.AddMonths(1)));
        branchSooner.Assign(Branch, null);
        var terminal = Range(Data("R3"));
        terminal.Assign(Branch, Terminal);
        var credit = Range(Data("R4", FiscalDocumentType.CreditNote));
        credit.Assign(Branch, null);
        var exhausted = Range(Data("R5", current: 100));
        exhausted.Assign(Branch, Guid.NewGuid());
        var unassigned = Range(Data("R6"));
        List<FiscalNumberingRange> all = [branchWide, branchSooner, terminal, credit, exhausted, unassigned];

        FiscalNumberingRange.Select(all, Branch, Terminal, FiscalDocumentType.InvoiceElectronic, Today).ShouldBe(terminal);
        FiscalNumberingRange.Select(all, Branch, Guid.NewGuid(), FiscalDocumentType.InvoiceElectronic, Today).ShouldBe(branchSooner);
        FiscalNumberingRange.Select(all, Branch, null, FiscalDocumentType.CreditNote, Today).ShouldBe(credit);
        FiscalNumberingRange.Select(all, Branch, null, FiscalDocumentType.SupportDocument, Today).ShouldBeNull();
        FiscalNumberingRange.Select(all, Guid.NewGuid(), null, FiscalDocumentType.InvoiceElectronic, Today).ShouldBeNull();
        FiscalNumberingRange.Select(null!, Branch, null, FiscalDocumentType.InvoiceElectronic, Today).ShouldBeNull();
    }

    [Fact]
    public void Encender_exige_credenciales_y_quitarlas_apaga()
    {
        var settings = BillingProviderSettings.Create(Company, "FACTUS");
        (settings.CompanyId, settings.Provider, settings.Mode, settings.Environment, settings.HasCredentials).ShouldBe(
            (Company, "FACTUS", BillingMode.Off, FiscalEnvironment.Sandbox, false));
        Should.Throw<ArgumentException>(() => BillingProviderSettings.Create(Company, ""));

        settings.Configure(BillingMode.EverySale, FiscalEnvironment.Sandbox, "FACTUS").Error.ShouldBe(BillingErrors.CredentialsRequired);
        settings.Configure(BillingMode.Off, FiscalEnvironment.Production, "FACTUS").IsSuccess.ShouldBeTrue();
        settings.Environment.ShouldBe(FiscalEnvironment.Production);

        settings.SetCredentials([1, 2, 3], Now);
        (settings.HasCredentials, settings.CredentialsUpdatedAt).ShouldBe((true, (DateTimeOffset?)Now));
        settings.Configure(BillingMode.EverySale, FiscalEnvironment.Sandbox, "FAKE").IsSuccess.ShouldBeTrue();
        (settings.Mode, settings.Provider).ShouldBe((BillingMode.EverySale, "FAKE"));
        Should.Throw<ArgumentException>(() => settings.Configure(BillingMode.Off, FiscalEnvironment.Sandbox, " "));

        settings.SetCredentials([], Now);
        (settings.HasCredentials, settings.Credentials, settings.CredentialsUpdatedAt, settings.Mode).ShouldBe((false, (byte[]?)null, (DateTimeOffset?)null, BillingMode.Off));

        settings.RecordSync(Now, new string('e', 600));
        (settings.LastSyncAt, settings.LastSyncError!.Length).ShouldBe(((DateTimeOffset?)Now, 500));
        settings.RecordSync(Now, null);
        settings.LastSyncError.ShouldBeNull();
    }

    [Theory]
    [InlineData(BillingMode.Off, false, false)]
    [InlineData(BillingMode.Off, true, false)]
    [InlineData(BillingMode.OnRequest, false, false)]
    [InlineData(BillingMode.OnRequest, true, true)]
    [InlineData(BillingMode.EverySale, false, true)]
    public void El_modo_decide_si_la_venta_es_electronica(BillingMode mode, bool requested, bool electronic) =>
        BillingProviderSettings.IsElectronicSale(mode, requested).ShouldBe(electronic);

    [Fact]
    public void La_espera_entre_reintentos_crece_con_tope()
    {
        FiscalRetryPolicy.Delay(0).ShouldBe(TimeSpan.FromMinutes(1));
        FiscalRetryPolicy.Delay(1).ShouldBe(TimeSpan.FromMinutes(2));
        FiscalRetryPolicy.Delay(3).ShouldBe(TimeSpan.FromMinutes(8));
        FiscalRetryPolicy.Delay(10).ShouldBe(TimeSpan.FromMinutes(60));
        FiscalRetryPolicy.Delay(10, maxMinutes: 15).ShouldBe(TimeSpan.FromMinutes(15));
        FiscalRetryPolicy.Delay(-3).ShouldBe(TimeSpan.FromMinutes(1));
    }
}

/// <summary>Mapeo fiscal desde el documento guardado (D11B-06): IVA, excluido, INC, bolsa, descuentos, redondeo y pagos.</summary>
public class FiscalDraftBuilderTests
{
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid RiceLine = Guid.NewGuid();
    private static readonly Guid TomatoLine = Guid.NewGuid();
    private static readonly Guid SodaLine = Guid.NewGuid();

    private static readonly FiscalParty Issuer = new("NIT", "31", "900123456", "8", "Súper SAS", "LEGAL", "48", [], "Calle 1", "05001", "a@b.co", null);
    private static readonly FiscalEstablishment Establishment = new(Branch, "S01", "Centro", "Calle 1", "05001", null, null, null, null);
    private static readonly FiscalNumbering Numbering = new(Guid.NewGuid(), "R1", "SETP", "18760000001");

    private static readonly FiscalParty FinalConsumer =
        new("CC", "13", FiscalParty.FinalConsumerIdentification, null, "Consumidor final", "NATURAL", "49", ["R-99-PN"], null, null, null, null);

    /// <summary>
    /// Arroz 2 × $3.000 con IVA 19 % y 10 % de descuento en una unidad; tomate excluido; gaseosa con INC 8 % más impuesto a la bolsa de
    /// $66 (renglón aparte); redondeo del efectivo +$25.
    /// </summary>
    private static FiscalSaleSnapshot Sale(decimal rounding = 25m, decimal? total = null) => new(
        Guid.NewGuid(), "C1-000010", new DateOnly(2026, 9, 30), Now, FinalConsumer,
        [
            new(RiceLine, 1, "ARROZ-500", "Arroz 500 g", "UND", 2m, 3_000m, true, 6_000m, 300m, 4_789.92m, 5_700m,
                [new FiscalTax("IVA19", "VAT", 19m, null, 4_789.92m, 910.08m, false, false)]),
            new(TomatoLine, 2, "TOMATE", "Tomate chonto", "KG", 1.25m, 4_980m, true, 6_225m, 0m, 6_225m, 6_225m,
                [new FiscalTax("IVA_EXCLUIDO", "VAT", 0m, null, 6_225m, 0m, false, true)]),
            new(SodaLine, 3, "GAS-400", "Gaseosa 400 ml", "UND", 1m, 2_566m, true, 2_566m, 0m, 2_314.81m, 2_566m,
                [
                    new FiscalTax("INC8", "CONSUMPTION", 8m, null, 2_314.81m, 185.19m, false, false),
                    new FiscalTax("BOLSA", "BAG_CONSUMPTION", null, 66m, 0m, 66m, false, false),
                ]),
        ],
        [
            new FiscalPayment("EFECTIVO", "CASH", "10", 14_516m, null),
            new FiscalPayment("CAMBIO", "EXCHANGE_CREDIT", null, 0m, null),
        ],
        rounding, total ?? 14_491m + rounding, "Se cobró dos veces");

    private static FiscalDocument Document(FiscalSource source = FiscalSource.Sale) => FiscalDocument.Issue(
        Guid.NewGuid(), Guid.NewGuid(),
        new FiscalIssue(source, Guid.NewGuid(), "C1-000010", Branch, null, new DateOnly(2026, 9, 30), "Consumidor final", "CC", FiscalParty.FinalConsumerIdentification,
            null, 0m, 0m, 0m),
        electronic: true, Now, null, Guid.NewGuid);

    private static FiscalHeader Header(FiscalDocument? document = null) => FiscalDraftBuilder.Header(document ?? Document(), Numbering, Issuer, Establishment, "nota");

    [Fact]
    public void La_factura_refleja_lo_cobrado_con_la_bolsa_como_renglon_y_el_redondeo_como_ajuste()
    {
        var document = Document();
        var header = FiscalDraftBuilder.Header(document, Numbering, Issuer, Establishment, "nota");
        (header.DocumentId, header.ReferenceCode, header.SourceNumber, header.Numbering, header.Issuer, header.Establishment, header.Notes, header.Resubmission)
            .ShouldBe((document.Id, document.ReferenceCode!, "C1-000010", Numbering, Issuer, Establishment, (string?)"nota", false));

        var draft = FiscalDraftBuilder.Invoice(header, FinalConsumer, Sale()).Value;

        draft.Lines.Count.ShouldBe(4);
        draft.Lines.Select(l => (l.LineNo, l.Kind, l.Code)).ShouldBe(
        [
            (1, FiscalLineKind.Product, "ARROZ-500"), (2, FiscalLineKind.Product, "TOMATE"), (3, FiscalLineKind.Product, "GAS-400"),
            (4, FiscalLineKind.BagTax, "BOLSA"),
        ]);
        var soda = draft.Lines[2];
        (soda.Total, soda.Taxes.Single().Kind, soda.TaxTotal).ShouldBe((2_500m, "CONSUMPTION", 185.19m));
        var bag = draft.Lines[3];
        (bag.Name, bag.Quantity, bag.UnitPrice, bag.Total, bag.Taxes.Count).ShouldBe((FiscalDraftBuilder.BagTaxName, 1m, 66m, 66m, 0));
        draft.Lines[1].Taxes.Single().IsExcluded.ShouldBeTrue();

        var totals = draft.Totals;
        (totals.Gross, totals.Discount, totals.TaxBase, totals.TaxTotal, totals.LinesTotal, totals.RoundingAdjustment, totals.Total).ShouldBe(
            (14_857m, 300m, 13_329.73m, 1_095.27m, 14_491m, 25m, 14_516m));
        draft.Payments.Single().MethodCode.ShouldBe("EFECTIVO");
        draft.Customer.IsFinalConsumer.ShouldBeTrue();
    }

    [Fact]
    public void Si_no_concilia_con_la_venta_no_se_envia()
    {
        var result = FiscalDraftBuilder.Invoice(Header(), FinalConsumer, Sale(total: 14_600m));

        result.Error.Code.ShouldBe(FiscalDraftBuilder.TotalsMismatch.Code);
        result.Error.Message.ShouldContain("14.600");
    }

    [Fact]
    public void El_impuesto_a_la_bolsa_sin_tarifa_fija_se_divide_por_la_cantidad()
    {
        var lines = FiscalDraftBuilder.Lines(
        [
            new FiscalSourceLine(Guid.NewGuid(), 1, "BOLSA", "Bolsa", "UND", 3m, 0m, true, 0m, 0m, 0m, 198m,
                [new FiscalTax("BOLSA", "BAG_CONSUMPTION", null, null, 0m, 198m, false, false)]),
            new FiscalSourceLine(Guid.NewGuid(), 2, "X", "Sin cantidad", "UND", 0m, 0m, true, 0m, 0m, 0m, 50m,
                [new FiscalTax("BOLSA", "BAG_CONSUMPTION", null, null, 0m, 50m, false, false), new FiscalTax("B0", "BAG_CONSUMPTION", null, 0m, 0m, 0m, false, false)]),
        ]);

        lines.Where(l => l.Kind == FiscalLineKind.BagTax).Select(l => l.UnitPrice).ShouldBe([66m, 50m]);
        lines.Where(l => l.Kind == FiscalLineKind.Product).Select(l => l.Total).ShouldBe([0m, 0m]);
        Should.Throw<ArgumentNullException>(() => FiscalDraftBuilder.Lines(null!));
        Should.Throw<ArgumentNullException>(() => FiscalDraftBuilder.Totals(null!, 0m));
    }

    [Fact]
    public void El_adquirente_usa_los_datos_del_documento_y_la_correccion()
    {
        var document = Document();
        var original = FinalConsumer with { Email = "cliente@correo.co", Address = "Cra 1", Phone = "300" };
        FiscalDraftBuilder.Buyer(document, original, _ => null).ShouldBe(original with { Name = "Consumidor final" });

        var fiscal = new FiscalBuyerData("LEGAL", "8", "48", ["O-13"], null, "05001", null);
        document.CorrectBuyer(new FiscalBuyerCorrection("Tienda SAS", "NIT", "900123456", null, fiscal), Now, Guid.NewGuid(), Guid.NewGuid);
        var corrected = FiscalDraftBuilder.Buyer(document, original, type => type == "NIT" ? "31" : null);
        (corrected.Name, corrected.IdentificationType, corrected.IdentificationFiscalCode, corrected.IdentificationNumber, corrected.CheckDigit).ShouldBe(
            ("Tienda SAS", "NIT", "31", "900123456", (string?)"8"));
        (corrected.PersonType, corrected.TaxRegime, corrected.Address, corrected.MunicipalityCode, corrected.Phone, corrected.Email).ShouldBe(
            ("LEGAL", "48", (string?)"Cra 1", (string?)"05001", (string?)"300", (string?)"cliente@correo.co"));
        corrected.Responsibilities.ShouldBe(["O-13"]);
        FiscalDraftBuilder.Buyer(document, original, _ => null).IdentificationFiscalCode.ShouldBe("13");

        Should.Throw<ArgumentNullException>(() => FiscalDraftBuilder.Buyer(null!, original, _ => null));
        Should.Throw<ArgumentNullException>(() => FiscalDraftBuilder.Buyer(document, null!, _ => null));
        Should.Throw<ArgumentNullException>(() => FiscalDraftBuilder.Buyer(document, original, null!));
        Should.Throw<ArgumentNullException>(() => FiscalDraftBuilder.Header(null!, Numbering, Issuer, Establishment));
    }

    [Fact]
    public void La_nota_credito_referencia_solo_una_factura_aceptada()
    {
        var invoice = Document();
        FiscalDraftBuilder.Reference(invoice).Error.ShouldBe(FiscalDraftBuilder.RelatedNotAccepted);
        FiscalDraftBuilder.Reference(null).Error.ShouldBe(FiscalDraftBuilder.RelatedNotAccepted);

        invoice.RecordResult(
            new FiscalProviderResult(FiscalOutcome.Accepted, "p-9", "VALIDATED", "SETP9", 9, "cufe-9", "qr", null, "200", null, Now), "FAKE", Now, Now, Guid.NewGuid);
        var reference = FiscalDraftBuilder.Reference(invoice).Value;
        reference.ShouldBe(new FiscalDocumentReference(invoice.Id, invoice.ReferenceCode!, "p-9", "SETP9", "cufe-9", new DateOnly(2026, 9, 30)));

        var sale = Sale();
        var credit = FiscalDraftBuilder.VoidCreditNote(Header(Document(FiscalSource.SaleVoid)), FinalConsumer, reference, sale).Value;
        (credit.Concept, credit.Reason, credit.Invoice, credit.Totals.Total, credit.Lines.Count).ShouldBe(
            (FiscalCorrectionConcept.Void, "Se cobró dos veces", reference, 14_516m, 4));
        FiscalDraftBuilder.VoidCreditNote(Header(), FinalConsumer, reference, sale with { VoidReason = null }).Value.Reason.ShouldBe("Anulación de la venta");
        FiscalDraftBuilder.VoidCreditNote(Header(), FinalConsumer, reference, Sale(total: 1m)).IsFailure.ShouldBeTrue();
        Should.Throw<ArgumentNullException>(() => FiscalDraftBuilder.VoidCreditNote(Header(), FinalConsumer, reference, null!));
    }

    [Fact]
    public void La_nota_credito_por_cambio_o_garantia_prorratea_los_impuestos_de_las_unidades_devueltas()
    {
        var reference = new FiscalDocumentReference(Guid.NewGuid(), "ref", null, "SETP1", "cufe", new DateOnly(2026, 9, 30));
        var customerReturn = new FiscalReturnSnapshot(
            Guid.NewGuid(), "D-1", "WARRANTY_REFUND", "Defectuoso", new DateOnly(2026, 9, 30),
            [new FiscalReturnLine(RiceLine, 1m, 2_850m), new FiscalReturnLine(SodaLine, 1m, 2_566m)],
            [new FiscalPayment("EFECTIVO", "CASH", "10", 5_416m, null)]);

        var credit = FiscalDraftBuilder.ReturnCreditNote(Header(), FinalConsumer, reference, Sale(), customerReturn).Value;

        (credit.Concept, credit.Reason, credit.Totals.Total, credit.Totals.RoundingAdjustment).ShouldBe((FiscalCorrectionConcept.PartialReturn, "Defectuoso", 5_416m, 0m));
        var rice = credit.Lines[0];
        (rice.Quantity, rice.Gross, rice.Discount, rice.Total, rice.Taxes.Single().Amount, rice.TaxBase).ShouldBe((1m, 3_000m, 150m, 2_850m, 455.04m, 2_394.96m));
        credit.Lines.Single(l => l.Kind == FiscalLineKind.BagTax).Total.ShouldBe(66m);
        credit.Lines.Where(l => l.Code == "GAS-400").Single().Total.ShouldBe(2_500m);
        credit.Payments.Single().Amount.ShouldBe(5_416m);

        var wrong = customerReturn with { Lines = [new FiscalReturnLine(Guid.NewGuid(), 1m, 10m)] };
        FiscalDraftBuilder.ReturnCreditNote(Header(), FinalConsumer, reference, Sale(), wrong).Error.Code.ShouldBe("BILLING.RETURN_LINE_MISMATCH");
        var tooMany = customerReturn with { Lines = [new FiscalReturnLine(RiceLine, 3m, 10m)] };
        FiscalDraftBuilder.ReturnCreditNote(Header(), FinalConsumer, reference, Sale(), tooMany).IsFailure.ShouldBeTrue();
        Should.Throw<ArgumentNullException>(() => FiscalDraftBuilder.ReturnCreditNote(Header(), FinalConsumer, reference, Sale(), null!));
        Should.Throw<ArgumentNullException>(() => FiscalDraftBuilder.ReturnCreditNote(null!, FinalConsumer, reference, Sale(), customerReturn));
    }

    [Fact]
    public void El_documento_soporte_usa_los_renglones_de_la_compra()
    {
        var supplier = new FiscalParty("CC", "13", "71234567", null, "Pedro Campesino", "NATURAL", "49", ["R-99-PN"], "Vereda 1", "05001", null, null);
        var purchase = new FiscalPurchaseSnapshot(
            Guid.NewGuid(), "CO-1", "SIN-FACTURA", new DateOnly(2026, 9, 29), supplier,
            [new FiscalSourceLine(Guid.NewGuid(), 1, "PAPA", "Papa pastusa", "KG", 50m, 1_200m, false, 60_000m, 0m, 60_000m, 60_000m, [])],
            [new FiscalPayment("EFECTIVO", "CASH", "10", 60_000m, null)]);

        var draft = FiscalDraftBuilder.SupportDocument(Header(Document(FiscalSource.Purchase)), purchase);

        (draft.Supplier, draft.SupplierInvoiceNumber, draft.Totals.Total, draft.Lines.Single().Code, draft.Payments.Count).ShouldBe(
            (supplier, "SIN-FACTURA", 60_000m, "PAPA", 1));
        draft.Header.ReferenceCode.ShouldStartWith("DS");
        Should.Throw<ArgumentNullException>(() => FiscalDraftBuilder.SupportDocument(Header(), null!));
        Should.Throw<ArgumentNullException>(() => FiscalDraftBuilder.SupportDocument(null!, purchase));
    }

    [Fact]
    public void Un_reenvio_se_marca_en_el_encabezado()
    {
        var document = Document();
        document.RecordResult(new FiscalProviderResult(FiscalOutcome.Rejected, Message: "FAK24"), "FAKE", Now, Now, Guid.NewGuid);

        FiscalDraftBuilder.Header(document, Numbering, Issuer, Establishment).Resubmission.ShouldBeTrue();
        Should.Throw<ArgumentNullException>(() => FiscalDraftBuilder.Invoice(null!, FinalConsumer, Sale()));
        Should.Throw<ArgumentNullException>(() => FiscalDraftBuilder.Invoice(Header(), FinalConsumer, null!));
    }
}
