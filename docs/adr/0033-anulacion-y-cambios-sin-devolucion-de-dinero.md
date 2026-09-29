# ADR-0033 · Anulación con la jornada abierta y cambios de mercancía sin devolución de dinero

- **Estado:** Aceptada · 2026-09-29 · Fase 7 · Decisiones D7-10 y D7-11 (preguntas 6 y 9 del propietario; resoluciones §15.2 y §15.3)

## Contexto
El propietario no devuelve dinero: solo cambia el producto por otro de igual o mayor valor. El reporte Z impreso y sellado
(ADR-0029) no puede cambiar después del cierre; anular ventas en efectivo de días anteriores sería una puerta al fraude. La Ley 1480
de 2011 puede obligar a devolver el dinero de un producto defectuoso.

## Decisión
- **Anular** (`POST /sales/{id}/void`, `sales.sale.void`, admite supervisor, con motivo) deshace una venta hecha **por error**: kardex
  `REVERSAL` al costo y lote originales, caja `SALE_VOID` por medio, comprobante anulado y tiquete de anulación. **Solo mientras la
  jornada de la venta siga abierta**; una venta pagada con crédito de cambio, o que ya tuvo cambios, no se anula (`SALES.VOID_NOT_ALLOWED`).
- **Cambio de mercancía** (`POST /exchanges`, `sales.exchange.create`, admite supervisor): líneas y cantidades ≤ vendido − cambiado,
  plazo `sales.exchange_days` (30), motivo y destino por línea (`ReturnToStock`, `SendToDamaged`, `Discard`). El **crédito** es lo que
  el cliente pagó por esas unidades (promoción y descuentos prorrateados) y abre una **venta nueva** que debe sumar igual o más
  (`SALES.EXCHANGE_BELOW_CREDIT`); la diferencia se paga con cualquier medio y el crédito con el medio `CAMBIO`, que no entra al cajón.
  Al cobrar, en una transacción: número `CUSTOMER_RETURN` de la caja, entrada al kardex al costo con que salió (a la venta o a
  averías), venta nueva completa y `return_status` de la original (`PARTIAL` / `FULL`). Cancelar la venta nueva cancela el cambio.
  Un solo cambio en borrador por venta (índice único); FK cruzadas venta nueva ↔ cambio `DEFERRABLE INITIALLY DEFERRED`.
- **Excepción de garantía** (`POST /exchanges/warranty-refund`, permiso `sales.refund.warranty`): **solo el rol OWNER**, sin
  autorización de supervisor, en efectivo desde su jornada abierta (movimiento `CUSTOMER_REFUND`), con motivo y auditoría crítica.

## Consecuencias
- ✅ Un Z impreso nunca deja de cuadrar; el cajón no se afecta por los cambios y no quedan saldos a favor pendientes.
- ✅ La excepción legal existe, pero solo el propietario la usa y queda registrada.
- ⚠️ Después del cierre, el único camino para corregir una venta es el cambio de mercancía.
