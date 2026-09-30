# Fase 11 · Backups — Propuesta

> Estado: **PROPUESTA** — pendiente de tu revisión y aprobación · 2026-09-29
> Requisitos previos: Fases 2 a 10 implementadas. Ya existen el rol de BD `pos_backup` (lectura total, Fase 2), el contador de "vidas"
> del nodo `org.nodes.incarnation` (se incrementa al restaurar, ADR-0014), las acciones reservadas `BACKUP_*` (Fase 10) y el sello de
> auditoría como ancla externa (ADR-0012).
> Base: plan [12 §S, fase 11](../12-plan-riesgos-decisiones.md), doc [10 §P "Estrategia de backups"](../10-offline-backups-actualizaciones.md),
> doc [06 permisos `backup.*`](../06-seguridad-usuarios-permisos.md), tu decisión de producto: **backup completo en la nube además de la
> sincronización**.

**Convenciones:** 🔒 = decisión difícil de cambiar después (matriz en la §3). ✅ = regla que se cumple. ⚙️ = parámetro configurable.
El signo **§** significa "sección" de un documento.

## 0. Objetivo y alcance

Después de esta fase, **si el computador servidor se daña, se lo roban o se quema, la tienda vuelve a vender en menos de una hora en
otro equipo** con los datos del último backup (máximo ⚙️ 4 horas de pérdida), y **nadie que robe un archivo de backup puede leerlo**.

Entregable verificable del plan: **restauración completa en otra máquina**: un backup hecho en el equipo A, copiado a la nube o a una
USB, se restaura en un equipo B con solo el archivo y el **código de recuperación** del propietario; el resultado pasa la verificación
de la auditoría (Fase 10) y del kardex (Fase 4), y el sello de auditoría del backup coincide.

| Incluido | Excluido (fase) |
|---|---|
| **Paquete de backup** propio (`.posbak`): volcado de la BD + configuración de la instalación + manifiesto, **cifrado AES-256-GCM** | Replicación continua / recuperación a un minuto exacto (WAL, PITR) — fase avanzada |
| **Código de recuperación** del propietario para abrir los backups en otro equipo | Instalador y asistente gráfico de recuperación (Fase 13 usa lo de esta fase) |
| Backups **programados** (cada ⚙️ 4 h en horario + nocturno), **al cerrar jornada**, **antes de actualizar** y **manuales** | Backup de las cajas autónomas (se diseñarán con la caja autónoma) |
| **Destinos**: carpeta local, **disco externo/USB**, **carpeta de red** y **nube** (almacenamiento compatible S3 — pregunta 1) | Sincronización en vivo con la nube (fase de sincronización) |
| **Retención** abuelo-padre-hijo (⚙️ 7 diarios, 4 semanales, 12 mensuales + los de antes de actualizar) | |
| **Verificación** de cada backup y **restauración de prueba** semanal automática en una BD temporal | |
| **Restauración** desde la consola del servidor (`Pos.Server.Migrator restore`), con backup previo del estado actual, BD nueva, migraciones hacia adelante, validaciones y cambio de BD activa | Restaurar desde el navegador (pregunta 6) |
| Historial, **alertas** (último backup viejo, destino fallando) en el tablero y en `/auth/me`, auditoría de todo | Notificaciones por correo/WhatsApp (posterior) |

Se entrega en **cinco bloques** con una sola aprobación: **11.1 Paquete, cifrado y código de recuperación**, **11.2 Programación y
destinos**, **11.3 Retención, verificación y restauración de prueba**, **11.4 Restauración y recuperación en otro equipo**,
**11.5 API, alertas y auditoría**.

---

## 1. Actores

| Actor | Qué hace |
|---|---|
| Propietario | Guarda el **código de recuperación**; configura destinos; restaura (en el servidor); ve alertas |
| Administrador | Respalda ahora, ve el historial y los destinos, descarga un backup; no restaura (pregunta 6) |
| Contador / demás | Nada |
| Sistema | Programa, cifra, copia a los destinos, verifica, aplica la retención, prueba restaurar y alerta |
| Técnico en sitio | Ejecuta la restauración con la herramienta de consola cuando el equipo falla (con el código del propietario) |

## 2. Decisiones principales

