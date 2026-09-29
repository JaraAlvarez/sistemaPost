-- =====================================================================================================
-- V2026.10.003 · portal · Usuarios del portal (equipo del propietario) y sesiones revocables.
-- Diseño: docs/fases/fase-12a-propuesta.md §4 y §6 (L-08): Argon2id + TOTP obligatorio, bloqueo por intentos.
-- Nada se borra: un usuario se deshabilita; una sesión se revoca o vence.
-- =====================================================================================================

CREATE TABLE portal.portal_users (
    id                     uuid          NOT NULL,
    email                  varchar(120)  NOT NULL,
    display_name           varchar(120)  NOT NULL,
    kind                   varchar(10)   NOT NULL,
    role                   varchar(20)   NOT NULL,
    reseller_account_id    uuid,
    status                 varchar(10)   NOT NULL,
    password_hash          varchar(200),
    must_change_password   boolean       NOT NULL DEFAULT false,
    password_changed_at    timestamptz,
    -- Secreto TOTP cifrado con la protección de datos de ASP.NET Core (llaves fuera de la BD).
    totp_secret_protected  text,
    totp_enabled           boolean       NOT NULL DEFAULT false,
    totp_last_step         bigint,
    failed_login_count     smallint      NOT NULL DEFAULT 0,
    locked_until           timestamptz,
    last_login_at          timestamptz,
    security_version       bigint        NOT NULL DEFAULT 1,
    created_at             timestamptz   NOT NULL,
    created_by             uuid          NOT NULL,
    updated_at             timestamptz,
    updated_by             uuid,
    CONSTRAINT pk_portal_users PRIMARY KEY (id),
    CONSTRAINT ux_portal_users__email UNIQUE (email),
    CONSTRAINT fk_portal_users__created_by FOREIGN KEY (created_by) REFERENCES portal.portal_users (id),
    CONSTRAINT fk_portal_users__updated_by FOREIGN KEY (updated_by) REFERENCES portal.portal_users (id),
    CONSTRAINT ck_portal_users__email CHECK (email = lower(email) AND email ~ '^[^@\s]+@[^@\s]+$'),
    CONSTRAINT ck_portal_users__kind CHECK (kind IN ('HUMAN', 'SYSTEM')),
    CONSTRAINT ck_portal_users__role CHECK (role IN ('SUPERADMIN', 'SUPPORT', 'RESELLER')),
    CONSTRAINT ck_portal_users__status CHECK (status IN ('ACTIVE', 'DISABLED')),
    CONSTRAINT ck_portal_users__reseller CHECK ((role = 'RESELLER') = (reseller_account_id IS NOT NULL)),
    CONSTRAINT ck_portal_users__failed CHECK (failed_login_count >= 0),
    CONSTRAINT ck_portal_users__totp CHECK (NOT totp_enabled OR totp_secret_protected IS NOT NULL),
    -- El usuario técnico nunca puede entrar: sin contraseña, sin TOTP y deshabilitado.
    CONSTRAINT ck_portal_users__system CHECK (
        kind = 'HUMAN' OR (status = 'DISABLED' AND password_hash IS NULL AND totp_secret_protected IS NULL)),
    CONSTRAINT ck_portal_users__human_password CHECK (kind = 'SYSTEM' OR password_hash IS NOT NULL)
);

-- Toda clave foránea tiene índice (convención del producto, verificada por las pruebas de BD).
CREATE INDEX ix_portal_users__created_by ON portal.portal_users (created_by);
CREATE INDEX ix_portal_users__updated_by ON portal.portal_users (updated_by) WHERE updated_by IS NOT NULL;

-- Usuario técnico "system": autor de la línea base y de los cambios automáticos (vencimientos, check-ins).
INSERT INTO portal.portal_users (id, email, display_name, kind, role, status, created_at, created_by)
VALUES ('01926a00-0000-7000-8000-000000000001', 'system@localhost', 'Sistema', 'SYSTEM', 'SUPERADMIN', 'DISABLED', now(),
        '01926a00-0000-7000-8000-000000000001');

-- -----------------------------------------------------------------------------------------------------
-- Sesiones: token opaco (solo su SHA-256). Etapas: PENDING_TOTP / PENDING_ENROLLMENT tras la contraseña (vida corta)
-- y ACTIVE tras el segundo factor. PORTAL = cookie del navegador; API = Bearer para /admin.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE portal.portal_sessions (
    id                uuid          NOT NULL,
    user_id           uuid          NOT NULL,
    token_hash        char(64)      NOT NULL,
    stage             varchar(20)   NOT NULL,
    channel           varchar(10)   NOT NULL,
    created_at        timestamptz   NOT NULL,
    expires_at        timestamptz   NOT NULL,
    idle_expires_at   timestamptz   NOT NULL,
    last_seen_at      timestamptz   NOT NULL,
    second_factor_at  timestamptz,
    security_version  bigint        NOT NULL,
    revoked_at        timestamptz,
    revoked_reason    varchar(200),
    ip_address        inet,
    user_agent        varchar(300),
    CONSTRAINT pk_portal_sessions PRIMARY KEY (id),
    CONSTRAINT ux_portal_sessions__token_hash UNIQUE (token_hash),
    CONSTRAINT fk_portal_sessions__user FOREIGN KEY (user_id) REFERENCES portal.portal_users (id),
    CONSTRAINT ck_portal_sessions__stage CHECK (stage IN ('PENDING_TOTP', 'PENDING_ENROLLMENT', 'ACTIVE')),
    CONSTRAINT ck_portal_sessions__channel CHECK (channel IN ('PORTAL', 'API')),
    CONSTRAINT ck_portal_sessions__token_hash CHECK (token_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_portal_sessions__expires CHECK (expires_at > created_at AND idle_expires_at > created_at),
    CONSTRAINT ck_portal_sessions__active CHECK (stage <> 'ACTIVE' OR second_factor_at IS NOT NULL),
    CONSTRAINT ck_portal_sessions__revoked CHECK ((revoked_at IS NULL) = (revoked_reason IS NULL))
);

CREATE INDEX ix_portal_sessions__user ON portal.portal_sessions (user_id, created_at);
