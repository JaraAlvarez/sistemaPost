using Pos.Api.Abstractions;
using Pos.Modules.Audit.Api;
using Pos.Modules.Backup.Api;
using Pos.Modules.Billing.Api;
using Pos.Modules.Cash.Api;
using Pos.Modules.Catalog.Api;
using Pos.Modules.Customers.Api;
using Pos.Modules.Expenses.Api;
using Pos.Modules.Identity.Api;
using Pos.Modules.Inventory.Api;
using Pos.Modules.Licensing.Api;
using Pos.Modules.Organization.Api;
using Pos.Modules.Parties.Api;
using Pos.Modules.Promotions.Api;
using Pos.Modules.Purchasing.Api;
using Pos.Modules.Reference.Api;
using Pos.Modules.Reporting.Api;
using Pos.Modules.Sales.Api;

namespace Pos.Server.Host.Modules;

/// <summary>
/// Lista explícita de módulos de negocio cargados por el servidor. Cada fase agrega aquí su módulo
/// (una línea). Se prefiere una lista explícita a descubrir módulos por reflexión: es visible,
/// ordenada y no carga código inesperado.
/// </summary>
internal static class ModuleCatalog
{
    public static IReadOnlyList<IModule> All { get; } =
    [
        new ReferenceModule(),
        new OrganizationModule(),
        new IdentityModule(),
        new AuditModule(),
        new CatalogModule(),
        new InventoryModule(),
        new PartiesModule(),
        new CashModule(),
        new PurchasingModule(),
        new ExpensesModule(),
        new PromotionsModule(),
        new BillingModule(),
        new SalesModule(),
        new CustomersModule(),
        new ReportingModule(),
        new BackupModule(),
        new LicensingModule(),
    ];
}