| # | Decisión | Por qué | |
|---|---|---|---|
| D11-01 | **Volcado con `pg_dump` (formato custom, comprimido) y restauración con `pg_restore`** de PostgreSQL 18 (licencia PostgreSQL), usando el rol `pos_backup` para leer; la ruta de los binarios la da la instalación ⚙️ (el instalador de la Fase 13 incluye PostgreSQL) | Es la herramienta oficial, consistente (foto transaccional sin detener la tienda) y restaurable en cualquier PostgreSQL 18 | 🔒 |
| D11-02 | **Paquete `.posbak` propio**: encabezado legible (formato, versión de la app y del esquema, NIT, sucursal, nodo, fecha, tipo, sello de auditoría, hash) + contenido cifrado (volcado + configuración de la instalación sin secretos en claro + manifiesto con SHA-256 de cada parte) | Un solo archivo que se copia a cualquier destino; se puede identificar sin descifrar; se valida antes de restaurar | 🔒 |
| D11-03 | **Cifrado AES-256-GCM por bloques** con una **clave de datos** aleatoria por instalación, guardada en el equipo protegida con DPAPI; en cada paquete va además la clave de datos **envuelta con el código de recuperación** (derivado con Argon2id, ya en el proyecto por NSec) | Robar el archivo no sirve; restaurar en otro equipo solo exige el archivo y el código; ni nosotros podemos leer los backups de la nube | 🔒 |
| D11-04 | **Código de recuperación** de 24 caracteres (grupos de 4) generado al activar los backups; se muestra **una sola vez** para imprimirlo; se puede **regenerar** (los backups nuevos usan el nuevo; los viejos siguen con el anterior) | Sin él no hay recuperación en otro equipo: debe ser del propietario, no del sistema | 🔒 |
| D11-05 | **Programación** ⚙️: cada 4 h entre 08:00 y 22:00, uno nocturno (23:30), uno **al cerrar cada jornada de caja** (máximo uno cada ⚙️ 30 min) y uno **obligatorio antes de migrar** la BD (el migrador lo hace solo) | RPO ≤ 4 h; el cierre del día queda siempre respaldado; una actualización fallida nunca pierde datos | |
| D11-06 | **Destinos** con una interfaz común: carpeta local (disco distinto al de la BD si existe), disco externo/USB (por etiqueta del volumen), carpeta de red (UNC con usuario guardado cifrado) y **nube compatible S3** (pregunta 1). Cada destino tiene su propia retención y su estado | Regla 3-2-1: 3 copias, 2 medios, 1 fuera del local | 🔒 |
| D11-07 | **Retención abuelo-padre-hijo** ⚙️ (7 diarios, 4 semanales, 12 mensuales) + los 3 últimos "antes de actualizar"; nunca se borra el último backup verificado de un destino | Espacio acotado sin perder historia | |
| D11-08 | **Un backup no verificado no cuenta**: tras crearlo se descifra y se comprueba el hash y `pg_restore --list`; **cada semana** ⚙️ se restaura el último en una BD temporal y se corre la verificación de la auditoría y del kardex | Descubrir un backup inservible antes de necesitarlo | |
| D11-09 | **Restauración solo desde la consola del servidor** (`Pos.Server.Migrator restore`): detiene el servicio, hace backup del estado actual, restaura en una **BD nueva**, aplica migraciones si el backup es de una versión anterior (nunca de una más nueva), verifica, cambia la BD activa, **incrementa la "vida" del nodo** y registra `BACKUP_RESTORED` | Una restauración reemplaza todo: exige estar frente al servidor y detener la venta | 🔒 |
| D11-10 | **Historial** en el esquema `backup` (`backup_runs`, `backup_copies`, `restore_tests`) y **alertas**: último backup verificado con más de ⚙️ 26 h, destino fallando, código de recuperación nunca confirmado | El dueño se entera antes de necesitar el backup | |
| D11-11 | **Permisos** del doc 06 ajustados: `backup.backup.run`, `backup.backup.view`, `backup.destination.configure` (sensible), `backup.recovery.manage` (sensible, solo propietario) | Configurar destinos o el código es tan delicado como restaurar | |

## 3. Revisión arquitectónica de las decisiones difíciles de cambiar

Impacto en licenciamiento: **ninguno** (backups completos en ambas ediciones, ADR-0015; la nube de backups no es un módulo pagado).

