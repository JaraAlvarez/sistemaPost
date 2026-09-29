-- =====================================================================================================
-- V2026.10.020 · promotions · La regla de cada tipo exige sus parámetros (Fase 7).
--
-- En V016, un CHECK que evalúa a NULL no falla: QUANTITY_PRICE sin min_quantity o PERCENT_OFF sin percent se
-- insertaban. Cada rama exige ahora sus columnas no nulas (el dominio ya lo validaba; la BD es la última defensa).
-- =====================================================================================================

ALTER TABLE promotions.promotions DROP CONSTRAINT ck_promotions__rule;

ALTER TABLE promotions.promotions ADD CONSTRAINT ck_promotions__rule CHECK (CASE type
    WHEN 'MULTI_BUY' THEN buy_quantity IS NOT NULL AND pay_quantity IS NOT NULL
        AND buy_quantity >= 2 AND pay_quantity >= 0 AND pay_quantity < buy_quantity
    WHEN 'SPECIAL_PRICE' THEN price IS NOT NULL AND price > 0
    WHEN 'PERCENT_OFF' THEN percent IS NOT NULL AND percent > 0 AND percent <= 100
    WHEN 'QUANTITY_PRICE' THEN price IS NOT NULL AND min_quantity IS NOT NULL AND price > 0 AND min_quantity > 0
    WHEN 'COMBO' THEN price IS NOT NULL AND price > 0
    ELSE false END);
