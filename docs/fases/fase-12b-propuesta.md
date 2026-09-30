# Fase 12-B · Licencia dentro del POS — Propuesta

> Estado: **PROPUESTA — pendiente de tu aprobación** · 2026-09-29
> Requisitos previos:
> - La Fase 12-A está implementada: el servidor y el portal de licencias en la nube, y el contrato compartido
>   `Pos.Licensing.Contracts` con la clave `POS-…`, la huella `fp1` 2 de 3, el token JWS EdDSA con `kid` y los códigos `LICENSE.*`.
> - `IFeatureGate` ya existe en modo stub (Fase 1).
> - Las acciones de auditoría `LICENSE_ACTIVATED` y `LICENSE_STATE_CHANGED` están reservadas (Fase 10).
> - La edición `SINGLE`/`MULTI` ya está en la configuración y en `org.nodes` (ADR-0015).
>
> Base:
> - plan [12 §S, fase 12](../12-plan-riesgos-decisiones.md);
> - doc [09](../09-licenciamiento.md), en especial "Ciclo de vida local", las reglas RN-LIC-01..05 y "Qué sigue en 12-B";
> - ADR [0038](../adr/0038-token-ed25519-con-rotacion-por-kid.md) y [0039](../adr/0039-modelo-por-edicion-y-licencia-por-nit.md).

**Convenciones:**
- 🔒 = decisión difícil de cambiar después (matriz en la §4).
- ⚙️ = parámetro configurable.
- **§** = "sección".

## 0. Objetivo y alcance

Después de esta fase, **el POS sabe si está licenciado y actúa en consecuencia sin castigar nunca al cliente honesto**:
- se activa con la clave en la configuración inicial;
- renueva el token solo una vez al día;
- sigue funcionando sin Internet mientras el token esté vigente;
- avisa antes de vencer;
- al terminar la gracia pasa a `RESTRICTED`. En ese estado se puede vender en las jornadas ya abiertas y cerrarlas, pero no abrir
  jornadas nuevas ni administrar. Los datos siguen siempre disponibles: consultas, reportes, exportación y backups.

**Entregable verificable del plan:** activar, vencer, gracia, restringir y reactivar, **en pruebas con reloj simulado** contra el
servidor de licencias real (12-A) levantado en la prueba.

| Incluido | Excluido (fase) |
|---|---|
| Módulo **`Licensing`** del POS: estado local, token guardado en la BD, claves públicas embebidas | Cobro y pasarela de pagos (posterior) |
| **Activación** con la clave: durante la configuración inicial o después, desde "Licencia" | Pantallas: todo es API hasta la Fase 15 |
| **Check-in diario** en segundo plano, con reintentos, y botón "verificar ahora" | Autoservicio de cambio de equipo sin soporte (pregunta 4) |
| Estados locales `DEMO`, `VALID`, `VALID_OFFLINE`, `GRACE`, `RESTRICTED`, `REACTIVATION_REQUIRED`, calculados por una función pura y probados con reloj simulado | Ofuscación del binario (Fase 13, firma de código) |
| **Restricciones en el backend** con una lista de lo permitido en `RESTRICTED` | Licencias por módulo (no existen, ADR-0015) |
| **Reloj atrasado**: máximo reloj observado y hora confiable del servidor de licencias (RN-LIC-05) | |
| **Liberar este equipo** (cambio de PC) y reactivación después de restaurar un backup en otro equipo | |
| **Avisos** en `/auth/me` y en el tablero: vence pronto, en gracia, restringida, mensajes del servidor, actualización disponible | |
| **Sello de auditoría** en cada check-in: el ancla externa pendiente del ADR-0048 (pregunta 3) | |

Se entrega en **cuatro bloques** con una sola aprobación:
- **12B.1** Estado y reglas (dominio puro).
- **12B.2** Activación, check-in y liberación.
- **12B.3** Restricciones y avisos.
- **12B.4** Reloj, restauración y auditoría.

---

## 1. Actores

