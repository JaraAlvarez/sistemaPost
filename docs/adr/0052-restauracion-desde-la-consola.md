# ADR-0052 · Restauración solo desde la consola del servidor

- **Estado:** Aceptada · 2026-09-29 · Fase 11 · Decisión D11-09 · Complementa ADR-0014 (vidas del nodo)

## Decisión
- `Pos.Server.Migrator restore --file … [--recovery-code …] --superuser … --yes` (guía: `docs/guia-recuperacion.md`):
  1. muestra empresa, sucursal, fecha, versiones y sello, y exige `--yes`;
  2. hace un backup `PRE_RESTORE` del estado actual;
  3. rechaza backups de un esquema **más nuevo** que el instalado (RN-BAK-02);
  4. abre el paquete con la clave del equipo o, si es de otro equipo, con el código de recuperación;
  5. restaura en una **BD nueva** (`pos_rAAAAMMDDhhmm`), aplica las migraciones hacia adelante y los privilegios;
  6. verifica la auditoría, que el **sello** coincida con el del encabezado, los conteos y el kardex; si la auditoría o el sello fallan
     **no cambia** la BD activa (código 4);
  7. incrementa la **vida del nodo** (`system.installation.node_epoch` y `org.nodes.epoch`) y registra `BACKUP_RESTORED` (CRÍTICO);
  8. cambia la BD activa en `server.json` (respetando DPAPI). La BD anterior no se borra.
- `migrate` hace un backup **PRE_UPDATE** obligatorio antes de aplicar migraciones pendientes (`--no-backup` solo en desarrollo; el
  script `dev-db.ps1` lo usa). `backup` y `verify-backup` completan los comandos.

## Consecuencias
- Nadie restaura por error o a distancia desde el navegador; exige estar en el servidor (o en escritorio remoto) y detener el servicio.
- La prueba de la fase restaura en otra BD solo con el archivo y el código (como en otro equipo) y comprueba auditoría, sello y conteos.
