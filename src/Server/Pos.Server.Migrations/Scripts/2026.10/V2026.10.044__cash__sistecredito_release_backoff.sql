-- =====================================================================================================
-- V2026.10.044 · cash · Sistecrédito: "No usar este crédito" y espera entre consultas fallidas (revisión de código).
--
-- released_at / released_by: el supervisor autorizó no usar en la venta un crédito ya aprobado (el cliente paga con otro medio); el
-- cobro queda "Requiere revisión" para reversarlo en el portal de Sistecrédito y ya no se ofrece como pago.
-- check_failures / next_check_at: la revisión automática espera cada vez más (hasta 60 min) antes de volver a consultar un cobro que
-- no se pudo guardar, para que no ocupe siempre el primer turno.
-- =====================================================================================================

ALTER TABLE cash.sistecredito_attempts
    ADD COLUMN released_at    timestamptz,
    ADD COLUMN released_by    uuid,
    ADD COLUMN check_failures integer NOT NULL DEFAULT 0,
    ADD COLUMN next_check_at  timestamptz;

ALTER TABLE cash.sistecredito_attempts
    ADD CONSTRAINT fk_sistecredito_attempts__released_by FOREIGN KEY (released_by) REFERENCES identity.users (id),
    ADD CONSTRAINT ck_sistecredito_attempts__released CHECK ((released_at IS NULL) = (released_by IS NULL)),
    ADD CONSTRAINT ck_sistecredito_attempts__check_failures CHECK (check_failures >= 0);

CREATE INDEX ix_sistecredito_attempts__released_by ON cash.sistecredito_attempts (released_by);

COMMENT ON COLUMN cash.sistecredito_attempts.released_at IS 'Crédito aprobado que, con autorización de supervisor, no se usó en la venta: la administración lo reversa en el portal.';
COMMENT ON COLUMN cash.sistecredito_attempts.next_check_at IS 'La revisión automática no lo vuelve a consultar antes de esta hora (espera creciente tras fallos al guardar).';
