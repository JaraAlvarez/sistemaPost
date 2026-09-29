-- =====================================================================================================
-- V2026.10.012 · purchasing · Proveedores, productos del proveedor, órdenes de compra, compras (factura del proveedor)
-- con impuestos y retenciones, cuentas por pagar como libro, pagos y devoluciones a proveedor.
-- Diseño: docs/04-base-de-datos.md §H.7 con los cambios de docs/fases/fase-05-propuesta.md §4.3.
--
-- Principios:
--   · Una compra solo afecta inventario y cartera al contabilizarse (RN-PUR-01); anularla = movimientos inversos.
--   · La cuenta por pagar es un LIBRO: su saldo cambia solo con asientos (payable_entries, solo inserción, D5-09).
--   · Costo de entrada = costo neto por unidad base: (valor − descuento + cargos prorrateados + impuestos no
--     descontables) ÷ cantidad base (D5-03).
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS purchasing;

-- -----------------------------------------------------------------------------------------------------
-- Proveedores: rol proveedor de un tercero (un tercero, un proveedor por empresa).
-- issues_invoices = false → sus compras requieren documento soporte electrónico (D5-12, se emite en la Fase 11-B).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE purchasing.suppliers (
    id                           uuid           NOT NULL,
    company_id                   uuid           NOT NULL,
    party_id                     uuid           NOT NULL,
    code                         varchar(20)    NOT NULL,
    payment_term_days            integer        NOT NULL DEFAULT 0,
    preferred_payment_method_id  uuid,
    credit_limit                 numeric(19,2),
    issues_invoices              boolean        NOT NULL DEFAULT true,
    notes                        varchar(500),
    status                       varchar(10)    NOT NULL,
    row_version                  bigint         NOT NULL DEFAULT 1,
    created_at                   timestamptz    NOT NULL,
    created_by                   uuid           NOT NULL,
    updated_at                   timestamptz,
    updated_by                   uuid,
    deleted_at                   timestamptz,
    deleted_by                   uuid,
    CONSTRAINT pk_suppliers PRIMARY KEY (id),
    CONSTRAINT ux_suppliers__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_suppliers__party FOREIGN KEY (party_id, company_id) REFERENCES parties.parties (id, company_id),
    CONSTRAINT fk_suppliers__payment_method FOREIGN KEY (preferred_payment_method_id, company_id)
        REFERENCES cash.payment_methods (id, company_id),
    CONSTRAINT ck_suppliers__code CHECK (code ~ '^[A-Z0-9_-]{1,20}$'),
    CONSTRAINT ck_suppliers__payment_term CHECK (payment_term_days BETWEEN 0 AND 365),
    CONSTRAINT ck_suppliers__credit_limit CHECK (credit_limit >= 0),
    CONSTRAINT ck_suppliers__status CHECK (status IN ('ACTIVE', 'BLOCKED', 'INACTIVE')),
    CONSTRAINT ck_suppliers__row_version CHECK (row_version > 0),
    CONSTRAINT ck_suppliers__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_suppliers__company_code ON purchasing.suppliers (company_id, code) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX ux_suppliers__company_party ON purchasing.suppliers (company_id, party_id) WHERE deleted_at IS NULL;
CREATE INDEX ix_suppliers__party_id_company_id ON purchasing.suppliers (party_id, company_id);
CREATE INDEX ix_suppliers__preferred_payment_method_id_company_id ON purchasing.suppliers (preferred_payment_method_id, company_id);

-- Productos que suministra cada proveedor: código del proveedor, presentación habitual y último costo (por unidad base).
CREATE TABLE purchasing.supplier_products (
    id                uuid           NOT NULL,
    company_id        uuid           NOT NULL,
    supplier_id       uuid           NOT NULL,
    product_id        uuid           NOT NULL,
    packaging_id      uuid,
    supplier_code     varchar(40),
    last_cost         numeric(19,4),
    last_purchase_at  timestamptz,
    lead_time_days    integer,
    is_preferred      boolean        NOT NULL DEFAULT false,
    row_version       bigint         NOT NULL DEFAULT 1,
    created_at        timestamptz    NOT NULL,
    created_by        uuid           NOT NULL,
    updated_at        timestamptz,
    updated_by        uuid,
    deleted_at        timestamptz,
    deleted_by        uuid,
    CONSTRAINT pk_supplier_products PRIMARY KEY (id),
    CONSTRAINT fk_supplier_products__supplier FOREIGN KEY (supplier_id, company_id) REFERENCES purchasing.suppliers (id, company_id),
    CONSTRAINT fk_supplier_products__product FOREIGN KEY (product_id, company_id) REFERENCES catalog.products (id, company_id),
    CONSTRAINT fk_supplier_products__packaging FOREIGN KEY (packaging_id, product_id) REFERENCES catalog.product_packagings (id, product_id),
    CONSTRAINT ck_supplier_products__last_cost CHECK (last_cost >= 0),
    CONSTRAINT ck_supplier_products__lead_time CHECK (lead_time_days BETWEEN 0 AND 365),
    CONSTRAINT ck_supplier_products__row_version CHECK (row_version > 0),
    CONSTRAINT ck_supplier_products__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_supplier_products__supplier_product ON purchasing.supplier_products (supplier_id, product_id) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX ux_supplier_products__supplier_code ON purchasing.supplier_products (supplier_id, supplier_code)
    WHERE deleted_at IS NULL AND supplier_code IS NOT NULL;
CREATE INDEX ix_supplier_products__supplier_id_company_id ON purchasing.supplier_products (supplier_id, company_id);
CREATE INDEX ix_supplier_products__product_id_company_id ON purchasing.supplier_products (product_id, company_id);
CREATE INDEX ix_supplier_products__packaging_id_product_id ON purchasing.supplier_products (packaging_id, product_id);

-- -----------------------------------------------------------------------------------------------------
-- Órdenes de compra. Cantidades en presentación (quantity) y en unidad base (base_quantity = quantity × factor);
-- lo recibido se lleva en unidad base (RN-PUR-04: recibido ≤ pedido + tolerancia).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE purchasing.purchase_orders (
    id             uuid           NOT NULL,
    company_id     uuid           NOT NULL,
    branch_id      uuid           NOT NULL,
    warehouse_id   uuid           NOT NULL,
    supplier_id    uuid           NOT NULL,
    number         varchar(40)    NOT NULL,
    order_date     date           NOT NULL,
    expected_date  date,
    status         varchar(20)    NOT NULL,
    notes          varchar(500),
    total          numeric(19,2)  NOT NULL DEFAULT 0,
    approved_at    timestamptz,
    approved_by    uuid,
    sent_at        timestamptz,
    closed_at      timestamptz,
    cancelled_at   timestamptz,
    created_at     timestamptz    NOT NULL,
    created_by     uuid           NOT NULL,
    updated_at     timestamptz,
    updated_by     uuid,
    CONSTRAINT pk_purchase_orders PRIMARY KEY (id),
    CONSTRAINT ux_purchase_orders__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_purchase_orders__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_purchase_orders__warehouse FOREIGN KEY (warehouse_id, branch_id) REFERENCES org.warehouses (id, branch_id),
    CONSTRAINT fk_purchase_orders__supplier FOREIGN KEY (supplier_id, company_id) REFERENCES purchasing.suppliers (id, company_id),
    CONSTRAINT ck_purchase_orders__status CHECK (status IN ('DRAFT', 'APPROVED', 'SENT', 'PARTIALLY_RECEIVED', 'RECEIVED', 'CLOSED', 'CANCELLED')),
    CONSTRAINT ck_purchase_orders__approved CHECK (status IN ('DRAFT', 'CANCELLED') OR approved_at IS NOT NULL),
    CONSTRAINT ck_purchase_orders__dates CHECK (expected_date IS NULL OR expected_date >= order_date)
);

CREATE UNIQUE INDEX ux_purchase_orders__branch_number ON purchasing.purchase_orders (branch_id, number);
CREATE INDEX ix_purchase_orders__branch_id_company_id ON purchasing.purchase_orders (branch_id, company_id);
CREATE INDEX ix_purchase_orders__warehouse_id_branch_id ON purchasing.purchase_orders (warehouse_id, branch_id);
CREATE INDEX ix_purchase_orders__supplier_id_company_id ON purchasing.purchase_orders (supplier_id, company_id);

CREATE TABLE purchasing.purchase_order_lines (
    id                      uuid           NOT NULL,
    order_id                uuid           NOT NULL,
    line_number             integer        NOT NULL,
    product_id              uuid           NOT NULL,
    packaging_id            uuid,
    factor                  numeric(18,4)  NOT NULL,
    quantity                numeric(18,4)  NOT NULL,
    base_quantity           numeric(18,4)  NOT NULL,
    unit_cost               numeric(19,4)  NOT NULL,
    received_base_quantity  numeric(18,4)  NOT NULL DEFAULT 0,
    CONSTRAINT pk_purchase_order_lines PRIMARY KEY (id),
    CONSTRAINT ux_purchase_order_lines__product UNIQUE NULLS NOT DISTINCT (order_id, product_id, packaging_id),
    CONSTRAINT fk_purchase_order_lines__order FOREIGN KEY (order_id) REFERENCES purchasing.purchase_orders (id) ON DELETE CASCADE,
    CONSTRAINT fk_purchase_order_lines__product FOREIGN KEY (product_id) REFERENCES catalog.products (id),
    CONSTRAINT fk_purchase_order_lines__packaging FOREIGN KEY (packaging_id, product_id) REFERENCES catalog.product_packagings (id, product_id),
    CONSTRAINT ck_purchase_order_lines__quantities CHECK (factor > 0 AND quantity > 0 AND base_quantity > 0 AND received_base_quantity >= 0),
    CONSTRAINT ck_purchase_order_lines__unit_cost CHECK (unit_cost >= 0)
);

CREATE INDEX ix_purchase_order_lines__product_id ON purchasing.purchase_order_lines (product_id);
CREATE INDEX ix_purchase_order_lines__packaging_id_product_id ON purchasing.purchase_order_lines (packaging_id, product_id);

-- -----------------------------------------------------------------------------------------------------
-- Compras: la factura del proveedor (número único por proveedor, RN-PUR-02) con la mercancía recibida.
-- invoice_total = total que dice la factura; debe cuadrar con el total calculado antes de contabilizar.
-- payable_total = total − retenciones (lo que efectivamente se le debe al proveedor).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE purchasing.purchases (
    id                         uuid           NOT NULL,
    company_id                 uuid           NOT NULL,
    branch_id                  uuid           NOT NULL,
    warehouse_id               uuid           NOT NULL,
    supplier_id                uuid           NOT NULL,
    purchase_order_id          uuid,
    number                     varchar(40)    NOT NULL,
    supplier_invoice_number    varchar(40)    NOT NULL,
    invoice_date               date           NOT NULL,
    business_date              date           NOT NULL,
    due_date                   date           NOT NULL,
    payment_mode               varchar(10)    NOT NULL,
    payment_method_id          uuid,
    payment_reference          varchar(60),
    requires_support_document  boolean        NOT NULL DEFAULT false,
    invoice_total              numeric(19,2),
    proration_method           varchar(10)    NOT NULL,
    charges_total              numeric(19,2)  NOT NULL DEFAULT 0,
    charges_notes              varchar(200),
    subtotal                   numeric(19,2)  NOT NULL DEFAULT 0,
    discount_total             numeric(19,2)  NOT NULL DEFAULT 0,
    tax_total                  numeric(19,2)  NOT NULL DEFAULT 0,
    deductible_tax_total       numeric(19,2)  NOT NULL DEFAULT 0,
    withholding_total          numeric(19,2)  NOT NULL DEFAULT 0,
    total                      numeric(19,2)  NOT NULL DEFAULT 0,
    payable_total              numeric(19,2)  NOT NULL DEFAULT 0,
    status                     varchar(10)    NOT NULL,
    notes                      varchar(500),
    posted_at                  timestamptz,
    posted_by                  uuid,
    voided_at                  timestamptz,
    voided_by                  uuid,
    void_reason                varchar(300),
    created_at                 timestamptz    NOT NULL,
    created_by                 uuid           NOT NULL,
    updated_at                 timestamptz,
    updated_by                 uuid,
    CONSTRAINT pk_purchases PRIMARY KEY (id),
    CONSTRAINT ux_purchases__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_purchases__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_purchases__warehouse FOREIGN KEY (warehouse_id, branch_id) REFERENCES org.warehouses (id, branch_id),
    CONSTRAINT fk_purchases__supplier FOREIGN KEY (supplier_id, company_id) REFERENCES purchasing.suppliers (id, company_id),
    CONSTRAINT fk_purchases__order FOREIGN KEY (purchase_order_id, company_id) REFERENCES purchasing.purchase_orders (id, company_id),
    CONSTRAINT fk_purchases__payment_method FOREIGN KEY (payment_method_id, company_id) REFERENCES cash.payment_methods (id, company_id),
    CONSTRAINT ck_purchases__invoice_number CHECK (btrim(supplier_invoice_number) <> ''),
    CONSTRAINT ck_purchases__payment_mode CHECK (payment_mode IN ('CASH', 'CREDIT')),
    CONSTRAINT ck_purchases__cash_method CHECK (payment_mode = 'CREDIT' OR status = 'DRAFT' OR payment_method_id IS NOT NULL),
    CONSTRAINT ck_purchases__proration CHECK (proration_method IN ('VALUE', 'QUANTITY', 'MANUAL')),
    CONSTRAINT ck_purchases__due_date CHECK (due_date >= invoice_date),
    CONSTRAINT ck_purchases__amounts CHECK (charges_total >= 0 AND discount_total >= 0 AND tax_total >= 0 AND withholding_total >= 0
        AND deductible_tax_total BETWEEN 0 AND tax_total AND invoice_total >= 0),
    CONSTRAINT ck_purchases__status CHECK (status IN ('DRAFT', 'POSTED', 'VOIDED')),
    CONSTRAINT ck_purchases__posted CHECK (status = 'DRAFT' OR posted_at IS NOT NULL),
    CONSTRAINT ck_purchases__voided CHECK ((status = 'VOIDED') = (voided_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_purchases__branch_number ON purchasing.purchases (branch_id, number);
CREATE UNIQUE INDEX ux_purchases__supplier_invoice ON purchasing.purchases (supplier_id, supplier_invoice_number) WHERE status <> 'VOIDED';
CREATE INDEX ix_purchases__branch_id_company_id ON purchasing.purchases (branch_id, company_id);
CREATE INDEX ix_purchases__warehouse_id_branch_id ON purchasing.purchases (warehouse_id, branch_id);
CREATE INDEX ix_purchases__supplier_id_company_id ON purchasing.purchases (supplier_id, company_id);
CREATE INDEX ix_purchases__purchase_order_id_company_id ON purchasing.purchases (purchase_order_id, company_id);
CREATE INDEX ix_purchases__payment_method_id_company_id ON purchasing.purchases (payment_method_id, company_id);
CREATE INDEX ix_purchases__support_document ON purchasing.purchases (company_id) WHERE requires_support_document AND status = 'POSTED';

-- Líneas: quantity en la presentación facturada; base_quantity en la unidad base. net_unit_cost = costo neto por
-- unidad base con que entra al kardex; returned_base_quantity = lo ya devuelto al proveedor (RN-PUR-06).
CREATE TABLE purchasing.purchase_lines (
    id                      uuid           NOT NULL,
    purchase_id             uuid           NOT NULL,
    line_number             integer        NOT NULL,
    product_id              uuid           NOT NULL,
    packaging_id            uuid,
    factor                  numeric(18,4)  NOT NULL,
    quantity                numeric(18,4)  NOT NULL,
    base_quantity           numeric(18,4)  NOT NULL,
    unit_cost               numeric(19,4)  NOT NULL,
    gross_amount            numeric(19,2)  NOT NULL,
    discount_amount         numeric(19,2)  NOT NULL DEFAULT 0,
    charges_amount          numeric(19,2)  NOT NULL DEFAULT 0,
    tax_amount              numeric(19,2)  NOT NULL DEFAULT 0,
    non_deductible_tax      numeric(19,2)  NOT NULL DEFAULT 0,
    line_total              numeric(19,2)  NOT NULL,
    net_unit_cost           numeric(19,4)  NOT NULL,
    lot_number              varchar(40),
    expiry_date             date,
    lot_id                  uuid,
    order_line_id           uuid,
    returned_base_quantity  numeric(18,4)  NOT NULL DEFAULT 0,
    CONSTRAINT pk_purchase_lines PRIMARY KEY (id),
    CONSTRAINT fk_purchase_lines__purchase FOREIGN KEY (purchase_id) REFERENCES purchasing.purchases (id) ON DELETE CASCADE,
    CONSTRAINT fk_purchase_lines__product FOREIGN KEY (product_id) REFERENCES catalog.products (id),
    CONSTRAINT fk_purchase_lines__packaging FOREIGN KEY (packaging_id, product_id) REFERENCES catalog.product_packagings (id, product_id),
    CONSTRAINT fk_purchase_lines__lot FOREIGN KEY (lot_id) REFERENCES inventory.inventory_lots (id),
    CONSTRAINT fk_purchase_lines__order_line FOREIGN KEY (order_line_id) REFERENCES purchasing.purchase_order_lines (id),
    CONSTRAINT ck_purchase_lines__quantities CHECK (factor > 0 AND quantity > 0 AND base_quantity > 0
        AND returned_base_quantity BETWEEN 0 AND base_quantity),
    CONSTRAINT ck_purchase_lines__amounts CHECK (unit_cost >= 0 AND gross_amount >= 0 AND discount_amount BETWEEN 0 AND gross_amount
        AND charges_amount >= 0 AND tax_amount >= 0 AND non_deductible_tax BETWEEN 0 AND tax_amount AND net_unit_cost >= 0),
    CONSTRAINT ck_purchase_lines__lot CHECK (lot_number IS NULL OR btrim(lot_number) <> '')
);

CREATE INDEX ix_purchase_lines__purchase_id ON purchasing.purchase_lines (purchase_id);
CREATE INDEX ix_purchase_lines__product_id ON purchasing.purchase_lines (product_id);
CREATE INDEX ix_purchase_lines__packaging_id_product_id ON purchasing.purchase_lines (packaging_id, product_id);
CREATE INDEX ix_purchase_lines__lot_id ON purchasing.purchase_lines (lot_id);
CREATE INDEX ix_purchase_lines__order_line_id ON purchasing.purchase_lines (order_line_id);

-- Impuestos de cada línea con su tarifa en la fecha de la factura; is_deductible = IVA descontable (no es costo).
CREATE TABLE purchasing.purchase_line_taxes (
    id                uuid           NOT NULL,
    purchase_line_id  uuid           NOT NULL,
    tax_id            uuid,
    tax_code          varchar(20)    NOT NULL,
    is_vat            boolean        NOT NULL,
    rate              numeric(7,4),
    fixed_amount      numeric(19,4),
    base              numeric(19,2)  NOT NULL,
    amount            numeric(19,2)  NOT NULL,
    is_deductible     boolean        NOT NULL,
    CONSTRAINT pk_purchase_line_taxes PRIMARY KEY (id),
    CONSTRAINT ux_purchase_line_taxes__code UNIQUE (purchase_line_id, tax_code),
    CONSTRAINT fk_purchase_line_taxes__line FOREIGN KEY (purchase_line_id) REFERENCES purchasing.purchase_lines (id) ON DELETE CASCADE,
    CONSTRAINT fk_purchase_line_taxes__tax FOREIGN KEY (tax_id) REFERENCES catalog.taxes (id),
    CONSTRAINT ck_purchase_line_taxes__rate CHECK ((rate IS NULL) <> (fixed_amount IS NULL) AND rate >= 0 AND fixed_amount >= 0),
    CONSTRAINT ck_purchase_line_taxes__amounts CHECK (base >= 0 AND amount >= 0),
    CONSTRAINT ck_purchase_line_taxes__deductible CHECK (NOT is_deductible OR is_vat)
);

CREATE INDEX ix_purchase_line_taxes__tax_id ON purchasing.purchase_line_taxes (tax_id);

-- Retenciones digitadas por la compra (v1, D5-11): reducen lo que se paga, no el costo.
CREATE TABLE purchasing.purchase_withholdings (
    id           uuid           NOT NULL,
    purchase_id  uuid           NOT NULL,
    kind         varchar(12)    NOT NULL,
    base         numeric(19,2)  NOT NULL,
    rate         numeric(7,4),
    amount       numeric(19,2)  NOT NULL,
    CONSTRAINT pk_purchase_withholdings PRIMARY KEY (id),
    CONSTRAINT ux_purchase_withholdings__kind UNIQUE (purchase_id, kind),
    CONSTRAINT fk_purchase_withholdings__purchase FOREIGN KEY (purchase_id) REFERENCES purchasing.purchases (id) ON DELETE CASCADE,
    CONSTRAINT ck_purchase_withholdings__kind CHECK (kind IN ('RETEFUENTE', 'RETEIVA', 'RETEICA')),
    CONSTRAINT ck_purchase_withholdings__amounts CHECK (base >= 0 AND amount > 0 AND (rate IS NULL OR rate BETWEEN 0 AND 100))
);

-- -----------------------------------------------------------------------------------------------------
-- Cuentas por pagar: una por compra contabilizada. balance = Σ asientos; nunca se edita a mano (D5-09).
-- Saldo negativo = saldo a favor de la empresa (p. ej. devolución después de pagar).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE purchasing.accounts_payable (
    id               uuid           NOT NULL,
    company_id       uuid           NOT NULL,
    branch_id        uuid           NOT NULL,
    supplier_id      uuid           NOT NULL,
    purchase_id      uuid           NOT NULL,
    document_number  varchar(40)    NOT NULL,
    issue_date       date           NOT NULL,
    due_date         date           NOT NULL,
    original_amount  numeric(19,2)  NOT NULL,
    balance          numeric(19,2)  NOT NULL,
    status           varchar(10)    NOT NULL,
    created_at       timestamptz    NOT NULL,
    created_by       uuid           NOT NULL,
    updated_at       timestamptz,
    updated_by       uuid,
    CONSTRAINT pk_accounts_payable PRIMARY KEY (id),
    CONSTRAINT ux_accounts_payable__id_company UNIQUE (id, company_id),
    CONSTRAINT ux_accounts_payable__purchase UNIQUE (purchase_id),
    CONSTRAINT fk_accounts_payable__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_accounts_payable__supplier FOREIGN KEY (supplier_id, company_id) REFERENCES purchasing.suppliers (id, company_id),
    CONSTRAINT fk_accounts_payable__purchase FOREIGN KEY (purchase_id, company_id) REFERENCES purchasing.purchases (id, company_id),
    CONSTRAINT ck_accounts_payable__amounts CHECK (original_amount >= 0),
    CONSTRAINT ck_accounts_payable__status CHECK (status IN ('OPEN', 'SETTLED', 'VOIDED'))
);

CREATE INDEX ix_accounts_payable__branch_id_company_id ON purchasing.accounts_payable (branch_id, company_id);
CREATE INDEX ix_accounts_payable__supplier_id_company_id ON purchasing.accounts_payable (supplier_id, company_id);
CREATE INDEX ix_accounts_payable__purchase_id_company_id ON purchasing.accounts_payable (purchase_id, company_id);
CREATE INDEX ix_accounts_payable__open ON purchasing.accounts_payable (supplier_id, due_date) WHERE status = 'OPEN';

-- Libro de la cuenta (solo inserción). amount > 0 aumenta la deuda (cargo inicial, anulación de un pago, reintegro,
-- reposición); amount < 0 la disminuye (pago, devolución, anulación de la compra).
CREATE TABLE purchasing.payable_entries (
    id             uuid           NOT NULL,
    account_id     uuid           NOT NULL,
    entry_type     varchar(15)    NOT NULL,
    amount         numeric(19,2)  NOT NULL,
    balance_after  numeric(19,2)  NOT NULL,
    source_type    varchar(30)    NOT NULL,
    source_id      uuid           NOT NULL,
    source_number  varchar(40),
    occurred_at    timestamptz    NOT NULL,
    user_id        uuid           NOT NULL,
    CONSTRAINT pk_payable_entries PRIMARY KEY (id),
    CONSTRAINT fk_payable_entries__account FOREIGN KEY (account_id) REFERENCES purchasing.accounts_payable (id),
    CONSTRAINT ck_payable_entries__type CHECK (entry_type IN ('CHARGE', 'PAYMENT', 'PAYMENT_VOID', 'RETURN', 'REFUND', 'REPLACEMENT', 'VOID')),
    CONSTRAINT ck_payable_entries__amount CHECK (amount <> 0),
    CONSTRAINT ck_payable_entries__sign CHECK (
        (entry_type IN ('CHARGE', 'PAYMENT_VOID', 'REFUND', 'REPLACEMENT') AND amount > 0)
        OR (entry_type IN ('PAYMENT', 'RETURN', 'VOID') AND amount < 0))
);

CREATE INDEX ix_payable_entries__account_id ON purchasing.payable_entries (account_id, occurred_at);
CREATE INDEX ix_payable_entries__source ON purchasing.payable_entries (source_type, source_id);

CREATE TRIGGER trg_payable_entries_append_only
    BEFORE UPDATE OR DELETE ON purchasing.payable_entries
    FOR EACH ROW EXECUTE FUNCTION inventory.fn_append_only();

-- Pagos a proveedor (transferencia, cheque, efectivo fuera de caja): uno puede cubrir varias cuentas del mismo
-- proveedor. cash_session_id queda reservado para el pago desde la caja (Fase 6). Se anulan, nunca se borran.
CREATE TABLE purchasing.payable_payments (
    id                 uuid           NOT NULL,
    company_id         uuid           NOT NULL,
    branch_id          uuid           NOT NULL,
    supplier_id        uuid           NOT NULL,
    number             varchar(40)    NOT NULL,
    payment_date       date           NOT NULL,
    payment_method_id  uuid           NOT NULL,
    reference          varchar(60),
    amount             numeric(19,2)  NOT NULL,
    notes              varchar(500),
    status             varchar(10)    NOT NULL,
    cash_session_id    uuid,
    voided_at          timestamptz,
    voided_by          uuid,
    void_reason        varchar(300),
    created_at         timestamptz    NOT NULL,
    created_by         uuid           NOT NULL,
    updated_at         timestamptz,
    updated_by         uuid,
    CONSTRAINT pk_payable_payments PRIMARY KEY (id),
    CONSTRAINT fk_payable_payments__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_payable_payments__supplier FOREIGN KEY (supplier_id, company_id) REFERENCES purchasing.suppliers (id, company_id),
    CONSTRAINT fk_payable_payments__payment_method FOREIGN KEY (payment_method_id, company_id) REFERENCES cash.payment_methods (id, company_id),
    CONSTRAINT ck_payable_payments__amount CHECK (amount > 0),
    CONSTRAINT ck_payable_payments__status CHECK (status IN ('POSTED', 'VOIDED')),
    CONSTRAINT ck_payable_payments__voided CHECK ((status = 'VOIDED') = (voided_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_payable_payments__branch_number ON purchasing.payable_payments (branch_id, number);
CREATE INDEX ix_payable_payments__branch_id_company_id ON purchasing.payable_payments (branch_id, company_id);
CREATE INDEX ix_payable_payments__supplier_id_company_id ON purchasing.payable_payments (supplier_id, company_id);
CREATE INDEX ix_payable_payments__payment_method_id_company_id ON purchasing.payable_payments (payment_method_id, company_id);

CREATE TABLE purchasing.payable_payment_allocations (
    id          uuid           NOT NULL,
    payment_id  uuid           NOT NULL,
    account_id  uuid           NOT NULL,
    amount      numeric(19,2)  NOT NULL,
    CONSTRAINT pk_payable_payment_allocations PRIMARY KEY (id),
    CONSTRAINT ux_payable_payment_allocations__account UNIQUE (payment_id, account_id),
    CONSTRAINT fk_payable_payment_allocations__payment FOREIGN KEY (payment_id) REFERENCES purchasing.payable_payments (id) ON DELETE CASCADE,
    CONSTRAINT fk_payable_payment_allocations__account FOREIGN KEY (account_id) REFERENCES purchasing.accounts_payable (id),
    CONSTRAINT ck_payable_payment_allocations__amount CHECK (amount > 0)
);

CREATE INDEX ix_payable_payment_allocations__account_id ON purchasing.payable_payment_allocations (account_id);

-- -----------------------------------------------------------------------------------------------------
-- Devoluciones a proveedor contra una compra: salen al costo de la compra (D5-07) y del lote original.
-- total = valor al costo (kardex); credit_total = lo que se descuenta de la cuenta por pagar (proporción del total de la
-- línea con sus impuestos). settlement: cómo el proveedor la liquidó (nota crédito, reintegro o reposición).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE purchasing.supplier_returns (
    id                    uuid           NOT NULL,
    company_id            uuid           NOT NULL,
    branch_id             uuid           NOT NULL,
    warehouse_id          uuid           NOT NULL,
    supplier_id           uuid           NOT NULL,
    purchase_id           uuid           NOT NULL,
    number                varchar(40)    NOT NULL,
    business_date         date           NOT NULL,
    reason                varchar(300)   NOT NULL,
    status                varchar(10)    NOT NULL,
    total                 numeric(19,2)  NOT NULL DEFAULT 0,
    credit_total          numeric(19,2)  NOT NULL DEFAULT 0,
    settlement            varchar(12),
    settlement_reference  varchar(60),
    posted_at             timestamptz,
    posted_by             uuid,
    settled_at            timestamptz,
    settled_by            uuid,
    cancelled_at          timestamptz,
    created_at            timestamptz    NOT NULL,
    created_by            uuid           NOT NULL,
    updated_at            timestamptz,
    updated_by            uuid,
    CONSTRAINT pk_supplier_returns PRIMARY KEY (id),
    CONSTRAINT fk_supplier_returns__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_supplier_returns__warehouse FOREIGN KEY (warehouse_id, branch_id) REFERENCES org.warehouses (id, branch_id),
    CONSTRAINT fk_supplier_returns__supplier FOREIGN KEY (supplier_id, company_id) REFERENCES purchasing.suppliers (id, company_id),
    CONSTRAINT fk_supplier_returns__purchase FOREIGN KEY (purchase_id, company_id) REFERENCES purchasing.purchases (id, company_id),
    CONSTRAINT ck_supplier_returns__reason CHECK (btrim(reason) <> ''),
    CONSTRAINT ck_supplier_returns__status CHECK (status IN ('DRAFT', 'POSTED', 'SETTLED', 'CANCELLED')),
    CONSTRAINT ck_supplier_returns__settlement CHECK (settlement IN ('CREDIT_NOTE', 'REFUND', 'REPLACEMENT')),
    CONSTRAINT ck_supplier_returns__settled CHECK ((status = 'SETTLED') = (settled_at IS NOT NULL) AND (status = 'SETTLED') = (settlement IS NOT NULL)),
    CONSTRAINT ck_supplier_returns__posted CHECK (status IN ('DRAFT', 'CANCELLED') OR posted_at IS NOT NULL)
);

CREATE UNIQUE INDEX ux_supplier_returns__branch_number ON purchasing.supplier_returns (branch_id, number);
CREATE INDEX ix_supplier_returns__branch_id_company_id ON purchasing.supplier_returns (branch_id, company_id);
CREATE INDEX ix_supplier_returns__warehouse_id_branch_id ON purchasing.supplier_returns (warehouse_id, branch_id);
CREATE INDEX ix_supplier_returns__supplier_id_company_id ON purchasing.supplier_returns (supplier_id, company_id);
CREATE INDEX ix_supplier_returns__purchase_id_company_id ON purchasing.supplier_returns (purchase_id, company_id);

CREATE TABLE purchasing.supplier_return_lines (
    id                uuid           NOT NULL,
    return_id         uuid           NOT NULL,
    line_number       integer        NOT NULL,
    purchase_line_id  uuid           NOT NULL,
    product_id        uuid           NOT NULL,
    base_quantity     numeric(18,4)  NOT NULL,
    unit_cost         numeric(19,4)  NOT NULL,
    total             numeric(19,2)  NOT NULL,
    credit_amount     numeric(19,2)  NOT NULL,
    lot_id            uuid,
    CONSTRAINT pk_supplier_return_lines PRIMARY KEY (id),
    CONSTRAINT ux_supplier_return_lines__purchase_line UNIQUE (return_id, purchase_line_id),
    CONSTRAINT fk_supplier_return_lines__return FOREIGN KEY (return_id) REFERENCES purchasing.supplier_returns (id) ON DELETE CASCADE,
    CONSTRAINT fk_supplier_return_lines__purchase_line FOREIGN KEY (purchase_line_id) REFERENCES purchasing.purchase_lines (id),
    CONSTRAINT fk_supplier_return_lines__product FOREIGN KEY (product_id) REFERENCES catalog.products (id),
    CONSTRAINT fk_supplier_return_lines__lot FOREIGN KEY (lot_id) REFERENCES inventory.inventory_lots (id),
    CONSTRAINT ck_supplier_return_lines__quantity CHECK (base_quantity > 0),
    CONSTRAINT ck_supplier_return_lines__amounts CHECK (unit_cost >= 0 AND total >= 0 AND credit_amount >= 0)
);

CREATE INDEX ix_supplier_return_lines__purchase_line_id ON purchasing.supplier_return_lines (purchase_line_id);
CREATE INDEX ix_supplier_return_lines__product_id ON purchasing.supplier_return_lines (product_id);
CREATE INDEX ix_supplier_return_lines__lot_id ON purchasing.supplier_return_lines (lot_id);
