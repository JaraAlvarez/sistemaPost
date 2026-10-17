-- =====================================================================================================
-- V2026.10.080 · cash · Sistecrédito: novedad posterior de un cobro en revisión e índice de la próxima consulta (revisión de código).
--
-- review_update: lo que pasó después de que el cobro entró en "Requiere revisión" (p. ej. Sistecrédito dejó de reportarlo aprobado);
-- el motivo con que entró (message) y su hora (review_required_at) no se pisan, para que el cierre de su jornada siga diciendo lo mismo.
-- ix_sistecredito_attempts__next_check_at: la revisión automática filtra en la base de datos los que todavía están en espera.
-- =====================================================================================================

ALTER TABLE cash.sistecredito_attempts ADD COLUMN review_update varchar(300);

CREATE INDEX ix_sistecredito_attempts__next_check_at ON cash.sistecredito_attempts (next_check_at) WHERE completed_sale_id IS NULL;

COMMENT ON COLUMN cash.sistecredito_attempts.review_update IS 'Novedad posterior de un cobro que ya estaba en revisión (el motivo original queda en message).';
