-- =====================================================================================================
-- V2026.10.009 · licensing · Pago en línea de las suscripciones con Wompi (docs/fases/fase-12c-propuesta.md, ADR-0063).
-- precios (historial de solo inserción) → enlace de pago por suscripción → orden de pago (valor congelado, referencia
-- BP-<año>-<consecutivo>) → eventos de la pasarela (solo inserción). Una orden renueva UNA sola vez: lo garantizan la
-- restricción única de renewal_event_id y el índice único de la referencia WOMPI en el historial de la suscripción.
-- Las tablas nacen VACÍAS: no se inventan precios (§10, respuesta 2).
-- =====================================================================================================

-- -----------------------------------------------------------------------------------------------------
-- Precios por edición y periodicidad, en pesos con IVA incluido. Vigente = el de mayor valid_from <= ahora.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE licensing.prices (
    id              uuid           NOT NULL,
    edition         varchar(10)    NOT NULL,
    billing_period  varchar(10)    NOT NULL,
    amount_cop      numeric(14,2)  NOT NULL,
    valid_from      timestamptz    NOT NULL,
    created_at      timestamptz    NOT NULL,
    created_by      uuid           NOT NULL,
    CONSTRAINT pk_prices PRIMARY KEY (id),
    CONSTRAINT fk_prices__created_by FOREIGN KEY (created_by) REFERENCES portal.portal_users (id),
    CONSTRAINT ck_prices__edition CHECK (edition IN ('SINGLE', 'MULTI')),
    CONSTRAINT ck_prices__billing_period CHECK (billing_period IN ('MONTHLY', 'ANNUAL')),
    CONSTRAINT ck_prices__amount CHECK (amount_cop > 0 AND amount_cop <= 100000000 AND amount_cop = trunc(amount_cop))
);

CREATE INDEX ix_prices__lookup ON licensing.prices (edition, billing_period, valid_from DESC);
CREATE INDEX ix_prices__created_by ON licensing.prices (created_by);

COMMENT ON TABLE licensing.prices IS 'Precios del pago en línea (pesos, IVA incluido). Solo inserción: un cambio de precio es una fila nueva.';

-- -----------------------------------------------------------------------------------------------------
-- Enlace público de pago: /pagar/<código de 128 bits en base64url>. Uno vigente por suscripción (regenerable).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE licensing.payment_links (
    id               uuid          NOT NULL,
    subscription_id  uuid          NOT NULL,
    code             varchar(32)   NOT NULL,
    created_at       timestamptz   NOT NULL,
    revoked_at       timestamptz,
    CONSTRAINT pk_payment_links PRIMARY KEY (id),
    CONSTRAINT ux_payment_links__code UNIQUE (code),
    CONSTRAINT fk_payment_links__subscription FOREIGN KEY (subscription_id) REFERENCES licensing.subscriptions (id),
    CONSTRAINT ck_payment_links__code CHECK (code ~ '^[A-Za-z0-9_-]{22}$'),
    CONSTRAINT ck_payment_links__revoked CHECK (revoked_at IS NULL OR revoked_at >= created_at)
);

CREATE UNIQUE INDEX ux_payment_links__subscription_active ON licensing.payment_links (subscription_id) WHERE revoked_at IS NULL;
CREATE INDEX ix_payment_links__subscription ON licensing.payment_links (subscription_id);

-- -----------------------------------------------------------------------------------------------------
-- Órdenes de pago: valor congelado al crearla; estado según la transacción CONFIRMADA con la llave privada.
-- -----------------------------------------------------------------------------------------------------
CREATE SEQUENCE licensing.payment_order_number_seq AS bigint START WITH 1 NO CYCLE;

