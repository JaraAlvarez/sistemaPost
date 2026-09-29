-- =====================================================================================================
-- V2026.10.001 · system · Línea base: extensiones, colación, esquemas y tablas de infraestructura.
-- Convenciones: docs/fases/fase-02-propuesta.md §1. Se ejecuta como pos_owner (SET ROLE desde el migrador).
-- El esquema system y la tabla system.schema_migrations los crea el propio migrador antes de este script.
-- =====================================================================================================

-- Extensiones "trusted": no requieren superusuario. Se instalan ya aunque se usen desde la Fase 4.
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE EXTENSION IF NOT EXISTS unaccent;

-- Orden alfabético en español: se usa de forma EXPLÍCITA (COLLATE public.es_co) solo donde se muestra al
-- usuario. La colación por defecto de la BD es builtin C.UTF-8 (estable ante actualizaciones de ICU/Windows).
CREATE COLLATION IF NOT EXISTS public.es_co (provider = icu, locale = 'es-CO');

CREATE SCHEMA IF NOT EXISTS ref;
CREATE SCHEMA IF NOT EXISTS org;
CREATE SCHEMA IF NOT EXISTS identity;
CREATE SCHEMA IF NOT EXISTS audit;

-- -----------------------------------------------------------------------------------------------------
-- Tipos de documento (catálogo sembrado por R__system__document_types.sql)
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE system.document_types (
    code          varchar(30)  NOT NULL,
    module        varchar(30)  NOT NULL,
    name          varchar(80)  NOT NULL,
    series_scope  varchar(10)  NOT NULL,
    CONSTRAINT pk_document_types PRIMARY KEY (code),
    CONSTRAINT ck_document_types__code CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT ck_document_types__series_scope CHECK (series_scope IN ('BRANCH', 'TERMINAL'))
);

-- -----------------------------------------------------------------------------------------------------
-- Instalación (= nodo local): exactamente una fila. Las FK hacia org se agregan en V2026.10.003.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE system.installation (
    id                  boolean      NOT NULL DEFAULT true,
    installation_id     uuid         NOT NULL,
    node_role           varchar(20)  NOT NULL,
    node_epoch          integer      NOT NULL DEFAULT 1,
    home_company_id     uuid,
    home_branch_id      uuid,
    setup_mode          varchar(20),
    setup_completed_at  timestamptz,
    created_at          timestamptz  NOT NULL,
    CONSTRAINT pk_installation PRIMARY KEY (id),
    CONSTRAINT ck_installation__single_row CHECK (id),
    CONSTRAINT ux_installation__installation_id UNIQUE (installation_id),
    CONSTRAINT ck_installation__node_role CHECK (node_role IN ('ALL_IN_ONE', 'STORE_SERVER')),
    CONSTRAINT ck_installation__node_epoch CHECK (node_epoch > 0),
    CONSTRAINT ck_installation__setup_mode CHECK (setup_mode IN ('NEW_COMPANY', 'JOIN_COMPANY')),
    CONSTRAINT ck_installation__setup_complete CHECK (
        setup_completed_at IS NULL
        OR (home_company_id IS NOT NULL AND home_branch_id IS NOT NULL AND setup_mode IS NOT NULL))
);

CREATE FUNCTION system.fn_installation_protect() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'system.installation no se puede borrar' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.installation_id <> OLD.installation_id THEN
        RAISE EXCEPTION 'installation_id es inmutable' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_installation_protect
    BEFORE UPDATE OR DELETE ON system.installation
    FOR EACH ROW EXECUTE FUNCTION system.fn_installation_protect();

-- -----------------------------------------------------------------------------------------------------
-- Outbox: eventos locales (LOCAL) y de integración (SYNC). node_seq ordena lo que se envía a la nube.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE system.outbox_messages (
    id               uuid          NOT NULL,
    node_seq         bigint        GENERATED ALWAYS AS IDENTITY,
    occurred_at      timestamptz   NOT NULL,
    destination      varchar(10)   NOT NULL,
    type             varchar(200)  NOT NULL,
    payload          jsonb         NOT NULL,
    correlation_id   varchar(64),
    status           varchar(12)   NOT NULL DEFAULT 'PENDING',
    attempts         integer       NOT NULL DEFAULT 0,
    next_attempt_at  timestamptz   NOT NULL,
    locked_until     timestamptz,
    last_error       text,
    processed_at     timestamptz,
    CONSTRAINT pk_outbox_messages PRIMARY KEY (id),
    CONSTRAINT ux_outbox_messages__node_seq UNIQUE (node_seq),
    CONSTRAINT ck_outbox_messages__destination CHECK (destination IN ('LOCAL', 'SYNC')),
    CONSTRAINT ck_outbox_messages__status CHECK (status IN ('PENDING', 'PROCESSING', 'PROCESSED', 'FAILED')),
    CONSTRAINT ck_outbox_messages__attempts CHECK (attempts >= 0)
);

CREATE INDEX ix_outbox_messages__pending
    ON system.outbox_messages (next_attempt_at)
    WHERE status IN ('PENDING', 'FAILED');

-- -----------------------------------------------------------------------------------------------------
-- Inbox: eventos recibidos de otros nodos (nube, paquete .possync, caja autónoma) ya aplicados.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE system.inbox_messages (
    message_id      uuid          NOT NULL,
    source_node_id  uuid          NOT NULL,
    source_seq      bigint        NOT NULL,
    type            varchar(200)  NOT NULL,
    received_via    varchar(10)   NOT NULL,
    applied_at      timestamptz   NOT NULL,
    result          varchar(12)   NOT NULL,
    CONSTRAINT pk_inbox_messages PRIMARY KEY (message_id),
    CONSTRAINT ux_inbox_messages__source UNIQUE (source_node_id, source_seq),
    CONSTRAINT ck_inbox_messages__received_via CHECK (received_via IN ('ONLINE', 'FILE')),
    CONSTRAINT ck_inbox_messages__result CHECK (result IN ('APPLIED', 'CONFLICT', 'IGNORED'))
);

CREATE TABLE system.sync_cursors (
    peer_node_id       uuid         NOT NULL,
    last_sent_seq      bigint       NOT NULL DEFAULT 0,
    last_acked_seq     bigint       NOT NULL DEFAULT 0,
    last_received_seq  bigint       NOT NULL DEFAULT 0,
    updated_at         timestamptz  NOT NULL,
    CONSTRAINT pk_sync_cursors PRIMARY KEY (peer_node_id),
    CONSTRAINT ck_sync_cursors__acked CHECK (last_acked_seq <= last_sent_seq)
);

-- -----------------------------------------------------------------------------------------------------
-- Idempotencia de comandos (RN-GEN-09)
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE system.idempotency_keys (
    scope            varchar(60)   NOT NULL,
    key              varchar(100)  NOT NULL,
    request_hash     char(64)      NOT NULL,
    response_status  smallint      NOT NULL,
    response_body    jsonb,
    created_at       timestamptz   NOT NULL,
    expires_at       timestamptz   NOT NULL,
    CONSTRAINT pk_idempotency_keys PRIMARY KEY (scope, key),
    CONSTRAINT ck_idempotency_keys__expires CHECK (expires_at > created_at)
);

CREATE INDEX ix_idempotency_keys__expires_at ON system.idempotency_keys (expires_at);
