using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Organization.Contracts;

/// <summary>Permisos del módulo Organization.</summary>
public static class OrganizationPermissions
{
    public const string CompanyView = "organization.company.view";
    public const string CompanyManage = "organization.company.manage";
    public const string BranchView = "organization.branch.view";
    public const string BranchManage = "organization.branch.manage";
    public const string WarehouseManage = "organization.warehouse.manage";
    public const string TerminalManage = "organization.terminal.manage";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(CompanyView, "Consultar los datos de la empresa", isSensitive: false),
        new(CompanyManage, "Modificar los datos de la empresa", isSensitive: true),
        new(BranchView, "Consultar sucursales, bodegas y cajas", isSensitive: false),
        new(BranchManage, "Crear, modificar e inactivar sucursales", isSensitive: true),
        new(WarehouseManage, "Crear, modificar e inactivar bodegas", isSensitive: true),
        new(TerminalManage, "Crear, modificar e inactivar cajas", isSensitive: true),
    ];
}

/// <summary>Permisos de la configuración general (se administra desde el módulo Organization).</summary>
public static class SettingsPermissions
{
    public const string SettingView = "settings.setting.view";
    public const string SettingManage = "settings.setting.manage";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(SettingView, "Consultar la configuración general", isSensitive: false),
        new(SettingManage, "Modificar la configuración general", isSensitive: true),
    ];
}
