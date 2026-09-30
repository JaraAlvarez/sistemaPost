-- =====================================================================================================
-- V2026.10.025 · purchasing · Mejoras de proveedores (Fase 8, bloque 8.4): pedido mínimo, agenda de visita/pedido/
-- entrega, cuentas bancarias con verificación y retenciones sugeridas. Catálogo de bancos (ref.banks).
-- Diseño: docs/fases/fase-08-propuesta.md §4.5, §5.5, D8-14, RN-PUR-09 y RN-PUR-10.
--
-- Principios:
--   · Cuenta bancaria nueva o modificada → PENDING_VERIFICATION; la verifica OTRO usuario con el permiso
--     purchasing.supplier.bank_manage (RN-PUR-09). changed_by guarda quién hizo el último cambio sensible.
--   · Las retenciones sugeridas solo pre-llenan la compra en borrador; se contabilizan al confirmar la compra (RN-PUR-10).
--   · Todas las tablas son maestros sincronizables (row_version) de la empresa.
-- =====================================================================================================

-- -----------------------------------------------------------------------------------------------------
-- Catálogo de bancos de Colombia (solo lectura para pos_app; se siembra en R__ref__banks.sql).
-- code = código de compensación ACH / Superintendencia Financiera. country_code sin clave foránea a ref.countries:
-- los repetibles corren en orden alfabético y R__ref__banks se ejecuta antes que R__ref__seed_colombia (que carga países).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE ref.banks (
    code          varchar(10)   NOT NULL,
    country_code  char(2)       NOT NULL,
    name          varchar(100)  NOT NULL,
    is_active     boolean       NOT NULL DEFAULT true,
    sort_order    integer       NOT NULL DEFAULT 100,
    CONSTRAINT pk_banks PRIMARY KEY (code),
    CONSTRAINT ck_banks__code CHECK (code ~ '^[0-9A-Z]{2,10}$'),
    CONSTRAINT ck_banks__country CHECK (country_code ~ '^[A-Z]{2}$'),
    CONSTRAINT ck_banks__name CHECK (btrim(name) <> '')
);

-- -----------------------------------------------------------------------------------------------------
-- Proveedores: pedido mínimo y nota de hora de corte ("pedidos hasta el martes a las 10 a. m.").
-- -----------------------------------------------------------------------------------------------------
ALTER TABLE purchasing.suppliers
    ADD COLUMN minimum_order_amount  numeric(19,2),
    ADD COLUMN order_cutoff_note     varchar(200),
    ADD CONSTRAINT ck_suppliers__minimum_order CHECK (minimum_order_amount >= 0);

