using Pos.Application.Abstractions.Security;

namespace Pos.Modules.Promotions.Contracts;

/// <summary>Permisos del módulo Promotions (docs/fases/fase-07-propuesta.md §7).</summary>
public static class PromotionsPermissions
{
    public const string PromotionManage = "promotions.promotion.manage";
    public const string PromotionView = "promotions.promotion.view";

    public static IEnumerable<PermissionDefinition> All =>
    [
        new(PromotionManage, "Crear, simular, activar, pausar y terminar promociones", isSensitive: true),
        new(PromotionView, "Consultar promociones y el reporte de descuentos por promoción", isSensitive: false),
    ];
}

/// <summary>
/// Línea de una venta que se evalúa contra las promociones vigentes (propuesta §5.5).
/// </summary>
/// <param name="LineId">Id de la línea de la venta (se devuelve en el ajuste).</param>
/// <param name="ProductId">Producto.</param>
/// <param name="PackagingId">Presentación vendida (<c>null</c> = unidad base).</param>
/// <param name="BrandId">Marca del producto, si tiene.</param>
/// <param name="CategoryIds">Categoría del producto y TODAS sus categorías ascendentes (una promoción de "Lácteos" aplica a "Lácteos / Leches").</param>
/// <param name="Quantity">Cantidad en unidades de la presentación, o en kilos/litros si <paramref name="IsWeighted"/>.</param>
/// <param name="UnitPrice">Precio de lista por unidad de venta tal como se exhibe (con impuestos si la lista los incluye), antes de promociones y descuentos manuales.</param>
/// <param name="IsWeighted">Producto vendido por peso o volumen (cantidad decimal).</param>
public sealed record PromotionLineInput(
    Guid LineId,
    Guid ProductId,
    Guid? PackagingId,
    Guid? BrandId,
    IReadOnlyList<Guid> CategoryIds,
    decimal Quantity,
    decimal UnitPrice,
    bool IsWeighted);

/// <summary>
/// Descuento que una promoción concede sobre una línea. Una línea puede recibir varios ajustes (unidades distintas en
/// promociones distintas), pero <b>cada unidad participa en una sola promoción</b>: la más favorable para el cliente,
/// sin acumular (D7-16). <c>Discount</c> en pesos, redondeado a 2 decimales, mayor que cero y nunca mayor que el bruto de
/// la línea (cantidad × precio unitario) menos los demás ajustes de la misma línea.
/// </summary>
/// <param name="LineId">Línea de la venta.</param>
/// <param name="PromotionId">Promoción aplicada.</param>
/// <param name="PromotionNumber">Número interno de la promoción (snapshot).</param>
/// <param name="PromotionName">Nombre de la promoción (snapshot).</param>
/// <param name="TicketText">Texto corto para el tiquete, p. ej. "Lleve 3 pague 2".</param>
/// <param name="Kind">BUY_X_PAY_Y, SPECIAL_PRICE, PERCENT_OFF, QUANTITY_PRICE o COMBO.</param>
/// <param name="Units">Unidades de la línea cubiertas por esta promoción (en la unidad de <see cref="PromotionLineInput.Quantity"/>).</param>
/// <param name="Discount">Descuento en pesos.</param>
public sealed record PromotionAdjustment(
    Guid LineId,
    Guid PromotionId,
    string PromotionNumber,
    string PromotionName,
    string TicketText,
    string Kind,
    decimal Units,
    decimal Discount);

/// <summary>
/// Evaluación de promociones que usa el módulo Sales cada vez que recalcula una venta. Considera solo las promociones
/// ACTIVAS de la sucursal que estén vigentes en <c>at</c> (fechas, días de la semana y horario en la zona del negocio) y
/// respeta el límite por venta. Es determinista: las mismas líneas y el mismo instante producen los mismos ajustes.
/// </summary>
public interface IPromotionPricing
{
    Task<IReadOnlyList<PromotionAdjustment>> EvaluateAsync(
        Guid branchId, DateTimeOffset at, IReadOnlyList<PromotionLineInput> lines, CancellationToken cancellationToken = default);
}
