-- =====================================================================================================
-- V2026.10.017 · sales · Ventas, pagos, descuentos y cambios de mercancía (Fase 7, bloques 7.1 y 7.2).
-- Diseño: docs/04-base-de-datos.md §H.8 con los cambios de docs/fases/fase-07-propuesta.md §4.1.
--
-- Principios:
--   · La venta se persiste desde el primer escaneo (D7-01); las líneas eliminadas quedan VOIDED (RN-SAL-06).
--   · Número interno al completar, serie por caja y sin huecos (D7-03); una sola venta en curso por caja.
--   · Snapshot completo de producto, impuestos, promoción y costo en cada línea (D7-07).
--   · Completada ⇒ pagado − cambio = total; solo el efectivo da cambio (D7-08).
--   · Sin devolución de dinero: un cambio de mercancía paga una venta nueva de igual o mayor valor (D7-11); el reintegro
--     por garantía (solo el propietario) es la única salida de dinero, y queda en la caja como CUSTOMER_REFUND.
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS sales;

-- El crédito de un cambio paga la venta nueva con este medio del sistema (no entra al cajón).
ALTER TABLE cash.payment_methods DROP CONSTRAINT ck_payment_methods__kind;
ALTER TABLE cash.payment_methods ADD CONSTRAINT ck_payment_methods__kind
    CHECK (kind IN ('CASH', 'DEBIT_CARD', 'CREDIT_CARD', 'TRANSFER', 'WALLET', 'VOUCHER', 'OTHER', 'EXCHANGE_CREDIT'));

CREATE TABLE sales.sales (
    id                            uuid           NOT NULL,
    company_id                    uuid           NOT NULL,
    branch_id                     uuid           NOT NULL,
    pos_terminal_id               uuid           NOT NULL,
    warehouse_id                  uuid           NOT NULL,
    cash_session_id               uuid           NOT NULL,
    cashier_id                    uuid           NOT NULL,
    business_date                 date           NOT NULL,
    status                        varchar(10)    NOT NULL,
    return_status                 varchar(10)    NOT NULL,
    number                        varchar(40),
    customer_id                   uuid,
    customer_name                 varchar(200)   NOT NULL,
    customer_identification_type  varchar(5)     NOT NULL,
    customer_identification       varchar(30)    NOT NULL,
    customer_email                varchar(254),
    opened_at                     timestamptz    NOT NULL,
    completed_at                  timestamptz,
    completion_key                varchar(100),
    hold_label                    varchar(60),
    held_at                       timestamptz,
    cancel_reason                 varchar(300),
    cancelled_by                  uuid,
    cancel_authorized_by          uuid,
    cancelled_at                  timestamptz,
    void_reason                   varchar(300),
    voided_by                     uuid,
    void_authorized_by            uuid,
    voided_at                     timestamptz,
    exchange_id                   uuid,
    exchange_credit               numeric(19,2)  NOT NULL DEFAULT 0,
    gross                         numeric(19,2)  NOT NULL DEFAULT 0,
    promotion_total               numeric(19,2)  NOT NULL DEFAULT 0,
    discount_total                numeric(19,2)  NOT NULL DEFAULT 0,
    subtotal                      numeric(19,2)  NOT NULL DEFAULT 0,
    tax_total                     numeric(19,2)  NOT NULL DEFAULT 0,
    rounding_adjustment           numeric(19,2)  NOT NULL DEFAULT 0,
    total                         numeric(19,2)  NOT NULL DEFAULT 0,
    paid_total                    numeric(19,2)  NOT NULL DEFAULT 0,
    change_total                  numeric(19,2)  NOT NULL DEFAULT 0,
    created_at                    timestamptz    NOT NULL,
    created_by                    uuid           NOT NULL,
    updated_at                    timestamptz,
    updated_by                    uuid,
    CONSTRAINT pk_sales PRIMARY KEY (id),
    CONSTRAINT ux_sales__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_sales__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_sales__terminal FOREIGN KEY (pos_terminal_id, branch_id) REFERENCES org.pos_terminals (id, branch_id),
    CONSTRAINT fk_sales__warehouse FOREIGN KEY (warehouse_id, branch_id) REFERENCES org.warehouses (id, branch_id),
    CONSTRAINT fk_sales__cash_session FOREIGN KEY (cash_session_id, company_id) REFERENCES cash.cash_sessions (id, company_id),
    CONSTRAINT fk_sales__cashier FOREIGN KEY (cashier_id) REFERENCES identity.users (id),
    CONSTRAINT fk_sales__customer FOREIGN KEY (customer_id, company_id) REFERENCES parties.parties (id, company_id),
    CONSTRAINT fk_sales__cancelled_by FOREIGN KEY (cancelled_by) REFERENCES identity.users (id),
    CONSTRAINT fk_sales__cancel_authorized_by FOREIGN KEY (cancel_authorized_by) REFERENCES identity.users (id),
    CONSTRAINT fk_sales__voided_by FOREIGN KEY (voided_by) REFERENCES identity.users (id),
    CONSTRAINT fk_sales__void_authorized_by FOREIGN KEY (void_authorized_by) REFERENCES identity.users (id),
    CONSTRAINT ck_sales__status CHECK (status IN ('OPEN', 'ON_HOLD', 'COMPLETED', 'CANCELLED', 'VOIDED')),
    CONSTRAINT ck_sales__return_status CHECK (return_status IN ('NONE', 'PARTIAL', 'FULL')),
    CONSTRAINT ck_sales__completed CHECK ((status IN ('COMPLETED', 'VOIDED')) = (number IS NOT NULL AND completed_at IS NOT NULL AND completion_key IS NOT NULL)),
    CONSTRAINT ck_sales__paid CHECK (status NOT IN ('COMPLETED', 'VOIDED') OR paid_total - change_total = total),
    CONSTRAINT ck_sales__hold CHECK (status <> 'ON_HOLD' OR held_at IS NOT NULL),
    CONSTRAINT ck_sales__cancelled CHECK ((status = 'CANCELLED') = (cancelled_at IS NOT NULL AND cancel_reason IS NOT NULL)),
    CONSTRAINT ck_sales__voided CHECK ((status = 'VOIDED') = (voided_at IS NOT NULL AND void_reason IS NOT NULL)),
    CONSTRAINT ck_sales__returns CHECK (return_status = 'NONE' OR status = 'COMPLETED'),
    CONSTRAINT ck_sales__amounts CHECK (gross >= 0 AND promotion_total >= 0 AND discount_total >= 0 AND subtotal >= 0 AND tax_total >= 0 AND total >= 0
        AND paid_total >= 0 AND change_total >= 0 AND exchange_credit >= 0),
    CONSTRAINT ck_sales__exchange CHECK (exchange_id IS NOT NULL OR exchange_credit = 0)
);

