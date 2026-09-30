using Pos.Modules.Audit.Contracts;
using Pos.Modules.Billing.Contracts;
using Pos.Modules.Cash.Contracts;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Customers.Contracts;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Identity.Domain;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Expenses.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Parties.Contracts;
using Pos.Modules.Promotions.Contracts;
using Pos.Modules.Purchasing.Contracts;
using Pos.Modules.Sales.Contracts;

namespace Pos.Modules.Identity.Application;

/// <summary>
/// Los 7 roles de sistema del doc 06 (más el encargado de promociones, Fase 7) con los permisos que existen a la fecha. Cada fase agrega los permisos de sus
/// módulos a estos roles. Propietario y Administrador reciben TODO el catálogo vigente.
/// </summary>
public static class SystemRoles
{
    public const string Owner = "OWNER";
    public const string Administrator = "ADMIN";
    public const string CashSupervisor = "CASH_SUPERVISOR";
    public const string Cashier = "CASHIER";
    public const string Inventory = "INVENTORY";
    public const string Purchasing = "PURCHASING";
    public const string Accountant = "ACCOUNTANT";
    public const string PromotionsManager = "PROMOTIONS_MANAGER";

    public static IReadOnlyList<SystemRoleDefinition> Build(IReadOnlyCollection<string> allPermissions) =>
    [
        new(Owner, "Propietario", "Dueño del negocio: acceso total.", allPermissions),
        // El reintegro de dinero por garantía es una excepción legal que solo autoriza el propietario (Fase 7).
        new(Administrator, "Administrador", "Administra la operación y la configuración.", [.. allPermissions.Where(p => p != SalesPermissions.WarrantyRefund)]),
        new(CashSupervisor, "Supervisor de caja", "Supervisa cajas, autoriza anulaciones y revisa cierres.",
            [OrganizationPermissions.BranchView, SettingsPermissions.SettingView, IdentityPermissions.UserView, IdentityPermissions.SessionRevoke,
             CatalogPermissions.ProductView, InventoryPermissions.StockView, InventoryPermissions.CountRegister, PartiesPermissions.PartyView,
             CashPermissions.SessionOperate, CashPermissions.MovementWithdraw, CashPermissions.DrawerOpen, CashPermissions.SessionCloseAny,
             CashPermissions.SessionReview, CashPermissions.ReportView, InventoryPermissions.AdjustmentQuick,
             SalesPermissions.SaleCreate, SalesPermissions.LineVoid, SalesPermissions.DiscountApply, SalesPermissions.PriceOverride, SalesPermissions.SaleCancel,
             SalesPermissions.SaleVoid, SalesPermissions.ExpiredSell, SalesPermissions.ExchangeCreate, SalesPermissions.SaleReprint, SalesPermissions.SaleView,
             PromotionsPermissions.PromotionView, CustomersPermissions.CustomerView, CustomersPermissions.CustomerQuickCreate, CustomersPermissions.CustomerManage,
             CustomersPermissions.HistoryView]),
        new(Cashier, "Cajero", "Vende y opera su caja.",
            [CatalogPermissions.ProductView, InventoryPermissions.StockView, InventoryPermissions.CountRegister, PartiesPermissions.PartyView,
             CashPermissions.SessionOperate, SalesPermissions.SaleCreate, SalesPermissions.LineVoid, SalesPermissions.SaleReprint, SalesPermissions.SaleView,
             CustomersPermissions.CustomerView, CustomersPermissions.CustomerQuickCreate]),
        new(Inventory, "Inventario", "Gestiona existencias, ajustes y conteos.",
            [OrganizationPermissions.BranchView, CatalogPermissions.ProductView, CatalogPermissions.ProductManage, CatalogPermissions.MasterManage,
             CatalogPermissions.ImportRun, InventoryPermissions.StockView, InventoryPermissions.CostView, InventoryPermissions.AdjustmentManage,
             InventoryPermissions.CountManage, InventoryPermissions.CountRegister, InventoryPermissions.TransferManage, PartiesPermissions.PartyView,
             PurchasingPermissions.PurchaseView, PurchasingPermissions.PurchaseManage, InventoryPermissions.AdjustmentQuick, PromotionsPermissions.PromotionView]),
        new(Purchasing, "Compras", "Gestiona proveedores y compras.",
            [OrganizationPermissions.BranchView, CatalogPermissions.ProductView, CatalogPermissions.ProductManage, InventoryPermissions.StockView,
             InventoryPermissions.CostView, PartiesPermissions.PartyView, PartiesPermissions.PartyManage, PurchasingPermissions.SupplierManage,
             PurchasingPermissions.OrderManage, PurchasingPermissions.PurchaseView, PurchasingPermissions.PurchaseManage, PurchasingPermissions.PayableView,
             PurchasingPermissions.ReturnManage, CustomersPermissions.CustomerView]),
        new(Accountant, "Contador", "Consulta información contable y de auditoría.",
            [OrganizationPermissions.CompanyView, OrganizationPermissions.BranchView, SettingsPermissions.SettingView,
             AuditPermissions.LogView, IdentityPermissions.PermissionView, CatalogPermissions.ProductView, InventoryPermissions.StockView,
             InventoryPermissions.CostView, PartiesPermissions.PartyView, PurchasingPermissions.PurchaseView, PurchasingPermissions.PayableView,
             CashPermissions.ReportView, ExpensesPermissions.ExpenseView, SalesPermissions.SaleView, BillingPermissions.DocumentView,
             PromotionsPermissions.PromotionView, CustomersPermissions.CustomerView, CustomersPermissions.HistoryView]),
        new(PromotionsManager, "Encargado de promociones", "Crea, simula, activa y termina promociones; liquida los productos próximos a vencer.",
            [OrganizationPermissions.BranchView, CatalogPermissions.ProductView, InventoryPermissions.StockView, PromotionsPermissions.PromotionManage,
             PromotionsPermissions.PromotionView, SalesPermissions.SaleView]),
    ];
}
