-- =====================================================================================================
-- V2026.10.050 · sales · Orden de las lecturas de la báscula sin depender de la hora (Fase 7-C, tercera revisión, BB4 y BB10).
-- Diseño: docs/fases/fase-07c-propuesta.md ("Tercera revisión de la báscula").
--
--   · El agente numera sus lecturas: arranque (boot_id, cambia cada vez que arranca) y secuencia (seq, solo crece). El retorno a cero
--     compara esa secuencia, no la hora: un salto del reloj del equipo ya no deja la caja trabada.
--   · last_purge_at: última limpieza de las lecturas de más de 90 días de la caja (se hace sola al pesar, una vez al día).
-- Numeración: 044 y 047 están ocupadas en otras ramas.
-- =====================================================================================================

ALTER TABLE sales.scale_interlocks ADD COLUMN last_boot_id uuid;
ALTER TABLE sales.scale_interlocks ADD COLUMN last_seq bigint;
ALTER TABLE sales.scale_interlocks ADD COLUMN last_purge_at timestamptz;

ALTER TABLE sales.scale_readings ADD COLUMN boot_id uuid;
ALTER TABLE sales.scale_readings ADD COLUMN seq bigint;
UPDATE sales.scale_readings SET boot_id = '00000000-0000-0000-0000-000000000000', seq = 0 WHERE boot_id IS NULL;
ALTER TABLE sales.scale_readings ALTER COLUMN boot_id SET NOT NULL;
ALTER TABLE sales.scale_readings ALTER COLUMN seq SET NOT NULL;
CREATE INDEX ix_scale_readings__recorded_at ON sales.scale_readings (pos_terminal_id, recorded_at);
