-- =====================================================================================================
-- V2026.10.018 · billing · Comprobantes y documentos fiscales (Fase 7, bloque 7.2).
-- Diseño: docs/04-base-de-datos.md §H.11 con los cambios de docs/fases/fase-07-propuesta.md §4.3 y D7-12.
--
-- Principios:
--   · Venta ≠ documento fiscal: la venta nunca espera al proveedor.
--   · Hoy cada venta genera un comprobante interno (INTERNAL_RECEIPT, NOT_REQUIRED); con la facturación electrónica
--     (Fase 11-B, Factus) nace PENDING y el proveedor asigna número fiscal, CUFE/CUDE y QR.
--   · Los eventos de un documento son de SOLO INSERCIÓN.
--   · Los rangos de numeración autorizados por la DIAN se agregan con Factus (11-B).
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS billing;

CREATE TABLE billing.fiscal_documents (
    id                         uuid           NOT NULL,
    company_id                 uuid           NOT NULL,
    branch_id                  uuid           NOT NULL,
    pos_terminal_id            uuid,
    source                     varchar(20)    NOT NULL,
    source_id                  uuid           NOT NULL,
    source_number              varchar(40)    NOT NULL,
    document_type              varchar(20)    NOT NULL,
    status                     varchar(15)    NOT NULL,
    provider                   varchar(30),
    fiscal_number              varchar(40),
    cufe                       varchar(120),
    qr_data                    varchar(1000),
    attempts                   integer        NOT NULL DEFAULT 0,
    next_attempt_at            timestamptz,
    related_document_id        uuid,
    business_date              date           NOT NULL,
    buyer_name                 varchar(200)   NOT NULL,
    buyer_identification_type  varchar(5)     NOT NULL,
    buyer_identification       varchar(30)    NOT NULL,
    buyer_email                varchar(254),
    subtotal                   numeric(19,2)  NOT NULL,
    tax_total                  numeric(19,2)  NOT NULL,
    total                      numeric(19,2)  NOT NULL,
    issued_at                  timestamptz    NOT NULL,
    created_at                 timestamptz    NOT NULL,
    created_by                 uuid           NOT NULL,
    updated_at                 timestamptz,
    updated_by                 uuid,
    CONSTRAINT pk_fiscal_documents PRIMARY KEY (id),
    CONSTRAINT ux_fiscal_documents__source UNIQUE (source, source_id),
    CONSTRAINT fk_fiscal_documents__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_fiscal_documents__terminal FOREIGN KEY (pos_terminal_id, branch_id) REFERENCES org.pos_terminals (id, branch_id),
    CONSTRAINT fk_fiscal_documents__related FOREIGN KEY (related_document_id) REFERENCES billing.fiscal_documents (id),
    CONSTRAINT ck_fiscal_documents__source CHECK (source IN ('SALE', 'SALE_VOID', 'CUSTOMER_RETURN')),
    CONSTRAINT ck_fiscal_documents__type CHECK (document_type IN ('INTERNAL_RECEIPT', 'POS_ELECTRONIC', 'INVOICE_ELECTRONIC', 'CREDIT_NOTE')),
    CONSTRAINT ck_fiscal_documents__status CHECK (status IN (
        'NOT_REQUIRED', 'PENDING', 'SUBMITTING', 'ACCEPTED', 'REJECTED', 'CONTINGENCY', 'ERROR', 'VOIDED')),
    CONSTRAINT ck_fiscal_documents__internal CHECK (document_type <> 'INTERNAL_RECEIPT' OR status IN ('NOT_REQUIRED', 'VOIDED')),
    CONSTRAINT ck_fiscal_documents__amounts CHECK (subtotal >= 0 AND tax_total >= 0 AND total >= 0 AND attempts >= 0)
);

CREATE INDEX ix_fiscal_documents__branch_id_company_id ON billing.fiscal_documents (branch_id, company_id);
CREATE INDEX ix_fiscal_documents__pos_terminal_id_branch_id ON billing.fiscal_documents (pos_terminal_id, branch_id);
CREATE INDEX ix_fiscal_documents__related_document_id ON billing.fiscal_documents (related_document_id);
CREATE INDEX ix_fiscal_documents__source_id ON billing.fiscal_documents (source_id);
CREATE INDEX ix_fiscal_documents__pending ON billing.fiscal_documents (next_attempt_at) WHERE status IN ('PENDING', 'ERROR');
CREATE INDEX ix_fiscal_documents__business_date ON billing.fiscal_documents (branch_id, business_date);

CREATE TABLE billing.fiscal_document_events (
    id                  uuid           NOT NULL,
    fiscal_document_id  uuid           NOT NULL,
    event_type          varchar(30)    NOT NULL,
    detail              varchar(1000),
    provider_code       varchar(30),
    occurred_at         timestamptz    NOT NULL,
    user_id             uuid,
    CONSTRAINT pk_fiscal_document_events PRIMARY KEY (id),
    CONSTRAINT fk_fiscal_document_events__document FOREIGN KEY (fiscal_document_id) REFERENCES billing.fiscal_documents (id),
    CONSTRAINT fk_fiscal_document_events__user FOREIGN KEY (user_id) REFERENCES identity.users (id)
);

CREATE INDEX ix_fiscal_document_events__fiscal_document_id ON billing.fiscal_document_events (fiscal_document_id);
CREATE INDEX ix_fiscal_document_events__user_id ON billing.fiscal_document_events (user_id);

CREATE TRIGGER trg_fiscal_document_events_append_only
    BEFORE UPDATE OR DELETE ON billing.fiscal_document_events
    FOR EACH ROW EXECUTE FUNCTION inventory.fn_append_only();

CREATE TRIGGER trg_fiscal_document_events_no_truncate
    BEFORE TRUNCATE ON billing.fiscal_document_events
    FOR EACH STATEMENT EXECUTE FUNCTION inventory.fn_append_only();
