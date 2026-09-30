-- =====================================================================================================
-- V2026.10.023 · catalog · Reglas de las listas de precio (Fase 8, bloque 8.2).
-- Diseño: docs/fases/fase-08-propuesta.md §4.3, D8-10 y D8-11.
--
--   · adjustment_percent: lista derivada, % sobre la general (p. ej. Empleados −5 %); null = lista de precios fijos. Los precios
--     fijos que tenga la lista ganan sobre el porcentaje.
--   · rounding_increment: redondeo del precio derivado (por defecto $50, al más cercano).
--   · allows_promotions: las promociones aplican sobre el precio de esta lista.
-- =====================================================================================================

ALTER TABLE catalog.price_lists
    ADD COLUMN adjustment_percent numeric(5,2),
    ADD COLUMN rounding_increment numeric(19,2) NOT NULL DEFAULT 50,
    ADD COLUMN allows_promotions boolean NOT NULL DEFAULT true;

ALTER TABLE catalog.price_lists ADD CONSTRAINT ck_price_lists__adjustment
    CHECK (adjustment_percent IS NULL OR (NOT is_default AND adjustment_percent BETWEEN -90 AND 100 AND adjustment_percent <> 0));
ALTER TABLE catalog.price_lists ADD CONSTRAINT ck_price_lists__rounding CHECK (rounding_increment BETWEEN 0 AND 1000);
