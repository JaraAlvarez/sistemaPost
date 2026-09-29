# ADR-0027 · Movimientos de caja de solo inserción y esperado calculado

- **Estado:** Aceptada · 2026-09-29 · Fase 6 · Decisiones D6-01, D6-02 y D6-03

## Contexto
La caja es donde más se concentra el riesgo de fraude y de error. Un total editable ("el sistema dice que hay X") permite
cuadrar a mano; un cierre sin trazabilidad no se puede explicar después.

## Decisión
- Cada peso que entra o sale es una fila de `cash.cash_movements` de **solo inserción** (privilegios + disparador, como el
  kardex): fondo inicial, ventas (Fase 7), ingresos, retiros, gastos, pagos a proveedores, correcciones y aperturas del cajón sin
  venta (dirección 0). La dirección la fija el tipo (CHECK); la corrección elige la suya y exige motivo.
- **Lo esperado se calcula siempre** de los movimientos, por medio de pago (Σ dirección × valor); nunca se guarda digitado. Al
  cerrar se guardan esperado, contado y diferencia por medio en `cash_session_totals`.
- Los movimientos de una jornada se numeran con la jornada bloqueada (`FOR UPDATE`): una salida de efectivo nunca deja el
  esperado negativo aunque dos equipos operen a la vez (RN-CSH-06).
- **Una jornada sin cerrar por caja y una abierta por cajero**, con índices únicos parciales: dos aperturas simultáneas dejan
  una sola jornada.
- Otros módulos (gastos, compras; ventas en la Fase 7) registran sus salidas con `ICashRegister` en la transacción de su
  documento; anular ese documento con la jornada abierta devuelve el dinero con una corrección (nunca se borra el movimiento).

## Consecuencias
- ✅ Cada diferencia se explica movimiento por movimiento; nadie "ajusta" lo esperado.
- ⚠️ Un cajero que olvida cerrar bloquea su usuario y su caja: lo resuelve el cierre por supervisor (auditado como crítico).
