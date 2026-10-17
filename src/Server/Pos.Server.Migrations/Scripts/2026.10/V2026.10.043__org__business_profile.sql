-- =====================================================================================================
-- V2026.10.043 · org · Tipo de negocio y funciones activables (Fase 7-C parte A).
-- Diseño: docs/fases/fase-07c-propuesta.md §1 e "Informe 7-C parte A".
--
-- business_profile: SUPERMARKET, FRESH_MARKET, PHARMACY, APPAREL, HARDWARE u OTHER. NO es un plan de precios (ADR-0015).
-- enabled_features: códigos de las funciones encendidas, ordenados y separados por coma (LOTS_EXPIRY,SCALE). Apagar una función
-- solo la oculta en la interfaz: ningún dato se borra.
--
-- Compatibilidad: las empresas que ya existen quedan como SUPERMARKET con TODAS las funciones que ya veían (báscula, lotes,
-- venta por fracción, precio abierto, promociones y compras): nada desaparece al actualizar. Tallas y colores (VARIANTS) no existía.
-- =====================================================================================================

ALTER TABLE org.companies
    ADD COLUMN business_profile varchar(20) NOT NULL DEFAULT 'SUPERMARKET',
    ADD COLUMN enabled_features varchar(200) NOT NULL DEFAULT 'FRACTIONAL_SALE,LOTS_EXPIRY,OPEN_PRICE,PROMOTIONS,PURCHASING,SCALE';

ALTER TABLE org.companies
    ADD CONSTRAINT ck_companies__business_profile
        CHECK (business_profile IN ('SUPERMARKET', 'FRESH_MARKET', 'PHARMACY', 'APPAREL', 'HARDWARE', 'OTHER')),
    ADD CONSTRAINT ck_companies__enabled_features
        CHECK (enabled_features ~ '^([A-Z_]+(,[A-Z_]+)*)?$');
