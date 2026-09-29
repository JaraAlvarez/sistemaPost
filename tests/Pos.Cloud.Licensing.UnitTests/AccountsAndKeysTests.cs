using Pos.Cloud.Licensing.Domain;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Results;
using static Pos.Cloud.Licensing.UnitTests.TestSupport;

namespace Pos.Cloud.Licensing.UnitTests;

public class NitNumberTests
{
    [Theory]
    [InlineData("900.123.456-8")]
    [InlineData("900123456-8")]
    [InlineData("900123456 - 8")]
    public void Acepta_el_NIT_con_puntos_espacios_y_su_DV(string text)
    {
        NitNumber.TryParse(text, out var nit).ShouldBeTrue();

        nit.ShouldBe(new NitNumber(ValidNit, ValidDv));
        nit.ToString().ShouldBe("900123456-8");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("900123456")]
    [InlineData("900123456-7")]
    [InlineData("900-123-456-8")]
    [InlineData("ABC-8")]
    public void Rechaza_el_NIT_sin_DV_o_con_DV_incorrecto(string? text)
    {
        NitNumber.TryParse(text, out var nit).ShouldBeFalse();
        nit.ShouldBeNull();
    }

    [Fact]
    public void Crear_recorta_espacios_y_exige_ambas_partes()
    {
        NitNumber.TryCreate(" 900123456 ", " 8 ", out var nit).ShouldBeTrue();
        nit!.Number.ShouldBe(ValidNit);
        NitNumber.TryCreate(null, null, out _).ShouldBeFalse();
    }
}

public class AccountTests
{
    private static AccountData Data(string name = "Distribuciones Andinas", string? nit = null, string? dv = null) =>
        new(name, nit, dv, "  Ana Pérez ", "ana@ejemplo.co", " ", "Cliente desde 2026");

    [Fact]
    public void Crea_la_cuenta_con_NIT_opcional_y_limpia_los_textos()
    {
        var account = Account.Create(Guid.CreateVersion7(), AccountKind.Direct, Data(" Tienda Don José ", ValidNit, ValidDv), null).Value;

        account.Name.ShouldBe("Tienda Don José");
        account.Kind.ShouldBe(AccountKind.Direct);
        account.Nit.ShouldBe(ValidNit);
        account.NitCheckDigit.ShouldBe(ValidDv);
        account.ContactName.ShouldBe("Ana Pérez");
        account.ContactEmail.ShouldBe("ana@ejemplo.co");
        account.ContactPhone.ShouldBeNull();
        account.Notes.ShouldBe("Cliente desde 2026");
        account.ParentAccountId.ShouldBeNull();
        account.Status.ShouldBe(RecordStatus.Active);
        account.AuditLabel.ShouldBe("Cuenta Tienda Don José");

        var withoutNit = Account.Create(Guid.CreateVersion7(), AccountKind.Direct, Data(), null).Value;
        withoutNit.Nit.ShouldBeNull();
        withoutNit.NitCheckDigit.ShouldBeNull();
    }

    [Fact]
    public void Valida_nombre_longitudes_y_NIT()
    {
        var id = Guid.CreateVersion7();

        Account.Create(id, AccountKind.Direct, Data("  "), null).Error.ShouldBe(LicensingErrors.InvalidAccount);
        Account.Create(id, AccountKind.Direct, Data(new string('a', 201)), null).Error.ShouldBe(LicensingErrors.InvalidAccount);
        Account.Create(id, AccountKind.Direct, Data() with { ContactName = new string('a', 121) }, null).Error.ShouldBe(LicensingErrors.InvalidAccount);
        Account.Create(id, AccountKind.Direct, Data() with { ContactEmail = new string('a', 121) }, null).Error.ShouldBe(LicensingErrors.InvalidAccount);
        Account.Create(id, AccountKind.Direct, Data() with { ContactPhone = new string('1', 31) }, null).Error.ShouldBe(LicensingErrors.InvalidAccount);
        Account.Create(id, AccountKind.Direct, Data() with { Notes = new string('a', 501) }, null).Error.ShouldBe(LicensingErrors.InvalidAccount);
        Account.Create(id, AccountKind.Direct, Data(nit: ValidNit, dv: "7"), null).Error.ShouldBe(LicensingErrors.NitInvalid);
        Account.Create(id, AccountKind.Direct, Data(nit: ValidNit), null).Error.ShouldBe(LicensingErrors.NitInvalid);
        Should.Throw<ArgumentNullException>(() => Account.Create(id, AccountKind.Direct, null!, null));
    }

