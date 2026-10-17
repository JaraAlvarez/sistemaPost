using Pos.Modules.Catalog.Domain;

namespace Pos.Modules.Catalog.UnitTests;

/// <summary>La lista general siempre admite promociones en la caja; el valor guardado se conserva y solo se rechaza apagarlo de forma explícita.</summary>
public class PriceListPromotionsTests
{
    [Fact]
    public void Volverla_general_conserva_el_valor_guardado()
    {
        var list = PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), "MAYORISTA", "Mayorista", pricesIncludeTax: true, isDefault: false).Value;
        list.ConfigureRules(null, 50m, allowsPromotions: false).IsSuccess.ShouldBeTrue();

        list.MakeDefault();

        list.IsDefault.ShouldBeTrue();
        list.AllowsPromotions.ShouldBeFalse();
    }

    [Fact]
    public void En_la_general_solo_se_rechaza_apagarlas_de_forma_explicita()
    {
        var list = PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), "VIEJA", "Vieja", pricesIncludeTax: true, isDefault: false).Value;
        list.ConfigureRules(null, 50m, allowsPromotions: false);
        list.MakeDefault();

        list.ConfigureRules(null, 100m, allowsPromotions: null).IsSuccess.ShouldBeTrue();
        list.RoundingIncrement.ShouldBe(100m);
        list.AllowsPromotions.ShouldBeFalse();
        list.ConfigureRules(null, 100m, allowsPromotions: false).Error.ShouldBe(CatalogErrors.DefaultListAllowsPromotions);
        list.ConfigureRules(null, 100m, allowsPromotions: true).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Una_lista_que_no_es_la_general_puede_apagarlas()
    {
        var list = PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), "CLIENTES", "Clientes", pricesIncludeTax: true, isDefault: false).Value;

        list.ConfigureRules(10m, 50m, allowsPromotions: false).IsSuccess.ShouldBeTrue();
        list.ConfigureRules(10m, 50m, allowsPromotions: null).IsSuccess.ShouldBeTrue();
        list.AllowsPromotions.ShouldBeFalse();
    }
}
