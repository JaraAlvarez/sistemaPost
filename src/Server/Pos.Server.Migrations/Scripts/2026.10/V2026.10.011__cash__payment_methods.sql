-- =====================================================================================================
-- V2026.10.011 · cash · Medios de pago de la empresa (D5-13). El esquema cash nace aquí y crece en la Fase 6 con las
-- jornadas y los movimientos de caja. dian_code: código de medio de pago de la factura electrónica (10 efectivo,
-- 47 transferencia, 48 tarjeta crédito, 49 tarjeta débito…; se validan con Factus en la Fase 11-B).
-- affects_cash_drawer: el medio entra o sale del cajón (efectivo) y cuenta en el arqueo como dinero físico.
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS cash;

CREATE TABLE cash.payment_methods (
    id                   uuid          NOT NULL,
    company_id           uuid          NOT NULL,
    code                 varchar(20)   NOT NULL,
    name                 varchar(60)   NOT NULL,
    kind                 varchar(15)   NOT NULL,
    dian_code            varchar(5),
    requires_reference   boolean       NOT NULL DEFAULT false,
    affects_cash_drawer  boolean       NOT NULL DEFAULT false,
    sort_order           integer       NOT NULL DEFAULT 0,
    is_system            boolean       NOT NULL DEFAULT false,
    status               varchar(10)   NOT NULL,
    row_version          bigint        NOT NULL DEFAULT 1,
    created_at           timestamptz   NOT NULL,
    created_by           uuid          NOT NULL,
    updated_at           timestamptz,
    updated_by           uuid,
    deleted_at           timestamptz,
    deleted_by           uuid,
    CONSTRAINT pk_payment_methods PRIMARY KEY (id),
    CONSTRAINT ux_payment_methods__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_payment_methods__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_payment_methods__code CHECK (code ~ '^[A-Z0-9_]{2,20}$'),
    CONSTRAINT ck_payment_methods__name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_payment_methods__kind CHECK (kind IN ('CASH', 'DEBIT_CARD', 'CREDIT_CARD', 'TRANSFER', 'WALLET', 'VOUCHER', 'OTHER')),
    CONSTRAINT ck_payment_methods__drawer CHECK (NOT affects_cash_drawer OR kind = 'CASH'),
    CONSTRAINT ck_payment_methods__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_payment_methods__row_version CHECK (row_version > 0),
    CONSTRAINT ck_payment_methods__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_payment_methods__company_code ON cash.payment_methods (company_id, code) WHERE deleted_at IS NULL;