    [Fact]
    public void El_padre_debe_ser_un_distribuidor_distinto()
    {
        var reseller = Account.Create(Guid.CreateVersion7(), AccountKind.Reseller, Data("Distribuidor"), null).Value;
        var direct = Account.Create(Guid.CreateVersion7(), AccountKind.Direct, Data("Directo"), null).Value;

        Account.Create(Guid.CreateVersion7(), AccountKind.Direct, Data(), direct).Error.ShouldBe(LicensingErrors.InvalidParentAccount);
        reseller.Update(Data("Distribuidor"), reseller, isActive: true).Error.ShouldBe(LicensingErrors.InvalidParentAccount);

        var child = Account.Create(Guid.CreateVersion7(), AccountKind.Direct, Data(), reseller).Value;
        child.ParentAccountId.ShouldBe(reseller.Id);
        child.Update(Data(), null, isActive: false).IsSuccess.ShouldBeTrue();
        child.ParentAccountId.ShouldBeNull();
        child.Status.ShouldBe(RecordStatus.Inactive);
    }
}

public class OrganizationTests
{
    private static readonly Account Owner =
        Account.Create(Guid.CreateVersion7(), AccountKind.Direct, new AccountData("Dueño", null, null, null, null, null, null), null).Value;

    [Fact]
    public void Crea_la_empresa_con_NIT_valido_y_compara_el_NIT_declarado()
    {
        var organization = Organization.Create(Guid.CreateVersion7(), Owner, " Supermercado La Economía S.A.S. ", ValidNit, ValidDv, " Bogotá ").Value;

        organization.AccountId.ShouldBe(Owner.Id);
        organization.LegalName.ShouldBe("Supermercado La Economía S.A.S.");
        organization.City.ShouldBe("Bogotá");
        organization.Status.ShouldBe(RecordStatus.Active);
        organization.NitNumber.ShouldBe(new NitNumber(ValidNit, ValidDv));
        organization.AuditLabel.ShouldBe("Empresa Supermercado La Economía S.A.S. · NIT 900123456-8");
        organization.HasNit(new NitNumber(ValidNit, ValidDv)).ShouldBeTrue();
        organization.HasNit(new NitNumber(ValidNit, "7")).ShouldBeFalse();
        organization.HasNit(new NitNumber("800123456", ValidDv)).ShouldBeFalse();
        Should.Throw<ArgumentNullException>(() => organization.HasNit(null!));
    }

    [Fact]
    public void Valida_NIT_razon_social_y_ciudad()
    {
        Organization.Create(Guid.CreateVersion7(), Owner, "Empresa", ValidNit, "7", null).Error.ShouldBe(LicensingErrors.NitInvalid);
        Organization.Create(Guid.CreateVersion7(), Owner, " ", ValidNit, ValidDv, null).Error.ShouldBe(LicensingErrors.InvalidOrganization);
        Organization.Create(Guid.CreateVersion7(), Owner, "Empresa", ValidNit, ValidDv, new string('c', 121)).Error.ShouldBe(LicensingErrors.InvalidOrganization);
        Should.Throw<ArgumentNullException>(() => Organization.Create(Guid.CreateVersion7(), null!, "Empresa", ValidNit, ValidDv, null));
    }

    [Fact]
    public void Actualizar_puede_inactivarla_sin_cambiar_el_NIT()
    {
        var organization = Organization.Create(Guid.CreateVersion7(), Owner, "Empresa", ValidNit, ValidDv, null).Value;

        organization.Update("Empresa Nueva", null, isActive: false).IsSuccess.ShouldBeTrue();

        organization.LegalName.ShouldBe("Empresa Nueva");
        organization.Status.ShouldBe(RecordStatus.Inactive);
        organization.Nit.ShouldBe(ValidNit);
        organization.NitCheckDigit.ShouldBe(ValidDv);
        organization.Update(null!, null, isActive: true).Error.ShouldBe(LicensingErrors.InvalidOrganization);
    }
}

public sealed class SigningKeyTests : IDisposable
{
    private readonly LicenseSigningKey _pair = LicenseSigningKey.Generate();

    public void Dispose() => _pair.Dispose();

