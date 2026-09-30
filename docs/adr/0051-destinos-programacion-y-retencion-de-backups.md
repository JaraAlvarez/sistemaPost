# ADR-0051 · Destinos, programación, retención y verificación de los backups

- **Estado:** Aceptada · 2026-09-29 · Fase 11 · Decisiones D11-05 a D11-08 y D11-10

## Decisión
- **Módulo `Backup`** con un proceso en segundo plano: atiende en seguida los backups manuales (`POST /backups`, en cola) y cada minuto
  revisa si toca el **nocturno** (⚙️ 23:30), uno por **cierre de jornada** (máx. uno cada ⚙️ 30 min), el **programado** (cada ⚙️ 4 h entre
  ⚙️ 08:00 y 22:00) o la **restauración de prueba** semanal (⚙️ domingo 04:00); también reintenta las copias pendientes. Un backup a la
  vez, con los binarios en prioridad baja: la venta no se detiene.
- **Un backup no verificado no cuenta:** se descifra y se comprueban hashes, manifiesto y `pg_restore --list` antes de registrarlo.
- **Destinos** (`backup.destinations`): LOCAL (carpeta del servidor, se crea sola), EXTERNAL (USB por ruta y etiqueta del volumen; si no
  está conectada la copia queda PENDIENTE y se hace al conectarla), NETWORK (UNC con la cuenta del servicio) y **S3** (MinIO en el VPS u
  otro compatible; cliente propio con firma V4 probada con el vector publicado por AWS; clave secreta cifrada con DPAPI). Cada destino
  indica con qué tipos de backup se usa.
- **Retención abuelo-padre-hijo por destino** (⚙️ 7/4/12) + los 3 últimos "antes de actualizar"; nunca se borra el último verificado ni
  se borra nada si no hay ninguno verificado.
- **Restauración de prueba semanal:** el rol `pos_backup` (ahora con `CREATEDB`) restaura el último backup en una BD temporal, verifica
  la auditoría, el sello y los conteos del encabezado, y la borra.
- **Alertas** (`/backups/alerts`, `/auth/me` → `backupAlerts`, tablero → `backupAlerts`): último backup verificado con más de ⚙️ 26 h,
  destinos fallando, prueba de restauración fallida, código de recuperación sin confirmar.
- **Auditoría:** `BACKUP_COMPLETED`, `BACKUP_FAILED` (CRÍTICO), `BACKUP_DOWNLOADED`, `BACKUP_DESTINATION_CHANGED`,
  `RECOVERY_CODE_GENERATED/CONFIRMED`, `RESTORE_TEST_PASSED/FAILED` (CRÍTICO).
- **Permisos:** `backup.backup.view`, `backup.backup.run`, `backup.destination.configure` (sensible) y `backup.recovery.manage`
  (sensible, solo Propietario).

## Consecuencias
- El servidor necesita los binarios de PostgreSQL 18 (`Pos:Backup:PgBinPath` o la ruta estándar); el instalador (Fase 13) los incluye.
- La carpeta de red usa la cuenta del servicio (sin usuario/clave propios en esta fase).
- Sin límite de ancho de banda configurable para la subida a la nube en esta fase (la subida es en segundo plano).
