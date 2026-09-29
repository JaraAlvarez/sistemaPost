namespace Pos.Modules.Organization.Contracts;

// Contrato HTTP del módulo. Los enumerados viajan como texto (SalesFloor, Active…).

public sealed record CompanyDto(
    Guid Id,
    string LegalName,
    string TradeName,
    string PersonType,
    string IdentificationType,
    string IdentificationNumber,
    string? CheckDigit,
    string TaxRegime,
    IReadOnlyList<string> FiscalResponsibilities,
    string CountryCode,
    string MunicipalityCode,
    string Address,
    string? Phone,
    string? Email,
    string CurrencyCode,
    string Timezone,
    string Status);

public sealed record BranchDto(
    Guid Id,
    string Code,
    string Name,
    string MunicipalityCode,
    string Address,
    string? Phone,
    Guid? DefaultWarehouseId,
    string Status,
    bool IsHomeBranch);

public sealed record WarehouseDto(Guid Id, Guid BranchId, string Code, string Name, string Kind, bool AllowsSales, string Status);

public sealed record TerminalDto(Guid Id, Guid BranchId, string Code, string Name, Guid WarehouseId, Guid? DeviceId, string Status);

/// <summary>
/// Impresora de tiquetes de la caja (Fase 7). <c>Connection</c>: FILE, NETWORK, WINDOWS_SPOOLER o SERIAL; <c>CodePage</c>: PC850 o
/// ASCII; <c>DrawerPin</c>: PIN2 o PIN5. <c>Configured</c> = false: valores por defecto (no se ha configurado).
/// </summary>
public sealed record ReceiptPrinterDto(
    Guid PosTerminalId, bool Configured, string Connection, string? Address, int PaperWidthMm, string CodePage, bool AutoCut, bool DrawerConnected, string DrawerPin);

/// <summary>Sucursal con sus bodegas y cajas.</summary>
public sealed record BranchDetailDto(BranchDto Branch, IReadOnlyList<WarehouseDto> Warehouses, IReadOnlyList<TerminalDto> Terminals);

/// <summary>Estado del asistente inicial y edición instalada.</summary>
public sealed record SetupStatusDto(bool IsCompleted, string Edition, Guid NodeId, Guid? CompanyId, Guid? BranchId, bool OwnerPending);

/// <summary>Resultado del asistente inicial.</summary>
public sealed record SetupResultDto(
    Guid CompanyId,
    Guid BranchId,
    Guid PosTerminalId,
    Guid SalesFloorWarehouseId,
    Guid NodeId,
    Guid SystemUserId,
    Guid OwnerUserId,
    string Edition);
