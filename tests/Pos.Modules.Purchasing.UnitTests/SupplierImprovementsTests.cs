using Pos.Modules.Purchasing.Domain;

namespace Pos.Modules.Purchasing.UnitTests;

/// <summary>Fase 8, bloque 8.4: pedido mínimo, agenda, retenciones sugeridas (RN-PUR-10) y cuentas bancarias (RN-PUR-09).</summary>
public class SupplierOrderingTermsTests
{
    [Fact]
    public void Pedido_minimo_y_nota_de_corte()
    {
        var supplier = Build.Supplier();

        supplier.SetOrderingTerms(250_000m, "  Pedidos hasta el martes 10 a. m. ").IsSuccess.ShouldBeTrue();
        supplier.MinimumOrderAmount.ShouldBe(250_000m);
        supplier.OrderCutoffNote.ShouldBe("Pedidos hasta el martes 10 a. m.");

        supplier.SetOrderingTerms(null, "  ").IsSuccess.ShouldBeTrue();
        supplier.MinimumOrderAmount.ShouldBeNull();
        supplier.OrderCutoffNote.ShouldBeNull();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10.555)]
    public void Pedido_minimo_invalido(double minimum) =>
        Build.Supplier().SetOrderingTerms((decimal)minimum, null).Error.ShouldBe(PurchasingErrors.InvalidOrderingTerms);

    [Fact]
    public void Nota_de_corte_de_mas_de_200_caracteres_es_invalida() =>
        Build.Supplier().SetOrderingTerms(0m, new string('x', 201)).Error.ShouldBe(PurchasingErrors.InvalidOrderingTerms);
}

public class SupplierScheduleTests
{
    private static readonly Guid Supplier = Guid.NewGuid();

    [Fact]
    public void Agenda_valida_con_sucursal_y_para_todas()
    {
        var branch = Guid.NewGuid();
        SupplierSchedule.Validate([
            new ScheduleEntryInput(1, ScheduleKind.Visit, null, "Vendedor Carlos"),
            new ScheduleEntryInput(1, ScheduleKind.Visit, branch, null),
            new ScheduleEntryInput(1, ScheduleKind.Order, null, null),
            new ScheduleEntryInput(4, ScheduleKind.Delivery, null, null),
        ]).IsSuccess.ShouldBeTrue();
        SupplierSchedule.Validate([]).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void Dia_fuera_de_rango(int day) =>
        SupplierSchedule.Validate([new ScheduleEntryInput(day, ScheduleKind.Visit, null, null)]).Error.ShouldBe(PurchasingErrors.InvalidSchedule);

    [Fact]
    public void Entradas_repetidas_notas_largas_o_demasiadas_son_invalidas()
    {
        SupplierSchedule.Validate([new ScheduleEntryInput(2, ScheduleKind.Order, null, null), new ScheduleEntryInput(2, ScheduleKind.Order, null, "otra")])
            .Error.ShouldBe(PurchasingErrors.InvalidSchedule);
        SupplierSchedule.Validate([new ScheduleEntryInput(2, ScheduleKind.Order, null, new string('x', 201))]).Error.ShouldBe(PurchasingErrors.InvalidSchedule);
        SupplierSchedule.Validate([.. Enumerable.Range(0, SupplierSchedule.MaxEntries + 1).Select(_ => new ScheduleEntryInput(1, ScheduleKind.Visit, Guid.NewGuid(), null))])
            .Error.ShouldBe(PurchasingErrors.InvalidSchedule);
        SupplierSchedule.Validate([null!]).Error.ShouldBe(PurchasingErrors.InvalidSchedule);
    }

    [Fact]
    public void Entrada_coincide_por_tipo_dia_y_sucursal_y_calcula_la_proxima_fecha()
    {
        var branch = Guid.NewGuid();
        var entry = SupplierSchedule.Create(Guid.NewGuid(), Build.Company, Supplier, new ScheduleEntryInput(3, ScheduleKind.Delivery, branch, "  8 a. m. "));

        entry.Notes.ShouldBe("8 a. m.");
        entry.DayOfWeek.ShouldBe((short)3);
        entry.Matches(new ScheduleEntryInput(3, ScheduleKind.Delivery, branch, "otra nota")).ShouldBeTrue();
        entry.Matches(new ScheduleEntryInput(3, ScheduleKind.Delivery, null, null)).ShouldBeFalse();
        entry.Matches(new ScheduleEntryInput(3, ScheduleKind.Order, branch, null)).ShouldBeFalse();
        entry.Matches(null!).ShouldBeFalse();
        entry.AuditLabel.ShouldBe("Agenda de proveedor: DELIVERY miércoles");

        // 2026-09-28 es lunes: el miércoles siguiente es el 30; desde el mismo miércoles, ese día.
        entry.NextOn(new DateOnly(2026, 9, 28)).ShouldBe(new DateOnly(2026, 9, 30));
        entry.NextOn(new DateOnly(2026, 9, 30)).ShouldBe(new DateOnly(2026, 9, 30));
        entry.NextOn(new DateOnly(2026, 10, 1)).ShouldBe(new DateOnly(2026, 10, 7));
        SupplierSchedule.Create(Guid.NewGuid(), Build.Company, Supplier, new ScheduleEntryInput(7, ScheduleKind.Visit, null, null))
            .NextOn(new DateOnly(2026, 10, 4)).ShouldBe(new DateOnly(2026, 10, 4)); // domingo

        entry.SetNotes(" ");
        entry.Notes.ShouldBeNull();
        SupplierSchedule.DayName(1).ShouldBe("lunes");
        SupplierSchedule.DayName(9).ShouldBe("9");
    }
}

public class WithholdingDefaultTests
{
    private static SupplierWithholdingDefault Default(WithholdingKind kind, decimal rate) =>
        SupplierWithholdingDefault.Create(Guid.NewGuid(), Build.Company, Guid.NewGuid(), kind, rate, "Compras").Value;