| Actor | Qué hace |
|---|---|
| **Propietario** | Activa la licencia, libera el equipo, ve el estado y los avisos |
| **Administrador** | Ve el estado y pulsa "verificar ahora". No activa ni libera (lo decides en la pregunta 5) |
| **Cajero / supervisor** | Solo ve el aviso de la barra: "Licencia vence en 5 días", "Modo demostración" |
| **Servidor de la tienda** | Único equipo que habla con la nube. Las cajas **nunca** se activan contra la nube; heredan el estado del servidor |
| **Servidor de licencias (12-A)** | Emite y renueva el token firmado; informa suspensiones, renovaciones y mensajes |

## 2. Estados locales y qué permite cada uno

El estado **no se guarda como verdad**: se **calcula** en cada consulta a partir de tres cosas:
- el token firmado;
- la huella del equipo;
- el reloj confiable.

Así, editar la BD no sirve de nada.

| Estado | Cuándo | Ventas y jornadas abiertas | Abrir jornada | Administrar | Consultar, reportes, exportar, backup |
|---|---|---|---|---|---|
| `DEMO` | Nunca activado. Dura ⚙️ 30 días desde la instalación (pregunta 1) | ✅ con **"DEMOSTRACIÓN"** en el tiquete | ✅ | ✅ | ✅ |
| `VALID` | Token vigente y check-in reciente | ✅ | ✅ | ✅ | ✅ |
| `VALID_OFFLINE` | Token vigente, sin check-in hace más de 24 h | ✅ | ✅ | ✅ | ✅ |
| `GRACE` | Pasó `valid_until` pero no `valid_until + grace_days`, o el servidor informa `PAST_DUE` | ✅ con aviso | ✅ | ✅ | ✅ |
| `RESTRICTED` | Fin de la gracia, suspensión o cancelación informada, demo vencida, o reloj atrasado sin verificar | ✅ **solo en jornadas ya abiertas**, y se pueden cerrar | ❌ | ❌ | ✅ |
| `REACTIVATION_REQUIRED` | El token no corresponde a este equipo (huella distinta en menos de 2 de 3 componentes), por ejemplo tras restaurar en otro PC | Igual que `RESTRICTED` | ❌ | ❌ | ✅ |

**Reglas (✅ se cumplen siempre):**
- **RN-LIC-01** Nunca se interrumpe una jornada abierta.
- **RN-LIC-02** La falta de Internet nunca detiene ventas mientras el token no haya vencido.
- **RN-LIC-04** Consultar, reportar, exportar, respaldar y restaurar **nunca** se bloquean, en ningún estado.
- **RN-LIC-05** Si el reloj del servidor retrocede más de ⚙️ 24 h respecto del máximo observado, el estado es `RESTRICTED` hasta
  que un check-in exitoso confirme la hora.

## 3. Decisiones principales

