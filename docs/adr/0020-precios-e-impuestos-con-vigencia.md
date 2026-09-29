# ADR-0020 · Precios e impuestos con vigencia (sin sobrescribir)

- **Estado:** Aceptada · 2026-09-28 · Fase 4 · Decisiones D4-05 y D4-08

## Contexto
RN-CAT-05 exige historial de precios; los supermercados programan cambios ("desde el lunes"); dos lugares (tienda y portal)
pueden cambiar un precio sin conexión entre ellos. Las tarifas de impuestos cambian por ley en una fecha.

## Decisión
- `catalog.product_prices`: cada precio es un registro con vigencia `[desde, hasta)` por lista × producto × presentación
  (opcional) × sucursal (opcional). Una **restricción de exclusión** (`btree_gist`) impide solapamientos entre filas no borradas.
- Fijar un precio cierra el vigente en la fecha nueva (o reemplaza uno programado para esa misma fecha); si hay uno programado
  después, el nuevo termina donde empieza ese. Cancelar un programado devuelve la vigencia al anterior.
- Precio vigente en caja: el de la sucursal si existe; si no, el general de la lista predeterminada. Los precios incluyen IVA
  por defecto (D9).
- Precio bajo el costo (RN-CAT-06): advertencia por defecto; bloqueo si `catalog.price_below_cost = BLOCK`.
- Impuestos como **datos maestros** (no configuración) con tarifas en `catalog.tax_rates` por fecha; exento ≠ excluido.
  Se siembran IVA 19 %, 5 %, exento y excluido; INC bolsa, IBUA e ICUI quedan **inactivos** hasta que cada empresa cargue sus
  valores con su contador. Un producto puede tener un valor fijo propio (bebidas azucaradas).

## Consecuencias
- ✅ Historial completo y consultable; cambios programados; dos cambios offline nunca se pisan (cada uno es un registro).
- ✅ Una venta de ayer se puede recalcular con la tarifa de ayer.
- ⚠️ Más filas; la consulta del precio vigente usa un índice por producto, lista y fecha (escaneo p95 < 20 ms, medido).