    [Fact]
    public void Tarifa_y_concepto_validos()
    {
        var item = SupplierWithholdingDefault.Create(Guid.NewGuid(), Build.Company, Guid.NewGuid(), WithholdingKind.Retefuente, 2.5m, "  Compras generales ").Value;
        item.Rate.ShouldBe(2.5m);
        item.Concept.ShouldBe("Compras generales");
        item.AuditLabel.ShouldStartWith("Retención sugerida RETEFUENTE 2");

        item.Update(3.5m, null).IsSuccess.ShouldBeTrue();
        item.Concept.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100.5)]
    [InlineData(1.23456)]
    public void Tarifa_invalida(double rate) =>
        SupplierWithholdingDefault.Create(Guid.NewGuid(), Build.Company, Guid.NewGuid(), WithholdingKind.Reteica, (decimal)rate, null)
            .Error.ShouldBe(PurchasingErrors.InvalidWithholdingDefault);

    [Fact]
    public void Concepto_de_mas_de_100_caracteres_es_invalido() =>
        Default(WithholdingKind.Reteiva, 15m).Update(15m, new string('x', 101)).Error.ShouldBe(PurchasingErrors.InvalidWithholdingDefault);

    [Fact]
    public void Sugerencia_retefuente_y_reteica_sobre_la_base_y_reteiva_sobre_el_iva()
    {
        // 10 × 100.000 con descuento de 20.000 e IVA 19 %: base 980.000, IVA 186.200.
        var costing = PurchaseCosting.Calculate([new CostingLine(10m, 1m, 100_000m, 20_000m, [Build.Vat19], null)], 5_000m, ProrationMethod.Value, true);
        var (taxable, vat) = WithholdingSuggestion.BasesOf(costing);
        taxable.ShouldBe(980_000m);
        vat.ShouldBe(186_200m);

        var suggested = WithholdingSuggestion.Suggest(
            [Default(WithholdingKind.Reteica, 0.414m), Default(WithholdingKind.Retefuente, 2.5m), Default(WithholdingKind.Reteiva, 15m)], taxable, vat);

        suggested.Select(w => (w.Kind, w.Base, w.Rate, w.Amount)).ShouldBe([
            (WithholdingKind.Retefuente, 980_000m, (decimal?)2.5m, 24_500m),
            (WithholdingKind.Reteiva, 186_200m, (decimal?)15m, 27_930m),
            (WithholdingKind.Reteica, 980_000m, (decimal?)0.414m, 4_057.20m),
        ]);
    }

