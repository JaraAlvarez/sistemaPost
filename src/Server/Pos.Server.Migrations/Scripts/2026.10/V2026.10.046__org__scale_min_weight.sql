-- =====================================================================================================
-- V2026.10.046 · org · Peso mínimo de la báscula de la caja (Fase 7-C, corrección de la revisión de la báscula).
-- Diseño: docs/fases/fase-07c-propuesta.md ("Correcciones de la báscula").
--
-- min_weight_grams: banda de cero ⚙️. Por debajo de este peso la báscula cuenta como vacía: la deriva de unos gramos (0,002 kg) no
-- se cobra y la tara manual se quita sola. Por defecto 20 g; de 0 a 1000 g. La caja lo entrega al agente con la configuración.
-- =====================================================================================================

ALTER TABLE org.terminal_scales ADD COLUMN min_weight_grams integer NOT NULL DEFAULT 20;
ALTER TABLE org.terminal_scales ADD CONSTRAINT ck_terminal_scales__min_weight CHECK (min_weight_grams BETWEEN 0 AND 1000);
