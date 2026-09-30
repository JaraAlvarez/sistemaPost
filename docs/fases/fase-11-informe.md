# Fase 11 · Backups — Informe de implementación

- **Estado:** Implementada — pendiente de tu validación · 2026-09-29
- **Propuesta:** [fase-11-propuesta.md](fase-11-propuesta.md) (aprobada con las recomendaciones de la §13)
- **ADR:** [0050](../adr/0050-paquete-de-backup-cifrado-y-codigo-de-recuperacion.md) · [0051](../adr/0051-destinos-programacion-y-retencion-de-backups.md) ·
  [0052](../adr/0052-restauracion-desde-la-consola.md)
- **Guías:** [recuperación en otro equipo](../guia-recuperacion.md) · [nube de backups con MinIO](../despliegue-nube.md#14-nube-de-backups-de-las-tiendas-fase-11-minio)
- **Pruebas manuales:** [http/fase-11.http](../../http/fase-11.http)

## 1. Qué se entregó

| Bloque | Entregado |
|---|---|
| 11.1 Paquete, cifrado y código | Paquete `.posbak` v1: encabezado legible + bloques AES-256-GCM autenticados con el encabezado, el número y la marca de último bloque; volcado `pg_dump` dentro de una **foto exportada** (conteos y sello del mismo momento); configuración sin secretos; manifiesto con SHA-256. Clave de datos en el equipo con DPAPI. **Código de recuperación** (24 caracteres) con Argon2id, mostrado una vez y confirmado escribiéndolo de nuevo |
| 11.2 Programación y destinos | Módulo `Backup` con proceso en segundo plano: manual (en cola), nocturno 23:30, cierre de jornada (máx. uno cada 30 min), cada 4 h entre 08:00 y 22:00, reintentos de copias pendientes. Destinos LOCAL (automático), EXTERNAL (USB con etiqueta de volumen; PENDIENTE si no está conectada), NETWORK (UNC) y **S3** (MinIO en el VPS; cliente propio con firma V4) |
| 11.3 Retención y verificación | Verificación inmediata de cada backup (descifrar, huellas, manifiesto, `pg_restore --list`); retención 7/4/12 por destino; **restauración de prueba semanal** en una BD temporal del rol `pos_backup` (auditoría, sello y conteos) |
| 11.4 Restauración | `Pos.Server.Migrator restore` (confirmación, backup previo, BD nueva, migraciones, verificación de auditoría, sello, conteos y kardex, vida del nodo +1, `BACKUP_RESTORED`, cambio de BD activa en `server.json`), `verify-backup`, `backup`; `migrate` hace un backup **PRE_UPDATE** obligatorio |
| 11.5 API, alertas y auditoría | `/backups` (historial, respaldar ahora, verificar, descargar, pruebas de restauración, alertas, destinos, prueba de destino, código de recuperación). Alertas en `/auth/me` (`backupAlerts`) y en el tablero. 9 acciones de auditoría nuevas |

### Base de datos y configuración
- `V2026.10.029__backup__backup.sql`: esquema `backup` (`destinations`, `backup_runs` y `restore_tests` de solo inserción, `backup_copies`,
  `recovery_keys`).
- `DatabaseCreator`: `pos_backup` ahora tiene `CREATEDB` (restauración de prueba) y hay `CreateDatabaseOnlyAsync` para restaurar en una BD
  nueva. **En tu BD de desarrollo ejecuta de nuevo `tools/scripts/dev-db.ps1`** para que el rol reciba el permiso.
- Configuración de la instalación: `Pos:Database:BackupConnectionString` (rol `pos_backup`, con DPAPI) y `Pos:Backup:PgBinPath`.
- 4 permisos nuevos (90 en total); `backup.recovery.manage` solo Propietario. 9 claves ⚙️ `backup.*` (intervalo, horario, nocturno,
  cierre, antigüedad de la alerta, día y hora de la restauración de prueba).
- Nube: servicio **MinIO** opcional en `deploy/cloud/docker-compose.yml` (perfil `backups`), bloque de Caddy comentado y guía §14.
  MinIO server es AGPL-3.0: se usa sin modificar como servicio aparte en el VPS (no se distribuye con el POS).

## 2. Desviaciones

| Propuesta | Implementado | Motivo |
|---|---|---|
| Confirmar el código con sus últimos 8 caracteres | Se escribe el código completo | Solo así se comprueba sin guardar el código (se abre la clave envuelta) |
| Carpeta de red con usuario guardado cifrado | Usa la cuenta del servicio de Windows | Montar credenciales de red exige otra API del sistema; queda para el instalador |
| Límite de ancho de banda ⚙️ para la nube | No en esta fase | La subida ya es en segundo plano; se agrega si una tienda lo necesita |
| — | `app_version` de los backups hasta 100 caracteres | La versión incluye el hash del commit |

## 3. Validación realizada

| Verificación | Resultado |
|---|---|
| `dotnet build Pos.slnx -c Release` | **0 advertencias, 0 errores** |
| `BackupTests` (unitarias): cifrado de 0 B a 2 MiB; byte alterado, archivo truncado, clave equivocada y encabezado cambiado → rechazo; código de recuperación (correcto, en minúsculas con espacios, incorrecto); retención 7/4/12; **firma S3 idéntica al ejemplo publicado por AWS**; configuración sin secretos | ✔ 10/10 |
| **`BackupApiTests` (entregable del plan)** con dos ventas del escenario de caja: código generado y confirmado (uno incorrecto se rechaza); destino "USB" probado; **respaldar ahora** → verificado y copiado a LOCAL y USB; verificar y descargar; la cajera recibe 403; **restauración en otra BD solo con el archivo de la USB y el código** (un código incorrecto se rechaza) → auditoría íntegra, **sello igual**, **conteos iguales**; vida del nodo +1 y `BACKUP_RESTORED` en la BD restaurada; **restauración de prueba semanal** correcta | ✔ |
| Esquema y migraciones (`SchemaTests`, `MigrationTests`), arquitectura (incluida R9 con las acciones nuevas), conformidad de permisos | ✔ 45 · 22 · 5 |

En las pruebas, `pg_dump` y `pg_restore` se ejecutan dentro del contenedor de PostgreSQL 18 (este equipo no los tiene instalados). La
implementación real (`PgClientTools`, que llama a los binarios instalados) **no se ejecutó**: es lo primero que debes probar.
No se ejecutó `build.ps1` completo ni la batería completa.

## 4. Qué debes probar tú

1. **Instala las herramientas de PostgreSQL 18** en el equipo (o configura `Pos:Backup:PgBinPath`), ejecuta de nuevo `dev-db.ps1` y haz un
   backup manual (`http/fase-11.http`).
2. **Restauración real en otro computador** siguiendo la [guía](../guia-recuperacion.md): copia el `.posbak` a una USB, restaura con el
   código y comprueba que vende. Prueba también `migrate` con una migración pendiente (debe hacer el backup PRE_UPDATE antes).
3. **USB real**: con etiqueta de volumen; retírala (la copia queda PENDIENTE) y conéctala (se copia sola en unos minutos).
4. **Carpeta de red** en otro PC o NAS con permiso para la cuenta del servicio.
5. **MinIO en tu VPS** (guía §14): DNS, bucket, usuario de la tienda y `POST /backups/destinations/{id}/test`.
6. **Programación**: deja el servidor encendido una noche (nocturno 23:30) y cierra una jornada (backup de cierre); el domingo la prueba de
   restauración.
7. **Alertas**: sin backup reciente o sin confirmar el código, `/auth/me` y el tablero muestran `backupAlerts`.
8. **Tamaño y tiempo** con la BD real de la tienda.

## 5. Archivos principales

```
src/BuildingBlocks/Pos.Infrastructure/Backup/   BackupPackage (formato y cifrado), BackupKeys (clave y código), BackupEngine
                                               (crear, verificar, restaurar), PgTools, RetentionPolicy, S3Storage (firma V4)
src/BuildingBlocks/Pos.Infrastructure/Security/ProtectedSecret.cs   DPAPI compartido (antes en el host)
src/Modules/Backup/*                           módulo: casos de uso, historial, destinos, ejecutor, proceso en segundo plano, alertas, API
src/Server/Pos.Server.Migrator/BackupCommands.cs   backup, verify-backup, restore y backup previo a migrate
src/Server/Pos.Server.Migrations/Scripts/2026.10/V2026.10.029__backup__backup.sql
deploy/cloud/docker-compose.yml · Caddyfile · .env.example   MinIO opcional
tests/Pos.Infrastructure.UnitTests/BackupTests.cs · tests/Pos.Server.IntegrationTests/Phase11/BackupApiTests.cs
```

Documentos actualizados: 06, 10, licencias de terceros, despliegue de la nube (§14), índice de ADR y README.