    [Fact]
    public void Sin_iva_no_se_sugiere_reteiva_y_la_compra_acepta_las_sugeridas()
    {
        var costing = PurchaseCosting.Calculate([new CostingLine(4m, 1m, 50_000m, 0m, [], null)], 0m, ProrationMethod.Value, true);
        var (taxable, vat) = WithholdingSuggestion.BasesOf(costing);
        var suggested = WithholdingSuggestion.Suggest([Default(WithholdingKind.Retefuente, 2.5m), Default(WithholdingKind.Reteiva, 15m)], taxable, vat);

        suggested.Single().Kind.ShouldBe(WithholdingKind.Retefuente);
        var purchase = Build.Purchase([Build.Line(4m, 50_000m)], withholdings: suggested);
        purchase.WithholdingTotal.ShouldBe(5_000m);
        purchase.PayableTotal.ShouldBe(195_000m);
        WithholdingSuggestion.Suggest([], taxable, vat).ShouldBeEmpty();
    }
}

public class SupplierBankAccountTests
{
    private static readonly Guid Registrar = Guid.NewGuid();
    private static readonly Guid Verifier = Guid.NewGuid();

    private static BankAccountData Data(string number = "123-456 789.01", string bank = "1007", BankAccountType type = BankAccountType.Savings) =>
        new(bank, type, number, " Distribuidora Láctea SAS ", "nit", "800197268");

    private static SupplierBankAccount Account(bool primary = true) =>
        SupplierBankAccount.Create(Guid.NewGuid(), Build.Company, Guid.NewGuid(), Data(), primary, Registrar, Build.Now).Value;

    [Fact]
    public void Cuenta_nueva_queda_por_verificar_con_numero_normalizado_y_enmascarado()
    {
        var account = Account();

        account.Status.ShouldBe(BankAccountStatus.PendingVerification);
        account.AccountNumber.ShouldBe("12345678901");
        account.MaskedNumber.ShouldBe("****8901");
        account.HolderName.ShouldBe("Distribuidora Láctea SAS");
        account.HolderIdentificationType.ShouldBe("NIT");
        account.ChangedBy.ShouldBe(Registrar);
        account.ChangedAt.ShouldBe(Build.Now);
        account.IsPrimary.ShouldBeTrue();
        account.IsActive.ShouldBeTrue();
        account.AuditLabel.ShouldBe("Cuenta bancaria 1007 ****8901");
        SupplierBankAccount.Mask("123").ShouldBe("****");
        SupplierBankAccount.Mask(null).ShouldBe("****");
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("12345678901234567890123")]
    [InlineData("12AB5678")]
    public void Numero_invalido(string number) =>
        SupplierBankAccount.Create(Guid.NewGuid(), Build.Company, Guid.NewGuid(), Data(number), false, Registrar, Build.Now)
            .Error.ShouldBe(PurchasingErrors.InvalidBankAccount);

    [Fact]
    public void Titular_banco_o_tipo_invalidos()
    {
        SupplierBankAccount.Create(Guid.NewGuid(), Build.Company, Guid.NewGuid(), Data() with { HolderName = " " }, false, Registrar, Build.Now)
            .Error.ShouldBe(PurchasingErrors.InvalidBankAccount);
        SupplierBankAccount.Create(Guid.NewGuid(), Build.Company, Guid.NewGuid(), Data(bank: "X"), false, Registrar, Build.Now)
            .Error.ShouldBe(PurchasingErrors.InvalidBankAccount);
        SupplierBankAccount.Create(Guid.NewGuid(), Build.Company, Guid.NewGuid(), Data(type: (BankAccountType)9), false, Registrar, Build.Now)
            .Error.ShouldBe(PurchasingErrors.InvalidBankAccount);
        SupplierBankAccount.Create(Guid.NewGuid(), Build.Company, Guid.NewGuid(), Data() with { HolderIdentificationNumber = "" }, false, Registrar, Build.Now)
            .Error.ShouldBe(PurchasingErrors.InvalidBankAccount);
        Account().Update(Data() with { HolderIdentificationType = "" }, true, true, Registrar, Build.Now).Error.ShouldBe(PurchasingErrors.InvalidBankAccount);
    }

