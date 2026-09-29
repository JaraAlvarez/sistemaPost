-- =====================================================================================================
-- V2026.10.003 · org · Empresa, sucursales, bodegas, nodos, dispositivos y cajas.
-- También: system.document_series, system.settings y las FK de system.installation.
-- Diseño: docs/fases/fase-02-propuesta.md §3.3, §9–§14 y revisión arquitectónica §2.
--
-- Coherencia de tenencia: las FK compuestas (branch_id, company_id) garantizan en la BD que una bodega,
-- caja o serie nunca apunte a una sucursal de otra empresa.
-- created_by / updated_by / deleted_by no tienen FK a identity.users (evita ciclos; el usuario 'system'
-- se crea en la misma transacción que la empresa).
-- =====================================================================================================

-- -----------------------------------------------------------------------------------------------------
-- Empresa (entidad legal: NIT + DV)
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE org.companies (
    id                     uuid          NOT NULL,
    legal_name             varchar(200)  NOT NULL,
    trade_name             varchar(200)  NOT NULL,
    person_type            varchar(10)   NOT NULL,
    identification_type    varchar(10)   NOT NULL,
    identification_number  varchar(30)   NOT NULL,
    check_digit            varchar(1),
    tax_regime             varchar(10)   NOT NULL,
    country_code           char(2)       NOT NULL,
    municipality_code      varchar(8)    NOT NULL,
    address                varchar(250)  NOT NULL,
    phone                  varchar(30),
    email                  varchar(200),
    currency_code          char(3)       NOT NULL,
    timezone               varchar(50)   NOT NULL,
    logo                   bytea,
    status                 varchar(10)   NOT NULL,
    row_version            bigint        NOT NULL DEFAULT 1,
    created_at             timestamptz   NOT NULL,
    created_by             uuid          NOT NULL,
    updated_at             timestamptz,
    updated_by             uuid,
    CONSTRAINT pk_companies PRIMARY KEY (id),
    CONSTRAINT ux_companies__identification UNIQUE (identification_type, identification_number),
    CONSTRAINT fk_companies__identification_type FOREIGN KEY (identification_type) REFERENCES ref.identification_types (code),
    CONSTRAINT fk_companies__tax_regime FOREIGN KEY (tax_regime) REFERENCES ref.tax_regimes (code),
    CONSTRAINT fk_companies__country FOREIGN KEY (country_code) REFERENCES ref.countries (code),
    CONSTRAINT fk_companies__municipality FOREIGN KEY (municipality_code) REFERENCES ref.municipalities (code),
    CONSTRAINT fk_companies__currency FOREIGN KEY (currency_code) REFERENCES ref.currencies (code),
    CONSTRAINT ck_companies__person_type CHECK (person_type IN ('LEGAL', 'NATURAL')),
    CONSTRAINT ck_companies__identification_number CHECK (identification_number ~ '^[0-9A-Za-z-]+$'),
    CONSTRAINT ck_companies__check_digit CHECK (check_digit ~ '^[0-9]$'),
    CONSTRAINT ck_companies__nit_check_digit CHECK (identification_type <> 'NIT' OR check_digit IS NOT NULL),
    CONSTRAINT ck_companies__logo_size CHECK (octet_length(logo) <= 524288),
    CONSTRAINT ck_companies__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_companies__row_version CHECK (row_version > 0)
);

CREATE INDEX ix_companies__tax_regime ON org.companies (tax_regime);
CREATE INDEX ix_companies__country_code ON org.companies (country_code);
CREATE INDEX ix_companies__municipality_code ON org.companies (municipality_code);
CREATE INDEX ix_companies__currency_code ON org.companies (currency_code);

CREATE TABLE org.company_fiscal_responsibilities (
    company_id           uuid         NOT NULL,
    responsibility_code  varchar(10)  NOT NULL,
    CONSTRAINT pk_company_fiscal_responsibilities PRIMARY KEY (company_id, responsibility_code),
    CONSTRAINT fk_company_fiscal_responsibilities__company FOREIGN KEY (company_id)
        REFERENCES org.companies (id) ON DELETE CASCADE,
    CONSTRAINT fk_company_fiscal_responsibilities__responsibility FOREIGN KEY (responsibility_code)
        REFERENCES ref.fiscal_responsibilities (code)
);

