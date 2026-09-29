# ADR-0030 · Venta persistida en el servidor y motor de cálculo puro

- **Estado:** Aceptada · 2026-09-29 · Fase 7 · Decisiones D7-01, D7-02, D7-03, D7-04 y D7-07

## Contexto
En un supermercado se va la luz, se reinicia un equipo o se cae la red en medio de una compra. Si el carrito vive solo en la
interfaz se pierde la venta y las líneas eliminadas no dejan rastro (fraude típico). Además, el precio de la góndola incluye
impuestos: si el sistema calcula el IVA "encima" aparecen diferencias de centavos con lo exhibido.

## Decisión
- **La venta en curso vive en el servidor** (`sales.sales` en `OPEN`): cada escaneo, cambio de cantidad, descuento o cliente es un
  comando que recalcula la venta. Una venta `OPEN` por caja (índice único parcial); suspender (`ON_HOLD`) y recuperar son cambios de
  estado en la misma caja; las líneas eliminadas quedan `VOIDED` con usuario y hora. Estados: `OPEN`, `ON_HOLD`, `COMPLETED`,
  `CANCELLED`, `VOIDED`.
- **Motor puro y determinista** (`SaleCalculator`, dominio de ventas, sin E/S): bruto → promoción (ADR-0034) → descuento de línea →
  descuento global prorrateado por valor (residuo en la línea mayor) → impuestos. Con precio que incluye impuestos, **el total de la
  línea es exactamente el neto** y la base absorbe el redondeo; impuestos porcentuales sobre la base y fijos por unidad base. El
  impuesto fijo (p. ej. la bolsa) nunca supera lo cobrado, para que la base gravable nunca sea negativa. El mismo motor lo usan la
  caja, el simulador de promociones y, en el futuro, la caja autónoma.
- **Número interno al completar** (serie `SALE` por caja, sin huecos, ADR-0013); cancelar no consume número.
- **Completar es atómico e idempotente**: `POST /sales/{id}/complete` con `Idempotency-Key`; en una transacción: pagos (ADR-0032),
  número, kardex FEFO con el costo de cada línea, movimientos de caja por medio, comprobante (ADR-0035), outbox
  (`sales.sale_completed.v1`) y auditoría. Repetir con la misma clave devuelve la misma venta; con otra, `SALES.ALREADY_COMPLETED`.
- **Snapshot completo por línea**: SKU, nombre, código leído, origen (barras, SKU, báscula por peso o por precio), presentación y
  factor, precio, promoción aplicada, descuentos, impuestos por línea (`sale_line_taxes`) y costo del kardex.
- La fecha de negocio de la venta es la de su jornada (ADR-0028). El cierre de caja consulta `IOpenSalesProbe` (contrato de Cash
  implementado por Sales): con ventas abiertas o suspendidas responde `CASH.OPEN_SALES`.

## Consecuencias
- ✅ Una venta sobrevive a cualquier corte; nada desaparece sin rastro; el cliente paga exactamente el precio exhibido.
- ✅ Reintentar un cobro por un corte de red nunca duplica la venta.
- ⚠️ Una petición LAN por escaneo (meta p95 < 50 ms con recálculo) y la base gravable lleva el residuo del redondeo.
