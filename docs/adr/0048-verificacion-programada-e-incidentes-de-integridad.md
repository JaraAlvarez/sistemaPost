# ADR-0048 · Verificación programada e incidentes de integridad

- **Estado:** Aceptada · 2026-09-29 · Fase 10 · Decisiones D10-04, D10-05, D10-09 y D10-10 · Complementa ADR-0012 y ADR-0029

## Contexto
El verificador de la bitácora existía desde la Fase 2, pero solo corría cuando alguien lo pedía y su resultado no quedaba guardado: una
alteración podía pasar meses sin que nadie lo notara.

## Decisión
- **Verificación automática:** cada 15 minutos se revisa si ya pasó la hora ⚙️ `audit.verification_hour` (03:00) y si no hubo una
  automática en 20 horas. El día ⚙️ `audit.full_verification_weekday` (domingo) es **completa**; los demás días **incremental**: la
  cadena y el hash de todos los sellos se revisan siempre (es barato), pero las filas solo se recalculan desde el último sello ya
  verificado y en las filas sin sellar. La manual (`POST /audit/verify`) es completa.
- La verificación es una **lectura de solo lectura con foto consistente** en su propia conexión y sin el límite de 30 s de las
  transacciones de `pos_app` (tiene su propio límite de 30 min). Nunca bloquea la venta.
- **Historial:** `audit.verification_runs` (solo inserción) guarda tipo, rango, sellos, filas, resultado, hallazgos y el código del
  último sello verificado.
- **Incidente de integridad:** hallazgos nuevos abren un incidente CRÍTICO (`audit.integrity_incidents`) y la acción
  `AUDIT_VERIFICATION_FAILED`. Queda visible en `/auth/me` (`openIntegrityIncidents`, para quien tiene `audit.log.verify`) y en el tablero
  hasta que el **propietario** lo reconoce con una nota (`audit.incident.acknowledge`, excluido del Administrador porque la alteración
  pudo hacerla un administrador). El reconocimiento es una fila nueva; los hallazgos ya reconocidos no abren otro incidente.
- **Constancia de integridad** (`GET /audit/integrity-certificate`): PDF con el último sello y su código, para guardar fuera del equipo,
  además del sello impreso en cada Z. El ancla automática en la nube se envía con el cliente de licencias (Fase 12).
- **Eventos del sistema al arrancar:** `SERVER_STARTED`, `DATABASE_MIGRATED` (cambió el número de migraciones) y `CLOCK_JUMP_DETECTED`
  (el reloj está atrasado más de ⚙️ `audit.clock_tolerance_minutes` frente a la última fila de la bitácora).

## Consecuencias
- Una alteración reciente se detecta en menos de 24 horas; una antigua, a más tardar en la verificación completa del domingo.
- Quien reescribe TODA la cadena de forma coherente no se detecta desde dentro de la BD; se descubre comparando con el código impreso en
  un Z o en una constancia (probado en `AuditIntegrityTests`).
