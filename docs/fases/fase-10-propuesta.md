# Fase 10 · Auditoría — Propuesta

> Estado: **PROPUESTA** — pendiente de tu revisión y aprobación · 2026-09-29
> Requisitos previos: Fases 2 a 9 implementadas. La **infraestructura** de auditoría ya existe desde la Fase 2 (se adelantó a propósito,
> plan 12 §S): bitácora particionada de solo inserción, sellos por lotes encadenados por nodo, verificador y sello impreso en el Z.
> Base: plan [12 §S, fase 10](../12-plan-riesgos-decisiones.md), doc [06 "Auditoría"](../06-seguridad-usuarios-permisos.md),
> ADR-0012 (sellado por lotes y anclas externas), ADR-0029 (sello en el reporte Z), ADR-0044/0045 (reportes y exportación auditada).

**Convenciones:** 🔒 = decisión difícil de cambiar después (matriz en la §3). ✅ = regla que se cumple. ⚙️ = parámetro configurable.
El signo **§** significa "sección" de un documento: "§3" es la sección 3.

## 0. Objetivo y alcance

Después de esta fase el propietario puede responder **quién hizo qué, cuándo, desde dónde, con qué autorización y qué valor tenía antes
y después**, sobre cualquier dato importante de la tienda, y puede **demostrar que la bitácora no fue alterada** (o descubrir que sí).

**Lo que ya existe (Fase 2 y siguientes)** y no se rehace:

| Pieza | Estado |
|---|---|
| Bitácora `audit.audit_log` particionada por mes, solo inserción (permisos + disparadores) | ✅ |
| Registro automático antes/después de 39 entidades marcadas `[Audited]` (productos, precios, impuestos, usuarios, roles, compras, clientes…) | ✅ |
| ~50 eventos de negocio explícitos (anular venta, descuento, precio cambiado, ajustes, compras, pagos, datos de clientes, exportaciones…) | ✅ |
| Sellos por lotes encadenados por nodo cada minuto; verificador completo (`POST /audit/verify` y `verify-audit` por consola) | ✅ |
| Sello impreso en cada reporte Z y comprobación del código (`GET /audit/seals/{n}/check`) | ✅ |
| Búsqueda básica `GET /audit/logs` (entidad, usuario, módulo, acción, fechas) | ✅ |

**Lo que falta y trae esta fase:**

| Incluido | Excluido (fase) |
|---|---|
| **Cobertura completa** de los eventos del doc 06 que aún no se registran (§5.1) y un **catálogo de acciones** en el código con nombre en español y severidad | Eventos de backups/restauración (Fase 11) y de estado de licencia (Fase 12): se definen aquí, se emiten en su fase |
| **Consultas**: historial de un registro con diferencias campo a campo en español, actividad de un usuario, búsqueda por severidad, texto, caja y autorizador | Pantallas (Fase 15) |
| **Verificación automática programada** (diaria incremental y semanal completa) con **historial de verificaciones** e **incidentes de integridad** que el propietario debe reconocer | Notificaciones por correo/WhatsApp (posterior) |
| **Pruebas de manipulación** directa en la BD: el verificador debe detectarlas (entregable del plan) | Impedir que el administrador del equipo reescriba la BD (imposible; se detecta, ADR-0012) |
| **Reportes de auditoría** en el catálogo de la Fase 9 (cambios de precios y costos, seguridad, eventos sensibles, actividad fuera de horario, exportaciones, integridad) con exportación | Envío del ancla a la nube (con el cliente de licencias, Fase 12 — pregunta 3) |
| **Retención y datos personales**: la bitácora no se borra; datos personales enmascarados en los antes/después | Archivo de particiones antiguas en el backup (Fase 11) |

Entregable verificable del plan: **una manipulación directa en la BD es detectada por el verificador** (modificar, borrar o insertar una
fila, alterar o borrar un sello, desactivar el disparador) y **reescribir toda la cadena se descubre con el sello impreso en un Z**.

Se entrega en **cinco bloques** con una sola aprobación: **10.1 Cobertura y catálogo de acciones**, **10.2 Consultas**,
**10.3 Verificación programada e incidentes**, **10.4 Reportes de auditoría**, **10.5 Retención, datos personales y pruebas de
manipulación**.

---

## 1. Actores

