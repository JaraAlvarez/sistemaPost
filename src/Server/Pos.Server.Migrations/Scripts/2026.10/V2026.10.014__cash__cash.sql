-- =====================================================================================================
-- V2026.10.014 · cash · Caja: denominaciones, jornadas, movimientos de solo inserción, conteos y totales del cierre.
-- Diseño: docs/04-base-de-datos.md §H.9 con los cambios de docs/fases/fase-06-propuesta.md §4.
--
-- Principios:
--   · Cada peso que entra o sale es un movimiento de SOLO INSERCIÓN (D6-01); una corrección es otro movimiento.
--   · Lo esperado se calcula siempre de los movimientos, por medio de pago (D6-02, RN-CSH-04); nunca se digita.
--   · Una jornada no cerrada por caja y una abierta por cajero, garantizadas con índices únicos parciales (D6-03).
--   · La fecha de negocio es la de apertura aunque el turno pase la medianoche (D6-04).
--   · El cierre es definitivo (D6-06) y el reporte Z guarda el sello de la auditoría (D6-08).
-- =====================================================================================================

-- Denominaciones de la moneda (billetes y monedas) para el arqueo por denominación.
CREATE TABLE cash.denominations (
    id             uuid           NOT NULL,
    company_id     uuid           NOT NULL,
    currency_code  char(3)        NOT NULL,
    value          numeric(19,2)  NOT NULL,
    kind           varchar(5)     NOT NULL,
    sort_order     integer        NOT NULL DEFAULT 0,
    status         varchar(10)    NOT NULL,
    row_version    bigint         NOT NULL DEFAULT 1,
    created_at     timestamptz    NOT NULL,
    created_by     uuid           NOT NULL,
    updated_at     timestamptz,
    updated_by     uuid,
    deleted_at     timestamptz,
    deleted_by     uuid,
    CONSTRAINT pk_denominations PRIMARY KEY (id),
    CONSTRAINT fk_denominations__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_denominations__currency FOREIGN KEY (currency_code) REFERENCES ref.currencies (code),
    CONSTRAINT ck_denominations__value CHECK (value > 0),
    CONSTRAINT ck_denominations__kind CHECK (kind IN ('BILL', 'COIN')),
    CONSTRAINT ck_denominations__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_denominations__row_version CHECK (row_version > 0),
    CONSTRAINT ck_denominations__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_denominations__company_value ON cash.denominations (company_id, currency_code, value) WHERE deleted_at IS NULL;
CREATE INDEX ix_denominations__currency_code ON cash.denominations (currency_code);

-- -----------------------------------------------------------------------------------------------------
-- Jornadas de caja. number = serie de la caja (CASH_SESSION, TERMINAL). expected/counted/difference = totales de todos
-- los medios al cerrar (el detalle por medio está en cash_session_totals). z_seal_*: sello de la auditoría impreso en el Z.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE cash.cash_sessions (
    id                    uuid           NOT NULL,
    company_id            uuid           NOT NULL,
    branch_id             uuid           NOT NULL,
    pos_terminal_id       uuid           NOT NULL,
    device_id             uuid,
    cashier_id            uuid           NOT NULL,
    number                varchar(40)    NOT NULL,
    business_date         date           NOT NULL,
    opened_at             timestamptz    NOT NULL,
    opening_float         numeric(19,2)  NOT NULL,
    status                varchar(10)    NOT NULL,
    blind_count           boolean        NOT NULL,
    closing_started_at    timestamptz,
    closed_at             timestamptz,
    closed_by             uuid,
    closed_by_supervisor  boolean        NOT NULL DEFAULT false,
    close_reason          varchar(300),
    expected_total        numeric(19,2),
    counted_total         numeric(19,2),
    difference            numeric(19,2),
    difference_note       varchar(500),
    review_required       boolean        NOT NULL DEFAULT false,
    reviewed_at           timestamptz,
    reviewed_by           uuid,
    review_note           varchar(500),
    z_seal_no             bigint,
    z_seal_code           varchar(19),
    created_at            timestamptz    NOT NULL,
    created_by            uuid           NOT NULL,
    updated_at            timestamptz,
    updated_by            uuid,
    CONSTRAINT pk_cash_sessions PRIMARY KEY (id),
    CONSTRAINT ux_cash_sessions__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_cash_sessions__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_cash_sessions__terminal FOREIGN KEY (pos_terminal_id, branch_id) REFERENCES org.pos_terminals (id, branch_id),
    CONSTRAINT fk_cash_sessions__device FOREIGN KEY (device_id) REFERENCES org.devices (id),
    CONSTRAINT fk_cash_sessions__cashier FOREIGN KEY (cashier_id) REFERENCES identity.users (id),
    CONSTRAINT ck_cash_sessions__opening_float CHECK (opening_float >= 0),
    CONSTRAINT ck_cash_sessions__status CHECK (status IN ('OPEN', 'CLOSING', 'CLOSED')),
    CONSTRAINT ck_cash_sessions__closing CHECK (status = 'OPEN' OR closing_started_at IS NOT NULL),
    CONSTRAINT ck_cash_sessions__closed CHECK ((status = 'CLOSED') = (closed_at IS NOT NULL)
        AND (status = 'CLOSED') = (expected_total IS NOT NULL AND counted_total IS NOT NULL AND difference IS NOT NULL)),
    CONSTRAINT ck_cash_sessions__reviewer CHECK (reviewed_by IS NULL OR reviewed_by <> cashier_id),
    CONSTRAINT ck_cash_sessions__seal CHECK ((z_seal_no IS NULL) = (z_seal_code IS NULL))
);

CREATE UNIQUE INDEX ux_cash_sessions__terminal_number ON cash.cash_sessions (pos_terminal_id, number);
CREATE UNIQUE INDEX ux_cash_sessions__terminal_open ON cash.cash_sessions (pos_terminal_id) WHERE status <> 'CLOSED';
CREATE UNIQUE INDEX ux_cash_sessions__cashier_open ON cash.cash_sessions (cashier_id) WHERE status <> 'CLOSED';
CREATE INDEX ix_cash_sessions__branch_id_company_id ON cash.cash_sessions (branch_id, company_id);
CREATE INDEX ix_cash_sessions__pos_terminal_id_branch_id ON cash.cash_sessions (pos_terminal_id, branch_id);
CREATE INDEX ix_cash_sessions__device_id ON cash.cash_sessions (device_id);
CREATE INDEX ix_cash_sessions__cashier_id ON cash.cash_sessions (cashier_id);
CREATE INDEX ix_cash_sessions__business_date ON cash.cash_sessions (branch_id, business_date);
CREATE INDEX ix_cash_sessions__review ON cash.cash_sessions (branch_id) WHERE review_required AND reviewed_at IS NULL;

-- -----------------------------------------------------------------------------------------------------
-- Movimientos de caja (SOLO INSERCIÓN). direction: 1 entra, −1 sale, 0 no mueve dinero (apertura del cajón sin venta).
-- line_no: orden dentro de la jornada. authorized_by: supervisor que autorizó (retiros, aperturas sin venta).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE cash.cash_movements (
    id                 uuid           NOT NULL,
    company_id         uuid           NOT NULL,
    session_id         uuid           NOT NULL,
    line_no            integer        NOT NULL,
    movement_type      varchar(25)    NOT NULL,
    payment_method_id  uuid           NOT NULL,
    direction          smallint       NOT NULL,
    amount             numeric(19,2)  NOT NULL,
    source_type        varchar(30),
    source_id          uuid,
    source_number      varchar(40),
    reason             varchar(300),
    authorized_by      uuid,
    user_id            uuid           NOT NULL,
    occurred_at        timestamptz    NOT NULL,
    CONSTRAINT pk_cash_movements PRIMARY KEY (id),
    CONSTRAINT ux_cash_movements__session_line UNIQUE (session_id, line_no),
    CONSTRAINT fk_cash_movements__session FOREIGN KEY (session_id, company_id) REFERENCES cash.cash_sessions (id, company_id),
    CONSTRAINT fk_cash_movements__payment_method FOREIGN KEY (payment_method_id, company_id) REFERENCES cash.payment_methods (id, company_id),
    CONSTRAINT ck_cash_movements__type CHECK (movement_type IN (
        'OPENING_FLOAT', 'SALE', 'SALE_VOID', 'CUSTOMER_REFUND', 'CASH_IN', 'CASH_OUT_WITHDRAWAL', 'EXPENSE', 'SUPPLIER_PAYMENT',
        'CORRECTION', 'NO_SALE_DRAWER_OPEN')),
    CONSTRAINT ck_cash_movements__direction CHECK (direction = CASE
        WHEN movement_type IN ('OPENING_FLOAT', 'SALE', 'CASH_IN') THEN 1
        WHEN movement_type IN ('SALE_VOID', 'CUSTOMER_REFUND', 'CASH_OUT_WITHDRAWAL', 'EXPENSE', 'SUPPLIER_PAYMENT') THEN -1
        WHEN movement_type = 'NO_SALE_DRAWER_OPEN' THEN 0
        ELSE direction END AND direction IN (1, -1, 0)),
    CONSTRAINT ck_cash_movements__amount CHECK (CASE movement_type
        WHEN 'NO_SALE_DRAWER_OPEN' THEN amount = 0
        WHEN 'OPENING_FLOAT' THEN amount >= 0
        ELSE amount > 0 END),
    CONSTRAINT ck_cash_movements__correction CHECK (movement_type <> 'CORRECTION' OR direction <> 0),
    CONSTRAINT ck_cash_movements__line CHECK (line_no > 0)
);

CREATE INDEX ix_cash_movements__session_id_company_id ON cash.cash_movements (session_id, company_id);
CREATE INDEX ix_cash_movements__payment_method_id_company_id ON cash.cash_movements (payment_method_id, company_id);
CREATE INDEX ix_cash_movements__source ON cash.cash_movements (source_type, source_id);

CREATE TRIGGER trg_cash_movements_append_only
    BEFORE UPDATE OR DELETE ON cash.cash_movements
    FOR EACH ROW EXECUTE FUNCTION inventory.fn_append_only();

CREATE TRIGGER trg_cash_movements_no_truncate
    BEFORE TRUNCATE ON cash.cash_movements
    FOR EACH STATEMENT EXECUTE FUNCTION inventory.fn_append_only();

-- Conteos (apertura, parciales y cierre). Una línea por denominación del efectivo o por total de otro medio.
CREATE TABLE cash.cash_counts (
    id          uuid           NOT NULL,
    session_id  uuid           NOT NULL,
    kind        varchar(10)    NOT NULL,
    total       numeric(19,2)  NOT NULL,
    counted_at  timestamptz    NOT NULL,
    counted_by  uuid           NOT NULL,
    CONSTRAINT pk_cash_counts PRIMARY KEY (id),
    CONSTRAINT fk_cash_counts__session FOREIGN KEY (session_id) REFERENCES cash.cash_sessions (id) ON DELETE CASCADE,
    CONSTRAINT ck_cash_counts__kind CHECK (kind IN ('OPENING', 'PARTIAL', 'CLOSING')),
    CONSTRAINT ck_cash_counts__total CHECK (total >= 0)
);

CREATE INDEX ix_cash_counts__session_id ON cash.cash_counts (session_id);

CREATE TABLE cash.cash_count_lines (
    id                 uuid           NOT NULL,
    count_id           uuid           NOT NULL,
    payment_method_id  uuid           NOT NULL,
    denomination_id    uuid,
    quantity           integer,
    amount             numeric(19,2)  NOT NULL,
    CONSTRAINT pk_cash_count_lines PRIMARY KEY (id),
    CONSTRAINT ux_cash_count_lines__line UNIQUE NULLS NOT DISTINCT (count_id, payment_method_id, denomination_id),
    CONSTRAINT fk_cash_count_lines__count FOREIGN KEY (count_id) REFERENCES cash.cash_counts (id) ON DELETE CASCADE,
    CONSTRAINT fk_cash_count_lines__payment_method FOREIGN KEY (payment_method_id) REFERENCES cash.payment_methods (id),
    CONSTRAINT fk_cash_count_lines__denomination FOREIGN KEY (denomination_id) REFERENCES cash.denominations (id),
    CONSTRAINT ck_cash_count_lines__quantity CHECK ((denomination_id IS NULL) = (quantity IS NULL) AND (quantity IS NULL OR quantity >= 0)),
    CONSTRAINT ck_cash_count_lines__amount CHECK (amount >= 0)
);

CREATE INDEX ix_cash_count_lines__payment_method_id ON cash.cash_count_lines (payment_method_id);
CREATE INDEX ix_cash_count_lines__denomination_id ON cash.cash_count_lines (denomination_id);

-- Totales del cierre por medio de pago: esperado (de los movimientos), contado y diferencia.
CREATE TABLE cash.cash_session_totals (
    id                 uuid           NOT NULL,
    session_id         uuid           NOT NULL,
    payment_method_id  uuid           NOT NULL,
    expected           numeric(19,2)  NOT NULL,
    counted            numeric(19,2)  NOT NULL,
    difference         numeric(19,2)  NOT NULL,
    transactions       integer        NOT NULL,
    CONSTRAINT pk_cash_session_totals PRIMARY KEY (id),
    CONSTRAINT ux_cash_session_totals__method UNIQUE (session_id, payment_method_id),
    CONSTRAINT fk_cash_session_totals__session FOREIGN KEY (session_id) REFERENCES cash.cash_sessions (id) ON DELETE CASCADE,
    CONSTRAINT fk_cash_session_totals__payment_method FOREIGN KEY (payment_method_id) REFERENCES cash.payment_methods (id),
    CONSTRAINT ck_cash_session_totals__difference CHECK (difference = counted - expected AND counted >= 0 AND transactions >= 0)
);

CREATE INDEX ix_cash_session_totals__payment_method_id ON cash.cash_session_totals (payment_method_id);

-- El pago a proveedor desde la caja (Fase 5 reservó la columna).
ALTER TABLE purchasing.payable_payments ADD CONSTRAINT fk_payable_payments__cash_session FOREIGN KEY (cash_session_id, company_id)
    REFERENCES cash.cash_sessions (id, company_id);
CREATE INDEX ix_payable_payments__cash_session_id_company_id ON purchasing.payable_payments (cash_session_id, company_id);
