using Pos.Application.Abstractions.Installation;
using Pos.Modules.Catalog.Domain;
using Pos.SharedKernel.Identifiers;

namespace Pos.Modules.Catalog.Application;

/// <summary>
/// Datos iniciales del catálogo en cada empresa (propuesta §4.4): categoría "General", lista de precios GENERAL por
/// defecto, impuestos de Colombia y dos reglas de báscula de ejemplo (inactivas). Idempotente: solo crea lo que falta.
/// Las tarifas de INC bolsa, IBUA e ICUI NO se siembran: cada empresa las carga con su contador (quedan inactivos).
/// </summary>
public sealed class CatalogInitializer(ICatalogStore store, IIdGenerator ids) : ICompanyInitializer
{
    public const string Vat19 = "IVA19";
    public const string Vat5 = "IVA5";
    public const string VatExempt = "IVA0_EXENTO";
    public const string VatExcluded = "IVA_EXCLUIDO";
    public const string BagTax = "INC_BOLSA";
    public const string SugaryDrinksTax = "IBUA";
    public const string UltraProcessedTax = "ICUI";
    public const string DefaultCategoryName = "General";

    /// <summary>Fecha desde la que rigen las tarifas de IVA sembradas (Ley 1819 de 2016).</summary>
    public static readonly DateOnly VatRatesSince = new(2017, 1, 1);

    public int Order => 100;

    public async Task InitializeAsync(Guid companyId, CancellationToken cancellationToken)
    {
        if ((await store.GetCategoriesAsync(cancellationToken)).Count == 0)
        {
            store.Add(Category.Create(ids.NewId(), companyId, DefaultCategoryName, parent: null).Value);
        }

        if ((await store.GetPriceListsAsync(cancellationToken)).Count == 0)
        {
            store.Add(PriceList.Create(ids.NewId(), companyId, PriceList.GeneralCode, "Precio general", pricesIncludeTax: true, isDefault: true).Value);
        }

        var existing = (await store.GetTaxesAsync(cancellationToken)).Select(t => t.Code).ToHashSet(StringComparer.Ordinal);
        AddTax(existing, companyId, Vat19, "IVA 19 %", TaxKind.Vat, TaxCalculation.Percentage, false, false, "01", MasterStatus.Active, 19m);
        AddTax(existing, companyId, Vat5, "IVA 5 %", TaxKind.Vat, TaxCalculation.Percentage, false, false, "01", MasterStatus.Active, 5m);
        AddTax(existing, companyId, VatExempt, "IVA exento (0 %)", TaxKind.Vat, TaxCalculation.Percentage, true, false, "01", MasterStatus.Active, 0m);
        AddTax(existing, companyId, VatExcluded, "Excluido de IVA", TaxKind.Vat, TaxCalculation.Percentage, false, true, "01", MasterStatus.Active, 0m);
        AddTax(existing, companyId, BagTax, "Impuesto al consumo de bolsas plásticas", TaxKind.BagConsumption, TaxCalculation.FixedPerUnit,
            false, false, null, MasterStatus.Inactive, null);
        AddTax(existing, companyId, SugaryDrinksTax, "Bebidas ultraprocesadas azucaradas", TaxKind.SugaryDrinks, TaxCalculation.FixedPerUnit,
            false, false, null, MasterStatus.Inactive, null);
        AddTax(existing, companyId, UltraProcessedTax, "Comestibles ultraprocesados", TaxKind.UltraProcessedFood, TaxCalculation.Percentage,
            false, false, null, MasterStatus.Inactive, null);

        if ((await store.GetBarcodeRulesAsync(cancellationToken)).Count == 0)
        {
            var weight = VariableBarcodeRule.Create(ids.NewId(), companyId, "20", VariableBarcodeContent.Weight, 3, 5, 8, 5, 3).Value;
            weight.Deactivate();
            store.Add(weight);
            var price = VariableBarcodeRule.Create(ids.NewId(), companyId, "23", VariableBarcodeContent.Price, 3, 5, 8, 5, 0).Value;
            price.Deactivate();
            store.Add(price);
        }
    }

    private void AddTax(
        HashSet<string> existing, Guid companyId, string code, string name, TaxKind kind, TaxCalculation calculation, bool exempt, bool excluded,
        string? dianCode, MasterStatus status, decimal? rate)
    {
        if (!existing.Add(code))
        {
            return;
        }

        var tax = Tax.Create(ids.NewId(), companyId, code, name, kind, calculation, exempt, excluded, dianCode, isSystem: true, status).Value;
        store.Add(tax);
        if (rate is not null)
        {
            store.Add(TaxRate.Create(ids.NewId(), tax, rate, null, VatRatesSince).Value);
        }
    }
}
