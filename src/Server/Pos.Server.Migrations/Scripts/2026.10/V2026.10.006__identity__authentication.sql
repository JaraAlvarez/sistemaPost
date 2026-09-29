-- =====================================================================================================
-- V2026.10.006 · identity · Autenticación: empleados, credenciales de caja, sesiones, intentos, historial de
-- contraseñas, autorizaciones de supervisor y emparejamiento de equipos.
-- Diseño: docs/fases/fase-03-propuesta.md §3. Solo agrega (patrón expand): no rompe instalaciones de la Fase 2.
-- Las tablas de seguridad llevan node_id: son LOCALES del nodo y no se sincronizan (ADR-0014, D3-09).
-- =====================================================================================================

-- -----------------------------------------------------------------------------------------------------
-- Empleados: la persona detrás del usuario (datos mínimos, sin nómina)
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE identity.employees (
    id                     uuid          NOT NULL,
    company_id             uuid          NOT NULL,
    branch_id              uuid,
    identification_type    varchar(10)   NOT NULL,
    identification_number  varchar(30)   NOT NULL,
    first_name             varchar(80)   NOT NULL,
    last_name              varchar(80)   NOT NULL,
    phone                  varchar(30),
    email                  varchar(200),
    status                 varchar(10)   NOT NULL,
    row_version            bigint        NOT NULL DEFAULT 1,
    created_at             timestamptz   NOT NULL,
    created_by             uuid          NOT NULL,
    updated_at             timestamptz,
    updated_by             uuid,
    deleted_at             timestamptz,
    deleted_by             uuid,
    CONSTRAINT pk_employees PRIMARY KEY (id),
    CONSTRAINT fk_employees__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_employees__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_employees__identification_type FOREIGN KEY (identification_type) REFERENCES ref.identification_types (code),
    CONSTRAINT ck_employees__identification_number CHECK (identification_number ~ '^[0-9A-Za-z-]+$'),
    CONSTRAINT ck_employees__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_employees__row_version CHECK (row_version > 0),
    CONSTRAINT ck_employees__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_employees__company_identification
    ON identity.employees (company_id, identification_type, identification_number) WHERE deleted_at IS NULL;
CREATE INDEX ix_employees__company_id ON identity.employees (company_id);
CREATE INDEX ix_employees__branch_id_company_id ON identity.employees (branch_id, company_id);
CREATE INDEX ix_employees__identification_type ON identity.employees (identification_type);

-- -----------------------------------------------------------------------------------------------------
-- Usuarios: código de cajero, versión de seguridad y vínculo con el empleado
-- -----------------------------------------------------------------------------------------------------
ALTER TABLE identity.users
    ADD COLUMN pos_code          varchar(6),
    ADD COLUMN pin_changed_at    timestamptz,
    ADD COLUMN security_version  bigint NOT NULL DEFAULT 1,
    ADD COLUMN locked_reason     varchar(30),
    ADD COLUMN employee_id       uuid,
    ADD CONSTRAINT ck_users__pos_code CHECK (pos_code ~ '^[0-9]{3,6}$'),
    ADD CONSTRAINT ck_users__pin_requires_pos_code CHECK (pin_hash IS NULL OR pos_code IS NOT NULL),
    ADD CONSTRAINT ck_users__human_active_password CHECK (kind <> 'HUMAN' OR status <> 'ACTIVE' OR password_hash IS NOT NULL),
    ADD CONSTRAINT ck_users__security_version CHECK (security_version > 0),
    ADD CONSTRAINT ck_users__locked_reason CHECK (locked_reason IN ('FAILED_ATTEMPTS', 'PIN_ATTEMPTS', 'ADMIN')),
    ADD CONSTRAINT fk_users__employee FOREIGN KEY (employee_id) REFERENCES identity.employees (id);

CREATE UNIQUE INDEX ux_users__company_pos_code ON identity.users (company_id, pos_code)
    WHERE pos_code IS NOT NULL AND deleted_at IS NULL;
CREATE UNIQUE INDEX ux_users__employee ON identity.users (employee_id)
    WHERE employee_id IS NOT NULL AND deleted_at IS NULL;

-- -----------------------------------------------------------------------------------------------------
-- Historial de contraseñas (RN-SEC-01: no reutilizar las últimas N)
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE identity.password_history (
    id             uuid          NOT NULL,
    user_id        uuid          NOT NULL,
    password_hash  varchar(255)  NOT NULL,
    created_at     timestamptz   NOT NULL,
    CONSTRAINT pk_password_history PRIMARY KEY (id),
    CONSTRAINT fk_password_history__user FOREIGN KEY (user_id) REFERENCES identity.users (id) ON DELETE CASCADE
);

CREATE INDEX ix_password_history__user_id ON identity.password_history (user_id, created_at DESC);

-- -----------------------------------------------------------------------------------------------------
-- Equipos: credencial del equipo y certificado fijado. org.devices existe desde la Fase 2 y siempre está vacía
-- (el emparejamiento llega ahora), por eso paired_by puede agregarse como NOT NULL.
-- -----------------------------------------------------------------------------------------------------
ALTER TABLE org.devices
    ADD COLUMN credential_hash  char(64),
    ADD COLUMN certificate_pin  char(64),
    ADD COLUMN paired_by        uuid NOT NULL,
    ADD COLUMN revoked_at       timestamptz,
    ADD COLUMN revoked_by       uuid,
    ADD CONSTRAINT ck_devices__active_credential CHECK (status <> 'ACTIVE' OR credential_hash IS NOT NULL),
    ADD CONSTRAINT ck_devices__credential_hash CHECK (credential_hash ~ '^[0-9a-f]{64}$'),
    ADD CONSTRAINT ck_devices__revoked CHECK ((status = 'REVOKED') = (revoked_at IS NOT NULL));

CREATE TABLE org.device_pairing_codes (
    id               uuid          NOT NULL,
    company_id       uuid          NOT NULL,
    node_id          uuid          NOT NULL,
    code_hash        char(64)      NOT NULL,
    device_kind      varchar(20)   NOT NULL,
    pos_terminal_id  uuid,
    created_by       uuid          NOT NULL,
    created_at       timestamptz   NOT NULL,
    expires_at       timestamptz   NOT NULL,
    used_at          timestamptz,
    used_by_device   uuid,
    CONSTRAINT pk_device_pairing_codes PRIMARY KEY (id),
    CONSTRAINT fk_device_pairing_codes__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_device_pairing_codes__node FOREIGN KEY (node_id) REFERENCES org.nodes (id),
    CONSTRAINT fk_device_pairing_codes__pos_terminal FOREIGN KEY (pos_terminal_id) REFERENCES org.pos_terminals (id),
    CONSTRAINT fk_device_pairing_codes__device FOREIGN KEY (used_by_device) REFERENCES org.devices (id),
    CONSTRAINT ck_device_pairing_codes__kind CHECK (device_kind IN ('TERMINAL', 'ADMIN_WORKSTATION')),
    CONSTRAINT ck_device_pairing_codes__terminal CHECK ((device_kind = 'TERMINAL') = (pos_terminal_id IS NOT NULL)),
    CONSTRAINT ck_device_pairing_codes__expiry CHECK (expires_at > created_at),
    CONSTRAINT ck_device_pairing_codes__used CHECK ((used_at IS NULL) = (used_by_device IS NULL))
);

CREATE UNIQUE INDEX ux_device_pairing_codes__code_pending ON org.device_pairing_codes (code_hash) WHERE used_at IS NULL;
CREATE INDEX ix_device_pairing_codes__company_id ON org.device_pairing_codes (company_id);
CREATE INDEX ix_device_pairing_codes__node_id ON org.device_pairing_codes (node_id);
CREATE INDEX ix_device_pairing_codes__pos_terminal_id ON org.device_pairing_codes (pos_terminal_id);
CREATE INDEX ix_device_pairing_codes__used_by_device ON org.device_pairing_codes (used_by_device);

-- -----------------------------------------------------------------------------------------------------
-- Sesiones (tokens opacos: solo se guarda su SHA-256). Locales del nodo.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE identity.user_sessions (
    id                    uuid          NOT NULL,
    company_id            uuid          NOT NULL,
    node_id               uuid          NOT NULL,
    user_id               uuid          NOT NULL,
    token_hash            char(64)      NOT NULL,
    kind                  varchar(12)   NOT NULL,
    device_id             uuid,
    pos_terminal_id       uuid,
    branch_id             uuid          NOT NULL,
    ip_address            inet,
    user_agent            varchar(200),
    security_version      bigint        NOT NULL,
    idle_timeout_seconds  integer       NOT NULL,
    created_at            timestamptz   NOT NULL,
    last_activity_at      timestamptz   NOT NULL,
    expires_at            timestamptz   NOT NULL,
    revoked_at            timestamptz,
    revoked_by            uuid,
    revoked_reason        varchar(40),
    CONSTRAINT pk_user_sessions PRIMARY KEY (id),
    CONSTRAINT ux_user_sessions__token_hash UNIQUE (token_hash),
    CONSTRAINT fk_user_sessions__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_user_sessions__node FOREIGN KEY (node_id) REFERENCES org.nodes (id),
    CONSTRAINT fk_user_sessions__user FOREIGN KEY (user_id) REFERENCES identity.users (id),
    CONSTRAINT fk_user_sessions__device FOREIGN KEY (device_id) REFERENCES org.devices (id),
    CONSTRAINT fk_user_sessions__pos_terminal FOREIGN KEY (pos_terminal_id) REFERENCES org.pos_terminals (id),
    CONSTRAINT fk_user_sessions__branch FOREIGN KEY (branch_id) REFERENCES org.branches (id),
    CONSTRAINT ck_user_sessions__kind CHECK (kind IN ('BACKOFFICE', 'TERMINAL')),
    CONSTRAINT ck_user_sessions__terminal CHECK (kind <> 'TERMINAL' OR pos_terminal_id IS NOT NULL),
    CONSTRAINT ck_user_sessions__token_hash CHECK (token_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_user_sessions__idle CHECK (idle_timeout_seconds > 0),
    CONSTRAINT ck_user_sessions__expiry CHECK (expires_at > created_at),
    CONSTRAINT ck_user_sessions__revoked CHECK ((revoked_at IS NULL) = (revoked_reason IS NULL))
);

CREATE INDEX ix_user_sessions__active_user ON identity.user_sessions (user_id) WHERE revoked_at IS NULL;
CREATE INDEX ix_user_sessions__user_id ON identity.user_sessions (user_id);
CREATE INDEX ix_user_sessions__company_id ON identity.user_sessions (company_id);
CREATE INDEX ix_user_sessions__node_id ON identity.user_sessions (node_id);
CREATE INDEX ix_user_sessions__device_id ON identity.user_sessions (device_id);
CREATE INDEX ix_user_sessions__pos_terminal_id ON identity.user_sessions (pos_terminal_id);
CREATE INDEX ix_user_sessions__branch_id ON identity.user_sessions (branch_id);

-- -----------------------------------------------------------------------------------------------------
-- Intentos de acceso (append-only; se purgan por antigüedad). Locales del nodo.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE identity.login_attempts (
    id                    uuid          NOT NULL,
    company_id            uuid,
    node_id               uuid          NOT NULL,
    occurred_at           timestamptz   NOT NULL,
    kind                  varchar(12)   NOT NULL,
    identifier_attempted  varchar(60)   NOT NULL,
    user_id               uuid,
    device_id             uuid,
    ip_address            inet,
    succeeded             boolean       NOT NULL,
    failure_reason        varchar(40),
    CONSTRAINT pk_login_attempts PRIMARY KEY (id),
    CONSTRAINT ck_login_attempts__kind CHECK (kind IN ('PASSWORD', 'PIN', 'SUPERVISOR', 'PAIRING')),
    CONSTRAINT ck_login_attempts__failure CHECK (succeeded = (failure_reason IS NULL))
);

-- Sin FK: el registro de intentos sobrevive a usuarios y equipos borrados (como la auditoría).
CREATE INDEX ix_login_attempts__identifier ON identity.login_attempts (identifier_attempted, occurred_at);
CREATE INDEX ix_login_attempts__ip ON identity.login_attempts (ip_address, occurred_at);
CREATE INDEX ix_login_attempts__occurred_at ON identity.login_attempts (occurred_at);

-- -----------------------------------------------------------------------------------------------------
-- Autorizaciones de supervisor: un solo uso, ligadas a permiso + acción + objetivo (RN-GEN-05, RN-SEC-03)
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE identity.authorization_grants (
    id                   uuid          NOT NULL,
    company_id           uuid          NOT NULL,
    node_id              uuid          NOT NULL,
    permission_code      varchar(100)  NOT NULL,
    requested_by         uuid          NOT NULL,
    authorized_by        uuid          NOT NULL,
    pos_terminal_id      uuid,
    action               varchar(100)  NOT NULL,
    target_type          varchar(60),
    target_id            uuid,
    reason               varchar(250),
    context              jsonb,
    granted_at           timestamptz   NOT NULL,
    expires_at           timestamptz   NOT NULL,
    consumed_at          timestamptz,
    consumed_by_request  varchar(64),
    CONSTRAINT pk_authorization_grants PRIMARY KEY (id),
    CONSTRAINT fk_authorization_grants__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_authorization_grants__node FOREIGN KEY (node_id) REFERENCES org.nodes (id),
    CONSTRAINT fk_authorization_grants__permission FOREIGN KEY (permission_code) REFERENCES identity.permissions (code),
    CONSTRAINT fk_authorization_grants__requested_by FOREIGN KEY (requested_by) REFERENCES identity.users (id),
    CONSTRAINT fk_authorization_grants__authorized_by FOREIGN KEY (authorized_by) REFERENCES identity.users (id),
    CONSTRAINT fk_authorization_grants__pos_terminal FOREIGN KEY (pos_terminal_id) REFERENCES org.pos_terminals (id),
    CONSTRAINT ck_authorization_grants__not_self CHECK (authorized_by <> requested_by),
    CONSTRAINT ck_authorization_grants__expiry CHECK (expires_at > granted_at)
);

CREATE INDEX ix_authorization_grants__company_id ON identity.authorization_grants (company_id);
CREATE INDEX ix_authorization_grants__node_id ON identity.authorization_grants (node_id);
CREATE INDEX ix_authorization_grants__permission_code ON identity.authorization_grants (permission_code);
CREATE INDEX ix_authorization_grants__requested_by ON identity.authorization_grants (requested_by);
CREATE INDEX ix_authorization_grants__authorized_by ON identity.authorization_grants (authorized_by);
CREATE INDEX ix_authorization_grants__pos_terminal_id ON identity.authorization_grants (pos_terminal_id);