CREATE TABLE licensing.payment_orders (
    id                      uuid           NOT NULL,
    reference               varchar(20)    NOT NULL,
    subscription_id         uuid           NOT NULL,
    organization_id         uuid           NOT NULL,
    edition                 varchar(10)    NOT NULL,
    billing_period          varchar(10)    NOT NULL,
    periods                 smallint       NOT NULL,
    amount_cop              numeric(14,2)  NOT NULL,
    amount_in_cents         bigint         NOT NULL,
    currency                char(3)        NOT NULL,
    status                  varchar(16)    NOT NULL,
    gateway                 varchar(10)    NOT NULL,
    gateway_transaction_id  varchar(60),
    payment_method_type     varchar(30),
    created_at              timestamptz    NOT NULL,
    updated_at              timestamptz    NOT NULL,
    applied_at              timestamptz,
    renewal_event_id        uuid,
    review_reason           varchar(300),
    reviewed_by             uuid,
    reviewed_at             timestamptz,
    review_note             varchar(300),
    CONSTRAINT pk_payment_orders PRIMARY KEY (id),
    CONSTRAINT ux_payment_orders__reference UNIQUE (reference),
    -- Una orden renueva una sola vez y un evento de renovación pertenece a una sola orden. (Sin clave foránea hacia
    -- subscription_events a propósito: esa tabla es de solo inserción y una FK permitiría que TRUNCATE fallara por otra causa.)
    CONSTRAINT ux_payment_orders__renewal_event UNIQUE (renewal_event_id),
    CONSTRAINT fk_payment_orders__subscription FOREIGN KEY (subscription_id, organization_id)
        REFERENCES licensing.subscriptions (id, organization_id),
    CONSTRAINT fk_payment_orders__organization FOREIGN KEY (organization_id) REFERENCES licensing.organizations (id),
    CONSTRAINT fk_payment_orders__reviewed_by FOREIGN KEY (reviewed_by) REFERENCES portal.portal_users (id),
    CONSTRAINT ck_payment_orders__reference CHECK (reference ~ '^BP-[0-9]{4}-[0-9]{6,11}$'),
    CONSTRAINT ck_payment_orders__edition CHECK (edition IN ('SINGLE', 'MULTI')),
    CONSTRAINT ck_payment_orders__billing_period CHECK (billing_period IN ('MONTHLY', 'ANNUAL')),
    CONSTRAINT ck_payment_orders__periods CHECK (
        (billing_period = 'MONTHLY' AND periods IN (1, 3, 6)) OR (billing_period = 'ANNUAL' AND periods = 1)),
    CONSTRAINT ck_payment_orders__amount CHECK (amount_cop > 0 AND amount_in_cents = amount_cop * 100),
    CONSTRAINT ck_payment_orders__currency CHECK (currency = 'COP'),
    CONSTRAINT ck_payment_orders__gateway CHECK (gateway = 'WOMPI'),
    CONSTRAINT ck_payment_orders__status CHECK (status IN (
        'PENDING', 'APPROVED', 'DECLINED', 'VOIDED', 'ERROR', 'EXPIRED', 'REVIEW_REQUIRED')),
    CONSTRAINT ck_payment_orders__applied CHECK (
        (applied_at IS NULL) = (renewal_event_id IS NULL)
        AND (applied_at IS NULL OR (status IN ('APPROVED', 'REVIEW_REQUIRED') AND gateway_transaction_id IS NOT NULL))
        AND (status <> 'APPROVED' OR applied_at IS NOT NULL)),
    CONSTRAINT ck_payment_orders__review CHECK (status <> 'REVIEW_REQUIRED' OR review_reason IS NOT NULL),
    CONSTRAINT ck_payment_orders__reviewed CHECK (
        (reviewed_at IS NULL) = (reviewed_by IS NULL) AND (reviewed_at IS NULL) = (review_note IS NULL)
        AND (reviewed_at IS NULL OR status = 'REVIEW_REQUIRED'))
);

CREATE INDEX ix_payment_orders__subscription ON licensing.payment_orders (subscription_id, organization_id);
CREATE INDEX ix_payment_orders__organization ON licensing.payment_orders (organization_id, created_at);
CREATE INDEX ix_payment_orders__status ON licensing.payment_orders (status, created_at);
CREATE INDEX ix_payment_orders__transaction ON licensing.payment_orders (gateway_transaction_id) WHERE gateway_transaction_id IS NOT NULL;
CREATE INDEX ix_payment_orders__reviewed_by ON licensing.payment_orders (reviewed_by) WHERE reviewed_by IS NOT NULL;

-- Segunda garantía en el historial de la suscripción: una misma transacción de Wompi no renueva dos veces.
CREATE UNIQUE INDEX ux_subscription_events__online_payment
    ON licensing.subscription_events (payment_reference) WHERE type = 'RENEWED' AND payment_reference LIKE 'WOMPI %';

-- -----------------------------------------------------------------------------------------------------
-- Eventos de la pasarela (webhook, firma válida o no) y consultas de conciliación: SOLO INSERCIÓN.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE licensing.payment_events (
    id                  uuid          NOT NULL,
    received_at         timestamptz   NOT NULL,
    gateway             varchar(10)   NOT NULL,
    source              varchar(10)   NOT NULL,
    event_type          varchar(60),
    signature_valid     boolean,
    transaction_id      varchar(60),
    transaction_status  varchar(20),
    reference           varchar(40),
    amount_in_cents     bigint,
    currency            varchar(3),
    environment         varchar(10),
    order_id            uuid,
    payload             jsonb,
    rejection_reason    varchar(300),
    CONSTRAINT pk_payment_events PRIMARY KEY (id),
    CONSTRAINT fk_payment_events__order FOREIGN KEY (order_id) REFERENCES licensing.payment_orders (id),
    CONSTRAINT ck_payment_events__gateway CHECK (gateway = 'WOMPI'),
    CONSTRAINT ck_payment_events__source CHECK (source IN ('WEBHOOK', 'QUERY')),
    CONSTRAINT ck_payment_events__signature CHECK ((source = 'WEBHOOK') = (signature_valid IS NOT NULL))
);

CREATE INDEX ix_payment_events__order ON licensing.payment_events (order_id, received_at);
CREATE INDEX ix_payment_events__reference ON licensing.payment_events (reference) WHERE reference IS NOT NULL;
CREATE INDEX ix_payment_events__received_at ON licensing.payment_events (received_at);

CREATE TRIGGER trg_prices_append_only
    BEFORE UPDATE OR DELETE ON licensing.prices
    FOR EACH ROW EXECUTE FUNCTION licensing.fn_append_only();

CREATE TRIGGER trg_prices_no_truncate
    BEFORE TRUNCATE ON licensing.prices
    FOR EACH STATEMENT EXECUTE FUNCTION licensing.fn_append_only();

CREATE TRIGGER trg_payment_events_append_only
    BEFORE UPDATE OR DELETE ON licensing.payment_events
    FOR EACH ROW EXECUTE FUNCTION licensing.fn_append_only();

CREATE TRIGGER trg_payment_events_no_truncate
    BEFORE TRUNCATE ON licensing.payment_events
    FOR EACH STATEMENT EXECUTE FUNCTION licensing.fn_append_only();
