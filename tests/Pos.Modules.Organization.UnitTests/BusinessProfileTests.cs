using Pos.Modules.Organization.Contracts;
using Pos.Modules.Organization.Domain;
using Pos.SharedKernel.Text;

namespace Pos.Modules.Organization.UnitTests;

/// <summary>Fase 7-C parte A: tipos de negocio, sus valores iniciales y los interruptores de la empresa.</summary>
public class BusinessProfileTests
{
    [Theory]
    [InlineData(BusinessProfile.Supermarket, "LOTS_EXPIRY,PROMOTIONS,PURCHASING,SCALE")]
    [InlineData(BusinessProfile.FreshMarket, "PROMOTIONS,PURCHASING,SCALE")]
    [InlineData(BusinessProfile.Pharmacy, "LOTS_EXPIRY,PROMOTIONS,PURCHASING")]
    [InlineData(BusinessProfile.Apparel, "PROMOTIONS,PURCHASING,VARIANTS")]
    [InlineData(BusinessProfile.Hardware, "FRACTIONAL_SALE,OPEN_PRICE,PROMOTIONS,PURCHASING")]
    [InlineData(BusinessProfile.Other, "PROMOTIONS,PURCHASING")]
    public void Cada_tipo_de_negocio_enciende_sus_funciones(BusinessProfile profile, string expected) =>
        BusinessProfiles.Format(BusinessProfiles.Preset(profile)).ShouldBe(expected);

    [Fact]
    public void Las_tallas_solo_se_encienden_en_ropa_y_la_bascula_nunca_en_drogueria_ni_ropa()
    {
        BusinessProfiles.AllProfiles.Where(p => BusinessProfiles.Preset(p).Contains(BusinessFeature.Variants)).ShouldBe([BusinessProfile.Apparel]);
        BusinessProfiles.Preset(BusinessProfile.Pharmacy).ShouldNotContain(BusinessFeature.Scale);
        BusinessProfiles.Preset(BusinessProfile.Apparel).ShouldNotContain(BusinessFeature.Scale);
        BusinessProfiles.Preset(BusinessProfile.Apparel).ShouldNotContain(BusinessFeature.LotsExpiry);
        BusinessProfiles.Preset(BusinessProfile.Hardware).ShouldNotContain(BusinessFeature.LotsExpiry);
    }

    [Fact]
    public void Las_empresas_que_ya_existian_conservan_todo_lo_que_veian()
    {
        BusinessProfiles.Format(BusinessProfiles.Legacy).ShouldBe("FRACTIONAL_SALE,LOTS_EXPIRY,OPEN_PRICE,PROMOTIONS,PURCHASING,SCALE");
        BusinessProfiles.Legacy.ShouldNotContain(BusinessFeature.Variants);
        BusinessProfiles.Default.ShouldBe(BusinessProfile.Supermarket);
        BusinessProfiles.ComingSoon.ShouldBe([BusinessFeature.Variants]);
    }

    [Theory]
    [InlineData("FRESH_MARKET", BusinessProfile.FreshMarket)]
    [InlineData("FreshMarket", BusinessProfile.FreshMarket)]
    [InlineData("pharmacy", BusinessProfile.Pharmacy)]
    [InlineData(" OTHER ", BusinessProfile.Other)]
    public void Los_codigos_del_tipo_se_aceptan_en_ambos_formatos(string code, BusinessProfile expected)
    {
        BusinessProfiles.TryParseProfile(code, out var profile).ShouldBeTrue();
        profile.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PANADERIA")]
    [InlineData("1")]
    public void Un_tipo_desconocido_no_se_acepta(string? code) => BusinessProfiles.TryParseProfile(code, out _).ShouldBeFalse();

    [Fact]
    public void El_texto_guardado_ignora_codigos_desconocidos_y_repetidos()
    {
        BusinessProfiles.Parse("SCALE, lots_expiry,DRONES,,SCALE").ShouldBe([BusinessFeature.Scale, BusinessFeature.LotsExpiry], ignoreOrder: true);
        BusinessProfiles.Parse(null).ShouldBeEmpty();
        BusinessProfiles.Format([BusinessFeature.Scale, BusinessFeature.Scale, BusinessFeature.FractionalSale]).ShouldBe("FRACTIONAL_SALE,SCALE");
        BusinessProfiles.Format([]).ShouldBe(string.Empty);
        BusinessProfiles.TryParseFeature("OPEN_PRICE", out var feature).ShouldBeTrue();
        feature.ShouldBe(BusinessFeature.OpenPrice);
        BusinessProfiles.TryParseFeature("2", out _).ShouldBeFalse();
    }

    [Fact]
    public void La_empresa_nace_como_supermercado_y_cambia_de_tipo_o_de_funciones_sin_perder_nada()
    {
        var company = Company.Create(Guid.CreateVersion7(), CompanyTests.Data()).Value;
        company.BusinessProfile.ShouldBe(BusinessProfile.Supermarket);
        company.Has(BusinessFeature.Scale).ShouldBeTrue();
        company.Has(BusinessFeature.FractionalSale).ShouldBeFalse();

        company.SetFeatures([BusinessFeature.OpenPrice]);
        company.BusinessProfile.ShouldBe(BusinessProfile.Supermarket);
        company.Features.ShouldBe([BusinessFeature.OpenPrice]);
        company.EnabledFeatures.ShouldBe("OPEN_PRICE");

        company.ApplyProfile(BusinessProfile.Hardware);
        company.BusinessProfile.ShouldBe(BusinessProfile.Hardware);
        company.Features.ShouldBe(BusinessProfiles.Preset(BusinessProfile.Hardware), ignoreOrder: true);

        company.SetFeatures([]);
        company.Features.ShouldBeEmpty();
    }

    [Fact]
    public void Los_textos_de_la_interfaz_cubren_cada_tipo_y_cada_funcion()
    {
        BusinessCatalog.Profiles.Select(p => p.Code).ShouldBe(BusinessProfiles.AllProfiles.Select(p => p.UpperSnake()), ignoreOrder: true);
        BusinessCatalog.Features.Select(f => f.Code).ShouldBe(BusinessProfiles.AllFeatures.Select(f => f.UpperSnake()), ignoreOrder: true);
        BusinessCatalog.Features.Where(f => f.ComingSoon).Select(f => f.Code).ShouldBe(BusinessProfiles.ComingSoon.Select(f => f.UpperSnake()));
        BusinessCatalog.Features.ShouldAllBe(f => f.Name.Length > 0 && f.Description.Length > 0);
        BusinessCatalog.Profile("PHARMACY")!.Name.ShouldBe("Droguería");
        BusinessCatalog.Feature("NADA").ShouldBeNull();

        var dto = new BusinessFeaturesDto("SUPERMARKET", ["SCALE"]);
        dto.Has(BusinessCatalog.Scale).ShouldBeTrue();
        dto.Has(BusinessCatalog.Purchasing).ShouldBeFalse();
    }
}