| # | Decisión | Recomendación |
|---|---|---|
| D12B-01 🔒 | Dónde vive el token | En la BD, en `licensing.license_state`: una fila con el token, el `kid`, la hora confiable y el máximo reloj observado. Viaja en los backups; al restaurar en otro PC la huella no coincide y pide reactivación, que es el comportamiento correcto |
| D12B-02 🔒 | Cómo se aplican las restricciones | **Lista de lo permitido** en `RESTRICTED`, no de lo prohibido. Un *behavior* del *pipeline* rechaza con `LICENSE.RESTRICTED` todo comando que no esté marcado `IAllowedWhenRestricted`. Las consultas nunca pasan por el filtro. Lo que se olvide marcar queda bloqueado en `RESTRICTED`, pero **nunca** afecta a `VALID`/`GRACE` |
| D12B-03 | Qué se permite en `RESTRICTED` | Iniciar y cerrar sesión, ventas y pagos, anulaciones y devoluciones **de jornadas abiertas**, movimientos y cierre de jornada, backups, exportaciones, verificaciones de auditoría, activar la licencia y verificar ahora |
| D12B-04 🔒 | Claves públicas | **Embebidas** en el binario (la activa y la de reserva, `kid`), con la lista actualizable **solo** desde el propio check-in firmado. `GET /v1/public-keys` sirve solo para diagnóstico. En desarrollo se aceptan claves de prueba por configuración (nunca en `Production`) |
| D12B-05 | Huella del equipo | `fp1` de 12-A con: UUID de la placa (`Win32_ComputerSystemProduct`), serie del disco del sistema y `MachineGuid` de Windows. Solo se guarda el hash |
| D12B-06 | Check-in | Cada ⚙️ 24 h con variación aleatoria de ±2 h, más al arrancar y con "verificar ahora". Reintentos con espera creciente (1 min → 1 h). Envía el número de cajas con jornada abierta y la versión |
| D12B-07 | Reloj confiable | Se guardan `trusted_utc` (hora del servidor de licencias en el último check-in) y `max_observed_utc` (el mayor reloj local visto, actualizado cada minuto y al escribir auditoría). Se aprovecha la detección `CLOCK_JUMP_DETECTED` de la Fase 10 |
| D12B-08 | Edición | La edición del token debe coincidir con la de la instalación. Una licencia `MULTI` en una instalación `SINGLE` se acepta (sobra). Una `SINGLE` en `MULTI` da `LICENSE.EDITION_MISMATCH` al activar |
| D12B-09 | Demostración | ⚙️ 30 días con todo habilitado y "DEMOSTRACIÓN" en el tiquete y en los reportes impresos. Al vencer pasa a `RESTRICTED` (pregunta 1) |
| D12B-10 | Mensajes (`msgs` del token) | Se muestran como avisos en `/auth/me` (`License { State, DaysLeft, Messages }`, junto a `OpenIntegrityIncidents`) y en el tablero |

## 4. Revisión arquitectónica de las decisiones difíciles de cambiar

| # | Motivo | Alternativas | Riesgos | Impacto en el POS local | Multisucursal | Sincronización | Licenciamiento |
|---|---|---|---|---|---|---|---|
| D12B-01 Token en la BD | Un solo lugar, transaccional; viaja en el backup | Archivo en `ProgramData` (se pierde al restaurar y puede quedar desfasado de la BD) | Una copia de la BD en otro PC trae el token. La huella lo detecta (`REACTIVATION_REQUIRED`) | Ninguno | Cada sucursal es una instalación con su propio token | La tabla **no se sincroniza** (dato del nodo) | Es la base del control |
| D12B-02 Lista de permitidos | Por defecto seguro: un comando nuevo nace bloqueado en `RESTRICTED` | Lista de prohibidos (un olvido deja administrar sin licencia) | Olvidar marcar un comando operativo lo bloquea en `RESTRICTED`. Mitigación: prueba de arquitectura que lista los comandos permitidos y exige revisarla | Nunca toca `VALID`, `GRACE` ni `DEMO` | Igual en todas | — | Autoritativo en el backend (la UI solo oculta) |
| D12B-04 Claves embebidas | Verificación sin Internet; nadie puede inyectar una clave falsa por configuración | Descargar claves al activar (ataque de intermediario en la primera conexión) | Si se comprometen **las dos** claves embebidas, hay que publicar una versión del POS. Mitigación: la rotación por `kid` de 12-A hace que casi nunca sea necesario | Ninguno | Ninguno | — | Rotación ya probada en 12-A |

## 5. Modelo de datos (migración `licensing`, número asignado al implementar)

Tablas nuevas:
- **`licensing.license_state`**: una sola fila con `installation_id`, `token`, `kid`, `license_key_prefix`, `activated_at`,
  `last_checkin_at`, `last_checkin_error`, `trusted_utc`, `max_observed_utc` y `demo_started_at`.
  - El estado calculado **no** se guarda.
  - Solo la escribe el módulo; `pos_app` tiene `SELECT` y `UPDATE`, sin `DELETE`.
- **`licensing.checkins`**: una fila por intento (fecha, resultado, código `LICENSE.*`, estado recibido, duración). Es de **solo
  inserción** y se purga a los ⚙️ 365 días.

