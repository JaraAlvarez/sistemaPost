-- =====================================================================================================
-- V2026.10.006 · portal · Rol CLIENTE (Fase 16, D16-06): el dueño del supermercado entra al portal y ve solo los datos de las
-- empresas de su cuenta. Como el distribuidor, va atado a una cuenta (reseller_account_id = cuenta a la que pertenece).
-- =====================================================================================================

ALTER TABLE portal.portal_users DROP CONSTRAINT ck_portal_users__role;
ALTER TABLE portal.portal_users ADD CONSTRAINT ck_portal_users__role CHECK (role IN ('SUPERADMIN', 'SUPPORT', 'RESELLER', 'CUSTOMER'));

ALTER TABLE portal.portal_users DROP CONSTRAINT ck_portal_users__reseller;
ALTER TABLE portal.portal_users ADD CONSTRAINT ck_portal_users__reseller CHECK ((role IN ('RESELLER', 'CUSTOMER')) = (reseller_account_id IS NOT NULL));

COMMENT ON COLUMN portal.portal_users.reseller_account_id IS 'Cuenta a la que está atado el usuario: distribuidor (RESELLER) o cliente (CUSTOMER).';