| ID | Decisión | Motivo | Alternativas | Consecuencia positiva | Riesgo | Impacto POS local | Impacto multisucursal | Impacto sincronización | Dificultad de cambiar | Recomendación |
|---|---|---|---|---|---|---|---|---|---|---|
| D11-01 | `pg_dump`/`pg_restore` | Herramienta oficial, foto consistente | Copia de archivos de la BD; replicación | Restaurable en cualquier PostgreSQL 18; no detiene la venta | Depende de los binarios instalados | Corre en el servidor con prioridad baja | Cada tienda respalda su BD | La restauración aumenta la "vida" del nodo para que la nube lo sepa | Alta | **Adoptar** |
| D11-02 | Paquete `.posbak` | Un archivo autodescriptivo | Carpeta suelta; zip sin cifrar | Se identifica y valida antes de restaurar | Formato propio a mantener (lleva versión) | — | El nombre incluye NIT y sucursal | — | Alta (backups viejos deben poder leerse siempre) | **Adoptar** |
| D11-03 | AES-256-GCM + clave envuelta con el código | Confidencialidad sin depender de nosotros | Sin cifrar; clave guardada en la nube | Un archivo robado no sirve; la nube guarda solo datos cifrados | **Perder el código = no restaurar en otro equipo** | La clave local (DPAPI) permite restaurar en el mismo equipo sin el código | — | — | **Muy alta** | **Adoptar** |
| D11-06 | Destinos intercambiables, nube S3 | 3-2-1 | Solo local; nube propia | MinIO en tu VPS, Backblaze B2 o Cloudflare R2 sirven igual | Credenciales de la nube en el equipo (cifradas con DPAPI) | Subida en segundo plano, con límite de ancho de banda ⚙️ | Cada tienda en su carpeta `NIT/sucursal/` | Independiente de la sincronización | Media | **Adoptar** |
| D11-09 | Restaurar solo desde la consola | Seguridad y consistencia | Desde el navegador | Nadie restaura por error o a distancia | Exige ir al servidor (o escritorio remoto) | Venta detenida durante la restauración (minutos) | Por tienda | La nube recibe la nueva "vida" del nodo | Media | **Adoptar** |

## 4. Modelo de datos

| Script | Contenido |
|---|---|
| `V2026.10.029__backup__backup.sql` | Esquema `backup`: `destinations` (tipo LOCAL/EXTERNAL/NETWORK/S3, ruta o bucket, retención, activo, secretos cifrados con DPAPI como bytes, último estado), `backup_runs` (tipo SCHEDULED/NIGHTLY/CASH_CLOSING/PRE_UPDATE/MANUAL, inicio, fin, tamaño, hash, versión de app y esquema, sello de auditoría, resultado, error), `backup_copies` (backup × destino: ruta, resultado, verificado, borrado por retención), `restore_tests` (semanales), `recovery_keys` (versión del código, clave de datos envuelta, fecha, confirmado por el propietario — nunca el código). Historiales de solo inserción donde aplica |
| `A__system__privileges.sql` | `pos_app` en `backup`; `pos_backup` sigue con lectura total |
| `R__identity__permissions_catalog.sql` | Los 4 permisos nuevos |

La **clave de datos** no se guarda en la BD (se guarda en el equipo con DPAPI, junto a los demás secretos de la instalación) para que
un volcado robado no la contenga.

## 5. Detalle por bloque

### 5.1 Paquete y cifrado

```
tienda-900123456-S01-20261005-2330-NIGHTLY.posbak
├─ Encabezado (texto JSON, sin cifrar): formato v1, app 1.4.0, esquema 2026.10.029, NIT, sucursal, nodo, vida del nodo, fecha,
│  tipo, sello de auditoría #N + código, tamaño, SHA-256 del contenido, clave de datos envuelta (Argon2id + AES-GCM) y versión del código
└─ Contenido (AES-256-GCM en bloques de 1 MiB, nonce por bloque): volcado pg_dump · configuración de la instalación · manifiesto
```

### 5.2 Programación y destinos

| Disparo | Cuándo | Destinos |
|---|---|---|
| Programado | Cada ⚙️ 4 h entre ⚙️ 08:00 y 22:00 | Local |
| Nocturno | ⚙️ 23:30 | Local + externos + nube |
| Cierre de jornada | Al cerrar cada jornada de caja (máx. uno cada ⚙️ 30 min) | Local + nube |
| Antes de actualizar | Siempre, lo hace el migrador antes de `migrate` | Local |
| Manual | `POST /backups` | Todos los activos |

Los backups corren en segundo plano con prioridad baja; la subida a la nube se reintenta con espera creciente y límite de ancho de
banda ⚙️. Un disco externo ausente no es error grave: queda "pendiente" y se copia cuando se conecte.

### 5.3 Retención y verificación

