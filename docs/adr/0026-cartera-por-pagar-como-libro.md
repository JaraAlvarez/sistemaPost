# ADR-0026 · Cuentas por pagar como libro de asientos

- **Estado:** Aceptada · 2026-09-28 · Fase 5 · Decisiones D5-08 y D5-09

## Contexto
El saldo de un proveedor debe poder explicarse y conciliarse contra su estado de cuenta: pagos parciales, pagos que cubren
varias facturas, devoluciones, notas crédito y anulaciones.

## Decisión
- Cada compra contabilizada crea su cuenta por pagar con un asiento `CHARGE`. El saldo cambia **solo** con asientos en
  `purchasing.payable_entries` (solo inserción: privilegios + disparador): `PAYMENT`, `PAYMENT_VOID`, `RETURN`, `REFUND`,
  `REPLACEMENT`, `VOID`. El signo lo fija el tipo (CHECK). Saldo negativo = saldo a favor de la empresa.
- Un pago no supera el saldo de ninguna cuenta; se anula con asientos inversos, nunca se borra.
- Anular una compra = movimientos inversos del kardex (`REVERSAL`, al costo original, un movimiento se revierte una sola
  vez) + asiento `VOID`; solo si no tiene pagos ni devoluciones y si revertir no deja existencias negativas (RN-PUR-05).
- La devolución a proveedor sale del kardex al costo de la compra y del lote original (D5-07) y descuenta de la cuenta la
  proporción del total de la línea con impuestos (el valor de la nota crédito esperada).

## Consecuencias
- ✅ Todo saldo tiene su historia; cartera por edades y estado de cuenta salen del libro.
- ⚠️ Más filas que un saldo editable; es el precio de la trazabilidad.