CREATE UNIQUE INDEX ux_sales__terminal_number ON sales.sales (pos_terminal_id, number) WHERE number IS NOT NULL;
CREATE UNIQUE INDEX ux_sales__terminal_open ON sales.sales (pos_terminal_id) WHERE status = 'OPEN';
CREATE INDEX ix_sales__branch_id_company_id ON sales.sales (branch_id, company_id);
CREATE INDEX ix_sales__pos_terminal_id_branch_id ON sales.sales (pos_terminal_id, branch_id);
CREATE INDEX ix_sales__warehouse_id_branch_id ON sales.sales (warehouse_id, branch_id);
CREATE INDEX ix_sales__cash_session_id_company_id ON sales.sales (cash_session_id, company_id);
CREATE INDEX ix_sales__cashier_id ON sales.sales (cashier_id);
CREATE INDEX ix_sales__customer_id_company_id ON sales.sales (customer_id, company_id);
CREATE INDEX ix_sales__cancelled_by ON sales.sales (cancelled_by);
CREATE INDEX ix_sales__cancel_authorized_by ON sales.sales (cancel_authorized_by);
CREATE INDEX ix_sales__voided_by ON sales.sales (voided_by);
CREATE INDEX ix_sales__void_authorized_by ON sales.sales (void_authorized_by);
CREATE INDEX ix_sales__exchange_id ON sales.sales (exchange_id);
CREATE INDEX ix_sales__business_date ON sales.sales (branch_id, business_date);
CREATE INDEX ix_sales__unfinished ON sales.sales (pos_terminal_id) WHERE status IN ('OPEN', 'ON_HOLD');

