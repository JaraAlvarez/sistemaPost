using Pos.SharedKernel.Results;

namespace Pos.Modules.Organization.Domain;

/// <summary>Errores de negocio del módulo con código estable (contrato con la UI y la documentación).</summary>
public static class OrganizationErrors
{
    public static readonly Error InvalidNitCheckDigit =
        Error.Validation("ORGANIZATION.INVALID_NIT_CHECK_DIGIT", "El dígito de verificación no corresponde al NIT.");

    public static readonly Error CompanyNotFound =
        Error.NotFound("ORGANIZATION.COMPANY_NOT_FOUND", "La empresa no existe o el asistente inicial no se ha completado.");

    public static readonly Error CompanyAlreadyExists =
        Error.Conflict("ORGANIZATION.COMPANY_ALREADY_EXISTS", "Ya existe una empresa con esa identificación.");

    public static readonly Error BranchNotFound = Error.NotFound("ORGANIZATION.BRANCH_NOT_FOUND", "La sucursal no existe.");

    public static readonly Error BranchCodeDuplicated =
        Error.Conflict("ORGANIZATION.BRANCH_CODE_DUPLICATED", "Ya existe una sucursal con ese código en la empresa.");

    public static readonly Error BranchHasActiveTerminals =
        Error.BusinessRule("ORGANIZATION.BRANCH_HAS_ACTIVE_TERMINALS", "No se puede inactivar una sucursal con cajas activas.");

    public static readonly Error LastActiveBranch =
        Error.BusinessRule("ORGANIZATION.LAST_ACTIVE_BRANCH", "No se puede inactivar la última sucursal activa de la empresa.");

    public static readonly Error HomeBranchCannotBeDeactivated =
        Error.BusinessRule("ORGANIZATION.HOME_BRANCH", "No se puede inactivar la sucursal a la que pertenece esta instalación.");

    public static readonly Error WarehouseNotFound = Error.NotFound("ORGANIZATION.WAREHOUSE_NOT_FOUND", "La bodega no existe.");

    public static readonly Error WarehouseCodeDuplicated =
        Error.Conflict("ORGANIZATION.WAREHOUSE_CODE_DUPLICATED", "Ya existe una bodega con ese código en la sucursal.");

    public static readonly Error InTransitWarehouseDuplicated =
        Error.Conflict("ORGANIZATION.IN_TRANSIT_WAREHOUSE_DUPLICATED", "La sucursal ya tiene una bodega de tránsito.");

    public static readonly Error WarehouseKindCannotSell =
        Error.BusinessRule("ORGANIZATION.WAREHOUSE_KIND_CANNOT_SELL", "Las bodegas de averías y de tránsito no admiten ventas.");

    public static readonly Error WarehouseInUse =
        Error.BusinessRule("ORGANIZATION.WAREHOUSE_IN_USE", "La bodega es la bodega por defecto de la sucursal o está asignada a una caja activa.");

    public static readonly Error SystemWarehouseCannotBeRemoved =
        Error.BusinessRule("ORGANIZATION.SYSTEM_WAREHOUSE", "Las bodegas de averías y de tránsito de la sucursal no se pueden inactivar.");

    public static readonly Error TerminalNotFound = Error.NotFound("ORGANIZATION.TERMINAL_NOT_FOUND", "La caja no existe.");

    public static readonly Error TerminalCodeDuplicated =
        Error.Conflict("ORGANIZATION.TERMINAL_CODE_DUPLICATED", "Ya existe una caja con ese código en la sucursal.");

    public static readonly Error TerminalWarehouseMustSell =
        Error.BusinessRule("ORGANIZATION.TERMINAL_WAREHOUSE_MUST_SELL", "La bodega de la caja debe pertenecer a la sucursal y admitir ventas.");

    public static readonly Error SingleTerminalEdition = Error.Conflict(
        "LICENSE.EDITION_SINGLE_TERMINAL",
        "La edición Caja Única admite una sola caja. Instale la edición Multicaja para agregar más cajas y equipos.");

    public static readonly Error SetupAlreadyCompleted =
        Error.Conflict("SETUP.ALREADY_COMPLETED", "La configuración inicial ya se completó.");

    public static readonly Error SetupModeNotAvailable = Error.BusinessRule(
        "SETUP.MODE_NOT_AVAILABLE", "Agregar una tienda a una empresa existente estará disponible con la sincronización en la nube.");

    public static readonly Error SetupRequired =
        Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
}
