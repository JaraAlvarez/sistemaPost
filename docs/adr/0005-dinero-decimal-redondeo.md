# ADR-0005 · Dinero con `decimal` y política de redondeo única

- **Estado:** Aceptada · 2026-09-28

## Contexto
Los errores de centavos, causados por `float/double` y por redondeos dispersos en el código, son una de las fallas más comunes de los POS (doc 01, problema 3).

## Decisión
- Importes: tipo `Money` (`decimal` + `Currency`). Cantidades: `Quantity` (hasta 4 decimales). Tasas: `Percentage` (base 100).
- Las operaciones conservan la precisión completa; **el redondeo siempre es explícito** y pasa por `RoundingPolicy`:
  - dinero: 2 decimales (COP según ISO 4217 y documentos DIAN), punto medio **lejos de cero**;
  - efectivo: múltiplo configurable (Colombia: $50);
  - cantidades y costos unitarios: 4 decimales.
- `Money.Allocate` reparte importes sin perder ni crear centavos (método del mayor residuo), por ejemplo al prorratear un descuento global entre líneas.
- Regla de arquitectura R6: prohibido `double/float` en la superficie pública de `Domain`, `Contracts` y `SharedKernel`.
- En BD: `numeric(19,4)` para dinero y `numeric(18,4)` para cantidades.

## Consecuencias
- ✅ Totales, impuestos y caja cuadran al centavo; comportamiento idéntico en todas las cajas.
- ⚠️ La política es configurable por empresa desde la Fase 2 (settings); hasta entonces se usa `RoundingPolicy.Colombia`.