No se sincronizan. Se crea el esquema `licensing`, que se agrega a las listas de SchemaTests y ConformityTests.

## 6. Flujos

1. **Configuración inicial.** `POST /api/v1/setup` puede terminar **sin** licencia (`DEMO`). Si trae `licenseKey`, se activa en el
   mismo paso. Si la activación falla por red, la configuración se completa igual en `DEMO` y se avisa.
2. **Activación** (`POST /api/v1/license/activate`, solo el propietario):
   - se calcula la huella;
   - se llama a `POST /v1/activations` con el NIT de la empresa, la sucursal, el rol y la versión;
   - se verifica la firma, el `inst`, el `dev` y la edición;
   - se guarda y se audita `LICENSE_ACTIVATED`.
3. **Check-in** (`LicenseCheckinWorker`, BackgroundService):
   - envía el último token aunque haya vencido, la huella, la versión, las cajas abiertas, el reloj local y el sello de auditoría;
   - recibe el token renovado y los mensajes;
   - si cambia el estado calculado, audita `LICENSE_STATE_CHANGED` (del estado anterior al nuevo).
4. **Liberar este equipo** (`POST /api/v1/license/deactivate`, propietario, con confirmación): `POST /v1/deactivations`; el nodo queda
   en `DEMO` vencida, es decir, `RESTRICTED`.
5. **Restauración en otro PC** (Fase 11):
   - el token restaurado no coincide con la huella, así que el estado pasa a `REACTIVATION_REQUIRED`;
   - el propietario vuelve a escribir la clave;
   - si la nube responde `INSTALLATION_ACTIVE_ON_OTHER_DEVICE`, soporte libera el equipo viejo desde el portal (12-A) y se reintenta.
6. **Cajas** (`MULTI`): leen el estado en `/auth/me`. La restricción se aplica en el servidor, porque todos los comandos pasan por él.

## 7. Permisos

| Permiso | Propietario | Administrador | Otros |
|---|---|---|---|
| `licensing.license.view` (estado, historial de check-ins) | ✅ | ✅ | — |
| `licensing.license.check` (verificar ahora) | ✅ | ✅ | — |
| `licensing.license.manage` 🔐 (activar, liberar equipo) | ✅ | ❌ (pregunta 5) | — |

**Auditoría:**
- Acciones existentes: `LICENSE_ACTIVATED`, `LICENSE_STATE_CHANGED`.
- Acciones nuevas: `LICENSE_DEACTIVATED`, `LICENSE_CHECKIN_FAILED` (solo el primero de una racha, para no llenar la bitácora) y
  `LICENSE_CLOCK_ROLLBACK`.

## 8. Cambio pequeño en la nube (12-A)

El ADR-0048 dejó pendiente enviar el **sello de auditoría** como ancla externa "con el cliente de licencias". Propuesta:
- agregar a `CheckinRequest` un campo **opcional** `auditSeal { sealNo, sealCode, nodeId }`, compatible hacia atrás;
- la nube lo guarda en `checkins` y el portal lo muestra.

Así, si alguien borra auditoría en la tienda, el último sello conocido en la nube lo delata (pregunta 3).

## 9. Validación de la fase (según tu forma de trabajo)

Yo compilo y corro pocas pruebas cortas:
- la función pura de estados, con reloj simulado en todos los bordes (vence hoy, último día de gracia, reloj atrasado 25 h);
- **una** prueba de punta a punta contra el servidor de licencias real (12-A) levantado en la prueba: activar, vencer, gracia,
  restringir (abrir jornada da `LICENSE.RESTRICTED` y vender en la jornada abierta funciona) y reactivar;
- la prueba de arquitectura de la lista de permitidos.

Tú validas con `http/fase-12b.http` contra tu nube local o el VPS.

## 10. Riesgos

