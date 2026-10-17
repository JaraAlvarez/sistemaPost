-- =====================================================================================================
-- V2026.10.034 · cash · Cobrar con Sistecrédito en la caja (Fase 7-B, D7B-01..08).
-- Diseño: docs/fases/fase-07b-propuesta.md y docs/adr/0064-sistecredito-en-la-caja.md.
--
--   · Tipo de medio de pago SISTECREDITO: medio del sistema (código SISTECREDITO) que nace inactivo y se activa al configurar las
--     llaves de la tienda. La referencia del pago en la venta es el id de la transacción de Sistecrédito.
--   · sistecredito_settings: una fila por empresa con el ambiente (STAGING / PRODUCTION), el encendido y las llaves del convenio
--     CIFRADAS con DPAPI (bytes, mismo mecanismo que las credenciales de Factus). La API nunca las devuelve.
--   · sistecredito_attempts: cada cobro queda guardado (D7B-05). Solo se completa una venta con un cobro APPROVED de esa venta, por
--     ese valor y sin usar (completed_sale_id). Un aprobado sin venta pasa a REVIEW_REQUIRED (revisión automática) y la
--     administración lo cierra con una nota. El documento del cliente se guarda completo aquí (Sistecrédito lo necesita para
--     consultar), pero la bitácora y los registros solo muestran los últimos 4.
-- Los permisos de pos_app sobre las tablas nuevas los da A__system__privileges.sql (DML del esquema cash); los cobros no se borran.
-- =====================================================================================================

ALTER TABLE cash.payment_methods DROP CONSTRAINT ck_payment_methods__kind;
ALTER TABLE cash.payment_methods ADD CONSTRAINT ck_payment_methods__kind
    CHECK (kind IN ('CASH', 'DEBIT_CARD', 'CREDIT_CARD', 'TRANSFER', 'WALLET', 'VOUCHER', 'OTHER', 'EXCHANGE_CREDIT', 'CUSTOMER_CREDIT', 'LOYALTY_POINTS',
                    'SISTECREDITO'));

CREATE TABLE cash.sistecredito_settings (
    company_id                 uuid           NOT NULL,
    environment                varchar(12)    NOT NULL,
    is_enabled                 boolean        NOT NULL DEFAULT false,
    credentials                bytea,
    credentials_updated_at     timestamptz,
    key_hint                   varchar(4),
    last_verified_at           timestamptz,
    last_verification_ok       boolean,
    last_verification_message  varchar(500),
    created_at                 timestamptz    NOT NULL,
    created_by                 uuid           NOT NULL,
    updated_at                 timestamptz,
    updated_by                 uuid,
    CONSTRAINT pk_sistecredito_settings PRIMARY KEY (company_id),
    CONSTRAINT fk_sistecredito_settings__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_sistecredito_settings__environment CHECK (environment IN ('STAGING', 'PRODUCTION')),
    -- Sin llaves no se enciende.
    CONSTRAINT ck_sistecredito_settings__credentials CHECK (NOT is_enabled OR credentials IS NOT NULL),
    CONSTRAINT ck_sistecredito_settings__credentials_updated CHECK ((credentials IS NULL) = (credentials_updated_at IS NULL))
);

COMMENT ON TABLE cash.sistecredito_settings IS 'Sistecrédito de la tienda: ambiente, encendido y llaves cifradas con DPAPI (Fase 7-B, D7B-02).';
COMMENT ON COLUMN cash.sistecredito_settings.credentials IS 'Llaves del convenio cifradas con DPAPI del equipo (bytes). Nunca en claro; la API nunca las devuelve.';
COMMENT ON COLUMN cash.sistecredito_settings.key_hint IS 'Últimos 4 caracteres de la llave de suscripción, para reconocerla en pantalla.';

