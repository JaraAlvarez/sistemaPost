# Fase 10 · Auditoría — Informe de implementación

- **Estado:** Implementada — pendiente de tu validación · 2026-09-29
- **Propuesta:** [fase-10-propuesta.md](fase-10-propuesta.md) (aprobada con las seis recomendaciones de la §14)
- **ADR:** [0047](../adr/0047-catalogo-de-acciones-de-auditoria.md) · [0048](../adr/0048-verificacion-programada-e-incidentes-de-integridad.md) ·
  [0049](../adr/0049-datos-personales-y-retencion-de-la-bitacora.md)
- **Pruebas manuales:** [http/fase-10.http](../../http/fase-10.http)

## 1. Qué se entregó

| Bloque | Entregado |
|---|---|
| 10.1 Cobertura y catálogo | Catálogo de **211 acciones** (73 explícitas + 3 por cada una de las 46 entidades auditadas) con nombre en español y severidad, en el código (`AuditActions`) y en la BD (`audit.action_types`). Eventos nuevos: `LOGIN_FAILED`, `PASSWORD_RESET`, `PIN_RESET`, `SALE_LINE_VOIDED`, `SERVER_STARTED`, `DATABASE_MIGRATED`, `CLOCK_JUMP_DETECTED`, `AUDIT_VERIFIED`, `AUDIT_VERIFICATION_FAILED`, `INTEGRITY_INCIDENT_ACKNOWLEDGED`, `INTEGRITY_CERTIFICATE_ISSUED`; reservados `BACKUP_*` y `LICENSE_*` |
| 10.2 Consultas | `/audit/logs` con severidad, caja, autorizador y texto; `/audit/entities/{tipo}/{id}/history`; `/audit/users/{id}/activity`; `/audit/actions`. Cada fila trae el nombre de la acción y los **cambios campo a campo en español** |
| 10.3 Verificación e incidentes | Verificación automática diaria **incremental** (03:00) y **completa** los domingos; manual con `POST /audit/verify`; historial `/audit/verifications`; **incidentes** `/audit/incidents` con reconocimiento del propietario; aviso en `/auth/me` (`openIntegrityIncidents`) y en el tablero; **constancia de integridad** en PDF |
| 10.4 Reportes de auditoría | `AUDIT_PRICE_COST_CHANGES`, `AUDIT_SECURITY`, `AUDIT_SENSITIVE_EVENTS`, `AUDIT_BY_USER`, `AUDIT_AFTER_HOURS`, `AUDIT_EXPORTS`, `AUDIT_INTEGRITY` (grupo Auditoría del catálogo de la Fase 9, permiso `audit.log.view`, exportables) |
| 10.5 Datos personales, retención y manipulación | Correo, teléfono, dirección, notas y detalle de solicitudes **enmascarados** en la bitácora (`[PersonalData]`); la bitácora no se borra; pruebas de manipulación directa en la BD |

### Base de datos
- `V2026.10.028__audit__verification_and_incidents.sql`: `action_types`, `verification_runs`, `integrity_incidents`,
  `integrity_incident_acknowledgements` (las tres últimas de solo inserción con disparador) e índices de la bitácora (severidad,
  autorizador, caja y texto con trigramas).
- `R__audit__action_types.sql` (repetible, generado junto con `AuditActions.cs`).
- `R__reporting__views.sql`: 5 vistas nuevas (`audit_log`, `audit_verifications`, `integrity_incidents`, `taxes`, `price_lists`) — 36 en total.
- `A__system__privileges.sql`: `pos_app` no puede insertar en `action_types`.
- Permiso nuevo `audit.incident.acknowledge` (86 permisos): solo Propietario.

### Configuración nueva (⚙️)
| Clave | Por defecto |
|---|---|
| `audit.verification_hour` | 3 (03:00) |
| `audit.full_verification_weekday` | 0 (domingo) |
| `audit.clock_tolerance_minutes` | 5 |
| `reporting.after_hours_start` / `reporting.after_hours_end` | 22 / 6 (por empresa o sucursal) |

## 2. Hallazgos y desviaciones

| Tema | Detalle |
|---|---|
| Vacíos de cobertura | Al revisar el código, retiros, cajón sin venta, cierres de jornada, cambios de contraseña/PIN y bloqueos **ya se registraban**; la propuesta los listaba como faltantes. Se agregaron solo los que faltaban de verdad |
| Acciones que el primer escaneo no veía | `PRODUCT_PRICE_CHANGED/SCHEDULED`, `SETUP_COMPLETED`, `SETTING_CHANGED`, `SETTING_OVERRIDE_REMOVED` se escriben con argumentos en varias líneas: se agregaron al catálogo y la prueba R9 ahora revisa toda la llamada |
| Enmascarado configurable (⚙️ de D10-07) | Se dejó **fijo**: lo sellado no se puede re-enmascarar, un interruptor solo agregaba riesgo (ADR-0049) |
| Verificación con el límite de 30 s de `pos_app` | El verificador ahora abre su transacción de solo lectura con límite propio de 30 min (una verificación completa de años de bitácora habría sido cortada) |
| `SERVER_STARTED` | Se registra al arrancar con la instalación **ya configurada** (el asistente inicial registra `SETUP_COMPLETED`) |
| Reporte de precios | Usa el evento explícito `PRODUCT_PRICE_CHANGED/SCHEDULED` (trae producto, precio anterior y nuevo), no la reconstrucción desde las filas de precios |