- Retención por destino ⚙️ (7/4/12 + 3 pre-actualización). Nunca se borra el último backup verificado.
- Verificación inmediata: descifrar, comprobar hashes y `pg_restore --list`.
- Restauración de prueba semanal ⚙️ (domingo 04:00, después de la verificación de la auditoría): restaura el último backup en una BD
  temporal, corre la verificación de la auditoría y del kardex, compara conteos y sello, y borra la BD temporal.

### 5.4 Restauración y recuperación en otro equipo

```
Pos.Server.Migrator restore --file E:\tienda-...-NIGHTLY.posbak [--recovery-code XXXX-XXXX-XXXX-XXXX-XXXX-XXXX]
  1. Lee el encabezado y muestra empresa, sucursal, fecha, versión y sello; pide confirmación.
  2. Rechaza backups de una versión de la app o del esquema MÁS NUEVA que la instalada.
  3. Descifra (clave local o código de recuperación) y verifica hashes.
  4. Detiene el servicio y hace un backup del estado actual (si hay BD).
  5. Restaura en una BD NUEVA, aplica migraciones hacia adelante, reaplica privilegios.
  6. Verifica la auditoría (y que el sello del encabezado coincida) y el kardex.
  7. Cambia la BD activa en la configuración, incrementa la "vida" del nodo, registra BACKUP_RESTORED y arranca el servicio.
  La BD anterior queda renombrada (no se borra) hasta que el propietario la elimine.
```

Recuperación en un equipo nuevo: instalar (Fase 13) → `restore` con el archivo (USB o descargado de la nube) y el código → reactivar la
licencia (Fase 12) → volver a emparejar las cajas.

### 5.5 API, alertas y auditoría

- `GET /backups` (historial), `POST /backups` (respaldar ahora), `GET /backups/{id}/download`, `POST /backups/{id}/verify`,
  `GET /backups/restore-tests`.
- `GET/POST/PUT /backups/destinations` y `POST /backups/destinations/{id}/test` (prueba de escritura).
- `POST /backups/recovery-code` (genera o regenera; se muestra una vez) y `POST /backups/recovery-code/confirm` (el propietario escribe
  los últimos 8 caracteres para confirmar que lo guardó).
- Alertas en el tablero y en `/auth/me` (quien tenga `backup.backup.view`): último backup verificado > ⚙️ 26 h, destino con fallas,
  prueba de restauración fallida, código de recuperación sin confirmar.
- Auditoría: `BACKUP_COMPLETED`, `BACKUP_FAILED` (CRÍTICO), `BACKUP_RESTORED` (CRÍTICO), `BACKUP_DOWNLOADED`,
  `BACKUP_DESTINATION_CHANGED`, `RECOVERY_CODE_GENERATED`, `RESTORE_TEST_FAILED`.

## 6. Reglas

| Regla | Implementación |
|---|---|
| RN-BAK-01 Un backup no verificado no cuenta | Estado VERIFIED solo tras descifrar y validar |
| RN-BAK-02 Nunca restaurar una versión más nueva | El migrador compara versión de app y esquema del encabezado |
| RN-BAK-03 Backup previo obligatorio | Antes de migrar y antes de restaurar |
| RN-BAK-04 La clave nunca viaja en claro | Clave local con DPAPI; en el paquete solo envuelta con el código |
| RN-BAK-05 La venta no se detiene por un backup | Proceso aparte con prioridad baja; `pg_dump` no bloquea escrituras |
| RN-BAK-06 Restaurar incrementa la vida del nodo | `org.nodes.incarnation + 1` (la sincronización lo usará) |
| RN-BAK-07 Todo queda auditado | Acciones `BACKUP_*` (§5.5) |

## 7. Permisos y roles

| Permiso | Roles de sistema |
|---|---|
| `backup.backup.view` | OWNER, ADMIN |
| `backup.backup.run` | OWNER, ADMIN |
| `backup.destination.configure` — sensible | OWNER, ADMIN |
| `backup.recovery.manage` — sensible | OWNER (se excluye del Administrador) |
| Restaurar | Consola del servidor (acceso físico o remoto al equipo); queda auditado |

## 8. Impacto en la sincronización y en la nube

- Cada restauración aumenta la **vida del nodo**: cuando exista la sincronización, la nube sabrá que la tienda "volvió atrás" y le
  reenviará lo que falte en lugar de tomar datos viejos como nuevos (ADR-0014).