-- -----------------------------------------------------------------------------------------------------
-- Líneas (snapshot). quantity: unidad de venta (presentación o kilos); base_quantity = quantity × factor. unit_cost: por
-- unidad base, del kardex al completar (D7-06). lot_id: lote principal del que salió (a él vuelve un cambio).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE sales.sale_lines (
    id                       uuid           NOT NULL,
    sale_id                  uuid           NOT NULL,
    line_no                  integer        NOT NULL,
    product_id               uuid           NOT NULL,
    sku                      varchar(40)    NOT NULL,
    name                     varchar(200)   NOT NULL,
    scanned_code             varchar(60),
    source                   varchar(15)    NOT NULL,
    base_unit_code           varchar(10)    NOT NULL,
    packaging_id             uuid,
    packaging_name           varchar(60),
    factor                   numeric(19,4)  NOT NULL,
    quantity                 numeric(19,4)  NOT NULL,
    base_quantity            numeric(19,4)  NOT NULL,
    unit_price               numeric(19,2)  NOT NULL,
    price_includes_tax       boolean        NOT NULL,
    price_overridden         boolean        NOT NULL DEFAULT false,
    original_unit_price      numeric(19,2),
    price_authorized_by      uuid,
    category_id              uuid           NOT NULL,
    brand_id                 uuid,
    is_stockable             boolean        NOT NULL,
    allows_decimal_quantity  boolean        NOT NULL,
    allows_open_price        boolean        NOT NULL,
    gross                    numeric(19,2)  NOT NULL DEFAULT 0,
    promotion_id             uuid,
    promotion_name           varchar(80),
    promotion_discount       numeric(19,2)  NOT NULL DEFAULT 0,
    line_discount            numeric(19,2)  NOT NULL DEFAULT 0,
    global_discount_share    numeric(19,2)  NOT NULL DEFAULT 0,
    tax_base                 numeric(19,2)  NOT NULL DEFAULT 0,
    tax_total                numeric(19,2)  NOT NULL DEFAULT 0,
    total                    numeric(19,2)  NOT NULL DEFAULT 0,
    unit_cost                numeric(19,4),
    cost_total               numeric(19,4),
    lot_id                   uuid,
    expired_authorized_by    uuid,
    status                   varchar(10)    NOT NULL,
    voided_by                uuid,
    voided_at                timestamptz,
    returned_quantity        numeric(19,4)  NOT NULL DEFAULT 0,
    CONSTRAINT pk_sale_lines PRIMARY KEY (id),
    CONSTRAINT ux_sale_lines__line UNIQUE (sale_id, line_no),
    CONSTRAINT fk_sale_lines__sale FOREIGN KEY (sale_id) REFERENCES sales.sales (id) ON DELETE CASCADE,
    CONSTRAINT fk_sale_lines__product FOREIGN KEY (product_id) REFERENCES catalog.products (id),
    CONSTRAINT fk_sale_lines__packaging FOREIGN KEY (packaging_id, product_id) REFERENCES catalog.product_packagings (id, product_id),
    CONSTRAINT fk_sale_lines__promotion FOREIGN KEY (promotion_id) REFERENCES promotions.promotions (id),
    CONSTRAINT fk_sale_lines__lot FOREIGN KEY (lot_id) REFERENCES inventory.inventory_lots (id),
    CONSTRAINT fk_sale_lines__price_authorized_by FOREIGN KEY (price_authorized_by) REFERENCES identity.users (id),
    CONSTRAINT fk_sale_lines__expired_authorized_by FOREIGN KEY (expired_authorized_by) REFERENCES identity.users (id),
    CONSTRAINT fk_sale_lines__voided_by FOREIGN KEY (voided_by) REFERENCES identity.users (id),
    CONSTRAINT ck_sale_lines__source CHECK (source IN ('BARCODE', 'SKU', 'SCALE_WEIGHT', 'SCALE_PRICE', 'PRODUCT')),
    CONSTRAINT ck_sale_lines__status CHECK (status IN ('ACTIVE', 'VOIDED')),
    CONSTRAINT ck_sale_lines__voided CHECK ((status = 'VOIDED') = (voided_at IS NOT NULL AND voided_by IS NOT NULL)),
    CONSTRAINT ck_sale_lines__quantity CHECK (quantity > 0 AND factor > 0 AND base_quantity > 0),
    CONSTRAINT ck_sale_lines__amounts CHECK (unit_price >= 0 AND gross >= 0 AND promotion_discount >= 0 AND line_discount >= 0 AND global_discount_share >= 0
        AND tax_total >= 0 AND total >= 0 AND promotion_discount + line_discount + global_discount_share <= gross),
    CONSTRAINT ck_sale_lines__promotion CHECK (promotion_id IS NOT NULL OR promotion_discount = 0),
    CONSTRAINT ck_sale_lines__override CHECK (NOT price_overridden OR original_unit_price IS NOT NULL),
    CONSTRAINT ck_sale_lines__returned CHECK (returned_quantity >= 0 AND returned_quantity <= quantity)
);

