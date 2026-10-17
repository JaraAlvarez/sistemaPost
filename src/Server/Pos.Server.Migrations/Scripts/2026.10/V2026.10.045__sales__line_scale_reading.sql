-- =====================================================================================================
-- V2026.10.045 · sales · Lectura de la báscula guardada en la línea (Fase 7-C, corrección de la revisión de la báscula).
-- Diseño: docs/fases/fase-07c-propuesta.md ("Correcciones de la báscula").
--
--   · scale_gross_kg: peso bruto que marcó la báscula de la caja en una línea SCALE_LIVE (antes de cualquier tara). Con weighed_at
--     el servidor rechaza cobrar dos veces la misma pesada (el producto anterior que nadie retiró de la báscula).
--   · weighed_at: cuándo registró el servidor la pesada.
--   Solo las líneas SCALE_LIVE los llevan; las demás quedan en NULL (también las anteriores a esta versión).
-- Numeración: main llega a V2026.10.043 y otra corrección puede usar la 044; esta usa la 045.
-- =====================================================================================================

ALTER TABLE sales.sale_lines ADD COLUMN scale_gross_kg numeric(12, 3);
ALTER TABLE sales.sale_lines ADD COLUMN weighed_at timestamptz;
ALTER TABLE sales.sale_lines ADD CONSTRAINT ck_sale_lines__scale_reading
    CHECK ((scale_gross_kg IS NULL AND weighed_at IS NULL) OR (source = 'SCALE_LIVE' AND scale_gross_kg > 0 AND weighed_at IS NOT NULL));
