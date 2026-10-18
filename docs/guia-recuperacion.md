# Guía de recuperación desde un backup (técnico en sitio)

> Fase 11 · Objetivo: la tienda vuelve a vender en **menos de una hora** en otro equipo. Pérdida máxima: desde el último backup
> (≤ 4 horas por defecto; el cierre de cada jornada también queda respaldado).

## Lo que necesita

| Qué | Dónde está |
|---|---|
| El archivo `.posbak` más reciente | Disco USB de la tienda, carpeta de red, nube (MinIO en el VPS) o `C:\ProgramData\BusinessPost\backups\local` si el disco sobrevivió |
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

## Después de una actualización fallida ("restaurar con soporte")

Si el instalador terminó con el código **21** (el migrador salió con **8**: la migración falló y no se pudo volver solo a los datos de
antes) o con el **22** (no se pudo activar la versión nueva), el servidor y el actualizador quedaron en **inicio manual** para que no
arranquen sobre una base que no les corresponde. El detalle está en `C:\ProgramData\BusinessPost\logs\actualizacion-fallida-*.txt` y en
el registro de la instalación; el backup previo es el `…-PRE_UPDATE.posbak` más reciente de `C:\ProgramData\BusinessPost\backups\local`.

1. Restaure ese backup PRE_UPDATE con los pasos de arriba (servicio detenido) o, en el caso 22, vuelva a ejecutar el instalador: si
   termina bien, deja solos los servicios en automático.
2. Si restauró a mano, vuelva a dejar los servicios en **inicio automático** y arránquelos (consola como administrador):
   ```bash
   sc config BusinessPost-Server start= auto
   sc config BusinessPost-Updater start= auto
   sc start BusinessPost-Server
   sc start BusinessPost-Updater
   ```
3. Compruebe que `http://localhost:5480/health/ready` responde y genere un paquete de soporte para el registro del caso.

## Actualización automática desde 1.0.x/1.1.0 que falló (1.2.0)

El actualizador de una tienda 1.0.x/1.1.0 escribe en el historial *"Falló la migración de la base de datos; no se aplicó ningún cambio y
sigue la versión anterior"*. Ese texto es del actualizador viejo y no dice lo que pasó. Lo que pasó lo dice el migrador 1.2.0 en el
registro del actualizador y en `C:\ProgramData\BusinessPost\updates\legacy-migrate-failed.json`:

- **Código 9** (lo normal): la migración falló y el migrador volvió solo a los datos de antes. Restauró su backup PRE_UPDATE (en
  `backups\local`) en una base nueva `pos_rb<fecha>` y `server.json` ya apunta a ella. El servidor anterior atiende sobre esa base. La
  base a medio migrar se **renombra a `pos_failed_<fecha>`** (el nombre queda en la marca) y se guarda para revisión: no la borre. Genere
  un paquete de soporte. La versión se instala después **con el instalador**, cuando soporte lo indique.
  Con el renombre, el actualizador viejo deja de intentar cada noche: guardó al arrancar la conexión con el nombre anterior y, antes de
  cada intento, cuenta las jornadas abiertas sobre esa base. Como ya no existe, falla y no hace backup ni reinicia nada. Esto dura hasta
  que se reinicie su servicio (por ejemplo, al reiniciar Windows). Entonces lee el `server.json` nuevo, lo vuelve a intentar y el
  migrador termina con el código 10. Nada más usa el nombre anterior: `server.json` (las tres conexiones), el backup y el servidor ya
  apuntan a `pos_rb<fecha>`.
- **Código 10**: es la misma versión que ya falló sobre los mismos datos. El migrador no hizo nada: el actualizador viejo la vuelve a
  intentar cada noche, hace su backup en `updates\backups\<fecha>` (bórrelos si ocupan mucho disco) y reinicia los servicios. Se corta
  instalando la versión con el instalador.
