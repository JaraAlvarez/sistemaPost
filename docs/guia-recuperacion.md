# Guía de recuperación desde un backup (técnico en sitio)

> Fase 11 · Objetivo: la tienda vuelve a vender en **menos de una hora** en otro equipo. Pérdida máxima: desde el último backup
> (≤ 4 horas por defecto; el cierre de cada jornada también queda respaldado).

## Lo que necesita

| Qué | Dónde está |
|---|---|
| El archivo `.posbak` más reciente | Disco USB de la tienda, carpeta de red, nube (MinIO en el VPS) o `C:\ProgramData\PosSupermercado\backups\local` si el disco sobrevivió |
| El **código de recuperación** (24 caracteres, `XXXX-XXXX-XXXX-XXXX-XXXX-XXXX`) | Lo tiene el **propietario** (impreso). Sin él, un backup no se abre en otro equipo |
| La cadena del superusuario de PostgreSQL del equipo nuevo | La define la instalación (Fase 13) |

Si se restaura en el **mismo** equipo (la BD se dañó pero el disco no), no hace falta el código: se usa la clave del equipo.

## Pasos

1. **Instale** el POS en el equipo nuevo (instalador, Fase 13) o, si ya existe, **detenga el servicio** del servidor.
2. **Revise el archivo** (no cambia nada):
   ```bash
   Pos.Server.Migrator verify-backup --file E:\tienda-9001234568-S01-20261005-233000-NIGHTLY.posbak --recovery-code XXXX-XXXX-XXXX-XXXX-XXXX-XXXX
   ```
   Muestra empresa, sucursal, fecha, versión y sello de auditoría. Confirme con el propietario que es la tienda y la fecha correctas.
3. **Restaure** (con el servicio detenido):
   ```bash
   Pos.Server.Migrator restore --file E:\...NIGHTLY.posbak --recovery-code XXXX-XXXX-XXXX-XXXX-XXXX-XXXX --superuser "Host=127.0.0.1;Port=5432;Username=postgres;Password=...;Database=postgres" --yes
   ```
   El comando:
   - hace un backup del estado actual (si hay una BD configurada);
   - restaura en una **BD nueva** (`pos_rAAAAMMDDhhmm`), aplica las migraciones si el backup es de una versión anterior (nunca acepta uno
     de una versión más nueva: actualice primero);
   - verifica la **auditoría**, que el **sello** coincida con el del backup, los **conteos** y el **kardex**;
   - incrementa la "vida" del nodo, registra `BACKUP_RESTORED` en la bitácora y cambia la BD activa en `server.json`;
   - **no borra** la BD anterior.
   Si la auditoría o el sello no coinciden, **no cambia** la BD activa (código de salida 4): avise al propietario.
4. **Arranque el servicio** y entre como propietario.
5. **Reactive la licencia** en el equipo nuevo (Fase 12) y **vuelva a emparejar las cajas** (Multicaja).
6. Revise en el backoffice: `GET /api/v1/audit/verifications` (haga `POST /api/v1/audit/verify`) y `GET /api/v1/backups/alerts`.
7. Genere un **backup inmediato** (`POST /api/v1/backups`) y, si el equipo es otro, configure de nuevo los destinos (USB, red, nube).

## Códigos de salida del migrador

| Código | Significado |
|---|---|
| 0 | Correcto |
| 1 | Backup dañado, alterado o código incorrecto |
| 2 | Falta un dato o falta `--yes` |
| 4 | La auditoría o el sello no coinciden: la BD restaurada queda para revisión |
| 6 | Falló el backup previo obligatorio (antes de `migrate`) |
