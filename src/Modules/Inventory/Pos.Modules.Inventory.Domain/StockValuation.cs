using Pos.SharedKernel;
using Pos.SharedKernel.Finance;

namespace Pos.Modules.Inventory.Domain;

/// <summary>Saldo de un producto en una bodega: cantidad (unidad base), valor total y costo promedio.</summary>
public readonly record struct StockState(decimal Quantity, decimal Value, decimal AverageCost)
{
    public static StockState Empty => new(0m, 0m, 0m);
}

/// <summary>Resultado de valorizar un movimiento: saldo después, costo unitario aplicado y costo total del movimiento.</summary>
public readonly record struct Valuation(StockState After, decimal UnitCost, decimal TotalCost);

/// <summary>
/// Costo promedio ponderado por bodega llevando el VALOR del saldo (D4-02, doc 07). El promedio es valor ÷ cantidad, así
/// no se acumulan errores de redondeo; una salida que deja el saldo en cero lleva exactamente el valor restante.
/// Reglas:
/// <list type="bullet">
/// <item>Entrada valorizada (saldo inicial, compra, traslado de entrada, devoluciones): valor += cantidad × costo; si el
/// saldo previo era ≤ 0, el promedio pasa a ser el costo de la entrada.</item>
/// <item>Entrada no valorizada (ajuste o conteo de entrada): entra al costo promedio vigente; el promedio no cambia.</item>
/// <item>Salida: al costo promedio vigente; el promedio no cambia.</item>
/// </list>
/// Todos los importes se redondean a 4 decimales (RoundingPolicy.UnitCostDecimals).
/// </summary>
public static class StockValuation
{
    public static Valuation Inflow(StockState before, decimal quantity, decimal? unitCost, bool affectsAverage)
    {
        EnsureQuantity(quantity);
        var newQuantity = before.Quantity + quantity;
        if (!affectsAverage || unitCost is null)
        {
            var cost = before.AverageCost;
            var total = Round(quantity * cost);
            return new Valuation(new StockState(newQuantity, before.Value + total, cost), cost, total);
        }

        var entryCost = unitCost.Value;
        if (entryCost < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(unitCost), "El costo no puede ser negativo.");
        }

        var totalCost = Round(quantity * entryCost);
        if (before.Quantity <= 0m)
        {
            // Sin existencias (o negativas): el promedio es el costo de la entrada y el valor se recalcula con él.
            return new Valuation(new StockState(newQuantity, Round(newQuantity * entryCost), Round(entryCost)), entryCost, totalCost);
        }

        var value = before.Value + totalCost;
        return new Valuation(new StockState(newQuantity, value, Round(value / newQuantity)), entryCost, totalCost);
    }

    public static Valuation Outflow(StockState before, decimal quantity)
    {
        EnsureQuantity(quantity);
        var newQuantity = before.Quantity - quantity;
        var cost = before.AverageCost;
        if (newQuantity == 0m)
        {
            // El último que sale se lleva exactamente el valor restante: el saldo queda en cero sin residuos.
            var remaining = Math.Max(0m, before.Value);
            return new Valuation(new StockState(0m, before.Value - remaining, cost), cost, remaining);
        }

        var total = Round(quantity * cost);
        return new Valuation(new StockState(newQuantity, before.Value - total, cost), cost, total);
    }

    /// <summary>
    /// Salida valorizada a un costo dado (devolución a proveedor al costo de la compra, reversión de una entrada, D5-07):
    /// el valor baja en cantidad × costo y el promedio se recalcula. Si la salida deja el saldo en cero se lleva el valor
    /// restante; si el valor quedara negativo, el movimiento se lleva solo el valor disponible (el promedio queda en cero).
    /// Con saldo resultante negativo se comporta como una salida al promedio.
    /// </summary>
    public static Valuation ValuedOutflow(StockState before, decimal quantity, decimal unitCost)
    {
        EnsureQuantity(quantity);
        if (unitCost < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(unitCost), "El costo no puede ser negativo.");
        }

        var newQuantity = before.Quantity - quantity;
        if (newQuantity < 0m)
        {
            return Outflow(before, quantity);
        }

        if (newQuantity == 0m)
        {
            var remaining = Math.Max(0m, before.Value);
            return new Valuation(new StockState(0m, before.Value - remaining, before.AverageCost), Round(remaining / quantity), remaining);
        }

        var total = Math.Min(Round(quantity * unitCost), Math.Max(0m, before.Value));
        var value = before.Value - total;
        return new Valuation(new StockState(newQuantity, value, Round(value / newQuantity)), Round(total / quantity), total);
    }

    private static decimal Round(decimal value) => decimal.Round(value, RoundingPolicy.UnitCostDecimals, MidpointRounding.AwayFromZero);

    private static void EnsureQuantity(decimal quantity)
    {
        if (quantity <= 0m || !Guard.HasAtMostDecimals(quantity, RoundingPolicy.QuantityDecimals))
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "La cantidad del movimiento es positiva y tiene hasta 4 decimales.");
        }
    }
}
