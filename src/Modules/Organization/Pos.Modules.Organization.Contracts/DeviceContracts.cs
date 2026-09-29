using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Organization.Contracts;

/// <summary>Equipo emparejado y autenticado por su credencial (D3-04).</summary>
public sealed record AuthenticatedDevice(Guid DeviceId, Guid CompanyId, DeviceKind Kind, Guid? PosTerminalId, Guid? BranchId);

/// <summary>Valida la credencial de un equipo (cabeceras <c>X-Device-Id</c> y <c>X-Device-Secret</c>).</summary>
public interface IDeviceAuthenticator
{
    Task<AuthenticatedDevice?> AuthenticateAsync(Guid deviceId, string secret, CancellationToken cancellationToken = default);
}

/// <summary>Datos de una caja para abrir una sesión en ella.</summary>
public sealed record TerminalInfo(Guid Id, Guid CompanyId, Guid BranchId, string Code, bool IsActive);

/// <summary>Consultas de cajas para otros módulos.</summary>
public interface ITerminalDirectory
{
    Task<TerminalInfo?> GetAsync(Guid posTerminalId, CancellationToken cancellationToken = default);

    /// <summary>La única caja activa (edición Caja Única), o <c>null</c> si hay cero o más de una.</summary>
    Task<TerminalInfo?> GetSingleActiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Bodega vista por otros módulos (inventario). <c>Kind</c>: SALES_FLOOR, STORAGE, DAMAGED o IN_TRANSIT.</summary>
public sealed record WarehouseInfo(Guid Id, Guid CompanyId, Guid BranchId, string Code, string Name, string Kind, bool AllowsSales, bool IsActive);

/// <summary>Consultas de bodegas y del nodo local para otros módulos.</summary>
public interface IWarehouseDirectory
{
    Task<WarehouseInfo?> GetAsync(Guid warehouseId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WarehouseInfo>> ListByBranchAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>Sucursal activa o inactiva (no borrada) con ese código.</summary>
    Task<Guid?> FindBranchIdByCodeAsync(string branchCode, CancellationToken cancellationToken = default);

    /// <summary>Número corto (1–999) de este nodo dentro de la empresa; forma parte de los códigos internos.</summary>
    Task<short> GetLocalNodeNumberAsync(CancellationToken cancellationToken = default);
}

public sealed record PairingCodeDto(string Code, DateTimeOffset ExpiresAt, string DeviceKind, Guid? PosTerminalId, string? CertificateFingerprint);

public sealed record PairedDeviceDto(Guid DeviceId, string DeviceSecret, string DeviceKind, Guid? PosTerminalId, string? CertificateFingerprint);

public sealed record DeviceDto(
    Guid Id, string Kind, string Hostname, Guid? PosTerminalId, string Status, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt,
    string? AppVersion);
