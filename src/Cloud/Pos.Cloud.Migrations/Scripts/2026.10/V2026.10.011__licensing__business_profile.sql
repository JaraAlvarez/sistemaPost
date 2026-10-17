-- =====================================================================================================
-- V2026.10.010 · licensing · Tipo de negocio de cada empresa (Fase 7-C parte A, docs/fases/fase-07c-propuesta.md §1).
-- La tienda lo informa al activar la licencia y en cada check-in (campo opcional de ActivationRequest y CheckinRequest).
-- El portal lo muestra en la ficha de la empresa ("Tipo de negocio") y filtra por él en Empresas. Nulo = aún no informado
-- (POS anteriores a 7-C). NO es un plan de precios (ADR-0015).
-- =====================================================================================================

ALTER TABLE licensing.organizations
    ADD COLUMN business_profile varchar(20);

ALTER TABLE licensing.organizations
    ADD CONSTRAINT ck_organizations__business_profile
        CHECK (business_profile IS NULL OR business_profile IN ('SUPERMARKET', 'FRESH_MARKET', 'PHARMACY', 'APPAREL', 'HARDWARE', 'OTHER'));

CREATE INDEX ix_organizations__business_profile ON licensing.organizations (business_profile) WHERE business_profile IS NOT NULL;