- La nube de backups (S3) es **independiente** del portal y de la sincronización: guarda archivos cifrados que ni el portal puede leer.
  Si eliges MinIO en tu VPS (pregunta 1), el despliegue de la nube (doc `despliegue-nube.md`) agrega ese servicio.

## 9. Validación de la fase (según tu forma de trabajo)

Compilación sin advertencias y coherencia: una prueba corta que **respalda la BD de una tienda con ventas, la restaura en otra BD**
(simulando el otro equipo, solo con el archivo y el código de recuperación), y comprueba que la auditoría y el kardex verifican, que el
sello coincide y que los conteos son iguales; pruebas de que un paquete alterado o un código incorrecto se rechazan, y de la retención.
La prueba en dos computadores reales la haces tú con `http/fase-11.http` y la guía de recuperación.

## 10. Riesgos

| Riesgo | Mitigación |
|---|---|
| El propietario pierde el código de recuperación | Confirmación obligatoria al generarlo; alerta mientras no lo confirme; puede regenerarlo (los backups nuevos quedan con el nuevo); en el mismo equipo se restaura sin código |
| Disco lleno | Retención automática, alerta de espacio ⚙️, tamaño estimado antes de respaldar |
| Backup "bueno" que no restaura | Verificación inmediata + restauración de prueba semanal |
| Internet lento | Subida en segundo plano con límite de ancho de banda y reanudación |
| Restaurar la tienda equivocada | El encabezado muestra NIT, sucursal y fecha y pide confirmación; la BD anterior no se borra |
| Ransomware cifra los backups locales | Copia fuera del equipo (USB desconectable y nube); en la nube, bucket con versionado/bloqueo si el proveedor lo permite |

## 11. Estructura de código

```
src/Modules/Backup/*               (nuevo) Paquete, cifrado, código de recuperación, destinos, programación, retención, verificación,
                                   restauración de prueba, API y alertas
src/Server/Pos.Server.Migrator     Comandos backup, restore y verify-backup; backup automático antes de migrate
src/Server/Pos.Server.Migrations   V029, privilegios, permisos
src/Modules/Identity               Roles; /auth/me con alertas de backup
src/Modules/Reporting              Alerta en el tablero
http/fase-11.http · docs/guia-recuperacion.md (paso a paso para el técnico)
```

## 12. Criterios de aceptación

- [ ] Backup cifrado programado, nocturno, al cierre, antes de migrar y manual; copia a local, externo, red y nube.
- [ ] Código de recuperación: generado una vez, confirmado, regenerable; sin él un archivo robado no se puede leer.
- [ ] Retención por destino; verificación inmediata; restauración de prueba semanal.
- [ ] Restauración en otra BD solo con el archivo y el código: auditoría y kardex verifican, el sello coincide, la vida del nodo aumenta.
- [ ] Rechazo de backups alterados, de otra versión más nueva o con código incorrecto.
- [ ] Alertas en el tablero y `/auth/me`; auditoría de todo.
- [ ] `dotnet build` sin advertencias; docs 06 y 10 actualizados; guía de recuperación; ADRs (paquete y cifrado; destinos y
      programación; restauración) e informe.

## 13. Preguntas para ti (la opción recomendada va primero)

1. **Nube de backups:** ¿usamos un almacenamiento **compatible S3** — **MinIO en tu VPS** (recomendado: sin costo extra, ya tienes el
   VPS) o un servicio como Backblaze B2 / Cloudflare R2 (unos pocos dólares al mes, más resistente si el VPS falla) —, o esperamos a la
   Fase 12 para subirlos a través del servidor de licencias?
2. **Frecuencia:** ¿cada 4 horas entre 08:00 y 22:00 + nocturno a las 23:30 + al cerrar cada jornada (**recomendado**), u otra?
3. **Código de recuperación:** ¿solo lo tiene el propietario (**recomendado**: ni nosotros podemos abrir los backups), o prefieres
   guardar además una copia en el portal (más cómodo, menos seguro)?
4. **Restauración de prueba:** ¿automática cada semana (**recomendado**), o solo cuando alguien la pida?
5. **Retención:** ¿7 diarios, 4 semanales y 12 mensuales (**recomendado**), u otra?
6. **Restaurar:** ¿solo desde la consola del servidor (**recomendado**), o también desde el navegador del backoffice?
7. **Destino externo:** ¿la tienda tendrá un **disco USB** conectado al servidor, una **carpeta de red** (otro PC o NAS) o ambos? (Se
   dejan configurables los dos; me sirve para la guía.)