| Riesgo | Mitigación |
|---|---|
| Un comando operativo olvidado queda bloqueado en `RESTRICTED` | Lista revisada en la prueba de arquitectura; `RESTRICTED` es un estado raro y avisado con 7 días |
| La huella cambia al cambiar la placa en un taller | Tolerancia 2 de 3; si cambian dos componentes, soporte libera el equipo desde el portal |
| Reloj de BIOS sin pila (vuelve a 2000 al apagar) | RN-LIC-05 pasa a `RESTRICTED` **sin cerrar jornadas**, y el primer check-in lo corrige. El aviso dice "revise la fecha del equipo" |
| El VPS está caído muchos días | El token dura el periodo pagado más 7 días de gracia; ninguna venta se detiene |
| El token copiado a otra tienda | `inst` + `dev` + huella: el token no sirve en otro equipo |

## 11. Estructura de código

```
src/Modules/Licensing/
  Pos.Modules.Licensing.Contracts        LicensePermissions, LicenseStatusDto, ILicenseStatus (para /auth/me y el tablero)
  Pos.Modules.Licensing.Domain           LicenseEvaluator (función pura: token + huella + reloj → estado), LicenseState
  Pos.Modules.Licensing.Application      Activate, Deactivate, CheckNow, GetStatus; RestrictionBehavior (pipeline)
  Pos.Modules.Licensing.Infrastructure   LicenseStateStore (Dapper), LicenseCloudClient (HttpClient), WindowsFingerprint,
                                         LicenseCheckinWorker, ClockWatcher, EmbeddedKeys
  Pos.Modules.Licensing.Api              /api/v1/license/*
src/BuildingBlocks/Pos.Application.Abstractions   IAllowedWhenRestricted (marcador); IFeatureGate se retira (ADR-0015 ya
                                         no tiene features)
```

## 12. Criterios de aceptación

1. Una instalación nueva queda en `DEMO`, con "DEMOSTRACIÓN" en el tiquete, y pasa a `RESTRICTED` a los 30 días (reloj simulado).
2. La activación con una clave válida da `VALID`. Las claves inválida, revocada o de otra edición dan el código `LICENSE.*` correcto
   en español.
3. Sin Internet, un token vigente mantiene `VALID_OFFLINE`; después viene `GRACE` con aviso y luego `RESTRICTED`.
4. En `RESTRICTED` se vende y se cierra la jornada abierta; abrir jornada o crear un producto da `LICENSE.RESTRICTED`; reportes,
   exportación y backup funcionan.
5. Suspender en el portal lleva a `RESTRICTED` en el siguiente check-in; reactivar lleva a `VALID`.
6. Atrasar el reloj más de 24 h lleva a `RESTRICTED` hasta un check-in exitoso, con `LICENSE_CLOCK_ROLLBACK` auditado.
7. Restaurar un backup en otro PC lleva a `REACTIVATION_REQUIRED` y la reactivación funciona.
8. Compila sin advertencias; las pruebas de arquitectura (con la nueva regla), de conformidad y de esquemas están en verde.
9. Documentación: doc 09 actualizado, ADRs (restricciones por lista de permitidos, token en la BD), informe y `http/fase-12b.http`.

## 13. Preguntas para ti (la opción recomendada va primero)

1. **Demostración:** ¿**30 días completos** con "DEMOSTRACIÓN" en el tiquete y luego restringida (**recomendado**), o un límite por
   número de ventas (p. ej. 200)?
2. **Días de gracia:** ¿se mantienen los **7 días** que ya usa la nube (**recomendado**), o prefieres otro valor? Se cambia en la nube,
   no en la tienda.
3. **Sello de auditoría en el check-in:** ¿lo agregamos (**recomendado**: cierra el pendiente del ADR-0048 con un campo opcional en la
   nube), o lo dejamos para la fase de sincronización?
4. **Cambio de equipo:** en v1, ¿lo hace **soporte desde el portal** (**recomendado**: ya existe en 12-A y tú controlas el abuso), o
   autoservicio con un máximo de ⚙️ 2 cambios al año?
5. **¿Quién activa y libera la licencia?** ¿**Solo el propietario** (**recomendado**), o también el administrador?
