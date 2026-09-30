-- =====================================================================================================
-- V2026.10.032 · billing · Facturación electrónica (Fase 11-B, núcleo independiente del proveedor).
-- Diseño: docs/fases/fase-11b-propuesta.md §5, D11B-01 a D11B-11 (aprobada: se construye y queda en modo OFF).
--
--   · fiscal_documents: documento soporte (compras a proveedores no obligados a facturar), origen PURCHASE, estado CANCELLED
--     (factura pendiente cancelada por la anulación de la venta antes de enviarla), reference_code ÚNICO (idempotencia del
--     proveedor: reintentar nunca duplica), rango asignado, id y estado en el proveedor, mensaje de rechazo, fecha de validación,
--     representación gráfica y los datos fiscales del adquirente corregidos por el supervisor (la venta es inmutable).
--   · fiscal_numbering_ranges: rangos autorizados por la DIAN sincronizados desde el proveedor y asignados a una sucursal (y
--     opcionalmente a una caja). Nunca se borran: se desactivan o se desasignan.
--   · provider_settings: modo de emisión (OFF / ON_REQUEST / EVERY_SALE), ambiente y credenciales CIFRADAS con DPAPI (bytes,
--     nunca en claro), resultado de la última sincronización de rangos.
--   · Índices de la cola de envío (orden de llegada entre los documentos vencidos).
-- =====================================================================================================

CREATE TABLE billing.fiscal_numbering_ranges (
    id                 uuid           NOT NULL,
    company_id         uuid           NOT NULL,
    provider           varchar(30)    NOT NULL,
    provider_range_id  varchar(60)    NOT NULL,
    document_type      varchar(20)    NOT NULL,
    prefix             varchar(10)    NOT NULL,
    range_from         bigint         NOT NULL,
    range_to           bigint         NOT NULL,
    current_number     bigint         NOT NULL,
    resolution_number  varchar(40),
    valid_from         date,
    valid_to           date,
    is_active          boolean        NOT NULL,
    branch_id          uuid,
    pos_terminal_id    uuid,
    synced_at          timestamptz    NOT NULL,
    created_at         timestamptz    NOT NULL,
    created_by         uuid           NOT NULL,
    updated_at         timestamptz,
    updated_by         uuid,
    CONSTRAINT pk_fiscal_numbering_ranges PRIMARY KEY (id),
    CONSTRAINT ux_fiscal_numbering_ranges__provider_range UNIQUE (company_id, provider, provider_range_id),
    CONSTRAINT fk_fiscal_numbering_ranges__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_fiscal_numbering_ranges__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_fiscal_numbering_ranges__terminal FOREIGN KEY (pos_terminal_id, branch_id) REFERENCES org.pos_terminals (id, branch_id),
    CONSTRAINT ck_fiscal_numbering_ranges__type CHECK (document_type IN ('INVOICE_ELECTRONIC', 'POS_ELECTRONIC', 'CREDIT_NOTE', 'SUPPORT_DOCUMENT')),
    CONSTRAINT ck_fiscal_numbering_ranges__numbers CHECK (range_from > 0 AND range_from <= range_to AND current_number BETWEEN range_from - 1 AND range_to),
    CONSTRAINT ck_fiscal_numbering_ranges__validity CHECK (valid_from IS NULL OR valid_to IS NULL OR valid_from <= valid_to),
    CONSTRAINT ck_fiscal_numbering_ranges__terminal CHECK (pos_terminal_id IS NULL OR branch_id IS NOT NULL)
);

CREATE INDEX ix_fiscal_numbering_ranges__branch_id_company_id ON billing.fiscal_numbering_ranges (branch_id, company_id);
CREATE INDEX ix_fiscal_numbering_ranges__pos_terminal_id_branch_id ON billing.fiscal_numbering_ranges (pos_terminal_id, branch_id);
-- Una asignación vigente por sucursal, caja (o toda la sucursal) y tipo de documento.
CREATE UNIQUE INDEX ux_fiscal_numbering_ranges__assignment ON billing.fiscal_numbering_ranges
    (branch_id, COALESCE(pos_terminal_id, '00000000-0000-0000-0000-000000000000'::uuid), document_type)
    WHERE branch_id IS NOT NULL AND is_active;

COMMENT ON TABLE billing.fiscal_numbering_ranges IS 'Rangos de numeración de la DIAN sincronizados desde el proveedor y asignados por sucursal/caja (D11B-05).';

CREATE TABLE billing.provider_settings (
    company_id              uuid           NOT NULL,
    provider                varchar(30)    NOT NULL,
    environment             varchar(12)    NOT NULL,
    mode                    varchar(12)    NOT NULL,
    credentials             bytea,
    credentials_updated_at  timestamptz,
    last_sync_at            timestamptz,
    last_sync_error         varchar(500),
    created_at              timestamptz    NOT NULL,
    created_by              uuid           NOT NULL,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_provider_settings PRIMARY KEY (company_id),
    CONSTRAINT fk_provider_settings__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_provider_settings__environment CHECK (environment IN ('SANDBOX', 'PRODUCTION')),
    CONSTRAINT ck_provider_settings__mode CHECK (mode IN ('OFF', 'ON_REQUEST', 'EVERY_SALE')),
    -- Sin credenciales no se enciende la facturación electrónica.
    CONSTRAINT ck_provider_settings__credentials CHECK (mode = 'OFF' OR credentials IS NOT NULL),
    CONSTRAINT ck_provider_settings__credentials_updated CHECK ((credentials IS NULL) = (credentials_updated_at IS NULL))
);