| Actor (rol de sistema) | Qué hace |
|---|---|
| Propietario | Todo: consulta, verifica, **reconoce incidentes de integridad**, exporta |
| Administrador | Consulta y verifica; ve los incidentes (no los reconoce, pregunta 2) |
| Contador (`ACCOUNTANT`) | Consulta la bitácora y los reportes de auditoría (ya tiene `audit.log.view`) |
| Supervisor de caja | Nada nuevo (el antifraude de la Fase 9 le basta) |
| Sistema | Registra, sella cada minuto, verifica cada día y abre incidentes |

## 2. Decisiones principales

| # | Decisión | Por qué | |
|---|---|---|---|
| D10-01 | **Catálogo de acciones en el código** (`AuditActions`: código, módulo, nombre en español, severidad por defecto) sincronizado a la tabla `audit.action_types` con un script repetible, igual que los permisos. Una prueba exige que toda acción escrita esté en el catálogo | Reportes y consultas muestran "Precio cambiado", no `PRODUCT_PRICE_CHANGED`; ninguna acción queda sin nombre | 🔒 |
| D10-02 | **Cerrar los vacíos de cobertura** del doc 06 (§5.1): login fallido y bloqueo, cambio de contraseña/PIN, línea eliminada, retiro/ingreso/cajón sin venta, cierre de jornada (con diferencia = WARNING), arranque del servidor, migración aplicada, salto de hora detectado, verificación fallida | La promesa del doc 06 debe cumplirse completa | |
| D10-03 | **Diferencias campo a campo** en la consulta: el antes/después se presenta como lista `campo · antes · después` con el nombre del campo en español cuando exista (diccionario por entidad) y el valor crudo si no | El dueño entiende "Precio: $4.500 → $4.800" sin leer JSON | |
| D10-04 | **Verificación programada** ⚙️ (diaria a las 03:00 hora local, **incremental** desde el último sello verificado; semanal **completa** los domingos) con historial en `audit.verification_runs` (solo inserción) | La verificación completa de millones de filas es lenta; la incremental es de segundos y la completa semanal cubre lo antiguo | 🔒 |
| D10-05 | **Incidente de integridad**: una verificación con hallazgos crea un incidente CRÍTICO (tabla `audit.integrity_incidents`) visible en el tablero y en `GET /auth/me` para quien tenga `audit.log.verify`, hasta que el **propietario lo reconozca** con una nota (el reconocimiento es una fila nueva, no una edición). **Nunca bloquea la venta** | Una alteración no puede pasar inadvertida, pero la tienda sigue vendiendo | 🔒 |
| D10-06 | **Reportes de auditoría en el catálogo de la Fase 9** sobre una vista `reporting.audit_log`, con permiso `audit.log.view` y exportación auditada | Reutiliza motor, exportación y permisos; nada nuevo que mantener | |
| D10-07 | **Datos personales enmascarados en la bitácora** ⚙️: en los antes/después de clientes y terceros, correo y teléfono se guardan parcialmente (`j***@gmail.com`, `***4567`) y la dirección y notas como "cambió"; la identificación y el nombre completos sí (son la evidencia) | La bitácora no se puede borrar (es evidencia): guardar menos datos personales reduce el choque con la supresión de la Ley 1581 (pregunta 4) | 🔒 |
| D10-08 | **Retención**: la bitácora **nunca se borra** en la tienda; mínimo ⚙️ 10 años (libros y papeles del comerciante). Las particiones mensuales antiguas se archivarán con el backup (Fase 11) | Evidencia legal y de fraude; el volumen es pequeño (≈ 1–3 GB/año en una tienda grande) | |
| D10-09 | **Anclas externas**: además del Z, el último sello se imprime/descarga como **"constancia de integridad"** (PDF con número y código) cuando el propietario lo pida, y cada verificación guarda el código del sello verificado. El envío automático a la nube se hace con el cliente de licencias (Fase 12) | Sin un ancla fuera de la BD, reescribir toda la cadena no se detecta (ADR-0012) | |
| D10-10 | **Permiso nuevo** `audit.incident.acknowledge` (sensible, solo Propietario) | Reconocer una alteración es una decisión del dueño | |

## 3. Revisión arquitectónica de las decisiones difíciles de cambiar

Impacto en licenciamiento: **ninguno** (auditoría completa en ambas ediciones, ADR-0015).

