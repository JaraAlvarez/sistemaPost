-- =====================================================================================================
-- V2026.10.040 · catalog · Productos de báscula conectada y tara por producto (Fase 7-C, decisión del propietario).
-- Diseño: docs/fases/fase-07c-propuesta.md.
--
--   · "Se vende por báscula" (is_sold_by_scale) ahora significa que la caja lee la báscula conectada al registrar el producto. Basta
--     que se venda por peso (kg, g, libra de 500 g o libra): el PLU solo hace falta para las etiquetas de la báscula etiquetadora.
--   · tare_grams: tara del empaque (bolsa, bandeja) que se descuenta sola al leer la báscula. Solo productos por peso.
-- Numeración: main llega a V2026.10.034 y otra rama puede usar 035; esta fase usa 040 a 042 para no chocar.
-- =====================================================================================================

ALTER TABLE catalog.products DROP CONSTRAINT ck_products__scale;
ALTER TABLE catalog.products ADD CONSTRAINT ck_products__scale CHECK (NOT is_sold_by_scale OR sale_mode = 'WEIGHT');

ALTER TABLE catalog.products ADD COLUMN tare_grams integer NOT NULL DEFAULT 0;
ALTER TABLE catalog.products ADD CONSTRAINT ck_products__tare
    CHECK (tare_grams BETWEEN 0 AND 5000 AND (tare_grams = 0 OR sale_mode = 'WEIGHT'));
