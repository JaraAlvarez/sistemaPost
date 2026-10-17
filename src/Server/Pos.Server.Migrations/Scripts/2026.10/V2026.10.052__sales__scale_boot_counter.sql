-- =====================================================================================================
-- V2026.10.052 · sales · Número de arranque del agente de la báscula (Fase 7-C, quinta revisión, BQ3).
-- Diseño: docs/fases/fase-07c-propuesta.md ("Quinta revisión de la báscula").
--
-- El agente guarda un número de arranque que solo sube y lo firma en cada lectura (formato BP-SCALE-3). El servidor guarda el más alto
-- aceptado por caja (last_boot_counter) y rechaza lecturas de un arranque menor: un arranque anterior nunca vale, aunque no haya tenido
-- pesadas aceptadas. Las claves ya se invalidaron en la V051, así que toda caja vincula de nuevo con un agente que firma el formato 3.
-- Numeración: 044, 047 y 060 están ocupadas en otras ramas.
-- =====================================================================================================

ALTER TABLE sales.scale_interlocks ADD COLUMN last_boot_counter bigint;
ALTER TABLE sales.scale_readings ADD COLUMN boot_counter bigint NOT NULL DEFAULT 0;
