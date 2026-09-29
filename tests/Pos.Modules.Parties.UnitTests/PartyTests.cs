using Pos.Modules.Parties.Application;
using Pos.Modules.Parties.Domain;

namespace Pos.Modules.Parties.UnitTests;

public class PartyTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly IdentificationTypeInfo Nit = new("NIT", true, true, true);
    private static readonly IdentificationTypeInfo Cc = new("CC", false, true, false);
    private static readonly IdentificationTypeInfo Passport = new("PA", false, true, false);

    private static PartyData Legal(string number = "900123456", string? dv = "8") =>
        new(PersonType.Legal, "NIT", number, dv, " Distribuidora Láctea SAS ", null, null, "DisLácteos", "48", ["o-15", "O-13", "O-13"],
            " Facturas@DisLacteos.co ", "6011234567", "Calle 1 # 2-3", "11001", null);

    private static PartyData Natural(string type = "CC", string number = "1020304050") =>
        new(PersonType.Natural, type, number, null, null, "María José", "Pérez Gómez", null, "49", [], null, null, null, null, null);

    [Fact]
    public void Persona_juridica_con_NIT_y_DV_validado()
    {
        var party = Party.Create(Guid.NewGuid(), Company, Legal(), Nit).Value;
        party.DisplayName.ShouldBe("Distribuidora Láctea SAS");
        party.FullIdentification.ShouldBe("NIT 900123456-8");
        party.FiscalResponsibilities.ShouldBe("O-13;O-15");
        party.Email.ShouldBe("facturas@dislacteos.co");
        party.SearchText.ShouldBe("distribuidora lactea sas dislacteos 900123456");
        party.AuditLabel.ShouldContain("900123456-8");

        Party.Create(Guid.NewGuid(), Company, Legal(dv: "7"), Nit).Error.ShouldBe(PartyErrors.InvalidNitCheckDigit);
        Party.Create(Guid.NewGuid(), Company, Legal(dv: null), Nit).Error.ShouldBe(PartyErrors.InvalidNitCheckDigit);
        Party.Create(Guid.NewGuid(), Company, Legal(number: "90012345A"), Nit).Error.ShouldBe(PartyErrors.InvalidNitCheckDigit);
    }

    [Fact]
    public void Persona_natural_tipos_de_documento_y_regimen_por_defecto()
    {
        var party = Party.Create(Guid.NewGuid(), Company, Natural(), Cc).Value;
        party.DisplayName.ShouldBe("María José Pérez Gómez");
        party.CheckDigit.ShouldBeNull();
        party.FiscalResponsibilities.ShouldBe(Party.DefaultResponsibility);
        party.LegalName.ShouldBeNull();

        Party.Create(Guid.NewGuid(), Company, Natural(number: "12AB"), Cc).Error.ShouldBe(PartyErrors.InvalidIdentification);
        Party.Create(Guid.NewGuid(), Company, Natural(number: "12"), Cc).Error.ShouldBe(PartyErrors.InvalidIdentification);
        Party.Create(Guid.NewGuid(), Company, Natural("PA", "ab-12345"), Passport).Value.IdentificationNumber.ShouldBe("AB-12345");
        Party.Create(Guid.NewGuid(), Company, Legal() with { IdentificationType = "CC", IdentificationNumber = "1020304050", CheckDigit = null }, Cc)
            .Error.ShouldBe(PartyErrors.PersonTypeNotAllowed);
        Party.Create(Guid.NewGuid(), Company, Natural() with { LastNames = " " }, Cc).Error.ShouldBe(PartyErrors.InvalidName);
        Party.Create(Guid.NewGuid(), Company, Legal() with { LegalName = null }, Nit).Error.ShouldBe(PartyErrors.InvalidName);
        Party.Create(Guid.NewGuid(), Company, Natural() with { Email = "no-es-correo" }, Cc).Error.ShouldBe(PartyErrors.InvalidEmail);
        Party.Create(Guid.NewGuid(), Company, Natural() with { TaxRegime = " " }, Cc).Error.ShouldBe(PartyErrors.InvalidFiscalData);
        Should.Throw<ArgumentException>(() => Party.Create(Guid.NewGuid(), Company, Natural(), Nit));
    }

    [Fact]
    public void Contactos_con_un_solo_principal()
    {
        var party = Party.Create(Guid.NewGuid(), Company, Legal(), Nit).Value;
        party.SetContacts(
            [new ContactData(" Pedro ", "Vendedor", "300", "PEDRO@X.CO", true), new ContactData("Ana", null, null, null, false)], Guid.NewGuid).IsSuccess.ShouldBeTrue();
        party.Contacts.Count.ShouldBe(2);
        party.Contacts[0].Name.ShouldBe("Pedro");
        party.Contacts[0].Email.ShouldBe("pedro@x.co");
        party.SetContacts([new ContactData("A", null, null, null, true), new ContactData("B", null, null, null, true)], Guid.NewGuid)
            .Error.ShouldBe(PartyErrors.InvalidContact);
        party.SetContacts([new ContactData(" ", null, null, null, false)], Guid.NewGuid).Error.ShouldBe(PartyErrors.InvalidContact);
        party.SetContacts([new ContactData("A", null, null, "malo", false)], Guid.NewGuid).Error.ShouldBe(PartyErrors.InvalidContact);
        party.SetContacts([], Guid.NewGuid).IsSuccess.ShouldBeTrue();
        party.Contacts.ShouldBeEmpty();
    }

    [Fact]
    public void Consumidor_final_es_del_sistema_y_no_se_modifica()
    {
        var consumer = Party.FinalConsumer(Guid.NewGuid(), Company);
        consumer.IsSystem.ShouldBeTrue();
        consumer.IdentificationNumber.ShouldBe(Party.FinalConsumerNumber);
        consumer.DisplayName.ShouldBe("Consumidor final");
        consumer.Update(Natural(), Cc).Error.ShouldBe(PartyErrors.SystemParty);
        consumer.SetActive(false).Error.ShouldBe(PartyErrors.SystemParty);
        consumer.MergeInto(Guid.NewGuid()).Error.ShouldBe(PartyErrors.SystemParty);
    }

    [Fact]
    public void Activar_inactivar_y_fusionar()
    {
        var party = Party.Create(Guid.NewGuid(), Company, Natural(), Cc).Value;
        party.SetActive(false).IsSuccess.ShouldBeTrue();
        party.Status.ShouldBe(PartyStatus.Inactive);
        party.Update(Natural() with { FirstNames = "Juan" }, Cc).IsSuccess.ShouldBeTrue();
        party.DisplayName.ShouldBe("Juan Pérez Gómez");
        party.MergeInto(party.Id).Error.ShouldBe(PartyErrors.SystemParty);

        var survivor = Guid.NewGuid();
        party.MergeInto(survivor).IsSuccess.ShouldBeTrue();
        party.Status.ShouldBe(PartyStatus.Merged);
        party.MergedIntoId.ShouldBe(survivor);
        party.Update(Natural(), Cc).Error.ShouldBe(PartyErrors.Merged);
        party.SetActive(true).Error.ShouldBe(PartyErrors.Merged);
    }

    [Fact]
    public void Identificacion_valida_por_tipo()
    {
        Party.IsValidIdentification("NIT", "800197268", "4", requiresCheckDigit: true).ShouldBeTrue();
        Party.IsValidIdentification("CE", "123456", null, false).ShouldBeTrue();
        Party.IsValidIdentification("DIE", "X-99", null, false).ShouldBeTrue();
        Party.IsValidIdentification("CC", null, null, false).ShouldBeFalse();
        new PartiesPermissionCatalog().GetPermissions().Count().ShouldBe(2);
    }
}
