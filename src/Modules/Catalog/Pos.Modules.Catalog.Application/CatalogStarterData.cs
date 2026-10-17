using Pos.Modules.Catalog.Domain;
using Pos.Modules.Organization.Contracts;
using Pos.SharedKernel.Identifiers;

namespace Pos.Modules.Catalog.Application;

/// <summary>
/// Categorías de ejemplo según el tipo de negocio (Fase 7-C parte A). Solo si el catálogo no tiene más categorías que "General" (la del
/// <see cref="CatalogInitializer"/>): nunca toca un catálogo en uso (el asistente inicial corre con la empresa recién creada). Son pocas a propósito; el dueño las cambia o borra.
/// </summary>
public sealed class CatalogStarterData(ICatalogStore store, IIdGenerator ids) : IBusinessStarterData
{
    public static IReadOnlyList<string> CategoriesFor(string profile) => profile switch
    {
        BusinessCatalog.Supermarket => ["Abarrotes", "Lácteos y huevos", "Bebidas", "Frutas y verduras", "Carnes", "Aseo y hogar", "Cuidado personal"],
        BusinessCatalog.FreshMarket => ["Frutas", "Verduras", "Carnes", "Pollo", "Pescado", "Abarrotes"],
        BusinessCatalog.Pharmacy => ["Medicamentos", "Cuidado personal", "Bebés y maternidad", "Primeros auxilios", "Vitaminas y suplementos"],
        BusinessCatalog.Apparel => ["Hombre", "Mujer", "Niños", "Calzado", "Accesorios"],
        BusinessCatalog.Hardware => ["Herramientas", "Eléctricos", "Plomería", "Pinturas", "Tornillería", "Papelería"],
        _ => [],
    };

    public async Task SeedAsync(Guid companyId, string profile, CancellationToken cancellationToken)
    {
        var names = CategoriesFor(profile);
        var existing = await store.GetCategoriesAsync(cancellationToken);
        if (names.Count == 0 || existing.Any(c => c.Name != CatalogInitializer.DefaultCategoryName))
        {
            return;
        }

        var order = 1;
        foreach (var name in names)
        {
            store.Add(Category.Create(ids.NewId(), companyId, name, parent: null, sortOrder: order++).Value);
        }
    }
}