    [Fact]
    public void La_verifica_otro_usuario_y_no_quien_la_registro()
    {
        var account = Account();

        account.Verify(Registrar, Build.Now).Error.ShouldBe(PurchasingErrors.BankAccountSameUser);
        account.Verify(Verifier, Build.Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        account.Status.ShouldBe(BankAccountStatus.Verified);
        account.VerifiedBy.ShouldBe(Verifier);
        account.VerifiedAt.ShouldBe(Build.Now.AddHours(1));

        account.Verify(Guid.NewGuid(), Build.Now).Error.ShouldBe(PurchasingErrors.BankAccountNotPending);
    }

    [Fact]
    public void Cambiar_el_numero_la_deja_por_verificar_otra_vez()
    {
        var account = Account();
        account.Verify(Verifier, Build.Now).IsSuccess.ShouldBeTrue();

        var changed = account.Update(Data("99988877766"), isActive: true, isPrimary: true, Verifier, Build.Now.AddDays(1)).Value;

        changed.ShouldBe(new BankAccountChange(Details: true, Status: true, Primary: false));
        changed.Any.ShouldBeTrue();
        account.Status.ShouldBe(BankAccountStatus.PendingVerification);
        account.VerifiedBy.ShouldBeNull();
        account.ChangedBy.ShouldBe(Verifier);
        // Ahora quien la cambió no puede verificarla; quien la registró al principio sí.
        account.Verify(Verifier, Build.Now).Error.ShouldBe(PurchasingErrors.BankAccountSameUser);
        account.Verify(Registrar, Build.Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Sin_cambios_de_datos_no_pierde_la_verificacion_y_cambiar_la_principal_se_informa()
    {
        var account = Account();
        account.Verify(Verifier, Build.Now).IsSuccess.ShouldBeTrue();

        account.Update(Data(), true, true, Registrar, Build.Now).Value.Any.ShouldBeFalse();
        account.Status.ShouldBe(BankAccountStatus.Verified);

        var primary = account.Update(Data(), true, false, Registrar, Build.Now).Value;
        primary.ShouldBe(new BankAccountChange(false, false, true));
        account.IsPrimary.ShouldBeFalse();
        account.Status.ShouldBe(BankAccountStatus.Verified);
    }

    [Fact]
    public void Inactivar_quita_la_principal_y_reactivar_exige_verificar()
    {
        var account = Account();
        account.Verify(Verifier, Build.Now).IsSuccess.ShouldBeTrue();

        var inactivated = account.Update(Data(), isActive: false, isPrimary: true, Verifier, Build.Now).Value;
        inactivated.Status.ShouldBeTrue();
        account.Status.ShouldBe(BankAccountStatus.Inactive);
        account.IsActive.ShouldBeFalse();
        account.IsPrimary.ShouldBeFalse();
        account.VerifiedBy.ShouldBeNull();
        account.Verify(Registrar, Build.Now).Error.ShouldBe(PurchasingErrors.BankAccountNotPending);

        // Inactiva y sin cambios: no hay nada que registrar.
        account.Update(Data(), false, false, Registrar, Build.Now).Value.Any.ShouldBeFalse();
        account.ChangedBy.ShouldBe(Verifier);

        account.Update(Data(), isActive: true, isPrimary: false, Registrar, Build.Now).Value.Status.ShouldBeTrue();
        account.Status.ShouldBe(BankAccountStatus.PendingVerification);
        account.ChangedBy.ShouldBe(Registrar);
    }

    [Fact]
    public void Otra_cuenta_puede_quitarle_la_marca_de_principal()
    {
        var account = Account();
        account.ClearPrimary();
        account.IsPrimary.ShouldBeFalse();
        SupplierBankAccount.NormalizeNumber(null).ShouldBe(string.Empty);
    }
}
