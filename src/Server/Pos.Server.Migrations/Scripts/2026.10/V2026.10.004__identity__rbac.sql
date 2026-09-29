-- =====================================================================================================
-- V2026.10.004 · identity · Estructura RBAC (el comportamiento de autenticación llega en la Fase 3).
-- Diseño: docs/fases/fase-02-propuesta.md §3.4 y §12.
-- =====================================================================================================

CREATE TABLE identity.users (
    id                    uuid          NOT NULL,
    company_id            uuid          NOT NULL,
    username              varchar(60)   NOT NULL,
    display_name          varchar(120)  NOT NULL,
    email                 varchar(200),
    kind                  varchar(10)   NOT NULL,
    password_hash         varchar(255),
    pin_hash              varchar(255),
    status                varchar(10)   NOT NULL,
    must_change_password  boolean       NOT NULL DEFAULT true,
    -- Estado LOCAL del nodo: no se sincroniza (bloqueos e intentos son de cada equipo).
    failed_login_count    smallint      NOT NULL DEFAULT 0,
    locked_until          timestamptz,
    last_login_at         timestamptz,
    password_changed_at   timestamptz,
    row_version           bigint        NOT NULL DEFAULT 1,
    created_at            timestamptz   NOT NULL,
    created_by            uuid          NOT NULL,
    updated_at            timestamptz,
    updated_by            uuid,
    deleted_at            timestamptz,
    deleted_by            uuid,
    CONSTRAINT pk_users PRIMARY KEY (id),
    CONSTRAINT fk_users__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_users__username CHECK (username ~ '^[a-z0-9._-]{3,60}$'),
    CONSTRAINT ck_users__kind CHECK (kind IN ('HUMAN', 'SYSTEM')),
    CONSTRAINT ck_users__status CHECK (status IN ('ACTIVE', 'LOCKED', 'DISABLED')),
    -- El usuario técnico 'system' nunca puede iniciar sesión.
    CONSTRAINT ck_users__system_cannot_login CHECK (
        kind <> 'SYSTEM' OR (status = 'DISABLED' AND password_hash IS NULL AND pin_hash IS NULL)),
    CONSTRAINT ck_users__failed_login_count CHECK (failed_login_count >= 0),
    CONSTRAINT ck_users__row_version CHECK (row_version > 0),
    CONSTRAINT ck_users__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_users__company_username ON identity.users (company_id, username) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX ux_users__company_system ON identity.users (company_id) WHERE kind = 'SYSTEM';
CREATE INDEX ix_users__company_id ON identity.users (company_id);

-- Catálogo sembrado desde el código en cada versión (R__identity__permissions_catalog.sql).
-- Los permisos no se borran: se marcan obsoletos para que las asignaciones y la auditoría sigan siendo válidas.
CREATE TABLE identity.permissions (
    code           varchar(100)  NOT NULL,
    module         varchar(30)   NOT NULL,
    description    varchar(200)  NOT NULL,
    is_sensitive   boolean       NOT NULL,
    is_deprecated  boolean       NOT NULL DEFAULT false,
    CONSTRAINT pk_permissions PRIMARY KEY (code),
    CONSTRAINT ck_permissions__code CHECK (code ~ '^[a-z]+\.[a-z_]+\.[a-z_]+$')
);

CREATE TABLE identity.roles (
    id           uuid          NOT NULL,
    company_id   uuid          NOT NULL,
    code         varchar(40)   NOT NULL,
    name         varchar(80)   NOT NULL,
    description  varchar(250),
    is_system    boolean       NOT NULL,
    row_version  bigint        NOT NULL DEFAULT 1,
    created_at   timestamptz   NOT NULL,
    created_by   uuid          NOT NULL,
    updated_at   timestamptz,
    updated_by   uuid,
    deleted_at   timestamptz,
    deleted_by   uuid,
    CONSTRAINT pk_roles PRIMARY KEY (id),
    CONSTRAINT fk_roles__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_roles__code CHECK (code ~ '^[A-Z][A-Z0-9_]{1,39}$'),
    CONSTRAINT ck_roles__system_not_deleted CHECK (NOT is_system OR deleted_at IS NULL),
    CONSTRAINT ck_roles__row_version CHECK (row_version > 0),
    CONSTRAINT ck_roles__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_roles__company_code ON identity.roles (company_id, code) WHERE deleted_at IS NULL;
CREATE INDEX ix_roles__company_id ON identity.roles (company_id);

CREATE TABLE identity.role_permissions (
    role_id          uuid          NOT NULL,
    permission_code  varchar(100)  NOT NULL,
    CONSTRAINT pk_role_permissions PRIMARY KEY (role_id, permission_code),
    CONSTRAINT fk_role_permissions__role FOREIGN KEY (role_id) REFERENCES identity.roles (id) ON DELETE CASCADE,
    CONSTRAINT fk_role_permissions__permission FOREIGN KEY (permission_code) REFERENCES identity.permissions (code)
);

CREATE INDEX ix_role_permissions__permission_code ON identity.role_permissions (permission_code);

-- branch_id NULL = el rol aplica en todas las sucursales de la empresa.
CREATE TABLE identity.user_roles (
    id          uuid         NOT NULL,
    user_id     uuid         NOT NULL,
    role_id     uuid         NOT NULL,
    branch_id   uuid,
    granted_at  timestamptz  NOT NULL,
    granted_by  uuid         NOT NULL,
    CONSTRAINT pk_user_roles PRIMARY KEY (id),
    CONSTRAINT fk_user_roles__user FOREIGN KEY (user_id) REFERENCES identity.users (id) ON DELETE CASCADE,
    CONSTRAINT fk_user_roles__role FOREIGN KEY (role_id) REFERENCES identity.roles (id) ON DELETE CASCADE,
    CONSTRAINT fk_user_roles__branch FOREIGN KEY (branch_id) REFERENCES org.branches (id)
);

CREATE UNIQUE INDEX ux_user_roles__user_role_branch ON identity.user_roles (user_id, role_id, branch_id) NULLS NOT DISTINCT;
CREATE INDEX ix_user_roles__role_id ON identity.user_roles (role_id);
CREATE INDEX ix_user_roles__branch_id ON identity.user_roles (branch_id);

CREATE TABLE identity.user_permission_overrides (
    id               uuid          NOT NULL,
    user_id          uuid          NOT NULL,
    permission_code  varchar(100)  NOT NULL,
    effect           varchar(5)    NOT NULL,
    branch_id        uuid,
    reason           varchar(250)  NOT NULL,
    created_at       timestamptz   NOT NULL,
    created_by       uuid          NOT NULL,
    updated_at       timestamptz,
    updated_by       uuid,
    CONSTRAINT pk_user_permission_overrides PRIMARY KEY (id),
    CONSTRAINT fk_user_permission_overrides__user FOREIGN KEY (user_id) REFERENCES identity.users (id) ON DELETE CASCADE,
    CONSTRAINT fk_user_permission_overrides__permission FOREIGN KEY (permission_code) REFERENCES identity.permissions (code),
    CONSTRAINT fk_user_permission_overrides__branch FOREIGN KEY (branch_id) REFERENCES org.branches (id),
    CONSTRAINT ck_user_permission_overrides__effect CHECK (effect IN ('GRANT', 'DENY'))
);

CREATE UNIQUE INDEX ux_user_permission_overrides__user_permission_branch
    ON identity.user_permission_overrides (user_id, permission_code, branch_id) NULLS NOT DISTINCT;
CREATE INDEX ix_user_permission_overrides__permission_code ON identity.user_permission_overrides (permission_code);
CREATE INDEX ix_user_permission_overrides__branch_id ON identity.user_permission_overrides (branch_id);