## 3. Validación realizada (construir y verificar coherencia)

| Verificación | Resultado |
|---|---|
| `dotnet build Pos.slnx -c Release` | **0 advertencias, 0 errores** |
| `ArchitectureTests` (incluye la nueva R9: toda acción está en el catálogo; comprobé que falla si se escribe un código inexistente) | ✔ 22/22 |
| `AuditIntegrityTests` (PostgreSQL 18): sello intermedio borrado → cadena rota; **reescritura completa** con el disparador desactivado → el verificador no la ve, pero **el código impreso del Z deja de coincidir**; incremental revisa lo nuevo y la completa lo antiguo; tablas nuevas de solo inserción | ✔ 4/4 |
| `ReportingSchemaTests`: 36 vistas compilan y `pos_app` no puede escribirlas | ✔ |
| `AuditApiTests`: catálogo código = BD; `LOGIN_FAILED`; correo y teléfono enmascarados en el historial; verificación limpia; manipulación → incidente → `/auth/me` = 1 → nota corta rechazada → reconocimiento → 0 → el mismo hallazgo no abre otro incidente; constancia PDF | ✔ |
| `ReportsApiTests` (todos los reportes del catálogo, incluidos los 7 de auditoría) y `ConformityTests` (permisos código = BD) | ✔ 6/6 |

Las pruebas de manipulación de la Fase 2 (`AuditTests`: fila modificada, hash recalculado, fila borrada, fila insertada, sello
alterado) siguen vigentes. No se ejecutó `build.ps1` completo ni la batería completa (por tu indicación).

## 4. Qué NO se probó — y qué debes probar tú

1. **La verificación automática programada** (03:00 incremental; domingo completa): cambie `audit.verification_hour` a una hora ya pasada
   y espere hasta 15 minutos; revise `GET /audit/verifications`.
2. **`SERVER_STARTED` y `DATABASE_MIGRATED`**: reinicie el servidor (y después de aplicar una migración) y consulte `?module=system`.
3. **`CLOCK_JUMP_DETECTED`**: atrase la hora del equipo más de 5 minutos y reinicie (vuelva a ponerla bien después).
4. **Incidente real** con el paso 10 del `.http`; que el **Administrador** reciba 403 al reconocer; que el aviso aparezca en el tablero.
5. **Constancia PDF**: ábrala y compruebe su código con `GET /audit/seals/{n}/check`.
6. **Reportes de auditoría** con datos reales: cambios de precios (antes/después/variación), seguridad, fuera de horario con tu horario.
7. **Rendimiento** de la verificación completa con meses de bitácora.
8. **Enmascarado**: cambie el correo y teléfono de un tercero y revise su historial.

## 5. Archivos principales

```
src/Modules/Audit/Pos.Modules.Audit.Contracts/AuditActions.cs          catálogo de acciones (generado junto con el SQL)
src/Modules/Audit/Pos.Modules.Audit.Contracts/AuditContracts.cs        DTOs, permiso, nombres de campos
src/Modules/Audit/Pos.Modules.Audit.Application/AuditQueries.cs        consultas, IntegrityService, incidentes, constancia
src/Modules/Audit/Pos.Modules.Audit.Infrastructure/AuditReadModel.cs   lectura con diferencias
src/Modules/Audit/Pos.Modules.Audit.Infrastructure/IntegrityInfrastructure.cs  verificación, tarea programada, arranque, PDF
src/BuildingBlocks/Pos.Infrastructure/Auditing/AuditVerifier.cs        modo incremental y transacción de solo lectura
src/BuildingBlocks/Pos.Infrastructure/Persistence/PosSaveChangesInterceptor.cs  enmascarado [PersonalData]
src/Modules/Reporting/.../ReportCatalog.cs                             7 reportes de auditoría
src/Server/Pos.Server.Migrations/Scripts/2026.10/V2026.10.028__audit__verification_and_incidents.sql
src/Server/Pos.Server.Migrations/Scripts/repeatable/R__audit__action_types.sql
tests/Pos.Database.Tests/AuditIntegrityTests.cs · tests/Pos.Server.IntegrationTests/Phase10/AuditApiTests.cs
```

Documentos actualizados: 02, 04, 06, índice de ADR y README.
