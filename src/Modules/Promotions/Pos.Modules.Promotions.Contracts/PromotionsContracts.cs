using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Promotions.Contracts;

/// <summary>Permisos del módulo Promotions (docs/fases/fase-07-propuesta.md §7): solo el encargado administra promociones.</summary>
public static class PromotionsPermissions
{
    public const string PromotionManage = "promotions.promotion.manage";
    public const string PromotionView = "promotions.promotion.view";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(PromotionManage, "Crear, simular, activar, pausar y terminar promociones", isSensitive: true),
        new(PromotionView, "Consultar promociones y su reporte de descuentos", isSensitive: false),
    ];
}

/// <summary>
/// A qué aplica una promoción ya resuelta: producto (y presentación), conjunto de categorías (la elegida y sus subcategorías)
/// o marca. <c>Quantity</c>: cantidad del componente en un combo.
/// </summary>
public sealed record PromotionTargetDefinition(Guid? ProductId, Guid? PackagingId, IReadOnlySet<Guid>? CategoryIds, Guid? BrandId, decimal Quantity);

/// <summary>
/// Promoción que rige, lista para el motor de la venta. <c>Kind</c>: MULTI_BUY, SPECIAL_PRICE, PERCENT_OFF,
/// QUANTITY_PRICE o COMBO.
/// </summary>
public sealed record PromotionDefinition(
    Guid Id,
    string Name,
    string Kind,
    IReadOnlyList<PromotionTargetDefinition> Targets,
    int? BuyQuantity,
    int? PayQuantity,
    decimal? Price,
    decimal? Percent,
    decimal? MinQuantity,
    int? MaxApplications,
    string? TicketText);

/// <summary>Promociones vigentes para la venta (las consulta el módulo de ventas en cada recálculo).</summary>
public interface IActivePromotions
{
    /// <summary>Promociones activas que rigen en la sucursal en ese instante (vigencia, días y horario de Colombia).</summary>
    Task<IReadOnlyList<PromotionDefinition>> GetActiveAsync(Guid branchId, DateTimeOffset at, CancellationToken cancellationToken = default);
}

public sealed record PromotionItemDto(Guid Id, Guid? ProductId, string? ProductName, Guid? PackagingId, Guid? CategoryId, string? CategoryName, Guid? BrandId, string? BrandName,
    decimal Quantity);

public sealed record PromotionDto(
    Guid Id,
    string Number,
    string Name,
    string Type,
    string Status,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    IReadOnlyList<string> Days,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    bool AllBranches,
    IReadOnlyList<Guid> BranchIds,
    int? MaxApplications,
    string? TicketText,
    int? BuyQuantity,
    int? PayQuantity,
    decimal? Price,
    decimal? Percent,
    decimal? MinQuantity,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<PromotionItemDto> Items);

public sealed record PromotionSummaryDto(Guid Id, string Number, string Name, string Type, string Status, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, bool InEffectNow);

/// <summary>Reporte por promoción: ventas completadas (no anuladas) en que se aplicó, unidades y descuento total.</summary>
public sealed record PromotionReportRowDto(Guid PromotionId, string Name, int Sales, decimal Quantity, decimal Discount, decimal NetSales);
