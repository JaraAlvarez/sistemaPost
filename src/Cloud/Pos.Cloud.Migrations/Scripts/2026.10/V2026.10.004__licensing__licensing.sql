-- =====================================================================================================
-- V2026.10.004 · licensing · Servidor de licencias (docs/fases/fase-12a-propuesta.md §4, L-03, L-06, L-07, L-09).
-- cuenta → empresa (NIT único) → suscripción (edición) → UNA licencia vigente por empresa → instalaciones (una por
-- sucursal) → equipos → activaciones → check-ins. Nada se borra: los cambios de estado son eventos; los eventos de la
-- suscripción y los check-ins son de solo inserción (privilegios + disparador).
-- =====================================================================================================

-- -----------------------------------------------------------------------------------------------------
-- Cuentas: cliente comercial o distribuidor (el distribuidor queda modelado, sin pantallas en 12-A).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE licensing.accounts (
    id                 uuid          NOT NULL,
    name               varchar(200)  NOT NULL,
    kind               varchar(10)   NOT NULL,
    nit                varchar(15),
    nit_check_digit    char(1),
    contact_name       varchar(120),
    contact_email      varchar(120),
    contact_phone      varchar(30),
    parent_account_id  uuid,
    status             varchar(10)   NOT NULL,
    notes              varchar(500),
    created_at         timestamptz   NOT NULL,
    created_by         uuid          NOT NULL,
    updated_at         timestamptz,
    updated_by         uuid,
    CONSTRAINT pk_accounts PRIMARY KEY (id),
    CONSTRAINT fk_accounts__parent FOREIGN KEY (parent_account_id) REFERENCES licensing.accounts (id),
    CONSTRAINT fk_accounts__created_by FOREIGN KEY (created_by) REFERENCES portal.portal_users (id),
    CONSTRAINT ck_accounts__kind CHECK (kind IN ('DIRECT', 'RESELLER')),
    CONSTRAINT ck_accounts__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_accounts__nit CHECK (
        (nit IS NULL AND nit_check_digit IS NULL)
        OR (nit ~ '^[0-9]{1,15}$' AND nit_check_digit ~ '^[0-9]$')),
    CONSTRAINT ck_accounts__parent CHECK (parent_account_id IS NULL OR parent_account_id <> id),
    CONSTRAINT ck_accounts__name CHECK (length(btrim(name)) > 0)
);

CREATE INDEX ix_accounts__parent ON licensing.accounts (parent_account_id) WHERE parent_account_id IS NOT NULL;

ALTER TABLE portal.portal_users
    ADD CONSTRAINT fk_portal_users__reseller_account FOREIGN KEY (reseller_account_id) REFERENCES licensing.accounts (id);

-- -----------------------------------------------------------------------------------------------------
-- Empresas licenciadas: la licencia es de la razón social (NIT con DV), única en todo el servidor.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE licensing.organizations (
    id               uuid          NOT NULL,
    account_id       uuid          NOT NULL,
    legal_name       varchar(200)  NOT NULL,
    nit              varchar(15)   NOT NULL,
    nit_check_digit  char(1)       NOT NULL,
    city             varchar(120),
    status           varchar(10)   NOT NULL,
    created_at       timestamptz   NOT NULL,
    created_by       uuid          NOT NULL,
    updated_at       timestamptz,
    updated_by       uuid,
    CONSTRAINT pk_organizations PRIMARY KEY (id),
    CONSTRAINT ux_organizations__nit UNIQUE (nit),
    CONSTRAINT fk_organizations__account FOREIGN KEY (account_id) REFERENCES licensing.accounts (id),
    CONSTRAINT ck_organizations__nit CHECK (nit ~ '^[0-9]{1,15}$' AND nit_check_digit ~ '^[0-9]$'),
    CONSTRAINT ck_organizations__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_organizations__legal_name CHECK (length(btrim(legal_name)) > 0)
);

CREATE INDEX ix_organizations__account ON licensing.organizations (account_id);

