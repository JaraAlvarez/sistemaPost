using Pos.Modules.Audit.Contracts;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Identity.Domain;
using Pos.Modules.Organization.Contracts;

namespace Pos.Modules.Identity.Application;

/// <summary>
/// Los 7 roles de sistema del doc 06 con los permisos que existen a la fecha. Cada fase agrega los permisos de sus
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

    public static IReadOnlyList<SystemRoleDefinition> Build(IReadOnlyCollection<string> allPermissions) =>
    [
        new(Owner, "Propietario", "Dueño del negocio: acceso total.", allPermissions),
        new(Administrator, "Administrador", "Administra la operación y la configuración.", allPermissions),
        new(CashSupervisor, "Supervisor de caja", "Supervisa cajas, autoriza anulaciones y revisa cierres.",
            [OrganizationPermissions.BranchView, SettingsPermissions.SettingView, IdentityPermissions.UserView, IdentityPermissions.SessionRevoke]),
        new(Cashier, "Cajero", "Vende y opera su caja.", []),
        new(Inventory, "Inventario", "Gestiona existencias, ajustes y conteos.", [OrganizationPermissions.BranchView]),
        new(Purchasing, "Compras", "Gestiona proveedores y compras.", [OrganizationPermissions.BranchView]),
        new(Accountant, "Contador", "Consulta información contable y de auditoría.",
            [OrganizationPermissions.CompanyView, OrganizationPermissions.BranchView, SettingsPermissions.SettingView,
             AuditPermissions.LogView, IdentityPermissions.PermissionView]),
    ];
}