| ID | Decisión | Motivo | Alternativas | Consecuencia positiva | Riesgo | Impacto POS local | Impacto multisucursal | Impacto sincronización | Dificultad de cambiar | Recomendación |
|---|---|---|---|---|---|---|---|---|---|---|
| D10-01 | Catálogo de acciones en código + tabla | Nombres y severidad uniformes | Textos libres en cada módulo | Reportes legibles, prueba de cobertura | Olvidar registrar una acción nueva (la prueba lo detecta) | Ninguno | Mismo catálogo en todas las tiendas | La nube recibe los mismos códigos | Alta (los códigos quedan en filas selladas para siempre) | **Adoptar** |
| D10-04 | Verificación diaria incremental + semanal completa | Rendimiento | Solo manual; completa diaria | Detección en menos de 24 h sin cargar la caja | Una alteración antigua se detecta hasta el domingo | Corre de madrugada, en transacción de solo lectura | Cada tienda verifica su nodo | La nube verificará lo que reciba | Media | **Adoptar** |
| D10-05 | Incidente que no bloquea la venta | Continuidad | Bloquear el sistema | La tienda no se detiene por un problema de evidencia | Un dueño que ignore el aviso | Aviso persistente | Por tienda | El incidente viajará a la nube (alerta central) | Media | **Adoptar** |
| D10-07 | Enmascarar datos personales en la bitácora | Ley 1581 vs. evidencia inmutable | Guardar todo; no auditar clientes | Menos datos personales en un registro que no se puede borrar | Menos detalle para investigar un cambio de correo | Ninguno | — | Menos datos personales viajando | **Muy alta** (lo sellado no se puede re-enmascarar) | **Adoptar** |

## 4. Modelo de datos

| Script | Contenido |
|---|---|
| `V2026.10.028__audit__verification_and_incidents.sql` | `audit.verification_runs` (inicio, fin, tipo INCREMENTAL/FULL/MANUAL, desde/hasta sello, filas y sellos revisados, resultado, código del último sello, hallazgos jsonb, quién la pidió) — solo inserción. `audit.integrity_incidents` (verificación que lo abrió, hallazgos, severidad) y `audit.incident_acknowledgements` (incidente, usuario, nota, fecha) — solo inserción. `audit.action_types` (código, módulo, nombre, severidad por defecto). Índices para las consultas nuevas (`severity`, `authorized_by`, `pos_terminal_id`, texto del resumen con trigramas) |
| `R__audit__action_types.sql` (repetible) | Catálogo de acciones sincronizado con el código (como el de permisos) |
| `R__reporting__views.sql` | Vista `reporting.audit_log` (con el nombre de la acción) y `reporting.audit_verifications` |
| `A__system__privileges.sql` | `pos_app`: `SELECT, INSERT` en las tablas nuevas de `audit`; nada de `UPDATE/DELETE` (y disparadores de solo inserción) |

## 5. Detalle por bloque

### 5.1 Cobertura (vacíos encontrados al revisar el código)

| Evento del doc 06 | Hoy | Acción nueva | Severidad |
|---|---|---|---|
| Login fallido y bloqueo por intentos | Solo en `identity.login_attempts` | `LOGIN_FAILED`, `USER_LOCKED` | WARNING |
| Cambio de contraseña / PIN, restablecimiento por el administrador | Implícito en el cambio del usuario | `PASSWORD_CHANGED`, `PIN_CHANGED`, `PASSWORD_RESET`, `PIN_RESET` | INFO / WARNING |
| Línea eliminada de una venta | Solo en la venta | `SALE_LINE_VOIDED` (con quién autorizó) | INFO |
| Retiro, ingreso, apertura de cajón sin venta | Solo en los movimientos de caja | `CASH_WITHDRAWAL`, `CASH_IN`, `NO_SALE_DRAWER_OPENED` | INFO / WARNING |
| Cierre de jornada | Solo apertura y revisión | `CASH_SESSION_CLOSED` (WARNING si hay diferencia) | INFO / WARNING |
| Sistema: arranque, migración, salto de hora | No | `SERVER_STARTED`, `DATABASE_MIGRATED`, `CLOCK_JUMP_DETECTED` (diferencia > ⚙️ 5 min entre reloj y última fila) | INFO / CRITICAL |
| Integridad | No | `AUDIT_VERIFIED`, `AUDIT_VERIFICATION_FAILED`, `INTEGRITY_INCIDENT_ACKNOWLEDGED` | INFO / CRITICAL |
| Backups y licencia | Fases 11 y 12 | Se agregan al catálogo ahora (`BACKUP_*`, `LICENSE_*`) y se emiten en su fase | — |

