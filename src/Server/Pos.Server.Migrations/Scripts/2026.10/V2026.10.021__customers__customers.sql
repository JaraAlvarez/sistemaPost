-- =====================================================================================================
-- V2026.10.021 · customers · Clientes, grupos y privacidad de datos (Fase 8, bloque 8.1).
-- Diseño: docs/fases/fase-08-propuesta.md §4.1, D8-01, D8-02, D8-06, D8-08, D8-15 y D8-16.
--
-- Principios:
--   · Módulo dueño del rol cliente; parties sigue siendo solo la identidad (D8-01).
--   · El rol usa LA MISMA clave del tercero (party_id): dos tiendas que lo crean sin conexión convergen (D8-02).
--   · Autorizaciones de tratamiento de datos de SOLO INSERCIÓN, con versión de la política, canal y evidencia (D8-06).
--   · Solicitudes de titulares con vencimiento en días hábiles (Ley 1581, arts. 14–15).
--   · Crédito y puntos: columnas reservadas en NONE hasta la Fase 8-B (D8-15, D8-16).
--   · Las FK al tercero son diferidas: el alta rápida crea tercero, rol y autorizaciones en la misma transacción.
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS customers;

CREATE TABLE customers.customer_groups (
    id             uuid          NOT NULL,
    company_id     uuid          NOT NULL,
    code           varchar(20)   NOT NULL,
    name           varchar(80)   NOT NULL,
    price_list_id  uuid,
    is_default     boolean       NOT NULL DEFAULT false,
    status         varchar(10)   NOT NULL,
    row_version    bigint        NOT NULL DEFAULT 1,
    created_at     timestamptz   NOT NULL,
    created_by     uuid          NOT NULL,
    updated_at     timestamptz,
    updated_by     uuid,
    deleted_at     timestamptz,
    deleted_by     uuid,
    CONSTRAINT pk_customer_groups PRIMARY KEY (id),
    CONSTRAINT fk_customer_groups__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_customer_groups__price_list FOREIGN KEY (price_list_id, company_id) REFERENCES catalog.price_lists (id, company_id),
    CONSTRAINT ck_customer_groups__code CHECK (code ~ '^[A-Z0-9_]{2,20}$'),
    CONSTRAINT ck_customer_groups__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_customer_groups__default_active CHECK (NOT is_default OR status = 'ACTIVE'),
    CONSTRAINT ck_customer_groups__row_version CHECK (row_version > 0),
    CONSTRAINT ck_customer_groups__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_customer_groups__code ON customers.customer_groups (company_id, code) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX ux_customer_groups__default ON customers.customer_groups (company_id) WHERE is_default AND deleted_at IS NULL;
CREATE INDEX ix_customer_groups__price_list_id_company_id ON customers.customer_groups (price_list_id, company_id);

CREATE TABLE customers.customers (
    party_id                 uuid           NOT NULL,
    company_id               uuid           NOT NULL,
    group_id                 uuid           NOT NULL,
    price_list_id            uuid,
    status                   varchar(10)    NOT NULL,
    block_reason             varchar(300),
    origin                   varchar(15)    NOT NULL,
    created_branch_id        uuid,
    always_requests_invoice  boolean        NOT NULL DEFAULT false,
    service_consent          boolean        NOT NULL DEFAULT false,
    marketing_consent        boolean        NOT NULL DEFAULT false,
    marketing_channels       varchar(60),
    consent_policy_version   integer,
    consent_updated_at       timestamptz,
    anonymized_at            timestamptz,
    credit_status            varchar(10)    NOT NULL DEFAULT 'NONE',
    credit_limit             numeric(19,2),
    credit_term_days         integer,
    loyalty_status           varchar(10)    NOT NULL DEFAULT 'NONE',
    row_version              bigint         NOT NULL DEFAULT 1,
    created_at               timestamptz    NOT NULL,
    created_by               uuid           NOT NULL,
    updated_at               timestamptz,
    updated_by               uuid,
    CONSTRAINT pk_customers PRIMARY KEY (party_id),
    CONSTRAINT fk_customers__party FOREIGN KEY (party_id, company_id) REFERENCES parties.parties (id, company_id) DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT fk_customers__group FOREIGN KEY (group_id) REFERENCES customers.customer_groups (id),
    CONSTRAINT fk_customers__price_list FOREIGN KEY (price_list_id, company_id) REFERENCES catalog.price_lists (id, company_id),
    CONSTRAINT fk_customers__branch FOREIGN KEY (created_branch_id) REFERENCES org.branches (id),
    CONSTRAINT ck_customers__status CHECK (status IN ('ACTIVE', 'INACTIVE', 'BLOCKED')),
    CONSTRAINT ck_customers__blocked CHECK ((status = 'BLOCKED') = (block_reason IS NOT NULL)),
    CONSTRAINT ck_customers__origin CHECK (origin IN ('POS_QUICK', 'BACKOFFICE', 'AUTO_ON_SALE', 'IMPORT')),
    CONSTRAINT ck_customers__marketing CHECK (marketing_consent OR marketing_channels IS NULL),
    CONSTRAINT ck_customers__anonymized CHECK (anonymized_at IS NULL OR (status = 'INACTIVE' AND NOT service_consent AND NOT marketing_consent)),
    -- Crédito y puntos reservados (8-B): hoy solo NONE y sin cupo.
    CONSTRAINT ck_customers__credit CHECK (credit_status IN ('NONE') AND credit_limit IS NULL AND credit_term_days IS NULL),
    CONSTRAINT ck_customers__loyalty CHECK (loyalty_status IN ('NONE')),
    CONSTRAINT ck_customers__row_version CHECK (row_version > 0)
);

CREATE INDEX ix_customers__party_id_company_id ON customers.customers (party_id, company_id);
CREATE INDEX ix_customers__group_id ON customers.customers (group_id);
CREATE INDEX ix_customers__price_list_id_company_id ON customers.customers (price_list_id, company_id);
CREATE INDEX ix_customers__created_branch_id ON customers.customers (created_branch_id);
CREATE INDEX ix_customers__without_consent ON customers.customers (company_id) WHERE NOT service_consent AND anonymized_at IS NULL;

-- Política de tratamiento de datos: versiones que nunca se editan (una activa a la vez).
CREATE TABLE customers.privacy_policies (
    id            uuid           NOT NULL,
    company_id    uuid           NOT NULL,
    version       integer        NOT NULL,
    text          text           NOT NULL,
    short_notice  varchar(600)   NOT NULL,
    text_hash     char(64)       NOT NULL,
    status        varchar(15)    NOT NULL,
    activated_at  timestamptz,
    activated_by  uuid,
    created_at    timestamptz    NOT NULL,
    created_by    uuid           NOT NULL,
    updated_at    timestamptz,
    updated_by    uuid,
    CONSTRAINT pk_privacy_policies PRIMARY KEY (id),
    CONSTRAINT ux_privacy_policies__version UNIQUE (company_id, version),
    CONSTRAINT fk_privacy_policies__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_privacy_policies__activated_by FOREIGN KEY (activated_by) REFERENCES identity.users (id),
    CONSTRAINT ck_privacy_policies__status CHECK (status IN ('PENDING_REVIEW', 'ACTIVE', 'RETIRED')),
    CONSTRAINT ck_privacy_policies__active CHECK (status = 'PENDING_REVIEW' OR activated_at IS NOT NULL OR status = 'RETIRED'),
    CONSTRAINT ck_privacy_policies__version CHECK (version > 0)
);

CREATE UNIQUE INDEX ux_privacy_policies__active ON customers.privacy_policies (company_id) WHERE status = 'ACTIVE';
CREATE INDEX ix_privacy_policies__activated_by ON customers.privacy_policies (activated_by);

-- Autorizaciones (SOLO INSERCIÓN): la prueba de qué aceptó el titular, cuándo, por qué canal y con qué política.
CREATE TABLE customers.customer_consents (
    id                  uuid          NOT NULL,
    company_id          uuid          NOT NULL,
    party_id            uuid          NOT NULL,
    purpose             varchar(10)   NOT NULL,
    granted             boolean       NOT NULL,
    channel             varchar(12)   NOT NULL,
    marketing_channels  varchar(60),
    policy_id           uuid          NOT NULL,
    policy_version      integer       NOT NULL,
    evidence            varchar(200),
    user_id             uuid          NOT NULL,
    branch_id           uuid,
    pos_terminal_id     uuid,
    node_id             uuid          NOT NULL,
    occurred_at         timestamptz   NOT NULL,
    CONSTRAINT pk_customer_consents PRIMARY KEY (id),
    CONSTRAINT fk_customer_consents__party FOREIGN KEY (party_id, company_id) REFERENCES parties.parties (id, company_id) DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT fk_customer_consents__policy FOREIGN KEY (policy_id) REFERENCES customers.privacy_policies (id),
    CONSTRAINT fk_customer_consents__user FOREIGN KEY (user_id) REFERENCES identity.users (id),
    CONSTRAINT fk_customer_consents__branch FOREIGN KEY (branch_id) REFERENCES org.branches (id),
    CONSTRAINT fk_customer_consents__terminal FOREIGN KEY (pos_terminal_id) REFERENCES org.pos_terminals (id),
    CONSTRAINT ck_customer_consents__purpose CHECK (purpose IN ('SERVICE', 'MARKETING', 'LOYALTY')),
    CONSTRAINT ck_customer_consents__channel CHECK (channel IN ('POS_VERBAL', 'POS_SIGNED', 'PAPER_FORM', 'WEB', 'PHONE', 'EMAIL', 'IMPORT')),
    CONSTRAINT ck_customer_consents__marketing CHECK (marketing_channels IS NULL OR (purpose = 'MARKETING' AND granted))
);

CREATE INDEX ix_customer_consents__party_id_company_id ON customers.customer_consents (party_id, company_id, occurred_at);
CREATE INDEX ix_customer_consents__policy_id ON customers.customer_consents (policy_id);
CREATE INDEX ix_customer_consents__user_id ON customers.customer_consents (user_id);
CREATE INDEX ix_customer_consents__branch_id ON customers.customer_consents (branch_id);
CREATE INDEX ix_customer_consents__pos_terminal_id ON customers.customer_consents (pos_terminal_id);

CREATE TRIGGER trg_customer_consents_append_only
    BEFORE UPDATE OR DELETE ON customers.customer_consents
    FOR EACH ROW EXECUTE FUNCTION inventory.fn_append_only();

CREATE TRIGGER trg_customer_consents_no_truncate
    BEFORE TRUNCATE ON customers.customer_consents
    FOR EACH STATEMENT EXECUTE FUNCTION inventory.fn_append_only();

-- Solicitudes de titulares (consultas y reclamos) con su vencimiento legal.
CREATE TABLE customers.data_requests (
    id           uuid            NOT NULL,
    company_id   uuid            NOT NULL,
    party_id     uuid            NOT NULL,
    type         varchar(10)     NOT NULL,
    channel      varchar(12)     NOT NULL,
    detail       varchar(1000)   NOT NULL,
    received_on  date            NOT NULL,
    due_on       date            NOT NULL,
    status       varchar(10)     NOT NULL,
    response     varchar(2000),
    resolved_by  uuid,
    resolved_at  timestamptz,
    created_at   timestamptz     NOT NULL,
    created_by   uuid            NOT NULL,
    updated_at   timestamptz,
    updated_by   uuid,
    CONSTRAINT pk_data_requests PRIMARY KEY (id),
    CONSTRAINT fk_data_requests__party FOREIGN KEY (party_id, company_id) REFERENCES parties.parties (id, company_id),
    CONSTRAINT fk_data_requests__resolved_by FOREIGN KEY (resolved_by) REFERENCES identity.users (id),
    CONSTRAINT ck_data_requests__type CHECK (type IN ('QUERY', 'UPDATE', 'REVOKE', 'DELETE', 'COMPLAINT')),
    CONSTRAINT ck_data_requests__channel CHECK (channel IN ('POS_VERBAL', 'POS_SIGNED', 'PAPER_FORM', 'WEB', 'PHONE', 'EMAIL', 'IMPORT')),
    CONSTRAINT ck_data_requests__status CHECK (status IN ('OPEN', 'RESOLVED', 'REJECTED')),
    CONSTRAINT ck_data_requests__closed CHECK ((status = 'OPEN') = (resolved_at IS NULL AND response IS NULL)),
    CONSTRAINT ck_data_requests__due CHECK (due_on > received_on)
);

CREATE INDEX ix_data_requests__party_id_company_id ON customers.data_requests (party_id, company_id);
CREATE INDEX ix_data_requests__resolved_by ON customers.data_requests (resolved_by);
CREATE INDEX ix_data_requests__open ON customers.data_requests (company_id, due_on) WHERE status = 'OPEN';
