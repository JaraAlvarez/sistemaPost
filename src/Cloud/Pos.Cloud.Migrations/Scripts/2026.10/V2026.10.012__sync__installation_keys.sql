-- =====================================================================================================
-- V2026.10.012 · sync · Firma de los paquetes .possync por la tienda (revisión S2, docs/fases/fase-sync-pagos-endurecimiento.md §1).
--
--   · installation_keys: clave pública Ed25519 de cada instalación. La registra (o la cambia) la sincronización EN LÍNEA, que ya está
--     autenticada con el token de licencia y la huella del equipo, junto con una prueba de posesión de la clave privada.
--   · batches.signature_status: cómo llegó un paquete cargado en el portal. VERIFIED = firmado con la clave registrada; UNVERIFIED =
--     la instalación aún no había registrado clave (se acepta por compatibilidad y queda marcado). NULL = lote en línea.
-- =====================================================================================================

CREATE TABLE sync.installation_keys (
    installation_id  uuid          NOT NULL,
    organization_id  uuid          NOT NULL,
    public_key       varchar(64)   NOT NULL,
    registered_at    timestamptz   NOT NULL,
    updated_at       timestamptz   NOT NULL,
    previous_key     varchar(64),
    CONSTRAINT pk_installation_keys PRIMARY KEY (installation_id),
    CONSTRAINT fk_installation_keys__organization FOREIGN KEY (organization_id) REFERENCES licensing.organizations (id),
    CONSTRAINT ck_installation_keys__public_key CHECK (public_key ~ '^[A-Za-z0-9_-]{43}$')
);

CREATE INDEX ix_installation_keys__organization_id ON sync.installation_keys (organization_id);

COMMENT ON TABLE sync.installation_keys IS 'Clave pública de firma de paquetes .possync de cada instalación (registrada por la sincronización en línea).';

ALTER TABLE sync.batches
    ADD COLUMN signature_status varchar(10),
    ADD CONSTRAINT ck_batches__signature_status CHECK (signature_status IS NULL OR signature_status IN ('VERIFIED', 'UNVERIFIED'));

COMMENT ON COLUMN sync.batches.signature_status IS
    'Paquetes cargados en el portal: VERIFIED (firma de la clave registrada) o UNVERIFIED (la instalación aún no registraba clave).';