-- -----------------------------------------------------------------------------------------------------
-- Suscripciones: edición (única diferencia comercial, ADR-0015), periodicidad, vigencia, prueba y gracia.
-- Una sola suscripción vigente (no cancelada) por empresa.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE licensing.subscriptions (
    id                    uuid          NOT NULL,
    organization_id       uuid          NOT NULL,
    edition               varchar(10)   NOT NULL,
    billing_period        varchar(10)   NOT NULL,
    status                varchar(12)   NOT NULL,
    trial_ends_at         timestamptz,
    current_period_start  timestamptz,
    current_period_end    timestamptz,
    grace_days            smallint      NOT NULL,
    suspended_reason      varchar(300),
    cancelled_at          timestamptz,
    created_at            timestamptz   NOT NULL,
    created_by            uuid          NOT NULL,
    updated_at            timestamptz,
    updated_by            uuid,
    CONSTRAINT pk_subscriptions PRIMARY KEY (id),
    CONSTRAINT ux_subscriptions__id_organization UNIQUE (id, organization_id),
    CONSTRAINT fk_subscriptions__organization FOREIGN KEY (organization_id) REFERENCES licensing.organizations (id),
    CONSTRAINT ck_subscriptions__edition CHECK (edition IN ('SINGLE', 'MULTI')),
    CONSTRAINT ck_subscriptions__billing_period CHECK (billing_period IN ('MONTHLY', 'ANNUAL')),
    CONSTRAINT ck_subscriptions__status CHECK (status IN ('TRIAL', 'ACTIVE', 'PAST_DUE', 'SUSPENDED', 'CANCELLED', 'EXPIRED')),
    CONSTRAINT ck_subscriptions__grace_days CHECK (grace_days BETWEEN 0 AND 90),
    CONSTRAINT ck_subscriptions__period CHECK (
        (current_period_start IS NULL) = (current_period_end IS NULL)
        AND (current_period_end IS NULL OR current_period_end > current_period_start)),
    CONSTRAINT ck_subscriptions__validity CHECK (trial_ends_at IS NOT NULL OR current_period_end IS NOT NULL),
    CONSTRAINT ck_subscriptions__suspended CHECK (status <> 'SUSPENDED' OR suspended_reason IS NOT NULL),
    CONSTRAINT ck_subscriptions__cancelled CHECK ((status = 'CANCELLED') = (cancelled_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_subscriptions__organization_current
    ON licensing.subscriptions (organization_id) WHERE status <> 'CANCELLED';

-- Historial de la suscripción: SOLO INSERCIÓN (L-09).
CREATE TABLE licensing.subscription_events (
    id                 uuid          NOT NULL,
    subscription_id    uuid          NOT NULL,
    type               varchar(20)   NOT NULL,
    occurred_at        timestamptz   NOT NULL,
    actor_id           uuid          NOT NULL,
    payment_reference  varchar(100),
    period_start       timestamptz,
    period_end         timestamptz,
    old_value          varchar(40),
    new_value          varchar(40),
    reason             varchar(300),
    CONSTRAINT pk_subscription_events PRIMARY KEY (id),
    CONSTRAINT fk_subscription_events__subscription FOREIGN KEY (subscription_id) REFERENCES licensing.subscriptions (id),
    CONSTRAINT fk_subscription_events__actor FOREIGN KEY (actor_id) REFERENCES portal.portal_users (id),
    CONSTRAINT ck_subscription_events__type CHECK (type IN (
        'CREATED', 'RENEWED', 'EDITION_CHANGED', 'SUSPENDED', 'REACTIVATED', 'CANCELLED', 'GRACE_EXTENDED', 'STATUS_CHANGED')),
    CONSTRAINT ck_subscription_events__renewal CHECK (
        type <> 'RENEWED' OR (payment_reference IS NOT NULL AND period_start IS NOT NULL AND period_end IS NOT NULL))
);

CREATE INDEX ix_subscription_events__subscription ON licensing.subscription_events (subscription_id, occurred_at);

-- -----------------------------------------------------------------------------------------------------
-- Licencias: la clave se guarda como SHA-256 + prefijo visible (L-06). Una sola licencia vigente por empresa;
-- regenerar revoca la anterior (replaced_by). max_installations NULL = sin límite (resolución 2 de la propuesta).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE licensing.licenses (
    id                 uuid          NOT NULL,
    organization_id    uuid          NOT NULL,
    subscription_id    uuid          NOT NULL,
    key_hash           char(64)      NOT NULL,
    key_prefix         varchar(9)    NOT NULL,
    status             varchar(10)   NOT NULL,
    max_installations  integer,
    issued_at          timestamptz   NOT NULL,
    revoked_at         timestamptz,
    revoked_reason     varchar(300),
    replaced_by        uuid,
    created_at         timestamptz   NOT NULL,
    created_by         uuid          NOT NULL,
    updated_at         timestamptz,
    updated_by         uuid,
    CONSTRAINT pk_licenses PRIMARY KEY (id),
    CONSTRAINT ux_licenses__key_hash UNIQUE (key_hash),
    CONSTRAINT fk_licenses__organization FOREIGN KEY (organization_id) REFERENCES licensing.organizations (id),
    CONSTRAINT fk_licenses__subscription FOREIGN KEY (subscription_id, organization_id)
        REFERENCES licensing.subscriptions (id, organization_id),
    -- Diferida: al regenerar, la licencia anterior se revoca (y apunta a la nueva) antes de insertar la nueva, porque solo
    -- puede haber una vigente por empresa.
    CONSTRAINT fk_licenses__replaced_by FOREIGN KEY (replaced_by) REFERENCES licensing.licenses (id) DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT ck_licenses__status CHECK (status IN ('ACTIVE', 'REVOKED')),
    CONSTRAINT ck_licenses__key_hash CHECK (key_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_licenses__key_prefix CHECK (key_prefix ~ '^POS-[2-9A-HJ-NP-Z]{5}$'),
    CONSTRAINT ck_licenses__max_installations CHECK (max_installations IS NULL OR max_installations > 0),
    CONSTRAINT ck_licenses__revoked CHECK ((status = 'REVOKED') = (revoked_at IS NOT NULL AND revoked_reason IS NOT NULL))
);

CREATE UNIQUE INDEX ux_licenses__organization_active ON licensing.licenses (organization_id) WHERE status = 'ACTIVE';
CREATE INDEX ix_licenses__key_prefix ON licensing.licenses (key_prefix);

-- -----------------------------------------------------------------------------------------------------
-- Instalaciones (una por sucursal), equipos y activaciones.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE licensing.installations (
    id                  uuid          NOT NULL,
    installation_id     uuid          NOT NULL,
    license_id          uuid          NOT NULL,
    organization_id     uuid          NOT NULL,
    branch_name         varchar(120),
    app_version         varchar(40)   NOT NULL,
    status              varchar(10)   NOT NULL,
    first_activated_at  timestamptz   NOT NULL,
    last_checkin_at     timestamptz,
    last_ip             inet,
    active_terminals    integer,
    created_at          timestamptz   NOT NULL,
    created_by          uuid          NOT NULL,
    updated_at          timestamptz,
    updated_by          uuid,
    CONSTRAINT pk_installations PRIMARY KEY (id),
    CONSTRAINT ux_installations__installation_id UNIQUE (installation_id),
    CONSTRAINT fk_installations__license FOREIGN KEY (license_id) REFERENCES licensing.licenses (id),
    CONSTRAINT fk_installations__organization FOREIGN KEY (organization_id) REFERENCES licensing.organizations (id),
    CONSTRAINT ck_installations__status CHECK (status IN ('ACTIVE', 'RELEASED')),
    CONSTRAINT ck_installations__active_terminals CHECK (active_terminals IS NULL OR active_terminals >= 0)
);

CREATE INDEX ix_installations__license ON licensing.installations (license_id, status);
CREATE INDEX ix_installations__last_checkin ON licensing.installations (last_checkin_at) WHERE status = 'ACTIVE';

CREATE TABLE licensing.devices (
    id                uuid          NOT NULL,
    installation_id   uuid          NOT NULL,
    fingerprint       varchar(120)  NOT NULL,
    role              varchar(12)   NOT NULL,
    device_name       varchar(120),
    operating_system  varchar(120),
    first_seen_at     timestamptz   NOT NULL,
    last_seen_at      timestamptz   NOT NULL,
    CONSTRAINT pk_devices PRIMARY KEY (id),
    CONSTRAINT fk_devices__installation FOREIGN KEY (installation_id) REFERENCES licensing.installations (id),
    CONSTRAINT ck_devices__role CHECK (role IN ('ALL_IN_ONE', 'STORE_SERVER')),
    CONSTRAINT ck_devices__fingerprint CHECK (fingerprint ~ '^fp1(\.([0-9a-f]{32}|-)){3}$')
);

CREATE INDEX ix_devices__installation ON licensing.devices (installation_id);

CREATE TABLE licensing.activations (
    id               uuid          NOT NULL,
    installation_id  uuid          NOT NULL,
    license_id       uuid          NOT NULL,
    device_id        uuid          NOT NULL,
    status           varchar(10)   NOT NULL,
    activated_at     timestamptz   NOT NULL,
    released_at      timestamptz,
    released_by      uuid,
    release_reason   varchar(300),
    CONSTRAINT pk_activations PRIMARY KEY (id),
    CONSTRAINT fk_activations__installation FOREIGN KEY (installation_id) REFERENCES licensing.installations (id),
    CONSTRAINT fk_activations__license FOREIGN KEY (license_id) REFERENCES licensing.licenses (id),
    CONSTRAINT fk_activations__device FOREIGN KEY (device_id) REFERENCES licensing.devices (id),
    CONSTRAINT fk_activations__released_by FOREIGN KEY (released_by) REFERENCES portal.portal_users (id),
    CONSTRAINT ck_activations__status CHECK (status IN ('ACTIVE', 'RELEASED')),
    CONSTRAINT ck_activations__released CHECK (
        (status = 'RELEASED') = (released_at IS NOT NULL AND released_by IS NOT NULL AND release_reason IS NOT NULL))
);

-- Un equipo tiene como máximo una activación vigente y una instalación solo está activa en un equipo a la vez.
CREATE UNIQUE INDEX ux_activations__device_active ON licensing.activations (device_id) WHERE status = 'ACTIVE';
CREATE UNIQUE INDEX ux_activations__installation_active ON licensing.activations (installation_id) WHERE status = 'ACTIVE';
CREATE INDEX ix_activations__license ON licensing.activations (license_id, status);

-- Check-ins (y rechazos de check-in): SOLO INSERCIÓN.
CREATE TABLE licensing.checkins (
    id                   uuid          NOT NULL,
    installation_id      uuid          NOT NULL,
    activation_id        uuid,
    occurred_at          timestamptz   NOT NULL,
    app_version          varchar(40)   NOT NULL,
    active_terminals     integer       NOT NULL,
    reported_clock       timestamptz   NOT NULL,
    ip_address           inet,
    result               varchar(12)   NOT NULL,
    rejection_code       varchar(60),
    subscription_status  varchar(12),
    token_valid_until    timestamptz,
    CONSTRAINT pk_checkins PRIMARY KEY (id),
    CONSTRAINT fk_checkins__installation FOREIGN KEY (installation_id) REFERENCES licensing.installations (id),
    CONSTRAINT fk_checkins__activation FOREIGN KEY (activation_id) REFERENCES licensing.activations (id),
    CONSTRAINT ck_checkins__result CHECK (result IN ('TOKEN_ISSUED', 'REJECTED')),
    CONSTRAINT ck_checkins__outcome CHECK (
        (result = 'REJECTED') = (rejection_code IS NOT NULL)
        AND (result <> 'TOKEN_ISSUED' OR (subscription_status IS NOT NULL AND token_valid_until IS NOT NULL))),
    CONSTRAINT ck_checkins__active_terminals CHECK (active_terminals >= 0)
);

CREATE INDEX ix_checkins__installation ON licensing.checkins (installation_id, occurred_at);

-- -----------------------------------------------------------------------------------------------------
-- Claves de firma: solo la PÚBLICA (L-04). La privada vive en un archivo protegido fuera de la BD.
-- STANDBY = de reserva, ya publicada (el POS la trae embebida); ACTIVE = firma hoy (una sola);
-- RETIRED = ya no firma pero verifica tokens anteriores; REVOKED = comprometida, ya no se confía en ella.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE licensing.signing_keys (
    kid           varchar(40)   NOT NULL,
    algorithm     varchar(10)   NOT NULL,
    public_key    varchar(64)   NOT NULL,
    status        varchar(10)   NOT NULL,
    created_at    timestamptz   NOT NULL,
    activated_at  timestamptz,
    retired_at    timestamptz,
    revoked_at    timestamptz,
    CONSTRAINT pk_signing_keys PRIMARY KEY (kid),
    CONSTRAINT ux_signing_keys__public_key UNIQUE (public_key),
    CONSTRAINT ck_signing_keys__algorithm CHECK (algorithm = 'EdDSA'),
    CONSTRAINT ck_signing_keys__status CHECK (status IN ('STANDBY', 'ACTIVE', 'RETIRED', 'REVOKED')),
    CONSTRAINT ck_signing_keys__public_key CHECK (public_key ~ '^[A-Za-z0-9_-]{43}$')
);

CREATE UNIQUE INDEX ux_signing_keys__single_active ON licensing.signing_keys ((true)) WHERE status = 'ACTIVE';

-- -----------------------------------------------------------------------------------------------------
-- Solo inserción: los disparadores bloquean UPDATE/DELETE/TRUNCATE incluso para el dueño de las tablas.
-- -----------------------------------------------------------------------------------------------------
CREATE FUNCTION licensing.fn_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'La tabla %.% es de solo inserción', TG_TABLE_SCHEMA, TG_TABLE_NAME
        USING ERRCODE = '42501';
END;
$$;

CREATE TRIGGER trg_subscription_events_append_only
    BEFORE UPDATE OR DELETE ON licensing.subscription_events
    FOR EACH ROW EXECUTE FUNCTION licensing.fn_append_only();

CREATE TRIGGER trg_subscription_events_no_truncate
    BEFORE TRUNCATE ON licensing.subscription_events
    FOR EACH STATEMENT EXECUTE FUNCTION licensing.fn_append_only();

CREATE TRIGGER trg_checkins_append_only
    BEFORE UPDATE OR DELETE ON licensing.checkins
    FOR EACH ROW EXECUTE FUNCTION licensing.fn_append_only();

CREATE TRIGGER trg_checkins_no_truncate
    BEFORE TRUNCATE ON licensing.checkins
    FOR EACH STATEMENT EXECUTE FUNCTION licensing.fn_append_only();
