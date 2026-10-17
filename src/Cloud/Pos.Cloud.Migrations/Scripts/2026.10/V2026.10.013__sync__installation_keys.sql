-- =====================================================================================================
-- V2026.10.013 · sync · Firma de los paquetes .possync por la tienda (revisión S2, docs/fases/fase-sync-pagos-endurecimiento.md §1).
--
--   · installation_keys: clave pública Ed25519 de cada instalación. La registra (o la rota) la sincronización EN LÍNEA, que ya está
--     autenticada con el token de licencia y la huella del equipo, junto con una prueba de posesión de la clave privada. Al rotar, la
--     anterior queda en previous_key y se acepta 7 días más (paquetes exportados antes del cambio). Un administrador puede REINICIAR la
--     clave (public_key y previous_key en NULL, reset_at): la próxima sincronización en línea registra una nueva.
--   · batches.signature_status: cómo llegó un paquete cargado en el portal. VERIFIED = firmado con la clave registrada; UNVERIFIED = la
--     instalación aún no tenía clave (se aplica por compatibilidad y queda marcado); DISCARDED = un administrador lo descartó y se
--     borraron los documentos que ese lote dejó. NULL = lote en línea. signer_key: la clave que firmó el paquete (v2), para verificarlo
--     cuando la instalación registre la suya.
--   · documents.batch_id: último lote que escribió el documento. Cuando un lote UNVERIFIED reemplaza un documento, el estado anterior queda
--     en previous_* : descartar el lote lo RESTAURA (o borra el documento si ese lote lo creó). Un lote verificado o en línea posterior
--     limpia previous_*.
-- =====================================================================================================

CREATE TABLE sync.installation_keys (
    installation_id  uuid          NOT NULL,
    organization_id  uuid          NOT NULL,
    public_key       varchar(64),
    registered_at    timestamptz   NOT NULL,
    updated_at       timestamptz   NOT NULL,
    previous_key     varchar(64),
    rotated_at       timestamptz,
    reset_at         timestamptz,
    reset_by         uuid,
    CONSTRAINT pk_installation_keys PRIMARY KEY (installation_id),
    CONSTRAINT fk_installation_keys__organization FOREIGN KEY (organization_id) REFERENCES licensing.organizations (id),
    CONSTRAINT fk_installation_keys__reset_by FOREIGN KEY (reset_by) REFERENCES portal.portal_users (id),
    CONSTRAINT ck_installation_keys__public_key CHECK (public_key IS NULL OR public_key ~ '^[A-Za-z0-9_-]{43}$'),
    CONSTRAINT ck_installation_keys__previous_key CHECK (previous_key IS NULL OR (previous_key ~ '^[A-Za-z0-9_-]{43}$' AND rotated_at IS NOT NULL))
);

CREATE INDEX ix_installation_keys__organization_id ON sync.installation_keys (organization_id);
CREATE INDEX ix_installation_keys__reset_by ON sync.installation_keys (reset_by) WHERE reset_by IS NOT NULL;

COMMENT ON TABLE sync.installation_keys IS 'Clave pública de firma de paquetes .possync de cada instalación (registrada por la sincronización en línea).';

ALTER TABLE sync.batches
    ADD COLUMN signature_status varchar(10),
    ADD COLUMN signer_key       varchar(64),
    ADD COLUMN reviewed_at      timestamptz,
    ADD COLUMN reviewed_by      uuid,
    ADD CONSTRAINT ck_batches__signature_status CHECK (signature_status IS NULL OR signature_status IN ('VERIFIED', 'UNVERIFIED', 'DISCARDED')),
    ADD CONSTRAINT fk_batches__reviewed_by FOREIGN KEY (reviewed_by) REFERENCES portal.portal_users (id);

CREATE INDEX ix_batches__unverified ON sync.batches (organization_id, received_at DESC) WHERE signature_status = 'UNVERIFIED';
CREATE INDEX ix_batches__reviewed_by ON sync.batches (reviewed_by) WHERE reviewed_by IS NOT NULL;

COMMENT ON COLUMN sync.batches.signature_status IS
    'Paquetes cargados en el portal: VERIFIED, UNVERIFIED (la instalación aún no registraba clave) o DISCARDED (descartado por un administrador).';

ALTER TABLE sync.documents
    ADD COLUMN batch_id                uuid,
    ADD COLUMN previous_version        timestamptz,
    ADD COLUMN previous_business_date  date,
    ADD COLUMN previous_data           jsonb,
    ADD COLUMN previous_received_at    timestamptz,
    ADD COLUMN previous_received_via   varchar(10),
    ADD COLUMN previous_batch_id       uuid,
    ADD CONSTRAINT ck_documents__previous CHECK (
        (previous_data IS NULL) = (previous_version IS NULL) AND (previous_data IS NULL) = (previous_received_at IS NULL)
        AND (previous_data IS NULL) = (previous_received_via IS NULL));

CREATE INDEX ix_documents__batch ON sync.documents (batch_id) WHERE batch_id IS NOT NULL;
