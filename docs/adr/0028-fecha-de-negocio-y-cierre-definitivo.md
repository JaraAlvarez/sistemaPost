# ADR-0028 · Fecha de negocio de la jornada y cierre definitivo

- **Estado:** Aceptada · 2026-09-29 · Fase 6 · Decisiones D6-04, D6-05 y D6-06 (preguntas 1, 2 y 4)

## Contexto
Los supermercados tienen turnos que pasan la medianoche. Los reportes por día deben reflejar turnos completos, y un reporte Z
impreso no puede cambiar después.

## Decisión
- La **fecha de negocio de la jornada es la de su apertura** (zona horaria del negocio) aunque el turno pase la medianoche; las
  ventas de la Fase 7 tomarán la fecha de su jornada.
- **Arqueo ciego** por defecto (`cash.blind_count`): el cajero cuenta sin ver lo esperado; el reporte X y lo esperado solo los ve
  quien tiene `cash.report.view` mientras la jornada no esté cerrada.
- Cierre en dos pasos: `OPEN` → `CLOSING` (sin ventas nuevas) → `CLOSED`. Una diferencia mayor a `cash.difference_threshold`
  ($5.000) exige observación y queda pendiente de revisión del supervisor (que no puede ser el mismo cajero).
- **El cierre es definitivo**: no hay reapertura; los errores se corrigen con un movimiento `CORRECTION` en una jornada abierta.
- El cambio de cajero es cierre y nueva apertura (no se transfiere la jornada); un cajero por caja a la vez.

## Consecuencias
- ✅ El papel y el sistema siempre coinciden; los turnos nocturnos no se parten en dos días.
- ⚠️ Las ventas de madrugada aparecen en el día anterior (el de la apertura), lo que es deseado para el cuadre del turno.
