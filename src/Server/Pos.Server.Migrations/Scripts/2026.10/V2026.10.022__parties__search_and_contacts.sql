-- =====================================================================================================
-- V2026.10.022 · parties · Búsqueda por teléfono y rol de los contactos (Fase 8).
-- Diseño: docs/fases/fase-08-propuesta.md §4.2 y D8-03.
--
--   · phone_digits: solo los dígitos del teléfono (columna generada) para buscar al cliente por celular en la caja.
--   · contact_role: para qué se le escribe a un contacto del proveedor (vendedor, cartera, logística).
-- =====================================================================================================

ALTER TABLE parties.parties
    ADD COLUMN phone_digits varchar(30) GENERATED ALWAYS AS (NULLIF(regexp_replace(COALESCE(phone, ''), '[^0-9]', '', 'g'), '')) STORED;

CREATE INDEX ix_parties__phone_digits ON parties.parties (company_id, phone_digits text_pattern_ops) WHERE phone_digits IS NOT NULL;
CREATE INDEX ix_parties__identification_prefix ON parties.parties (company_id, identification_number text_pattern_ops);

ALTER TABLE parties.party_contacts ADD COLUMN contact_role varchar(15) NOT NULL DEFAULT 'OTHER';
ALTER TABLE parties.party_contacts ADD CONSTRAINT ck_party_contacts__role CHECK (contact_role IN ('OTHER', 'SALES', 'COLLECTIONS', 'LOGISTICS'));
