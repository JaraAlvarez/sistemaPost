-- =====================================================================================================
-- V2026.10.048 · org · Clave de las lecturas firmadas de la báscula (Fase 7-C, segunda revisión, decisión D3).
-- Diseño: docs/fases/fase-07c-propuesta.md ("Segunda revisión de la báscula").
--
-- El agente de la caja firma cada lectura con una clave ECDSA P-256 que crea al vincular la caja; aquí se guarda solo la clave
-- PÚBLICA (SubjectPublicKeyInfo en Base64) con su identificador y la fecha: la privada nunca sale del agente (protegida con DPAPI).
-- Volver a vincular reemplaza la clave. Sin clave, el servidor no acepta pesos de la báscula de esa caja.
-- Numeración: 044 (Sistecrédito) y 047 (reposición) pueden estar en uso en otras ramas; esta corrección usa 048 y 049.
-- =====================================================================================================

ALTER TABLE org.terminal_scales ADD COLUMN reading_key_id uuid;
ALTER TABLE org.terminal_scales ADD COLUMN reading_public_key varchar(400);
ALTER TABLE org.terminal_scales ADD COLUMN reading_key_set_at timestamptz;
ALTER TABLE org.terminal_scales ADD CONSTRAINT ck_terminal_scales__reading_key
    CHECK ((reading_key_id IS NULL AND reading_public_key IS NULL AND reading_key_set_at IS NULL)
        OR (reading_key_id IS NOT NULL AND reading_public_key IS NOT NULL AND reading_key_set_at IS NOT NULL));
