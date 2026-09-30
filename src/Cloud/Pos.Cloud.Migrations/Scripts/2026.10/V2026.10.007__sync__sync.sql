-- =====================================================================================================
-- V2026.10.007 · sync · Datos recibidos de las tiendas (Fase 16, docs/fases/fase-16-propuesta.md D16-05).
--   · documents: el último estado de cada venta, cierre de caja, saldo de existencias y producto de cada instalación (JSON), con su
--     versión: si llega uno igual o más viejo se ignora (idempotencia: en línea y por paquete, repetido o solapado).
--   · batches: cada lote recibido (en línea o cargado en el portal) con lo aplicado. Solo inserción y su resumen.
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS sync;

CREATE TABLE sync.documents (
    installation_id  uuid          NOT NULL,
    kind             varchar(20)   NOT NULL,
    doc_id           varchar(64)   NOT NULL,
    organization_id  uuid          NOT NULL,
    version          timestamptz   NOT NULL,
    business_date    date,
    data             jsonb         NOT NULL,
    received_at      timestamptz   NOT NULL,
    received_via     varchar(10)   NOT NULL,
    CONSTRAINT pk_documents PRIMARY KEY (installation_id, kind, doc_id),
    CONSTRAINT fk_documents__organization FOREIGN KEY (organization_id) REFERENCES licensing.organizations (id),
    CONSTRAINT ck_documents__kind CHECK (kind IN ('SALE', 'CASH_SESSION', 'STOCK', 'PRODUCT')),
    CONSTRAINT ck_documents__via CHECK (received_via IN ('ONLINE', 'FILE'))
);

CREATE INDEX ix_documents__organization_kind_date ON sync.documents (organization_id, kind, business_date);
CREATE INDEX ix_documents__organization_kind_version ON sync.documents (organization_id, kind, version DESC);

COMMENT ON TABLE sync.documents IS 'Último estado de los documentos de cada tienda (Fase 16). Solo lectura para el portal.';

CREATE TABLE sync.batches (
    id               uuid          NOT NULL,
    installation_id  uuid          NOT NULL,
    organization_id  uuid          NOT NULL,
    via              varchar(10)   NOT NULL,
    received_at      timestamptz   NOT NULL,
    items            integer       NOT NULL,
    applied          integer,
    ignored          integer,
    uploaded_by      uuid,
    CONSTRAINT pk_batches PRIMARY KEY (id),
    CONSTRAINT fk_batches__organization FOREIGN KEY (organization_id) REFERENCES licensing.organizations (id),
    CONSTRAINT fk_batches__uploaded_by FOREIGN KEY (uploaded_by) REFERENCES portal.portal_users (id),
    CONSTRAINT ck_batches__via CHECK (via IN ('ONLINE', 'FILE'))
);

CREATE INDEX ix_batches__installation_received ON sync.batches (installation_id, received_at DESC);
CREATE INDEX ix_batches__organization_id ON sync.batches (organization_id);
CREATE INDEX ix_batches__uploaded_by ON sync.batches (uploaded_by) WHERE uploaded_by IS NOT NULL;

COMMENT ON TABLE sync.batches IS 'Lotes recibidos de las tiendas (en línea o paquete .possync cargado en el portal).';
