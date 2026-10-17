-- =====================================================================================================
-- V2026.10.053 · sales · Retorno a cero ligado a la clave de la báscula (Fase 7-C, sexta revisión, BS1).
-- Diseño: docs/fases/fase-07c-propuesta.md ("Sexta revisión de la báscula").
--
-- last_key_id: clave con la que se aceptó la última lectura de la caja. El número de arranque más alto y los arranques retirados valen
-- solo para esa clave: al volver a vincular (otra clave) se olvidan. Así reinstalar el agente, cambiar el computador o restaurar una
-- imagen (el número de arranque vuelve a 1) nunca deja la caja trabada: basta con volver a vincular. El último arranque y su secuencia se
-- conservan: volver a vincular no salta el retorno a cero.
-- Numeración: 044 a 052, 060, 080, 090, 100 y 101 (fix-sync-pagos) están ocupadas.
-- =====================================================================================================

ALTER TABLE sales.scale_interlocks ADD COLUMN last_key_id uuid;