    private SigningKey Register() => SigningKey.Register(_pair.PublicKey.X, Start).Value;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corta")]
    public void Solo_registra_claves_publicas_Ed25519(string? publicKey)
    {
        var result = SigningKey.Register(publicKey!, Start);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("LICENSING.SIGNING_KEY_INVALID");
        result.Error.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public void Se_registra_en_reserva_con_el_kid_de_la_clave()
    {
        var key = Register();

        key.Kid.ShouldBe(_pair.Kid);
        key.Id.ShouldBe(_pair.Kid);
        key.Algorithm.ShouldBe(SigningKey.EdDsa);
        key.PublicKey.ShouldBe(_pair.PublicKey.X);
        key.Status.ShouldBe(SigningKeyStatus.Standby);
        key.CreatedAt.ShouldBe(Start);
        key.IsTrusted.ShouldBeTrue();
    }

    [Fact]
    public void Ciclo_reserva_activa_retirada_revocada()
    {
        var key = Register();

        key.Retire(Start).Error.ShouldBe(LicensingErrors.SigningKeyTransition);
        key.Activate(Start.AddDays(1)).IsSuccess.ShouldBeTrue();
        key.Status.ShouldBe(SigningKeyStatus.Active);
        key.ActivatedAt.ShouldBe(Start.AddDays(1));
        key.Activate(Start).Error.ShouldBe(LicensingErrors.SigningKeyTransition);
        key.Revoke(Start).Error.ShouldBe(LicensingErrors.SigningKeyTransition);

        key.Retire(Start.AddDays(2)).IsSuccess.ShouldBeTrue();
        key.Status.ShouldBe(SigningKeyStatus.Retired);
        key.RetiredAt.ShouldBe(Start.AddDays(2));
        key.IsTrusted.ShouldBeTrue();
        key.Retire(Start).Error.ShouldBe(LicensingErrors.SigningKeyTransition);

        key.Revoke(Start.AddDays(3)).IsSuccess.ShouldBeTrue();
        key.Status.ShouldBe(SigningKeyStatus.Revoked);
        key.RevokedAt.ShouldBe(Start.AddDays(3));
        key.IsTrusted.ShouldBeFalse();
        key.Revoke(Start).Error.ShouldBe(LicensingErrors.SigningKeyTransition);
        key.Activate(Start).Error.ShouldBe(LicensingErrors.SigningKeyTransition);
    }

    [Fact]
    public void Una_clave_en_reserva_comprometida_se_revoca_sin_activarse()
    {
        var key = Register();

        key.Revoke(Start).IsSuccess.ShouldBeTrue();

        key.IsTrusted.ShouldBeFalse();
        key.ActivatedAt.ShouldBeNull();
    }
}

public class LicensingCodesTests
{
    [Theory]
    [InlineData(LicenseEdition.SingleTerminal, LicenseEditions.SingleTerminal, "Caja Única")]
    [InlineData(LicenseEdition.MultiTerminal, LicenseEditions.MultiTerminal, "Multicaja")]
    public void Las_ediciones_van_y_vuelven_del_codigo(LicenseEdition edition, string code, string display)
    {
        edition.ToCode().ShouldBe(code);
        edition.DisplayName().ShouldBe(display);
        LicensingCodes.TryParseEdition(code, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(edition);
    }

    [Theory]
    [InlineData(DeviceRole.AllInOne, DeviceRoles.AllInOne)]
    [InlineData(DeviceRole.StoreServer, DeviceRoles.StoreServer)]
    public void Los_roles_van_y_vuelven_del_codigo(DeviceRole role, string code)
    {
        role.ToCode().ShouldBe(code);
        LicensingCodes.TryParseRole(code, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(role);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("single")]
    public void Los_codigos_desconocidos_no_se_aceptan(string? code)
    {
        LicensingCodes.TryParseEdition(code, out _).ShouldBeFalse();
        LicensingCodes.TryParseRole(code, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(SubscriptionStatus.Trial, SubscriptionStatuses.Trial, "En prueba")]
    [InlineData(SubscriptionStatus.Active, SubscriptionStatuses.Active, "Activa")]
    [InlineData(SubscriptionStatus.PastDue, SubscriptionStatuses.PastDue, "En gracia")]
    [InlineData(SubscriptionStatus.Suspended, SubscriptionStatuses.Suspended, "Suspendida")]
    [InlineData(SubscriptionStatus.Cancelled, SubscriptionStatuses.Cancelled, "Cancelada")]
    [InlineData(SubscriptionStatus.Expired, SubscriptionStatuses.Expired, "Vencida")]
    public void Los_estados_tienen_codigo_y_nombre(SubscriptionStatus status, string code, string display)
    {
        status.ToCode().ShouldBe(code);
        status.DisplayName().ShouldBe(display);
    }

    [Theory]
    [InlineData(LicenseEdition.SingleTerminal, DeviceRole.AllInOne, true)]
    [InlineData(LicenseEdition.SingleTerminal, DeviceRole.StoreServer, false)]
    [InlineData(LicenseEdition.MultiTerminal, DeviceRole.AllInOne, true)]
    [InlineData(LicenseEdition.MultiTerminal, DeviceRole.StoreServer, true)]
    public void Caja_Unica_solo_admite_un_equipo_que_hace_todo(LicenseEdition edition, DeviceRole role, bool allowed) =>
        EditionRules.Allows(edition, role).ShouldBe(allowed);

    [Fact]
    public void Los_errores_de_la_API_usan_los_codigos_del_contrato()
    {
        LicenseApiErrors.KeyInvalid.Code.ShouldBe(LicenseErrorCodes.KeyInvalid);
        LicenseApiErrors.SigningUnavailable.Type.ShouldBe(ErrorType.Unexpected);
        LicensingErrors.AccountNotFound.Type.ShouldBe(ErrorType.NotFound);

        var invalid = LicenseApiErrors.InvalidField("fingerprint", "Huella inválida.");

        invalid.Type.ShouldBe(ErrorType.Validation);
        invalid.FieldErrors.ShouldHaveSingleItem().Field.ShouldBe("fingerprint");
    }
}