- **Para volver a intentarlo** (cuando soporte resolvió la causa): borre `C:\ProgramData\BusinessPost\updates\legacy-migrate-failed.json`
  y use **Administración → Actualizaciones → Instalar ahora**, o ejecute el instalador. Los dos detienen los servicios antes de migrar.
  Soporte también puede correr `Pos.Server.Migrator migrate --no-backup --data-root C:\ProgramData\BusinessPost --retry-failed`, pero
  **solo con los servicios detenidos**: si `BusinessPost-Server` está corriendo, el migrador no hace nada y termina con el código **11**.
  Esto vale para cualquier `migrate --no-backup --data-root` a mano. `--no-guard` quita toda esta protección (backup propio, vuelta
  atrás, marca y revisión del servicio). Es **solo para soporte**, en un diagnóstico y con un backup hecho a mano.
- **Migra bien pero la 1.2.0 arranca enferma.** El migrador terminó con 0 y borró la marca. Después el actualizador viejo no ve sano el
  servidor nuevo dentro de su tiempo de espera y hace su propia vuelta atrás: activa la versión anterior y restaura con su migrador
  (`restore --file … --yes`) el backup que hizo en `updates\backups\<fecha>`, en una base nueva a la que pasa `server.json`. Ese backup se
  hizo **antes** de detener el servidor: se pierde lo escrito en los segundos que pasaron entre el backup y la detención (no había
  jornadas abiertas). La base migrada queda con su nombre, para revisión. El migrador 1.2.0 no se entera de que la versión arrancó
  enferma, así que no deja marca: **la noche siguiente se repite todo el ciclo** (migrar, arrancar enferma y volver atrás). Revise el
  historial de Actualizaciones y el registro del servidor (`logs`), genere un paquete de soporte e instale la 1.2.0 con el instalador
  cuando se conozca la causa. El instalador espera la salud y, si falla, deja los servicios en inicio manual (códigos 21/22, arriba).
  Este caso hay que probarlo en la VM antes de publicar el manifiesto.
- **Código 8**: no se pudo volver atrás. El servidor anterior queda en `SCHEMA_OUTDATED` (no atiende con un esquema que no es el suyo).
  Restaure el backup PRE_UPDATE más reciente de `backups\local` con los pasos de arriba. Si ese no sirve, use el de
  `updates\backups\<fecha>` que hizo el actualizador viejo antes de detener el servidor.
- **Código 6**: falló el backup previo. No se migró y la base no se tocó.

## Portal: "El paquete no está firmado con la clave registrada de esa tienda" (SYNC.WRONG_SIGNER)

Al cargar un paquete de sincronización en el portal, la nube lo rechaza si no viene firmado con la clave registrada de la tienda. No se
carga nada. Si el paquete es de esa tienda y la reinstalaron, cambió de equipo o restauró un backup en otro equipo (la clave de firma
`sync-signing.key` va con DPAPI de la máquina y se regenera), entonces:

1. En el portal, un superadministrador entra a **Tiendas → Reiniciar clave** de esa tienda. Queda auditado como `SYNC_KEY_RESET`.
2. La tienda vuelve a registrar su clave en la próxima sincronización en línea.
3. Vuelva a cargar el **mismo** archivo. Si se carga antes de que la tienda sincronice en línea, se aplica como `UNVERIFIED` (sin clave
   registrada). Si no se reconoce el paquete como de la tienda, **no** reinicie la clave: puede ser fabricado.

## Códigos de salida del migrador

| Código | Significado |
|---|---|
| 0 | Correcto |
| 1 | Backup dañado, alterado o código incorrecto |
| 2 | Falta un dato o falta `--yes` |
| 4 | La auditoría o el sello no coinciden: la BD restaurada queda para revisión |
| 6 | Falló el backup previo obligatorio (antes de `migrate`) |
| 3 | `install`: la migración falló y se volvió solo a los datos de antes (backup PRE_UPDATE restaurado) |
| 8 | `install`, o `migrate` del actualizador viejo: la migración falló y NO se pudo volver atrás: restaurar con soporte (ver arriba) |
| 9 | `migrate` del actualizador 1.0.x/1.1.0: la migración falló y se volvió solo a los datos de antes (backup PRE_UPDATE restaurado) |
| 10 | `migrate` del actualizador 1.0.x/1.1.0: esta versión ya falló sobre estos datos; no se intentó de nuevo y la BD no se tocó |
| 11 | `migrate --no-backup --data-root` (actualizador viejo o a mano) con `BusinessPost-Server` corriendo: no se hizo nada |