CREATE TABLE cash.sistecredito_attempts (
    id                    uuid           NOT NULL,
    company_id            uuid           NOT NULL,
    sale_id               uuid           NOT NULL,
    pos_terminal_id       uuid,
    invoice               varchar(60)    NOT NULL,
    amount                numeric(19,2)  NOT NULL,
    document_type         varchar(5)     NOT NULL,
    document_number       varchar(20)    NOT NULL,
    environment           varchar(12)    NOT NULL,
    transaction_id        varchar(60),
    provider_status       varchar(40),
    status                varchar(16)    NOT NULL,
    redirect_url          varchar(1000),
    installments          integer,
    message               varchar(300),
    started_at            timestamptz    NOT NULL,
    last_checked_at       timestamptz,
    approved_at           timestamptz,
    finished_at           timestamptz,
    completed_sale_id     uuid,
    used_at               timestamptz,
    review_required_at    timestamptz,
    reviewed_at           timestamptz,
    reviewed_by           uuid,
    review_note           varchar(300),
    created_at            timestamptz    NOT NULL,
    created_by            uuid           NOT NULL,
    updated_at            timestamptz,
    updated_by            uuid,
    CONSTRAINT pk_sistecredito_attempts PRIMARY KEY (id),
    CONSTRAINT fk_sistecredito_attempts__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_sistecredito_attempts__sale FOREIGN KEY (sale_id) REFERENCES sales.sales (id),
    CONSTRAINT fk_sistecredito_attempts__completed_sale FOREIGN KEY (completed_sale_id) REFERENCES sales.sales (id),
    CONSTRAINT fk_sistecredito_attempts__reviewed_by FOREIGN KEY (reviewed_by) REFERENCES identity.users (id),
    CONSTRAINT ck_sistecredito_attempts__status CHECK (status IN ('PENDING', 'APPROVED', 'REJECTED', 'EXPIRED', 'CANCELLED', 'REVIEW_REQUIRED')),
    CONSTRAINT ck_sistecredito_attempts__environment CHECK (environment IN ('STAGING', 'PRODUCTION')),
    -- Sistecrédito recibe el valor en pesos enteros.
    CONSTRAINT ck_sistecredito_attempts__amount CHECK (amount > 0 AND amount = round(amount)),
    CONSTRAINT ck_sistecredito_attempts__document_type CHECK (document_type IN ('CC', 'CE', 'NIT', 'PA', 'TI')),
    CONSTRAINT ck_sistecredito_attempts__document_number CHECK (document_number ~ '^[A-Z0-9]{3,20}$'),
    CONSTRAINT ck_sistecredito_attempts__installments CHECK (installments IS NULL OR installments > 0),
    -- Solo un cobro aprobado se usa en una venta, y de esa misma venta.
    CONSTRAINT ck_sistecredito_attempts__used CHECK ((completed_sale_id IS NULL) = (used_at IS NULL)),
    CONSTRAINT ck_sistecredito_attempts__used_approved CHECK (completed_sale_id IS NULL OR (completed_sale_id = sale_id AND approved_at IS NOT NULL)),
    CONSTRAINT ck_sistecredito_attempts__reviewed CHECK ((reviewed_at IS NULL) = (reviewed_by IS NULL) AND (reviewed_at IS NULL) = (review_note IS NULL))
);

CREATE UNIQUE INDEX ux_sistecredito_attempts__invoice ON cash.sistecredito_attempts (invoice);
CREATE UNIQUE INDEX ux_sistecredito_attempts__transaction ON cash.sistecredito_attempts (transaction_id) WHERE transaction_id IS NOT NULL;
CREATE INDEX ix_sistecredito_attempts__company_id ON cash.sistecredito_attempts (company_id);
CREATE INDEX ix_sistecredito_attempts__sale_id ON cash.sistecredito_attempts (sale_id);
CREATE INDEX ix_sistecredito_attempts__completed_sale_id ON cash.sistecredito_attempts (completed_sale_id);
CREATE INDEX ix_sistecredito_attempts__reviewed_by ON cash.sistecredito_attempts (reviewed_by);
-- Revisión automática: pendientes, cancelados recientes y aprobados sin venta.
CREATE INDEX ix_sistecredito_attempts__open ON cash.sistecredito_attempts (status, started_at) WHERE completed_sale_id IS NULL;

COMMENT ON TABLE cash.sistecredito_attempts IS 'Cobros con Sistecrédito desde la caja (Fase 7-B, D7B-05): uno por intento, nunca se borran.';
COMMENT ON COLUMN cash.sistecredito_attempts.document_number IS 'Documento del cliente para Sistecrédito. La bitácora y los registros solo muestran los últimos 4.';
COMMENT ON COLUMN cash.sistecredito_attempts.completed_sale_id IS 'Venta completada con este cobro (una sola vez).';
