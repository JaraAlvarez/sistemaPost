-- =====================================================================================================
-- V2026.10.051 · sales/org · Báscula: arranques retirados del agente y claves anteriores invalidadas (Fase 7-C, cuarta revisión).
-- Diseño: docs/fases/fase-07c-propuesta.md ("Cuarta revisión de la báscula").
--
--   · retired_boot_ids (BK2): arranques del agente reemplazados por uno posterior (con una pesada aceptada). Una lectura firmada por un
--     arranque retirado nunca vale. Se guardan los últimos 20 por caja.
--   · BK3: las claves de báscula registradas antes de esta versión se invalidan (el agente las guardaba con DPAPI de máquina y una
--     entropía fija, y ahora las borra al arrancar). Cada caja vuelve a vincular con F1 → Vincular báscula; mientras tanto la caja avisa
--     "Báscula no vinculada" y el Tablero lo muestra.
-- Numeración: 044 y 047 están ocupadas en otras ramas.
-- =====================================================================================================

ALTER TABLE sales.scale_interlocks ADD COLUMN retired_boot_ids uuid[] NOT NULL DEFAULT '{}';

UPDATE org.terminal_scales SET reading_key_id = NULL, reading_public_key = NULL, reading_key_set_at = NULL WHERE reading_key_id IS NOT NULL;