### 5.2 Consultas (API)

- `GET /audit/logs` — filtros nuevos: `severity`, `terminalId`, `authorizedBy`, `q` (texto en resumen y etiqueta); cada fila trae el
  nombre de la acción y los cambios como lista `campo · antes · después`.
- `GET /audit/entities/{entityType}/{entityId}/history` — historial completo de un registro en orden, con diferencias.
- `GET /audit/users/{userId}/activity?from=&to=` — actividad de un usuario (incluye lo que autorizó como supervisor).
- `GET /audit/actions` — catálogo de acciones (código, nombre, módulo, severidad).
- `GET /audit/verifications` — historial de verificaciones; `POST /audit/verify` ya existe (queda registrado como MANUAL).
- `GET /audit/incidents` y `POST /audit/incidents/{id}/acknowledge` (nota obligatoria, solo Propietario).
- `GET /audit/integrity-certificate` — constancia en PDF con el último sello, su código y la fecha (ancla externa manual).

### 5.3 Reportes de auditoría (catálogo de la Fase 9, permiso `audit.log.view`)

| Código | Contenido |
|---|---|
| `AUDIT_PRICE_COST_CHANGES` | Cambios de precio, costo manual e impuestos: producto, antes, después, variación %, quién, cuándo |
| `AUDIT_SECURITY` | Ingresos correctos y fallidos, bloqueos, sesiones revocadas, cambios de roles, permisos y excepciones, autorizaciones de supervisor |
| `AUDIT_SENSITIVE_EVENTS` | Eventos WARNING y CRITICAL con usuario, caja y autorizador |
| `AUDIT_BY_USER` | Acciones por usuario, módulo y severidad en el período |
| `AUDIT_AFTER_HOURS` | Actividad fuera del horario ⚙️ de la tienda (p. ej. 22:00–06:00) |
| `AUDIT_EXPORTS` | Exportaciones de reportes y de datos de clientes |
| `AUDIT_INTEGRITY` | Verificaciones, resultados, incidentes y reconocimientos |

## 6. Flujos

1. **Registro:** igual que hoy, en la misma transacción del cambio; el sellador sella cada minuto.
2. **Verificación diaria (03:00):** verifica desde el último sello verificado hasta el último sello; guarda la verificación; si hay
   hallazgos abre un incidente CRÍTICO. El domingo, completa.
3. **Incidente:** aparece en el tablero y en `/auth/me` de quienes pueden verificar; el propietario lo revisa (qué filas o sellos, desde
   cuándo) y lo reconoce con una nota; el incidente queda para siempre con su reconocimiento.
4. **Ancla:** cada Z imprime el sello (ya existe); el propietario puede descargar la constancia cuando quiera; al verificar un Z antiguo
   con su código se comprueba que nada anterior fue reescrito.

## 7. Reglas

| Regla | Implementación |
|---|---|
| RN-AUD-01 Misma transacción | Ya se cumple (`IAuditWriter` y el interceptor) |
| RN-AUD-02 Toda acción en el catálogo | Prueba de arquitectura: los códigos usados existen en `AuditActions` |
| RN-AUD-03 Solo inserción | Permisos + disparadores también en las tablas nuevas |
| RN-AUD-04 Verificación no bloquea | Transacción de solo lectura con tiempo límite ⚙️; nunca detiene la venta |
| RN-AUD-05 Incidente reconocido solo por el propietario | Permiso `audit.incident.acknowledge` |
| RN-AUD-06 Datos personales mínimos | Enmascarado de correo, teléfono, dirección y notas en antes/después de clientes y terceros |
| RN-AUD-07 Sin borrado | La bitácora no tiene retención con borrado en la tienda |

## 8. Permisos y roles

| Permiso | Roles de sistema |
|---|---|
| `audit.log.view` (existe) | ACCOUNTANT, OWNER, ADMIN |
| `audit.log.verify` (existe) | OWNER, ADMIN |
| `audit.incident.acknowledge` — nuevo, sensible | OWNER (se excluye de ADMIN, igual que el reintegro por garantía) |

