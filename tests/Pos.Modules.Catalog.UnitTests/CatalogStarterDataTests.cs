using Pos.Modules.Catalog.Application;
using Pos.Modules.Catalog.Domain;
using Pos.Modules.Organization.Contracts;

namespace Pos.Modules.Catalog.UnitTests;

/// <summary>Fase 7-C parte A: categorías de ejemplo según el tipo de negocio (pocas y válidas; "Otro" no crea ninguna).</summary>
public class CatalogStarterDataTests
{
    [Fact]
    public void Cada_tipo_de_negocio_trae_pocas_categorias_validas_y_otro_ninguna()
    {
        foreach (var profile in BusinessCatalog.Profiles.Select(p => p.Code).Where(c => c != BusinessCatalog.Other))
        {
            var names = CatalogStarterData.CategoriesFor(profile);
            names.Count.ShouldBeInRange(4, 8, profile);
            names.ShouldBeUnique();
            names.ShouldNotContain(CatalogInitializer.DefaultCategoryName);
            names.ShouldAllBe(n => Category.Create(Guid.NewGuid(), Guid.NewGuid(), n, null, 0).IsSuccess);
        }

        CatalogStarterData.CategoriesFor(BusinessCatalog.Other).ShouldBeEmpty();
        CatalogStarterData.CategoriesFor("DESCONOCIDO").ShouldBeEmpty();
        CatalogStarterData.CategoriesFor(BusinessCatalog.Pharmacy).ShouldContain("Medicamentos");
    }
}
