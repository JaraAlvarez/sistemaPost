# ADR-0010 · Migraciones SQL-first con migrador propio

- **Estado:** Aceptada · 2026-09-28 · Fase 2

## Contexto
El esquema usa lo que las migraciones de EF Core no generan bien: CHECK con nombre, índices parciales, particiones, triggers, FK compuestas y diferibles, funciones `SECURITY DEFINER` y privilegios por rol. Además, el instalador y el actualizador deben migrar en el equipo del cliente sin herramientas de desarrollo.

## Decisión
- Scripts `.sql` escritos a mano, incrustados en `Pos.Server.Migrations`: versionados `V{AAAA.MM.NNN}__{módulo}__{descripción}.sql`, repetibles `R__…` (datos de referencia idempotentes) y `A__…` (se ejecutan al final de cada migración: privilegios).
- Cada versionado corre en **su propia transacción**; su **checksum** (SHA-256, sin BOM ni CRLF) es inmutable: si cambia, el migrador se niega (`MIGRATION.CHECKSUM_MISMATCH`). Bloqueo consultivo para que dos procesos no migren a la vez. Una BD más nueva que la aplicación se rechaza (`MIGRATION.DATABASE_NEWER`).
- El migrador se conecta como `pos_migrator` y crea los objetos como `pos_owner` (`SET ROLE`).
- El servidor **no migra en producción**: si la versión no coincide, responde `503 SYSTEM.SCHEMA_OUTDATED`. En desarrollo, `MigrateOnStartup=true`.
- Una prueba de **conformidad** compara el modelo de EF con el esquema real (tablas, columnas, familia de tipo y nulabilidad).

## Consecuencias
- ✅ DDL revisable, determinista y completo; el actualizador solo necesita el ejecutable del migrador.
- ✅ La base de datos es la tercera línea de defensa (restricciones con nombre traducidas a errores de negocio).
- ⚠️ El mapeo de EF se mantiene a mano; lo protege la prueba de conformidad.
