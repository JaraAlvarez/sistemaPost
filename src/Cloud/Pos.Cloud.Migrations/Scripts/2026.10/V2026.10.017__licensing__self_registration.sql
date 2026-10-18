-- =====================================================================================================
-- V2026.10.017 · licensing · Registro propio desde la tienda y "Continuar con Google" sin clave (instalador 1.2.1).
--
--   · accounts.self_registered_at: la cuenta se creó sola cuando una persona sin cuenta en el portal entró con Google en el primer
--     ingreso a una tienda (cliente nuevo, en prueba). El portal la lista en "Registros nuevos" (posibles clientes).
--   · accounts.first_store_linked_at: primera vez que un usuario de la cuenta quedó como Propietario de una tienda.
--   · owner_link_requests sin clave: license_id y organization_id quedan vacíos (van juntos); account_id es la cuenta de cliente de
--     quien autorizó (vacía si no tiene, p. ej. un superadministrador).
-- =====================================================================================================

ALTER TABLE licensing.accounts
    ADD COLUMN self_registered_at     timestamptz,
    ADD COLUMN first_store_linked_at  timestamptz;

CREATE INDEX ix_accounts__self_registered_at ON licensing.accounts (self_registered_at) WHERE self_registered_at IS NOT NULL;

ALTER TABLE licensing.owner_link_requests
    ALTER COLUMN license_id DROP NOT NULL,
    ALTER COLUMN organization_id DROP NOT NULL,
    ADD COLUMN account_id uuid,
    ADD CONSTRAINT fk_owner_link_requests__account FOREIGN KEY (account_id) REFERENCES licensing.accounts (id),
    ADD CONSTRAINT ck_owner_link_requests__license_organization CHECK ((license_id IS NULL) = (organization_id IS NULL)),
    ADD CONSTRAINT ck_owner_link_requests__account CHECK (account_id IS NULL OR license_id IS NULL);

CREATE INDEX ix_owner_link_requests__account_id ON licensing.owner_link_requests (account_id) WHERE account_id IS NOT NULL;