CREATE INDEX ix_sale_lines__product_id ON sales.sale_lines (product_id);
CREATE INDEX ix_sale_lines__packaging_id_product_id ON sales.sale_lines (packaging_id, product_id);
CREATE INDEX ix_sale_lines__promotion_id ON sales.sale_lines (promotion_id);
CREATE INDEX ix_sale_lines__lot_id ON sales.sale_lines (lot_id);
CREATE INDEX ix_sale_lines__price_authorized_by ON sales.sale_lines (price_authorized_by);
CREATE INDEX ix_sale_lines__expired_authorized_by ON sales.sale_lines (expired_authorized_by);
CREATE INDEX ix_sale_lines__voided_by ON sales.sale_lines (voided_by);

-- Impuesto de cada línea: tarifa (o valor fijo por unidad base) fijada al escanear; base y valor recalculados.
CREATE TABLE sales.sale_line_taxes (
    id            uuid           NOT NULL,
    sale_line_id  uuid           NOT NULL,
    tax_id        uuid           NOT NULL,
    code          varchar(20)    NOT NULL,
    kind          varchar(20)    NOT NULL,
    rate          numeric(7,4),
    fixed_amount  numeric(19,4),
    tax_base      numeric(19,4)  NOT NULL DEFAULT 0,
    amount        numeric(19,2)  NOT NULL DEFAULT 0,
    CONSTRAINT pk_sale_line_taxes PRIMARY KEY (id),
    CONSTRAINT ux_sale_line_taxes__tax UNIQUE (sale_line_id, tax_id),
    CONSTRAINT fk_sale_line_taxes__line FOREIGN KEY (sale_line_id) REFERENCES sales.sale_lines (id) ON DELETE CASCADE,
    CONSTRAINT fk_sale_line_taxes__tax FOREIGN KEY (tax_id) REFERENCES catalog.taxes (id),
    CONSTRAINT ck_sale_line_taxes__value CHECK ((rate IS NULL) <> (fixed_amount IS NULL) AND amount >= 0)
);

CREATE INDEX ix_sale_line_taxes__tax_id ON sales.sale_line_taxes (tax_id);

-- Pagos aplicados (snapshot del medio). Solo el efectivo da cambio; de la tarjeta, solo franquicia y últimos 4 dígitos.
CREATE TABLE sales.sale_payments (
    id                  uuid           NOT NULL,
    sale_id             uuid           NOT NULL,
    line_no             integer        NOT NULL,
    payment_method_id   uuid           NOT NULL,
    method_code         varchar(20)    NOT NULL,
    method_kind         varchar(15)    NOT NULL,
    affects_cash_drawer boolean        NOT NULL,
    tendered            numeric(19,2)  NOT NULL,
    applied             numeric(19,2)  NOT NULL,
    change              numeric(19,2)  NOT NULL,
    reference           varchar(60),
    card_franchise      varchar(20),
    card_last4          char(4),
    CONSTRAINT pk_sale_payments PRIMARY KEY (id),
    CONSTRAINT ux_sale_payments__line UNIQUE (sale_id, line_no),
    CONSTRAINT fk_sale_payments__sale FOREIGN KEY (sale_id) REFERENCES sales.sales (id) ON DELETE CASCADE,
    CONSTRAINT fk_sale_payments__payment_method FOREIGN KEY (payment_method_id) REFERENCES cash.payment_methods (id),
    CONSTRAINT ck_sale_payments__amounts CHECK (tendered >= 0 AND applied >= 0 AND change >= 0 AND tendered = applied + change),
    CONSTRAINT ck_sale_payments__change CHECK (change = 0 OR affects_cash_drawer),
    CONSTRAINT ck_sale_payments__card CHECK (card_last4 IS NULL OR card_last4 ~ '^[0-9]{4}$')
);

CREATE INDEX ix_sale_payments__payment_method_id ON sales.sale_payments (payment_method_id);

