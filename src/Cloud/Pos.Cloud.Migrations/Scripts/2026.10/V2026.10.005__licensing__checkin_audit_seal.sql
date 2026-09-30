-- =====================================================================================================
-- V2026.10.005 · licensing · Sello de auditoría del POS en cada check-in (Fase 12-B, docs/fases/fase-12b-propuesta.md §8).
-- Ancla externa pendiente del ADR-0048: si alguien reescribe la auditoría de la tienda, el último sello guardado aquí deja de
-- coincidir. Columnas opcionales: los POS anteriores a 12-B no envían el sello.
-- =====================================================================================================

ALTER TABLE licensing.checkins
    ADD COLUMN audit_seal_no    bigint,
    ADD COLUMN audit_seal_code  varchar(40),
    ADD COLUMN audit_sealed_at  timestamptz;
