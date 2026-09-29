-- =====================================================================================================
-- V2026.10.015 · expenses · Gastos y sus categorías (árbol de dos niveles). Un gasto pagado desde la caja lleva la
-- jornada y nace en la misma transacción que su movimiento de caja EXPENSE. Diseño: docs/fases/fase-06-propuesta.md §4.
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS expenses;

CREATE TABLE expenses.expense_categories (
    id           uuid          NOT NULL,
    company_id   uuid          NOT NULL,
    parent_id    uuid,
    name         varchar(80)   NOT NULL,
    sort_order   integer       NOT NULL DEFAULT 0,
    status       varchar(10)   NOT NULL,
    row_version  bigint        NOT NULL DEFAULT 1,
    created_at   timestamptz   NOT NULL,
    created_by   uuid          NOT NULL,
    updated_at   timestamptz,
    updated_by   uuid,
    deleted_at   timestamptz,
    deleted_by   uuid,
    CONSTRAINT pk_expense_categories PRIMARY KEY (id),
    CONSTRAINT ux_expense_categories__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_expense_categories__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_expense_categories__parent FOREIGN KEY (parent_id, company_id) REFERENCES expenses.expense_categories (id, company_id),
    CONSTRAINT ck_expense_categories__name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_expense_categories__not_self CHECK (parent_id <> id),
    CONSTRAINT ck_expense_categories__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_expense_categories__row_version CHECK (row_version > 0),
    CONSTRAINT ck_expense_categories__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_expense_categories__parent_name ON expenses.expense_categories (company_id, parent_id, lower(name)) NULLS NOT DISTINCT
    WHERE deleted_at IS NULL;
CREATE INDEX ix_expense_categories__parent_id_company_id ON expenses.expense_categories (parent_id, company_id);

CREATE TABLE expenses.expenses (
    id                 uuid           NOT NULL,
    company_id         uuid           NOT NULL,
    branch_id          uuid           NOT NULL,
    number             varchar(40)    NOT NULL,
    business_date      date           NOT NULL,
    category_id        uuid           NOT NULL,
    party_id           uuid,
    description        varchar(300)   NOT NULL,
    amount             numeric(19,2)  NOT NULL,
    tax_amount         numeric(19,2)  NOT NULL DEFAULT 0,
    payment_method_id  uuid           NOT NULL,
    cash_session_id    uuid,
    reference          varchar(60),
    status             varchar(10)    NOT NULL,
    voided_at          timestamptz,
    voided_by          uuid,
    void_reason        varchar(300),
    created_at         timestamptz    NOT NULL,
    created_by         uuid           NOT NULL,
    updated_at         timestamptz,
    updated_by         uuid,
    CONSTRAINT pk_expenses PRIMARY KEY (id),
    CONSTRAINT fk_expenses__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_expenses__category FOREIGN KEY (category_id, company_id) REFERENCES expenses.expense_categories (id, company_id),
    CONSTRAINT fk_expenses__party FOREIGN KEY (party_id, company_id) REFERENCES parties.parties (id, company_id),
    CONSTRAINT fk_expenses__payment_method FOREIGN KEY (payment_method_id, company_id) REFERENCES cash.payment_methods (id, company_id),
    CONSTRAINT fk_expenses__cash_session FOREIGN KEY (cash_session_id, company_id) REFERENCES cash.cash_sessions (id, company_id),
    CONSTRAINT ck_expenses__description CHECK (btrim(description) <> ''),
    CONSTRAINT ck_expenses__amounts CHECK (amount > 0 AND tax_amount >= 0 AND tax_amount <= amount),
    CONSTRAINT ck_expenses__status CHECK (status IN ('POSTED', 'VOIDED')),
    CONSTRAINT ck_expenses__voided CHECK ((status = 'VOIDED') = (voided_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_expenses__branch_number ON expenses.expenses (branch_id, number);
CREATE INDEX ix_expenses__branch_id_company_id ON expenses.expenses (branch_id, company_id);
CREATE INDEX ix_expenses__category_id_company_id ON expenses.expenses (category_id, company_id);
CREATE INDEX ix_expenses__party_id_company_id ON expenses.expenses (party_id, company_id);
CREATE INDEX ix_expenses__payment_method_id_company_id ON expenses.expenses (payment_method_id, company_id);
CREATE INDEX ix_expenses__cash_session_id_company_id ON expenses.expenses (cash_session_id, company_id);
CREATE INDEX ix_expenses__business_date ON expenses.expenses (branch_id, business_date);