-- Descuentos manuales, siempre autorizados (RN-SAL-04): de línea o global (prorrateado).
CREATE TABLE sales.sale_discounts (
    id             uuid           NOT NULL,
    sale_id        uuid           NOT NULL,
    scope          varchar(10)    NOT NULL,
    sale_line_id   uuid,
    percent        numeric(5,2),
    amount         numeric(19,2),
    reason         varchar(300)   NOT NULL,
    applied_by     uuid           NOT NULL,
    authorized_by  uuid,
    applied_at     timestamptz    NOT NULL,
    status         varchar(10)    NOT NULL,
    CONSTRAINT pk_sale_discounts PRIMARY KEY (id),
    CONSTRAINT fk_sale_discounts__sale FOREIGN KEY (sale_id) REFERENCES sales.sales (id) ON DELETE CASCADE,
    CONSTRAINT fk_sale_discounts__line FOREIGN KEY (sale_line_id) REFERENCES sales.sale_lines (id),
    CONSTRAINT fk_sale_discounts__applied_by FOREIGN KEY (applied_by) REFERENCES identity.users (id),
    CONSTRAINT fk_sale_discounts__authorized_by FOREIGN KEY (authorized_by) REFERENCES identity.users (id),
    CONSTRAINT ck_sale_discounts__scope CHECK ((scope = 'LINE') = (sale_line_id IS NOT NULL) AND scope IN ('LINE', 'GLOBAL')),
    CONSTRAINT ck_sale_discounts__status CHECK (status IN ('ACTIVE', 'REMOVED')),
    CONSTRAINT ck_sale_discounts__value CHECK ((percent IS NULL) <> (amount IS NULL) AND (percent IS NULL OR (percent > 0 AND percent <= 100))
        AND (amount IS NULL OR amount > 0))
);

CREATE INDEX ix_sale_discounts__sale_id ON sales.sale_discounts (sale_id);
CREATE INDEX ix_sale_discounts__sale_line_id ON sales.sale_discounts (sale_line_id);
CREATE INDEX ix_sale_discounts__applied_by ON sales.sale_discounts (applied_by);
CREATE INDEX ix_sale_discounts__authorized_by ON sales.sale_discounts (authorized_by);

-- -----------------------------------------------------------------------------------------------------
-- Cambios de mercancía (EXCHANGE) y reintegros por garantía (WARRANTY_REFUND). Un solo cambio en borrador por venta.
-- La referencia cruzada venta nueva ↔ cambio se verifica al confirmar la transacción (FK diferidas).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE sales.customer_returns (
    id                        uuid           NOT NULL,
    company_id                uuid           NOT NULL,
    branch_id                 uuid           NOT NULL,
    pos_terminal_id           uuid           NOT NULL,
    cash_session_id           uuid,
    original_sale_id          uuid           NOT NULL,
    original_sale_number      varchar(40)    NOT NULL,
    kind                      varchar(20)    NOT NULL,
    status                    varchar(10)    NOT NULL,
    number                    varchar(40),
    business_date             date           NOT NULL,
    reason                    varchar(300)   NOT NULL,
    credit_total              numeric(19,2)  NOT NULL,
    replacement_sale_id       uuid,
    refund_payment_method_id  uuid,
    received_by               uuid           NOT NULL,
    authorized_by             uuid,
    received_at               timestamptz    NOT NULL,
    completed_at              timestamptz,
    cancelled_at              timestamptz,
    created_at                timestamptz    NOT NULL,
    created_by                uuid           NOT NULL,
    updated_at                timestamptz,
    updated_by                uuid,
    CONSTRAINT pk_customer_returns PRIMARY KEY (id),
    CONSTRAINT fk_customer_returns__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_customer_returns__terminal FOREIGN KEY (pos_terminal_id, branch_id) REFERENCES org.pos_terminals (id, branch_id),
    CONSTRAINT fk_customer_returns__cash_session FOREIGN KEY (cash_session_id, company_id) REFERENCES cash.cash_sessions (id, company_id),
    CONSTRAINT fk_customer_returns__original_sale FOREIGN KEY (original_sale_id, company_id) REFERENCES sales.sales (id, company_id),
    CONSTRAINT fk_customer_returns__replacement_sale FOREIGN KEY (replacement_sale_id) REFERENCES sales.sales (id) DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT fk_customer_returns__refund_method FOREIGN KEY (refund_payment_method_id) REFERENCES cash.payment_methods (id),
    CONSTRAINT fk_customer_returns__received_by FOREIGN KEY (received_by) REFERENCES identity.users (id),
    CONSTRAINT fk_customer_returns__authorized_by FOREIGN KEY (authorized_by) REFERENCES identity.users (id),
    CONSTRAINT ck_customer_returns__kind CHECK (kind IN ('EXCHANGE', 'WARRANTY_REFUND')),
    CONSTRAINT ck_customer_returns__status CHECK (status IN ('DRAFT', 'COMPLETED', 'CANCELLED')),
    CONSTRAINT ck_customer_returns__completed CHECK ((status = 'COMPLETED') = (number IS NOT NULL AND completed_at IS NOT NULL AND cash_session_id IS NOT NULL)),
    CONSTRAINT ck_customer_returns__refund CHECK ((kind = 'WARRANTY_REFUND' AND status = 'COMPLETED') = (refund_payment_method_id IS NOT NULL)),
    CONSTRAINT ck_customer_returns__exchange CHECK (kind = 'WARRANTY_REFUND' OR replacement_sale_id IS NOT NULL),
    CONSTRAINT ck_customer_returns__credit CHECK (credit_total > 0)
);

