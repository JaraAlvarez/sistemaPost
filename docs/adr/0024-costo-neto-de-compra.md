# ADR-0024 · Costo neto de entrada de las compras

- **Estado:** Aceptada · 2026-09-28 · Fase 5 · Decisión D5-03 (pregunta 2)

## Contexto
El costo con que la compra entra al kardex determina el costo promedio y, con él, la utilidad. La factura trae
presentaciones (cajas), descuentos, fletes y distintos impuestos; el IVA es descontable solo para responsables de IVA.

## Decisión
- **Costo neto por unidad base** = (cantidad × costo − descuento + cargos prorrateados + impuestos no descontables) ÷
  cantidad base. Importes a 2 decimales y costos a 4 (`RoundingPolicy`).
- El IVA es descontable por defecto (`purchasing.vat_deductible` = true); si la empresa no es responsable de IVA, el IVA es
  costo. Los demás impuestos (INC, bolsa, saludables) siempre son costo.
- Los cargos (fletes) se prorratean **por valor neto** (por defecto), por cantidad o a mano; el residuo del redondeo va a la
  línea de mayor peso para que la suma cuadre exacta.
- Antes de contabilizar, el total calculado debe coincidir con el total de la factura (`purchasing.invoice_total_tolerance`,
  $0). Las retenciones (digitadas en v1) reducen lo que se paga, no el costo.
- Al contabilizar se alerta si el precio de venta sin impuestos queda bajo el nuevo costo o si el costo varió más de
  `purchasing.cost_variation_alert_percent` (20 %) frente a la última compra al proveedor.

## Consecuencias
- ✅ Utilidad real por producto; la compra cuadra con la factura del proveedor.
- ⚠️ El prorrateo por valor puede no reflejar el peso físico del flete: se puede elegir cantidad o manual por compra.