CREATE INDEX ix_company_fiscal_responsibilities__responsibility_code
    ON org.company_fiscal_responsibilities (responsibility_code);

-- -----------------------------------------------------------------------------------------------------
-- Sucursal (establecimiento). El código lo asigna la autoridad de la empresa y forma parte de los
-- prefijos de numeración interna.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE org.branches (
    id                    uuid          NOT NULL,
    company_id            uuid          NOT NULL,
    code                  varchar(6)    NOT NULL,
    name                  varchar(120)  NOT NULL,
    municipality_code     varchar(8)    NOT NULL,
    address               varchar(250)  NOT NULL,
    phone                 varchar(30),
    default_warehouse_id  uuid,
    status                varchar(10)   NOT NULL,
    row_version           bigint        NOT NULL DEFAULT 1,
    created_at            timestamptz   NOT NULL,
    created_by            uuid          NOT NULL,
    updated_at            timestamptz,
    updated_by            uuid,
    deleted_at            timestamptz,
    deleted_by            uuid,
    CONSTRAINT pk_branches PRIMARY KEY (id),
    CONSTRAINT ux_branches__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_branches__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_branches__municipality FOREIGN KEY (municipality_code) REFERENCES ref.municipalities (code),
    CONSTRAINT ck_branches__code CHECK (code ~ '^[A-Z0-9]{2,6}$'),
    CONSTRAINT ck_branches__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_branches__row_version CHECK (row_version > 0),
    CONSTRAINT ck_branches__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_branches__company_code ON org.branches (company_id, code) WHERE deleted_at IS NULL;
CREATE INDEX ix_branches__company_id ON org.branches (company_id);
CREATE INDEX ix_branches__municipality_code ON org.branches (municipality_code);

-- -----------------------------------------------------------------------------------------------------
-- Bodega. v2: código único POR SUCURSAL (lo crea la sucursal; dos tiendas offline no pueden chocar).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE org.warehouses (
    id            uuid          NOT NULL,
    company_id    uuid          NOT NULL,
    branch_id     uuid          NOT NULL,
    code          varchar(10)   NOT NULL,
    name          varchar(120)  NOT NULL,
    kind          varchar(20)   NOT NULL,
    allows_sales  boolean       NOT NULL,
    status        varchar(10)   NOT NULL,
    row_version   bigint        NOT NULL DEFAULT 1,
    created_at    timestamptz   NOT NULL,
    created_by    uuid          NOT NULL,
    updated_at    timestamptz,
    updated_by    uuid,
    deleted_at    timestamptz,
    deleted_by    uuid,
    CONSTRAINT pk_warehouses PRIMARY KEY (id),
    CONSTRAINT ux_warehouses__id_branch UNIQUE (id, branch_id),
    CONSTRAINT fk_warehouses__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT ck_warehouses__code CHECK (code ~ '^[A-Z0-9-]{2,10}$'),
    CONSTRAINT ck_warehouses__kind CHECK (kind IN ('SALES_FLOOR', 'STORAGE', 'DAMAGED', 'IN_TRANSIT')),
    CONSTRAINT ck_warehouses__sales CHECK (kind NOT IN ('DAMAGED', 'IN_TRANSIT') OR NOT allows_sales),
    CONSTRAINT ck_warehouses__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_warehouses__row_version CHECK (row_version > 0),
    CONSTRAINT ck_warehouses__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_warehouses__branch_code ON org.warehouses (branch_id, code) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX ux_warehouses__branch_in_transit ON org.warehouses (branch_id)
    WHERE kind = 'IN_TRANSIT' AND deleted_at IS NULL;
CREATE INDEX ix_warehouses__branch_id_company_id ON org.warehouses (branch_id, company_id);

-- Bodega por defecto de la sucursal: se crean juntas en la misma transacción (FK diferible).
ALTER TABLE org.branches
    ADD CONSTRAINT fk_branches__default_warehouse FOREIGN KEY (default_warehouse_id, id)
        REFERENCES org.warehouses (id, branch_id) DEFERRABLE INITIALLY DEFERRED;

CREATE INDEX ix_branches__default_warehouse_id ON org.branches (default_warehouse_id, id);

-- -----------------------------------------------------------------------------------------------------
-- Nodo: instalación con BD propia (identidad de auditoría, series, sincronización y licencia).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE org.nodes (
    id             uuid          NOT NULL,
    company_id     uuid          NOT NULL,
    branch_id      uuid,
    kind           varchar(20)   NOT NULL,
    name           varchar(100)  NOT NULL,
    epoch          integer       NOT NULL DEFAULT 1,
    status         varchar(10)   NOT NULL,
    registered_at  timestamptz   NOT NULL,
    created_at     timestamptz   NOT NULL,
    created_by     uuid          NOT NULL,
    updated_at     timestamptz,
    updated_by     uuid,
    CONSTRAINT pk_nodes PRIMARY KEY (id),
    CONSTRAINT fk_nodes__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_nodes__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT ck_nodes__kind CHECK (kind IN ('ALL_IN_ONE', 'STORE_SERVER', 'TERMINAL_AUTONOMOUS', 'CLOUD')),
    CONSTRAINT ck_nodes__branch CHECK ((kind = 'CLOUD') = (branch_id IS NULL)),
    CONSTRAINT ck_nodes__epoch CHECK (epoch > 0),
    CONSTRAINT ck_nodes__status CHECK (status IN ('ACTIVE', 'RETIRED'))
);

CREATE INDEX ix_nodes__company_id ON org.nodes (company_id);
CREATE INDEX ix_nodes__branch_id_company_id ON org.nodes (branch_id, company_id);

-- -----------------------------------------------------------------------------------------------------
-- Dispositivo: equipo físico (solo el hash de su huella de hardware).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE org.devices (
    id                        uuid          NOT NULL,
    company_id                uuid          NOT NULL,
    node_id                   uuid          NOT NULL,
    kind                      varchar(20)   NOT NULL,
    hostname                  varchar(100)  NOT NULL,
    machine_fingerprint_hash  char(64)      NOT NULL,
    os_version                varchar(60),
    app_version               varchar(60),
    paired_at                 timestamptz   NOT NULL,
    last_seen_at              timestamptz,
    status                    varchar(10)   NOT NULL,
    created_at                timestamptz   NOT NULL,
    created_by                uuid          NOT NULL,
    updated_at                timestamptz,
    updated_by                uuid,
    CONSTRAINT pk_devices PRIMARY KEY (id),
    CONSTRAINT fk_devices__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_devices__node FOREIGN KEY (node_id) REFERENCES org.nodes (id),
    CONSTRAINT ck_devices__kind CHECK (kind IN ('STORE_SERVER', 'TERMINAL', 'ADMIN_WORKSTATION', 'ALL_IN_ONE')),
    CONSTRAINT ck_devices__fingerprint CHECK (machine_fingerprint_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_devices__status CHECK (status IN ('ACTIVE', 'REVOKED'))
);

CREATE UNIQUE INDEX ux_devices__company_fingerprint ON org.devices (company_id, machine_fingerprint_hash)
    WHERE status = 'ACTIVE';
CREATE INDEX ix_devices__node_id ON org.devices (node_id);

-- -----------------------------------------------------------------------------------------------------
-- Caja: puesto LÓGICO de venta (no es el PC). BLOCKED solo por seguridad.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE org.pos_terminals (
    id            uuid         NOT NULL,
    company_id    uuid         NOT NULL,
    branch_id     uuid         NOT NULL,
    code          varchar(6)   NOT NULL,
    name          varchar(60)  NOT NULL,
    warehouse_id  uuid         NOT NULL,
    device_id     uuid,
    status        varchar(10)  NOT NULL,
    row_version   bigint       NOT NULL DEFAULT 1,
    created_at    timestamptz  NOT NULL,
    created_by    uuid         NOT NULL,
    updated_at    timestamptz,
    updated_by    uuid,
    deleted_at    timestamptz,
    deleted_by    uuid,
    CONSTRAINT pk_pos_terminals PRIMARY KEY (id),
    CONSTRAINT ux_pos_terminals__id_branch UNIQUE (id, branch_id),
    CONSTRAINT fk_pos_terminals__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_pos_terminals__warehouse FOREIGN KEY (warehouse_id, branch_id) REFERENCES org.warehouses (id, branch_id),
    CONSTRAINT fk_pos_terminals__device FOREIGN KEY (device_id) REFERENCES org.devices (id),
    CONSTRAINT ck_pos_terminals__code CHECK (code ~ '^[A-Z0-9]{2,6}$'),
    CONSTRAINT ck_pos_terminals__status CHECK (status IN ('ACTIVE', 'INACTIVE', 'BLOCKED')),
    CONSTRAINT ck_pos_terminals__row_version CHECK (row_version > 0),
    CONSTRAINT ck_pos_terminals__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_pos_terminals__branch_code ON org.pos_terminals (branch_id, code) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX ux_pos_terminals__device ON org.pos_terminals (device_id)
    WHERE device_id IS NOT NULL AND deleted_at IS NULL;
CREATE INDEX ix_pos_terminals__branch_id_company_id ON org.pos_terminals (branch_id, company_id);
CREATE INDEX ix_pos_terminals__warehouse_id_branch_id ON org.pos_terminals (warehouse_id, branch_id);

-- La bodega de despacho de una caja debe admitir ventas (la misma sucursal ya la garantiza la FK compuesta).
CREATE FUNCTION org.fn_pos_terminal_warehouse() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM org.warehouses w WHERE w.id = NEW.warehouse_id AND w.allows_sales) THEN
        RAISE EXCEPTION 'La bodega de la caja debe admitir ventas'
            USING ERRCODE = '23514', CONSTRAINT = 'ck_pos_terminals__warehouse_allows_sales';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_pos_terminal_warehouse
    BEFORE INSERT OR UPDATE OF warehouse_id ON org.pos_terminals
    FOR EACH ROW EXECUTE FUNCTION org.fn_pos_terminal_warehouse();

-- -----------------------------------------------------------------------------------------------------
-- Numeración INTERNA de documentos (el número fiscal lo asigna el proveedor en billing, Fase 11-B).
-- El prefijo lo genera el sistema ({sucursal}{caja} o {sucursal}); no es editable ni es el prefijo DIAN.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE system.document_series (
    id               uuid         NOT NULL,
    company_id       uuid         NOT NULL,
    branch_id        uuid         NOT NULL,
    pos_terminal_id  uuid,
    document_type    varchar(30)  NOT NULL,
    prefix           varchar(20)  NOT NULL,
    next_number      bigint       NOT NULL DEFAULT 1,
    padding          smallint     NOT NULL DEFAULT 6,
    status           varchar(10)  NOT NULL,
    created_at       timestamptz  NOT NULL,
    created_by       uuid         NOT NULL,
    updated_at       timestamptz,
    updated_by       uuid,
    CONSTRAINT pk_document_series PRIMARY KEY (id),
    CONSTRAINT ux_document_series__company_type_prefix UNIQUE (company_id, document_type, prefix),
    CONSTRAINT fk_document_series__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT fk_document_series__pos_terminal FOREIGN KEY (pos_terminal_id, branch_id)
        REFERENCES org.pos_terminals (id, branch_id),
    CONSTRAINT fk_document_series__document_type FOREIGN KEY (document_type) REFERENCES system.document_types (code),
    CONSTRAINT ck_document_series__prefix CHECK (prefix ~ '^[A-Z0-9-]+$'),
    CONSTRAINT ck_document_series__next_number CHECK (next_number > 0),
    CONSTRAINT ck_document_series__padding CHECK (padding BETWEEN 4 AND 12),
    CONSTRAINT ck_document_series__status CHECK (status IN ('ACTIVE', 'INACTIVE'))
);

CREATE UNIQUE INDEX ux_document_series__branch_type_active ON system.document_series (branch_id, document_type)
    WHERE pos_terminal_id IS NULL AND status = 'ACTIVE';
CREATE UNIQUE INDEX ux_document_series__terminal_type_active ON system.document_series (pos_terminal_id, document_type)
    WHERE pos_terminal_id IS NOT NULL AND status = 'ACTIVE';
CREATE INDEX ix_document_series__branch_id_company_id ON system.document_series (branch_id, company_id);
CREATE INDEX ix_document_series__pos_terminal_id_branch_id ON system.document_series (pos_terminal_id, branch_id);
CREATE INDEX ix_document_series__document_type ON system.document_series (document_type);

-- El alcance de la serie debe coincidir con el del tipo de documento; next_number nunca retrocede.
CREATE FUNCTION system.fn_document_series_scope() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_scope varchar(10);
BEGIN
    SELECT series_scope INTO v_scope FROM system.document_types WHERE code = NEW.document_type;
    IF v_scope = 'TERMINAL' AND NEW.pos_terminal_id IS NULL THEN
        RAISE EXCEPTION 'El tipo % exige una serie por caja', NEW.document_type
            USING ERRCODE = '23514', CONSTRAINT = 'ck_document_series__scope';
    END IF;
    IF v_scope = 'BRANCH' AND NEW.pos_terminal_id IS NOT NULL THEN
        RAISE EXCEPTION 'El tipo % exige una serie por sucursal', NEW.document_type
            USING ERRCODE = '23514', CONSTRAINT = 'ck_document_series__scope';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.next_number < OLD.next_number THEN
        RAISE EXCEPTION 'La numeración de una serie no puede retroceder'
            USING ERRCODE = '23514', CONSTRAINT = 'ck_document_series__monotonic';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_document_series_scope
    BEFORE INSERT OR UPDATE ON system.document_series
    FOR EACH ROW EXECUTE FUNCTION system.fn_document_series_scope();

-- -----------------------------------------------------------------------------------------------------
-- Configuración: solo EXCEPCIONES al valor por defecto del código. Resolución caja → sucursal → empresa.
-- scope_id no tiene FK (es polimórfico): lo valida la aplicación.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE system.settings (
    id           uuid          NOT NULL,
    company_id   uuid          NOT NULL,
    scope_type   varchar(10)   NOT NULL,
    scope_id     uuid          NOT NULL,
    key          varchar(100)  NOT NULL,
    value        jsonb         NOT NULL,
    row_version  bigint        NOT NULL DEFAULT 1,
    created_at   timestamptz   NOT NULL,
    created_by   uuid          NOT NULL,
    updated_at   timestamptz,
    updated_by   uuid,
    CONSTRAINT pk_settings PRIMARY KEY (id),
    CONSTRAINT ux_settings__scope_key UNIQUE (scope_type, scope_id, key),
    CONSTRAINT fk_settings__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_settings__scope_type CHECK (scope_type IN ('COMPANY', 'BRANCH', 'TERMINAL')),
    CONSTRAINT ck_settings__company_scope CHECK (scope_type <> 'COMPANY' OR scope_id = company_id),
    CONSTRAINT ck_settings__key CHECK (key ~ '^[a-z]+(\.[a-z_]+)+$'),
    CONSTRAINT ck_settings__row_version CHECK (row_version > 0)
);

CREATE INDEX ix_settings__company_id ON system.settings (company_id);

-- -----------------------------------------------------------------------------------------------------
-- FK de la instalación hacia la organización local
-- -----------------------------------------------------------------------------------------------------
ALTER TABLE system.installation
    ADD CONSTRAINT fk_installation__home_company FOREIGN KEY (home_company_id) REFERENCES org.companies (id),
    ADD CONSTRAINT fk_installation__home_branch FOREIGN KEY (home_branch_id, home_company_id)
        REFERENCES org.branches (id, company_id);

CREATE INDEX ix_installation__home_company_id ON system.installation (home_company_id);
CREATE INDEX ix_installation__home_branch_id_home_company_id ON system.installation (home_branch_id, home_company_id);