CREATE UNIQUE INDEX ux_customer_returns__terminal_number ON sales.customer_returns (pos_terminal_id, number) WHERE number IS NOT NULL;
CREATE UNIQUE INDEX ux_customer_returns__draft ON sales.customer_returns (original_sale_id) WHERE status = 'DRAFT';
CREATE INDEX ix_customer_returns__branch_id_company_id ON sales.customer_returns (branch_id, company_id);
CREATE INDEX ix_customer_returns__pos_terminal_id_branch_id ON sales.customer_returns (pos_terminal_id, branch_id);
CREATE INDEX ix_customer_returns__cash_session_id_company_id ON sales.customer_returns (cash_session_id, company_id);
CREATE INDEX ix_customer_returns__original_sale_id_company_id ON sales.customer_returns (original_sale_id, company_id);
CREATE INDEX ix_customer_returns__replacement_sale_id ON sales.customer_returns (replacement_sale_id);
CREATE INDEX ix_customer_returns__refund_payment_method_id ON sales.customer_returns (refund_payment_method_id);
CREATE INDEX ix_customer_returns__received_by ON sales.customer_returns (received_by);
CREATE INDEX ix_customer_returns__authorized_by ON sales.customer_returns (authorized_by);

ALTER TABLE sales.sales ADD CONSTRAINT fk_sales__exchange FOREIGN KEY (exchange_id) REFERENCES sales.customer_returns (id) DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE sales.customer_return_lines (
    id                  uuid           NOT NULL,
    customer_return_id  uuid           NOT NULL,
    sale_line_id        uuid           NOT NULL,
    product_id          uuid           NOT NULL,
    sku                 varchar(40)    NOT NULL,
    name                varchar(200)   NOT NULL,
    quantity            numeric(19,4)  NOT NULL,
    base_quantity       numeric(19,4)  NOT NULL,
    unit_cost           numeric(19,4)  NOT NULL,
    lot_id              uuid,
    credit_amount       numeric(19,2)  NOT NULL,
    destination         varchar(20)    NOT NULL,
    CONSTRAINT pk_customer_return_lines PRIMARY KEY (id),
    CONSTRAINT ux_customer_return_lines__sale_line UNIQUE (customer_return_id, sale_line_id),
    CONSTRAINT fk_customer_return_lines__return FOREIGN KEY (customer_return_id) REFERENCES sales.customer_returns (id) ON DELETE CASCADE,
    CONSTRAINT fk_customer_return_lines__sale_line FOREIGN KEY (sale_line_id) REFERENCES sales.sale_lines (id),
    CONSTRAINT fk_customer_return_lines__product FOREIGN KEY (product_id) REFERENCES catalog.products (id),
    CONSTRAINT fk_customer_return_lines__lot FOREIGN KEY (lot_id) REFERENCES inventory.inventory_lots (id),
    CONSTRAINT ck_customer_return_lines__destination CHECK (destination IN ('RETURN_TO_STOCK', 'SEND_TO_DAMAGED', 'DISCARD')),
    CONSTRAINT ck_customer_return_lines__amounts CHECK (quantity > 0 AND base_quantity > 0 AND unit_cost >= 0 AND credit_amount >= 0)
);

CREATE INDEX ix_customer_return_lines__sale_line_id ON sales.customer_return_lines (sale_line_id);
CREATE INDEX ix_customer_return_lines__product_id ON sales.customer_return_lines (product_id);
CREATE INDEX ix_customer_return_lines__lot_id ON sales.customer_return_lines (lot_id);