COMMENT ON TABLE billing.provider_settings IS 'Modo de emisión, ambiente y credenciales del proveedor cifradas con DPAPI (D11B-01, D11B-08).';
COMMENT ON COLUMN billing.provider_settings.credentials IS 'Credenciales cifradas con DPAPI del equipo (bytes). Nunca en claro; la API nunca las devuelve.';

-- Documentos fiscales: documento soporte, estados y datos del proveedor.
ALTER TABLE billing.fiscal_documents
    ADD COLUMN reference_code        varchar(60),
    ADD COLUMN numbering_range_id    uuid,
    ADD COLUMN provider_document_id  varchar(80),
    ADD COLUMN provider_status       varchar(40),
    ADD COLUMN rejection_message     varchar(2000),
    ADD COLUMN validated_at          timestamptz,
    ADD COLUMN pdf_url               varchar(500),
    ADD COLUMN buyer_fiscal          jsonb;

-- Los documentos electrónicos anteriores (si los hubiera) toman su referencia del origen.
UPDATE billing.fiscal_documents
SET reference_code = CASE source
        WHEN 'SALE' THEN replace(source_id::text, '-', '')
        WHEN 'SALE_VOID' THEN 'NCA' || replace(source_id::text, '-', '')
        ELSE 'NCD' || replace(source_id::text, '-', '') END
WHERE document_type <> 'INTERNAL_RECEIPT';

ALTER TABLE billing.fiscal_documents DROP CONSTRAINT ck_fiscal_documents__source;
ALTER TABLE billing.fiscal_documents DROP CONSTRAINT ck_fiscal_documents__type;
ALTER TABLE billing.fiscal_documents DROP CONSTRAINT ck_fiscal_documents__status;

ALTER TABLE billing.fiscal_documents
    ADD CONSTRAINT ck_fiscal_documents__source CHECK (source IN ('SALE', 'SALE_VOID', 'CUSTOMER_RETURN', 'PURCHASE')),
    ADD CONSTRAINT ck_fiscal_documents__type CHECK (document_type IN (
        'INTERNAL_RECEIPT', 'POS_ELECTRONIC', 'INVOICE_ELECTRONIC', 'CREDIT_NOTE', 'SUPPORT_DOCUMENT')),
    ADD CONSTRAINT ck_fiscal_documents__status CHECK (status IN (
        'NOT_REQUIRED', 'PENDING', 'SUBMITTING', 'ACCEPTED', 'REJECTED', 'CONTINGENCY', 'ERROR', 'VOIDED', 'CANCELLED')),
    ADD CONSTRAINT ux_fiscal_documents__reference_code UNIQUE (reference_code),
    ADD CONSTRAINT fk_fiscal_documents__numbering_range FOREIGN KEY (numbering_range_id) REFERENCES billing.fiscal_numbering_ranges (id),
    -- Todo documento electrónico lleva su código de referencia (idempotencia del proveedor, RN-FE-02).
    ADD CONSTRAINT ck_fiscal_documents__reference CHECK (document_type = 'INTERNAL_RECEIPT' OR reference_code IS NOT NULL),
    -- El documento soporte es exclusivo de las compras.
    ADD CONSTRAINT ck_fiscal_documents__support CHECK ((source = 'PURCHASE') = (document_type = 'SUPPORT_DOCUMENT')),
    -- Aceptado = con número fiscal y CUFE/CUDE/CUDS; rechazado = con el mensaje del rechazo.
    ADD CONSTRAINT ck_fiscal_documents__accepted CHECK (status <> 'ACCEPTED' OR (fiscal_number IS NOT NULL AND cufe IS NOT NULL AND validated_at IS NOT NULL)),
    ADD CONSTRAINT ck_fiscal_documents__rejected CHECK (status <> 'REJECTED' OR rejection_message IS NOT NULL),
    ADD CONSTRAINT ck_fiscal_documents__cancelled CHECK (status <> 'CANCELLED' OR document_type <> 'INTERNAL_RECEIPT');

CREATE INDEX ix_fiscal_documents__numbering_range_id ON billing.fiscal_documents (numbering_range_id);

-- Cola de envío: los documentos vencidos en orden de llegada (incluye los reclamados por un envío que no terminó).
DROP INDEX billing.ix_fiscal_documents__pending;
CREATE INDEX ix_fiscal_documents__queue ON billing.fiscal_documents (next_attempt_at, issued_at)
    WHERE status IN ('PENDING', 'ERROR', 'CONTINGENCY', 'SUBMITTING');
-- Alertas: rechazos sin atender y pendientes antiguos.
CREATE INDEX ix_fiscal_documents__attention ON billing.fiscal_documents (company_id, status, issued_at)
    WHERE status IN ('REJECTED', 'PENDING', 'ERROR', 'CONTINGENCY', 'SUBMITTING');

COMMENT ON COLUMN billing.fiscal_documents.reference_code IS 'Código de referencia ante el proveedor (id del origen): reintentar nunca duplica (RN-FE-02).';
COMMENT ON COLUMN billing.fiscal_documents.buyer_fiscal IS 'Datos fiscales del adquirente corregidos por el supervisor tras un rechazo (la venta no se modifica).';
