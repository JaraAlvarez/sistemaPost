-- =====================================================================================================
-- V2026.10.042 · sales · De dónde salió el peso de una línea (Fase 7-C parte C).
-- Diseño: docs/fases/fase-07c-propuesta.md ("Decisión del propietario").
--
-- Nuevos orígenes de línea: SCALE_LIVE (peso leído de la báscula conectada a la caja) y MANUAL_WEIGHT (peso digitado porque la
-- báscula no respondía; exige el permiso sales.weight.manual o autorización de supervisor). Con SCALE_WEIGHT y SCALE_PRICE (etiqueta
-- de la báscula etiquetadora) cada línea responde si el peso fue de báscula, de etiqueta o digitado (auditoría y reportes).
-- =====================================================================================================

ALTER TABLE sales.sale_lines DROP CONSTRAINT ck_sale_lines__source;
ALTER TABLE sales.sale_lines ADD CONSTRAINT ck_sale_lines__source
    CHECK (source IN ('BARCODE', 'SKU', 'SCALE_WEIGHT', 'SCALE_PRICE', 'PRODUCT', 'SCALE_LIVE', 'MANUAL_WEIGHT'));
