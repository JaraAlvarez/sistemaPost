using Pos.Client.Services;

namespace Pos.Client.Pages.Caja;

/// <summary>Pago en la pantalla de cobro: si es efectivo (da cambio) y el valor entregado.</summary>
internal readonly record struct PagoCobro(bool Efectivo, decimal Valor);

/// <summary>
/// Lo que muestra la pantalla de cobro y si el servidor aceptará el cobro. Refleja las reglas de <c>PaymentAllocator</c> (D7-08, D7-09):
/// primero los medios sin cambio (no pueden superar lo que falta), el efectivo cubre el resto redondeado al múltiplo configurado y da el
/// cambio; un solo pago en efectivo; si no queda nada por pagar, el efectivo sobra.
/// <para>
/// Todo en efectivo: se usa <see cref="Sale.CashAmountDue"/> tal como lo calculó el servidor. Con medios mixtos el redondeo aplica
/// solo a la parte en efectivo; se calcula con el múltiplo que envía el servidor (<see cref="Sale.CashRoundingIncrement"/>) y su misma
/// regla (al múltiplo más cercano, punto medio hacia arriba; <c>RoundingPolicy.RoundToCash</c>).
/// </para>
/// </summary>
/// <param name="EnEfectivo">El resto se cubre en efectivo (hay un pago en efectivo o el medio elegido es efectivo).</param>
/// <param name="TotalAPagar">Lo que se cobra: con efectivo, lo pagado con otros medios más el efectivo redondeado.</param>
/// <param name="Redondeo">Ajuste de redondeo del efectivo (positivo o negativo) respecto de <c>AmountDue</c>.</param>
/// <param name="Recibido">Suma de lo entregado.</param>
/// <param name="Diferencia">Positivo: lo que falta; negativo: el cambio.</param>
/// <param name="Completo">El servidor aceptará el cobro con estos pagos.</param>
/// <param name="Bloqueo">Por qué no se puede cobrar aunque se haya entregado suficiente (el servidor lo rechazaría).</param>
/// <param name="EfectivoEnCero">
/// El resto es menor que medio múltiplo (p. ej. $20 con múltiplo $50): el servidor lo absorbe con un pago en efectivo de $0, que la
/// pantalla agrega al cobrar.
/// </param>
/// <param name="TodoEnEfectivo">Se paga todo en efectivo (sin otros medios): la pantalla lo muestra como "Total en efectivo".</param>
internal sealed record EstadoCobro(
    bool EnEfectivo, decimal TotalAPagar, decimal Redondeo, decimal Recibido, decimal Diferencia, bool Completo, string? Bloqueo, bool EfectivoEnCero,
    bool TodoEnEfectivo = false)
{
    public static EstadoCobro Calcular(Sale sale, IReadOnlyCollection<PagoCobro> pagos, bool medioElegidoEsEfectivo, bool hayMedioEfectivo)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(pagos);
        var otros = pagos.Where(p => !p.Efectivo).Sum(p => p.Valor);
        var pagosEfectivo = pagos.Count(p => p.Efectivo);
        var efectivo = pagos.Where(p => p.Efectivo).Sum(p => p.Valor);
        var restante = sale.AmountDue - otros;
        var recibido = otros + efectivo;

        if (otros > sale.AmountDue)
        {
            return new EstadoCobro(false, sale.AmountDue, 0, recibido, restante, false,
                "La tarjeta o la transferencia no pueden superar lo que falta por pagar (no dan cambio).", false);
        }

        if (pagosEfectivo > 1)
        {
            return new EstadoCobro(true, sale.AmountDue, 0, recibido, restante - efectivo, false, "Solo se admite un pago en efectivo.", false);
        }

        if (restante == 0)
        {
            return pagosEfectivo > 0
                ? new EstadoCobro(false, sale.AmountDue, 0, recibido, -efectivo, false, "Ya está todo pagado con otros medios: quite el pago en efectivo.", false)
                : new EstadoCobro(false, sale.AmountDue, 0, recibido, 0, true, null, false);
        }

        var enEfectivo = pagosEfectivo > 0 || medioElegidoEsEfectivo;
        if (!enEfectivo)
        {
            // Sin efectivo el cobro es exacto; un resto que el redondeo absorbe se cubre con un efectivo de $0.
            var absorbe = hayMedioEfectivo && EfectivoPorCobrar(sale, otros, restante) == 0;
            return absorbe
                ? new EstadoCobro(false, otros, -restante, recibido, 0, true, null, true)
                : new EstadoCobro(false, sale.AmountDue, 0, recibido, restante, false, null, false);
        }

        var porEfectivo = EfectivoPorCobrar(sale, otros, restante);
        var diferencia = porEfectivo - efectivo;
        var completo = diferencia <= 0 && (pagosEfectivo > 0 || hayMedioEfectivo);
        return new EstadoCobro(true, otros + porEfectivo, porEfectivo - restante, recibido, diferencia, completo, null, completo && pagosEfectivo == 0,
            otros == 0);
    }

    /// <summary>
    /// Efectivo que se cobra por <paramref name="restante"/>. Todo en efectivo: el valor del servidor. Mixto: el mismo redondeo del
    /// servidor sobre la parte en efectivo.
    /// </summary>
    private static decimal EfectivoPorCobrar(Sale sale, decimal otros, decimal restante) =>
        otros == 0 ? sale.CashAmountDue
        : sale.CashRoundingIncrement > 0 ? decimal.Round(restante / sale.CashRoundingIncrement, 0, MidpointRounding.AwayFromZero) * sale.CashRoundingIncrement
        : restante;
}