-- -----------------------------------------------------------------------------------------------------
-- Agenda del proveedor: día de la semana ISO (1 = lunes … 7 = domingo) y tipo; branch_id NULL = todas las sucursales.
-- Base para el sugerido de pedido (fases 9/10).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE purchasing.supplier_schedules (
    id           uuid          NOT NULL,
    company_id   uuid          NOT NULL,
    supplier_id  uuid          NOT NULL,
    branch_id    uuid,
    day_of_week  smallint      NOT NULL,
    kind         varchar(10)   NOT NULL,
    notes        varchar(200),
    row_version  bigint        NOT NULL DEFAULT 1,
    created_at   timestamptz   NOT NULL,
    created_by   uuid          NOT NULL,
    updated_at   timestamptz,
    updated_by   uuid,
    deleted_at   timestamptz,
    deleted_by   uuid,
    CONSTRAINT pk_supplier_schedules PRIMARY KEY (id),
    CONSTRAINT fk_supplier_schedules__supplier FOREIGN KEY (supplier_id, company_id) REFERENCES purchasing.suppliers (id, company_id),
    CONSTRAINT fk_supplier_schedules__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT ck_supplier_schedules__day CHECK (day_of_week BETWEEN 1 AND 7),
    CONSTRAINT ck_supplier_schedules__kind CHECK (kind IN ('VISIT', 'ORDER', 'DELIVERY')),
    CONSTRAINT ck_supplier_schedules__row_version CHECK (row_version > 0),
    CONSTRAINT ck_supplier_schedules__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_supplier_schedules__entry ON purchasing.supplier_schedules (supplier_id, kind, day_of_week, branch_id) NULLS NOT DISTINCT
    WHERE deleted_at IS NULL;
CREATE INDEX ix_supplier_schedules__supplier_id_company_id ON purchasing.supplier_schedules (supplier_id, company_id);
CREATE INDEX ix_supplier_schedules__branch_id_company_id ON purchasing.supplier_schedules (branch_id, company_id);

-- -----------------------------------------------------------------------------------------------------
-- Cuentas bancarias del proveedor. El cambio fraudulento de la cuenta de un proveedor es un fraude frecuente: toda
-- cuenta nueva o modificada queda PENDING_VERIFICATION hasta que otro usuario la confirme (verified_by <> changed_by).
-- Una sola cuenta principal activa por proveedor (restricción diferida: cambiar la principal es un solo guardado).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE purchasing.supplier_bank_accounts (
    id                            uuid          NOT NULL,
    company_id                    uuid          NOT NULL,
    supplier_id                   uuid          NOT NULL,
    bank_code                     varchar(10)   NOT NULL,
    account_type                  varchar(10)   NOT NULL,
    account_number                varchar(20)   NOT NULL,
    holder_name                   varchar(150)  NOT NULL,
    holder_identification_type    varchar(10)   NOT NULL,
    holder_identification_number  varchar(20)   NOT NULL,
    status                        varchar(20)   NOT NULL,
    is_primary                    boolean       NOT NULL DEFAULT false,
    changed_at                    timestamptz   NOT NULL,
    changed_by                    uuid          NOT NULL,
    verified_at                   timestamptz,
    verified_by                   uuid,
    row_version                   bigint        NOT NULL DEFAULT 1,
    created_at                    timestamptz   NOT NULL,
    created_by                    uuid          NOT NULL,
    updated_at                    timestamptz,
    updated_by                    uuid,
    CONSTRAINT pk_supplier_bank_accounts PRIMARY KEY (id),
    CONSTRAINT ux_supplier_bank_accounts__number UNIQUE (supplier_id, bank_code, account_number),
    CONSTRAINT fk_supplier_bank_accounts__supplier FOREIGN KEY (supplier_id, company_id) REFERENCES purchasing.suppliers (id, company_id),
    CONSTRAINT fk_supplier_bank_accounts__bank FOREIGN KEY (bank_code) REFERENCES ref.banks (code),
    CONSTRAINT fk_supplier_bank_accounts__identification_type FOREIGN KEY (holder_identification_type) REFERENCES ref.identification_types (code),
    CONSTRAINT ck_supplier_bank_accounts__type CHECK (account_type IN ('SAVINGS', 'CHECKING')),
    CONSTRAINT ck_supplier_bank_accounts__number CHECK (account_number ~ '^[0-9]{5,20}$'),
    CONSTRAINT ck_supplier_bank_accounts__holder CHECK (btrim(holder_name) <> '' AND btrim(holder_identification_number) <> ''),
    CONSTRAINT ck_supplier_bank_accounts__status CHECK (status IN ('PENDING_VERIFICATION', 'VERIFIED', 'INACTIVE')),
    CONSTRAINT ck_supplier_bank_accounts__verified CHECK (
        (status = 'VERIFIED') = (verified_at IS NOT NULL) AND (verified_at IS NULL) = (verified_by IS NULL)),
    CONSTRAINT ck_supplier_bank_accounts__verifier CHECK (verified_by IS NULL OR verified_by <> changed_by),
    CONSTRAINT ck_supplier_bank_accounts__primary CHECK (NOT is_primary OR status <> 'INACTIVE'),
    CONSTRAINT ck_supplier_bank_accounts__row_version CHECK (row_version > 0),
    CONSTRAINT ex_supplier_bank_accounts__primary EXCLUDE USING btree (supplier_id WITH =) WHERE (is_primary) DEFERRABLE INITIALLY DEFERRED
);

CREATE INDEX ix_supplier_bank_accounts__supplier_id_company_id ON purchasing.supplier_bank_accounts (supplier_id, company_id);
CREATE INDEX ix_supplier_bank_accounts__bank_code ON purchasing.supplier_bank_accounts (bank_code);
CREATE INDEX ix_supplier_bank_accounts__holder_identification_type ON purchasing.supplier_bank_accounts (holder_identification_type);

-- -----------------------------------------------------------------------------------------------------
-- Retenciones sugeridas del proveedor: tipo y tarifa (%) que pre-llenan la compra nueva (D5-11 sigue: se digitan y
-- confirman en la compra). Una por tipo.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE purchasing.supplier_withholding_defaults (
    id           uuid          NOT NULL,
    company_id   uuid          NOT NULL,
    supplier_id  uuid          NOT NULL,
    kind         varchar(12)   NOT NULL,
    rate         numeric(7,4)  NOT NULL,
    concept      varchar(100),
    row_version  bigint        NOT NULL DEFAULT 1,
    created_at   timestamptz   NOT NULL,
    created_by   uuid          NOT NULL,
    updated_at   timestamptz,
    updated_by   uuid,
    deleted_at   timestamptz,
    deleted_by   uuid,
    CONSTRAINT pk_supplier_withholding_defaults PRIMARY KEY (id),
    CONSTRAINT fk_supplier_withholding_defaults__supplier FOREIGN KEY (supplier_id, company_id) REFERENCES purchasing.suppliers (id, company_id),
    CONSTRAINT ck_supplier_withholding_defaults__kind CHECK (kind IN ('RETEFUENTE', 'RETEIVA', 'RETEICA')),
    CONSTRAINT ck_supplier_withholding_defaults__rate CHECK (rate > 0 AND rate <= 100),
    CONSTRAINT ck_supplier_withholding_defaults__row_version CHECK (row_version > 0),
    CONSTRAINT ck_supplier_withholding_defaults__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_supplier_withholding_defaults__kind ON purchasing.supplier_withholding_defaults (supplier_id, kind) WHERE deleted_at IS NULL;
CREATE INDEX ix_supplier_withholding_defaults__supplier_id_company_id ON purchasing.supplier_withholding_defaults (supplier_id, company_id);

-- Vencimientos próximos (GET /purchasing/payables/due): cuentas abiertas por fecha de vencimiento en la empresa.
CREATE INDEX ix_accounts_payable__due ON purchasing.accounts_payable (company_id, due_date) WHERE status = 'OPEN';

-- Historial de costos por producto: solo compras contabilizadas (se cruza por purchase_lines.product_id, ya indexado).