## 9. Impacto en la sincronización

La bitácora es **por nodo** (ADR-0012): cada tienda y la nube tienen su cadena. Cuando exista la sincronización, las filas y los sellos
de cada tienda subirán a la nube de solo lectura (la nube podrá verificarlas y alertar centralmente); los incidentes también. Las
tablas nuevas llevan `node_id` para eso. Nada de esta fase cambia la venta ni los datos que se sincronizan.

## 10. Validación de la fase (según tu forma de trabajo)

Compilación sin advertencias y coherencia: una prueba corta de BD que **manipula la bitácora como superusuario** (modifica, borra e
inserta filas en un rango sellado, altera y borra un sello, desactiva el disparador y reescribe la cadena) y comprueba que el
verificador lo detecta y que el código del Z deja de coincidir; una prueba corta de API que recorre las consultas nuevas y los reportes
de auditoría; la prueba de catálogo de acciones. Las pruebas funcionales las haces tú con `http/fase-10.http`.

## 11. Riesgos

| Riesgo | Mitigación |
|---|---|
| La verificación completa tarda con años de datos | Incremental diaria; completa semanal de madrugada; transacción de solo lectura con tiempo límite |
| Falsos positivos (p. ej. restauración de un backup antiguo) | El hallazgo explica qué sello o fila falla; la restauración (Fase 11) registrará su propio evento y ancla |
| El administrador del equipo reescribe toda la BD | Se detecta con el sello impreso en el Z y la constancia; con la Fase 12, con el ancla en la nube |
| Datos personales en una bitácora imborrable | Enmascarado desde esta fase; lo ya registrado antes de la fase queda como está (pregunta 4) |
| Crecimiento de la bitácora | Particiones mensuales; archivo con el backup (Fase 11) |

## 12. Estructura de código

```
src/BuildingBlocks/Pos.Infrastructure/Auditing   Catálogo de acciones, enmascarado, verificación incremental, tarea programada
src/Modules/Audit/*                             Consultas nuevas, verificaciones, incidentes, constancia PDF
src/Modules/Identity, Sales, Cash               Eventos de cobertura (§5.1)
src/Modules/Reporting                           Reportes de auditoría
src/Server/Pos.Server.Migrations                V028, R__audit__action_types.sql, vistas
http/fase-10.http
```

## 13. Criterios de aceptación

- [ ] Todos los eventos del doc 06 (salvo backups y licencia) se registran; toda acción está en el catálogo.
- [ ] Historial de un registro y actividad de un usuario con diferencias en español.
- [ ] Verificación diaria y semanal automática con historial; incidente crítico visible hasta que el propietario lo reconozca.
- [ ] La manipulación directa en la BD es detectada; la reescritura de toda la cadena se descubre con el código del Z.
- [ ] Siete reportes de auditoría con exportación auditada.
- [ ] Correo, teléfono, dirección y notas enmascarados en la bitácora de clientes y terceros.
- [ ] `dotnet build` sin advertencias; docs 04 y 06 actualizados; ADRs (catálogo de acciones; verificación e incidentes; datos
      personales en la bitácora) e informe.

## 14. Preguntas para ti (la opción recomendada va primero)

1. **Verificación automática:** ¿diaria incremental a las 03:00 y completa los domingos (**recomendado**), o solo cuando alguien la
   pida?
2. **Quién reconoce un incidente de integridad:** ¿solo el **propietario** (**recomendado**: la alteración pudo hacerla un
   administrador), o también el administrador?
3. **Ancla en la nube:** ¿la enviamos **con el cliente de licencias de la Fase 12** (**recomendado**: un solo canal tienda → nube), o
   la adelantamos ahora con un canal propio?
4. **Datos personales en la bitácora:** ¿enmascarar correo, teléfono, dirección y notas de clientes y terceros (**recomendado**), o
   guardar todo completo?
5. **Retención:** ¿nunca borrar la bitácora en la tienda y archivar lo antiguo con el backup (**recomendado**, mínimo 10 años), o
   fijar un plazo de borrado?
6. **Horario para el reporte "fuera de horario":** ¿lo tomamos de una configuración nueva por tienda (**recomendado**, por defecto
   22:00–06:00), o me dices el horario de tu tienda?
