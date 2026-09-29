-- =====================================================================================================
-- V2026.10.009 · inventory · Kardex, saldos, lotes (estructura), políticas, motivos, ajustes, conteos, traslados y
-- verificaciones. Diseño: docs/04-base-de-datos.md §H.6, docs/07-inventario-kardex.md y
-- docs/fases/fase-04-propuesta.md §4.2 y §5.
--
-- Principio: el stock es la consecuencia de los movimientos (RN-INV-01). stock_movements es de SOLO INSERCIÓN
-- (privilegios + disparador) y stock_balances es un caché del kardex que se actualiza en la misma transacción.
-- Cada bodega tiene un único nodo escritor (el de su sucursal, D4-04).
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS inventory;

-- -----------------------------------------------------------------------------------------------------
-- Motivos de ajuste. kind fija el tipo de movimiento: INITIAL_BALANCE (entrada con costo), ADJUSTMENT (entrada o
-- salida según el signo de la línea) o una salida tipificada (LOSS, DAMAGE, EXPIRY, INTERNAL_USE).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE inventory.adjustment_reasons (
    id             uuid          NOT NULL,
    company_id     uuid          NOT NULL,
    code           varchar(30)   NOT NULL,
    name           varchar(80)   NOT NULL,
    kind           varchar(20)   NOT NULL,
    requires_note  boolean       NOT NULL DEFAULT false,
    is_system      boolean       NOT NULL DEFAULT false,
    status         varchar(10)   NOT NULL,
    row_version    bigint        NOT NULL DEFAULT 1,
    created_at     timestamptz   NOT NULL,
    created_by     uuid          NOT NULL,
    updated_at     timestamptz,
    updated_by     uuid,
    deleted_at     timestamptz,
    deleted_by     uuid,
    CONSTRAINT pk_adjustment_reasons PRIMARY KEY (id),
    CONSTRAINT ux_adjustment_reasons__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_adjustment_reasons__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_adjustment_reasons__code CHECK (code ~ '^[A-Z0-9_]{2,30}$'),
    CONSTRAINT ck_adjustment_reasons__kind CHECK (kind IN ('INITIAL_BALANCE', 'ADJUSTMENT', 'LOSS', 'DAMAGE', 'EXPIRY', 'INTERNAL_USE')),
    CONSTRAINT ck_adjustment_reasons__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_adjustment_reasons__row_version CHECK (row_version > 0),
    CONSTRAINT ck_adjustment_reasons__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_adjustment_reasons__company_code ON inventory.adjustment_reasons (company_id, code) WHERE deleted_at IS NULL;

-- -----------------------------------------------------------------------------------------------------
-- Lotes: la estructura queda lista; la gestión (lote en la entrada, FEFO, vencimientos) llega con las compras (Fase 5).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE inventory.inventory_lots (
    id                 uuid          NOT NULL,
    company_id         uuid          NOT NULL,
    product_id         uuid          NOT NULL,
    lot_number         varchar(40)   NOT NULL,
    expiry_date        date,
    manufactured_date  date,
    status             varchar(12)   NOT NULL,
    created_at         timestamptz   NOT NULL,
    created_by         uuid          NOT NULL,
    updated_at         timestamptz,
    updated_by         uuid,
    CONSTRAINT pk_inventory_lots PRIMARY KEY (id),
    CONSTRAINT ux_inventory_lots__product_lot UNIQUE (product_id, lot_number),
    CONSTRAINT fk_inventory_lots__product FOREIGN KEY (product_id, company_id) REFERENCES catalog.products (id, company_id),
    CONSTRAINT ck_inventory_lots__status CHECK (status IN ('AVAILABLE', 'QUARANTINE', 'EXPIRED', 'EXHAUSTED'))
);

CREATE INDEX ix_inventory_lots__product_id_company_id ON inventory.inventory_lots (product_id, company_id);

-- -----------------------------------------------------------------------------------------------------
-- Kardex (append-only). seq ordena los movimientos: por bodega y producto es creciente en el orden de confirmación,
-- porque el movimiento se inserta con la fila del saldo bloqueada (lo usa el conteo físico, RN-INV-06).
-- total_cost = valorización del movimiento; balance_* = saldo, valor y costo promedio después del movimiento.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE inventory.stock_movements (
    id                    uuid           NOT NULL,
    seq                   bigint         GENERATED ALWAYS AS IDENTITY,
    company_id            uuid           NOT NULL,
    node_id               uuid           NOT NULL,
    branch_id             uuid           NOT NULL,
    warehouse_id          uuid           NOT NULL,
    product_id            uuid           NOT NULL,
    lot_id                uuid,
    movement_type         varchar(30)    NOT NULL,
    direction             smallint       NOT NULL,
    quantity              numeric(18,4)  NOT NULL,
    unit_cost             numeric(19,4)  NOT NULL,
    total_cost            numeric(19,4)  NOT NULL,
    balance_quantity      numeric(18,4)  NOT NULL,
    balance_value         numeric(19,4)  NOT NULL,
    balance_avg_cost      numeric(19,4)  NOT NULL,
    source_type           varchar(30)    NOT NULL,
    source_id             uuid           NOT NULL,
    source_line_id        uuid,
    source_number         varchar(40),
    reason_id             uuid,
    reverses_movement_id  uuid,
    packaging_id          uuid,
    packaging_quantity    numeric(18,4),
    business_date         date           NOT NULL,
    occurred_at           timestamptz    NOT NULL,
    user_id               uuid           NOT NULL,
    CONSTRAINT pk_stock_movements PRIMARY KEY (id),
    CONSTRAINT ux_stock_movements__seq UNIQUE (seq),
    CONSTRAINT fk_stock_movements__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_stock_movements__warehouse FOREIGN KEY (warehouse_id, branch_id) REFERENCES org.warehouses (id, branch_id),
    CONSTRAINT fk_stock_movements__product FOREIGN KEY (product_id, company_id) REFERENCES catalog.products (id, company_id),
    CONSTRAINT fk_stock_movements__lot FOREIGN KEY (lot_id) REFERENCES inventory.inventory_lots (id),
    CONSTRAINT fk_stock_movements__reason FOREIGN KEY (reason_id, company_id) REFERENCES inventory.adjustment_reasons (id, company_id),
    CONSTRAINT fk_stock_movements__reverses FOREIGN KEY (reverses_movement_id) REFERENCES inventory.stock_movements (id),
    CONSTRAINT ck_stock_movements__type CHECK (movement_type IN (
        'INITIAL_BALANCE', 'PURCHASE_RECEIPT', 'SALE', 'SALE_VOID', 'CUSTOMER_RETURN', 'CUSTOMER_RETURN_DAMAGED',
        'SUPPLIER_RETURN', 'ADJUSTMENT_IN', 'ADJUSTMENT_OUT', 'LOSS', 'DAMAGE', 'EXPIRY', 'INTERNAL_USE',
        'TRANSFER_OUT', 'TRANSFER_IN', 'COUNT_ADJUSTMENT_IN', 'COUNT_ADJUSTMENT_OUT', 'REVERSAL')),
    CONSTRAINT ck_stock_movements__direction CHECK (direction IN (1, -1)),
    CONSTRAINT ck_stock_movements__type_direction CHECK (movement_type = 'REVERSAL' OR direction = CASE
        WHEN movement_type IN ('INITIAL_BALANCE', 'PURCHASE_RECEIPT', 'SALE_VOID', 'CUSTOMER_RETURN', 'CUSTOMER_RETURN_DAMAGED',
                               'ADJUSTMENT_IN', 'TRANSFER_IN', 'COUNT_ADJUSTMENT_IN') THEN 1 ELSE -1 END),
    CONSTRAINT ck_stock_movements__reversal CHECK ((movement_type = 'REVERSAL') = (reverses_movement_id IS NOT NULL)),
    CONSTRAINT ck_stock_movements__quantity CHECK (quantity > 0),
    CONSTRAINT ck_stock_movements__costs CHECK (unit_cost >= 0 AND total_cost >= 0 AND balance_avg_cost >= 0),
    CONSTRAINT ck_stock_movements__packaging CHECK ((packaging_id IS NULL) = (packaging_quantity IS NULL))
);

CREATE INDEX ix_stock_movements__product_warehouse_seq ON inventory.stock_movements (product_id, warehouse_id, seq);
CREATE INDEX ix_stock_movements__product_id_company_id ON inventory.stock_movements (product_id, company_id);
CREATE INDEX ix_stock_movements__source ON inventory.stock_movements (source_type, source_id);
CREATE INDEX ix_stock_movements__branch_id_company_id ON inventory.stock_movements (branch_id, company_id);
CREATE INDEX ix_stock_movements__warehouse_id_branch_id ON inventory.stock_movements (warehouse_id, branch_id);
CREATE INDEX ix_stock_movements__lot_id ON inventory.stock_movements (lot_id);
CREATE INDEX ix_stock_movements__reason_id_company_id ON inventory.stock_movements (reason_id, company_id);
CREATE INDEX ix_stock_movements__reverses_movement_id ON inventory.stock_movements (reverses_movement_id);
CREATE INDEX ix_stock_movements__business_date ON inventory.stock_movements (business_date);

CREATE FUNCTION inventory.fn_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'La tabla %.% es de solo inserción: un error se corrige con un movimiento inverso', TG_TABLE_SCHEMA, TG_TABLE_NAME
        USING ERRCODE = '42501';
END;
$$;

CREATE TRIGGER trg_stock_movements_append_only
    BEFORE UPDATE OR DELETE ON inventory.stock_movements
    FOR EACH ROW EXECUTE FUNCTION inventory.fn_append_only();

CREATE TRIGGER trg_stock_movements_no_truncate
    BEFORE TRUNCATE ON inventory.stock_movements
    FOR EACH STATEMENT EXECUTE FUNCTION inventory.fn_append_only();

-- -----------------------------------------------------------------------------------------------------
-- Saldo por bodega × producto (× lote): caché del kardex, reconstruible (RN-INV-11). average_cost = total_value ÷
-- quantity mientras la cantidad es positiva; con saldo ≤ 0 conserva el último promedio conocido.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE inventory.stock_balances (
    id                uuid           NOT NULL,
    company_id        uuid           NOT NULL,
    node_id           uuid           NOT NULL,
    branch_id         uuid           NOT NULL,
    warehouse_id      uuid           NOT NULL,
    product_id        uuid           NOT NULL,
    lot_id            uuid,
    quantity          numeric(18,4)  NOT NULL DEFAULT 0,
    total_value       numeric(19,4)  NOT NULL DEFAULT 0,
    average_cost      numeric(19,4)  NOT NULL DEFAULT 0,
    last_seq          bigint         NOT NULL DEFAULT 0,
    last_movement_at  timestamptz,
    CONSTRAINT pk_stock_balances PRIMARY KEY (id),
    CONSTRAINT ux_stock_balances__warehouse_product_lot UNIQUE NULLS NOT DISTINCT (warehouse_id, product_id, lot_id),
    CONSTRAINT fk_stock_balances__warehouse FOREIGN KEY (warehouse_id, branch_id) REFERENCES org.warehouses (id, branch_id),
    CONSTRAINT fk_stock_balances__product FOREIGN KEY (product_id, company_id) REFERENCES catalog.products (id, company_id),
    CONSTRAINT fk_stock_balances__lot FOREIGN KEY (lot_id) REFERENCES inventory.inventory_lots (id),
    CONSTRAINT ck_stock_balances__average_cost CHECK (average_cost >= 0)
);

CREATE INDEX ix_stock_balances__product_id_company_id ON inventory.stock_balances (product_id, company_id);
CREATE INDEX ix_stock_balances__warehouse_id_branch_id ON inventory.stock_balances (warehouse_id, branch_id);
CREATE INDEX ix_stock_balances__lot_id ON inventory.stock_balances (lot_id);

-- -----------------------------------------------------------------------------------------------------
-- Políticas de reposición por bodega (mínimo, máximo, punto de pedido).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE inventory.stock_policies (
    id             uuid           NOT NULL,
    company_id     uuid           NOT NULL,
    branch_id      uuid           NOT NULL,
    warehouse_id   uuid           NOT NULL,
    product_id     uuid           NOT NULL,
    min_qty        numeric(18,4)  NOT NULL,
    max_qty        numeric(18,4),
    reorder_point  numeric(18,4),
    reorder_qty    numeric(18,4),
    row_version    bigint         NOT NULL DEFAULT 1,
    created_at     timestamptz    NOT NULL,
    created_by     uuid           NOT NULL,
    updated_at     timestamptz,
    updated_by     uuid,
    deleted_at     timestamptz,
    deleted_by     uuid,
    CONSTRAINT pk_stock_policies PRIMARY KEY (id),
    CONSTRAINT fk_stock_policies__warehouse FOREIGN KEY (warehouse_id, branch_id) REFERENCES org.warehouses (id, branch_id),
    CONSTRAINT fk_stock_policies__product FOREIGN KEY (product_id, company_id) REFERENCES catalog.products (id, company_id),
    CONSTRAINT ck_stock_policies__quantities CHECK (min_qty >= 0 AND (max_qty IS NULL OR max_qty >= min_qty)
        AND (reorder_point IS NULL OR reorder_point >= 0) AND (reorder_qty IS NULL OR reorder_qty > 0)),
    CONSTRAINT ck_stock_policies__row_version CHECK (row_version > 0),
    CONSTRAINT ck_stock_policies__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_stock_policies__warehouse_product ON inventory.stock_policies (warehouse_id, product_id) WHERE deleted_at IS NULL;
CREATE INDEX ix_stock_policies__warehouse_id_branch_id ON inventory.stock_policies (warehouse_id, branch_id);
CREATE INDEX ix_stock_policies__product_id_company_id ON inventory.stock_policies (product_id, company_id);

-- -----------------------------------------------------------------------------------------------------
-- Ajustes (incluye el saldo inicial). Quien aprueba no puede ser quien creó el ajuste (RN-INV-04).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE inventory.inventory_adjustments (
    id                 uuid           NOT NULL,
    company_id         uuid           NOT NULL,
    branch_id          uuid           NOT NULL,
    warehouse_id       uuid           NOT NULL,
    number             varchar(40)    NOT NULL,
    business_date      date           NOT NULL,
    reason_id          uuid           NOT NULL,
    status             varchar(20)    NOT NULL,
    notes              varchar(500),
    total_value        numeric(19,4),
    approval_required  boolean        NOT NULL DEFAULT false,
    submitted_at       timestamptz,
    approved_at        timestamptz,
    approved_by        uuid,
    posted_at          timestamptz,
    posted_by          uuid,
    cancelled_at       timestamptz,
    cancelled_by       uuid,
    created_at         timestamptz    NOT NULL,
    created_by         uuid           NOT NULL,
    updated_at         timestamptz,
    updated_by         uuid,
    CONSTRAINT pk_inventory_adjustments PRIMARY KEY (id),
    CONSTRAINT fk_inventory_adjustments__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_inventory_adjustments__warehouse FOREIGN KEY (warehouse_id, branch_id) REFERENCES org.warehouses (id, branch_id),
    CONSTRAINT fk_inventory_adjustments__reason FOREIGN KEY (reason_id, company_id) REFERENCES inventory.adjustment_reasons (id, company_id),
    CONSTRAINT ck_inventory_adjustments__status CHECK (status IN ('DRAFT', 'PENDING_APPROVAL', 'POSTED', 'CANCELLED')),
    CONSTRAINT ck_inventory_adjustments__approver CHECK (approved_by IS NULL OR approved_by <> created_by),
    CONSTRAINT ck_inventory_adjustments__posted CHECK ((status = 'POSTED') = (posted_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_inventory_adjustments__branch_number ON inventory.inventory_adjustments (branch_id, number);
CREATE INDEX ix_inventory_adjustments__branch_id_company_id ON inventory.inventory_adjustments (branch_id, company_id);
CREATE INDEX ix_inventory_adjustments__warehouse_id_branch_id ON inventory.inventory_adjustments (warehouse_id, branch_id);
CREATE INDEX ix_inventory_adjustments__reason_id_company_id ON inventory.inventory_adjustments (reason_id, company_id);

CREATE TABLE inventory.inventory_adjustment_lines (
    id             uuid           NOT NULL,
    adjustment_id  uuid           NOT NULL,
    line_number    integer        NOT NULL,
    product_id     uuid           NOT NULL,
    quantity       numeric(18,4)  NOT NULL,
    unit_cost      numeric(19,4),
    notes          varchar(200),
    CONSTRAINT pk_inventory_adjustment_lines PRIMARY KEY (id),
    CONSTRAINT ux_inventory_adjustment_lines__product UNIQUE (adjustment_id, product_id),
    CONSTRAINT fk_inventory_adjustment_lines__adjustment FOREIGN KEY (adjustment_id)
        REFERENCES inventory.inventory_adjustments (id) ON DELETE CASCADE,
    CONSTRAINT fk_inventory_adjustment_lines__product FOREIGN KEY (product_id) REFERENCES catalog.products (id),
    CONSTRAINT ck_inventory_adjustment_lines__quantity CHECK (quantity <> 0),
    CONSTRAINT ck_inventory_adjustment_lines__unit_cost CHECK (unit_cost >= 0)
);

CREATE INDEX ix_inventory_adjustment_lines__product_id ON inventory.inventory_adjustment_lines (product_id);

-- -----------------------------------------------------------------------------------------------------
-- Conteos físicos. Cada línea congela el saldo teórico y el seq del último movimiento al iniciar; cada captura de
-- cada contador se guarda aparte (round 2 = reconteo).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE inventory.inventory_counts (
    id            uuid          NOT NULL,
    company_id    uuid          NOT NULL,
    branch_id     uuid          NOT NULL,
    warehouse_id  uuid          NOT NULL,
    number        varchar(40)   NOT NULL,
    count_type    varchar(10)   NOT NULL,
    is_blind      boolean       NOT NULL DEFAULT true,
    scope         jsonb         NOT NULL,
    status        varchar(15)   NOT NULL,
    notes         varchar(500),
    started_at    timestamptz,
    reviewed_at   timestamptz,
    approved_by   uuid,
    posted_at     timestamptz,
    cancelled_at  timestamptz,
    created_at    timestamptz   NOT NULL,
    created_by    uuid          NOT NULL,
    updated_at    timestamptz,
    updated_by    uuid,
    CONSTRAINT pk_inventory_counts PRIMARY KEY (id),
    CONSTRAINT fk_inventory_counts__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_inventory_counts__warehouse FOREIGN KEY (warehouse_id, branch_id) REFERENCES org.warehouses (id, branch_id),
    CONSTRAINT ck_inventory_counts__type CHECK (count_type IN ('FULL', 'PARTIAL')),
    CONSTRAINT ck_inventory_counts__status CHECK (status IN ('DRAFT', 'IN_PROGRESS', 'IN_REVIEW', 'POSTED', 'CANCELLED')),
    CONSTRAINT ck_inventory_counts__started CHECK (status IN ('DRAFT', 'CANCELLED') OR started_at IS NOT NULL),
    CONSTRAINT ck_inventory_counts__posted CHECK ((status = 'POSTED') = (posted_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_inventory_counts__branch_number ON inventory.inventory_counts (branch_id, number);
CREATE INDEX ix_inventory_counts__branch_id_company_id ON inventory.inventory_counts (branch_id, company_id);
CREATE INDEX ix_inventory_counts__warehouse_id_branch_id ON inventory.inventory_counts (warehouse_id, branch_id);

CREATE TABLE inventory.inventory_count_lines (
    id            uuid           NOT NULL,
    count_id      uuid           NOT NULL,
    product_id    uuid           NOT NULL,
    system_qty    numeric(18,4)  NOT NULL,
    snapshot_seq  bigint         NOT NULL,
    counted_qty   numeric(18,4),
    expected_qty  numeric(18,4),
    difference    numeric(18,4),
    CONSTRAINT pk_inventory_count_lines PRIMARY KEY (id),
    CONSTRAINT ux_inventory_count_lines__product UNIQUE (count_id, product_id),
    CONSTRAINT fk_inventory_count_lines__count FOREIGN KEY (count_id) REFERENCES inventory.inventory_counts (id) ON DELETE CASCADE,
    CONSTRAINT fk_inventory_count_lines__product FOREIGN KEY (product_id) REFERENCES catalog.products (id),
    CONSTRAINT ck_inventory_count_lines__counted CHECK (counted_qty >= 0)
);

CREATE INDEX ix_inventory_count_lines__product_id ON inventory.inventory_count_lines (product_id);

CREATE TABLE inventory.inventory_count_entries (
    id          uuid           NOT NULL,
    count_id    uuid           NOT NULL,
    product_id  uuid           NOT NULL,
    quantity    numeric(18,4)  NOT NULL,
    round       smallint       NOT NULL,
    location    varchar(40),
    counted_by  uuid           NOT NULL,
    counted_at  timestamptz    NOT NULL,
    CONSTRAINT pk_inventory_count_entries PRIMARY KEY (id),
    CONSTRAINT fk_inventory_count_entries__count FOREIGN KEY (count_id) REFERENCES inventory.inventory_counts (id) ON DELETE CASCADE,
    CONSTRAINT fk_inventory_count_entries__line FOREIGN KEY (count_id, product_id)
        REFERENCES inventory.inventory_count_lines (count_id, product_id) ON DELETE CASCADE,
    CONSTRAINT ck_inventory_count_entries__quantity CHECK (quantity >= 0),
    CONSTRAINT ck_inventory_count_entries__round CHECK (round IN (1, 2))
);

CREATE INDEX ix_inventory_count_entries__count_id_product_id ON inventory.inventory_count_entries (count_id, product_id);

-- -----------------------------------------------------------------------------------------------------
-- Traslados entre bodegas de la MISMA sucursal (FK compuestas con branch_id). La mercancía pasa por la bodega de
-- tránsito de la sucursal entre el despacho y la recepción.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE inventory.stock_transfers (
    id                        uuid          NOT NULL,
    company_id                uuid          NOT NULL,
    branch_id                 uuid          NOT NULL,
    number                    varchar(40)   NOT NULL,
    origin_warehouse_id       uuid          NOT NULL,
    destination_warehouse_id  uuid          NOT NULL,
    status                    varchar(30)   NOT NULL,
    notes                     varchar(500),
    dispatched_at             timestamptz,
    dispatched_by             uuid,
    received_at               timestamptz,
    received_by               uuid,
    cancelled_at              timestamptz,
    created_at                timestamptz   NOT NULL,
    created_by                uuid          NOT NULL,
    updated_at                timestamptz,
    updated_by                uuid,
    CONSTRAINT pk_stock_transfers PRIMARY KEY (id),
    CONSTRAINT fk_stock_transfers__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_stock_transfers__origin FOREIGN KEY (origin_warehouse_id, branch_id) REFERENCES org.warehouses (id, branch_id),
    CONSTRAINT fk_stock_transfers__destination FOREIGN KEY (destination_warehouse_id, branch_id) REFERENCES org.warehouses (id, branch_id),
    CONSTRAINT ck_stock_transfers__distinct CHECK (origin_warehouse_id <> destination_warehouse_id),
    CONSTRAINT ck_stock_transfers__status CHECK (status IN ('DRAFT', 'IN_TRANSIT', 'RECEIVED', 'RECEIVED_WITH_DIFFERENCES', 'CANCELLED')),
    CONSTRAINT ck_stock_transfers__dispatched CHECK (status IN ('DRAFT', 'CANCELLED') OR dispatched_at IS NOT NULL),
    CONSTRAINT ck_stock_transfers__received CHECK ((status IN ('RECEIVED', 'RECEIVED_WITH_DIFFERENCES')) = (received_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_stock_transfers__branch_number ON inventory.stock_transfers (branch_id, number);
CREATE INDEX ix_stock_transfers__branch_id_company_id ON inventory.stock_transfers (branch_id, company_id);
CREATE INDEX ix_stock_transfers__origin_warehouse_id_branch_id ON inventory.stock_transfers (origin_warehouse_id, branch_id);
CREATE INDEX ix_stock_transfers__destination_warehouse_id_branch_id ON inventory.stock_transfers (destination_warehouse_id, branch_id);

CREATE TABLE inventory.stock_transfer_lines (
    id                 uuid           NOT NULL,
    transfer_id        uuid           NOT NULL,
    line_number        integer        NOT NULL,
    product_id         uuid           NOT NULL,
    quantity_sent      numeric(18,4)  NOT NULL,
    quantity_received  numeric(18,4),
    unit_cost          numeric(19,4),
    CONSTRAINT pk_stock_transfer_lines PRIMARY KEY (id),
    CONSTRAINT ux_stock_transfer_lines__product UNIQUE (transfer_id, product_id),
    CONSTRAINT fk_stock_transfer_lines__transfer FOREIGN KEY (transfer_id) REFERENCES inventory.stock_transfers (id) ON DELETE CASCADE,
    CONSTRAINT fk_stock_transfer_lines__product FOREIGN KEY (product_id) REFERENCES catalog.products (id),
    CONSTRAINT ck_stock_transfer_lines__quantities CHECK (quantity_sent > 0
        AND (quantity_received IS NULL OR quantity_received BETWEEN 0 AND quantity_sent)),
    CONSTRAINT ck_stock_transfer_lines__unit_cost CHECK (unit_cost >= 0)
);

CREATE INDEX ix_stock_transfer_lines__product_id ON inventory.stock_transfer_lines (product_id);

-- -----------------------------------------------------------------------------------------------------
-- Verificaciones saldo ↔ kardex (RN-INV-11): cada ejecución y las diferencias encontradas.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE inventory.stock_verification_runs (
    id                uuid          NOT NULL,
    company_id        uuid          NOT NULL,
    node_id           uuid          NOT NULL,
    kind              varchar(10)   NOT NULL,
    started_at        timestamptz   NOT NULL,
    finished_at       timestamptz   NOT NULL,
    checked_balances  integer       NOT NULL,
    discrepancies     integer       NOT NULL,
    details           jsonb         NOT NULL,
    triggered_by      uuid,
    CONSTRAINT pk_stock_verification_runs PRIMARY KEY (id),
    CONSTRAINT fk_stock_verification_runs__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_stock_verification_runs__kind CHECK (kind IN ('AUTOMATIC', 'MANUAL'))
);

CREATE INDEX ix_stock_verification_runs__company_id ON inventory.stock_verification_runs (company_id, started_at DESC);
