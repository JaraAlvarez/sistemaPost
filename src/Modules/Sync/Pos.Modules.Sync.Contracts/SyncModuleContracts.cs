using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Sync.Contracts;

/// <summary>Permisos de la sincronización (docs/fases/fase-16-propuesta.md).</summary>
public static class SyncPermissions
{
    public const string View = "sync.sync.view";
    public const string Manage = "sync.sync.manage";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(View, "Ver el estado de la sincronización con la nube", isSensitive: false),
        new(Manage, "Sincronizar ahora y exportar el paquete de sincronización (.possync)", isSensitive: true),
    ];
}

/// <summary>Un lote enviado o exportado.</summary>
public sealed record SyncBatchDto(Guid Id, DateTimeOffset CreatedAt, string Via, int Items, string Status, int? Applied, int? Ignored, string? Error, DateTimeOffset? AckedAt);

/// <summary>Estado de la sincronización: datos pendientes por tipo, último acuse y los lotes recientes.</summary>
public sealed record SyncStatusDto(
    bool Configured,
    string? NotConfiguredReason,
    DateTimeOffset? LastAckAt,
    string? LastError,
    IReadOnlyDictionary<string, int> Pending,
    IReadOnlyList<SyncBatchDto> Batches);
