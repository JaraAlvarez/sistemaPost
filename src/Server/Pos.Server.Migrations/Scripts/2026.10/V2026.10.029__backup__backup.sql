-- =====================================================================================================
-- V2026.10.029 · backup · Destinos, historial de backups, copias, restauraciones de prueba y código de recuperación (Fase 11).
-- Diseño: docs/fases/fase-11-propuesta.md §4, D11-03 a D11-10.
--
--   · destinations: LOCAL (carpeta del servidor), EXTERNAL (disco USB), NETWORK (carpeta de red) y S3 (MinIO en el VPS u otro
--     compatible). El secreto de S3 se guarda cifrado con DPAPI del equipo (bytes), nunca en claro.
--   · backup_runs: cada backup terminado (correcto o fallido). Solo inserción.
--   · backup_copies: el paquete en cada destino (pendiente, copiado, fallido o borrado por retención).
--   · restore_tests: restauraciones de prueba semanales. Solo inserción.
--   · recovery_keys: la clave de datos envuelta con el código de recuperación (nunca el código ni la clave en claro).
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS backup;

CREATE TABLE backup.destinations (
    id               uuid           NOT NULL,
    company_id       uuid           NOT NULL,
    kind             varchar(10)    NOT NULL,
    name             varchar(80)    NOT NULL,
    path             varchar(400),
    volume_label     varchar(60),
    endpoint         varchar(300),
    region           varchar(40),
    bucket           varchar(100),
    prefix           varchar(200),
    access_key       varchar(200),
    secret           bytea,
    on_scheduled     boolean        NOT NULL,
    on_nightly       boolean        NOT NULL,
    on_closing       boolean        NOT NULL,
    on_manual        boolean        NOT NULL,
    keep_daily       integer        NOT NULL,
    keep_weekly      integer        NOT NULL,
    keep_monthly     integer        NOT NULL,
    is_active        boolean        NOT NULL,
    last_status      varchar(10),
    last_error       varchar(500),
    last_success_at  timestamptz,
    created_at       timestamptz    NOT NULL,
    created_by       uuid           NOT NULL,
    updated_at       timestamptz,
    updated_by       uuid,
    CONSTRAINT pk_destinations PRIMARY KEY (id),
    CONSTRAINT ux_destinations__company_name UNIQUE (company_id, name),
    CONSTRAINT fk_destinations__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_destinations__kind CHECK (kind IN ('LOCAL', 'EXTERNAL', 'NETWORK', 'S3')),
    CONSTRAINT ck_destinations__target CHECK (
        (kind = 'S3') = (endpoint IS NOT NULL AND bucket IS NOT NULL AND access_key IS NOT NULL AND secret IS NOT NULL)
        AND (kind = 'S3' OR kind = 'LOCAL' OR path IS NOT NULL)),
    CONSTRAINT ck_destinations__retention CHECK (keep_daily BETWEEN 1 AND 60 AND keep_weekly BETWEEN 0 AND 52 AND keep_monthly BETWEEN 0 AND 120),
    CONSTRAINT ck_destinations__status CHECK (last_status IS NULL OR last_status IN ('OK', 'FAILED', 'PENDING'))
);

CREATE UNIQUE INDEX ux_destinations__local ON backup.destinations (company_id) WHERE kind = 'LOCAL';

CREATE TABLE backup.backup_runs (
    id               uuid           NOT NULL,
    node_id          uuid           NOT NULL,
    company_id       uuid,
    kind             varchar(15)    NOT NULL,
    started_at       timestamptz    NOT NULL,
    finished_at      timestamptz    NOT NULL,
    succeeded        boolean        NOT NULL,
    file_name        varchar(200),
    size_bytes       bigint,
    payload_sha256   char(64),
    app_version      varchar(100),
    schema_version   varchar(20),
    audit_seal_no    bigint,
    audit_seal_code  varchar(19),
    verified         boolean        NOT NULL,
    dump_entries     integer,
    error            varchar(1000),
    requested_by     uuid,
    CONSTRAINT pk_backup_runs PRIMARY KEY (id),
    CONSTRAINT ck_backup_runs__kind CHECK (kind IN ('SCHEDULED', 'NIGHTLY', 'CASH_CLOSING', 'PRE_UPDATE', 'PRE_RESTORE', 'MANUAL')),
    CONSTRAINT ck_backup_runs__result CHECK (succeeded = (file_name IS NOT NULL AND error IS NULL)),
    CONSTRAINT ck_backup_runs__verified CHECK (NOT verified OR succeeded)
);

CREATE INDEX ix_backup_runs__started ON backup.backup_runs (node_id, started_at DESC);
CREATE INDEX ix_backup_runs__kind_started ON backup.backup_runs (kind, started_at DESC);

CREATE TABLE backup.backup_copies (
    id              uuid           NOT NULL,
    run_id          uuid           NOT NULL,
    destination_id  uuid           NOT NULL,
    status          varchar(10)    NOT NULL,
    location        varchar(500),
    attempts        integer        NOT NULL DEFAULT 0,
    copied_at       timestamptz,
    deleted_at      timestamptz,
    error           varchar(500),
    CONSTRAINT pk_backup_copies PRIMARY KEY (id),
    CONSTRAINT ux_backup_copies__run_destination UNIQUE (run_id, destination_id),
    CONSTRAINT fk_backup_copies__run FOREIGN KEY (run_id) REFERENCES backup.backup_runs (id),
    CONSTRAINT fk_backup_copies__destination FOREIGN KEY (destination_id) REFERENCES backup.destinations (id),
    CONSTRAINT ck_backup_copies__status CHECK (status IN ('PENDING', 'COPIED', 'FAILED', 'DELETED')),
    CONSTRAINT ck_backup_copies__copied CHECK (status NOT IN ('COPIED', 'DELETED') OR copied_at IS NOT NULL)
);

CREATE INDEX ix_backup_copies__destination_id ON backup.backup_copies (destination_id);

CREATE TABLE backup.restore_tests (
    id             uuid           NOT NULL,
    run_id         uuid           NOT NULL,
    started_at     timestamptz    NOT NULL,
    finished_at    timestamptz    NOT NULL,
    succeeded      boolean        NOT NULL,
    audit_valid    boolean,
    seal_matches   boolean,
    counts_match   boolean,
    error          varchar(1000),
    CONSTRAINT pk_restore_tests PRIMARY KEY (id),
    CONSTRAINT fk_restore_tests__run FOREIGN KEY (run_id) REFERENCES backup.backup_runs (id)
);

CREATE INDEX ix_restore_tests__run_id ON backup.restore_tests (run_id);

CREATE TABLE backup.recovery_keys (
    version       integer        NOT NULL,
    key_id        varchar(16)    NOT NULL,
    wrapped_key   jsonb          NOT NULL,
    created_at    timestamptz    NOT NULL,
    created_by    uuid           NOT NULL,
    confirmed_at  timestamptz,
    confirmed_by  uuid,
    CONSTRAINT pk_recovery_keys PRIMARY KEY (version),
    CONSTRAINT fk_recovery_keys__created_by FOREIGN KEY (created_by) REFERENCES identity.users (id),
    CONSTRAINT fk_recovery_keys__confirmed_by FOREIGN KEY (confirmed_by) REFERENCES identity.users (id),
    CONSTRAINT ck_recovery_keys__version CHECK (version > 0),
    CONSTRAINT ck_recovery_keys__confirmed CHECK ((confirmed_at IS NULL) = (confirmed_by IS NULL))
);

CREATE INDEX ix_recovery_keys__created_by ON backup.recovery_keys (created_by);
CREATE INDEX ix_recovery_keys__confirmed_by ON backup.recovery_keys (confirmed_by);

-- Historiales de solo inserción (como la auditoría).
CREATE FUNCTION backup.fn_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'La tabla %.% es de solo inserción', TG_TABLE_SCHEMA, TG_TABLE_NAME USING ERRCODE = '42501';
END;
$$;

CREATE TRIGGER trg_backup_runs_append_only BEFORE UPDATE OR DELETE ON backup.backup_runs
    FOR EACH ROW EXECUTE FUNCTION backup.fn_append_only();

CREATE TRIGGER trg_restore_tests_append_only BEFORE UPDATE OR DELETE ON backup.restore_tests
    FOR EACH ROW EXECUTE FUNCTION backup.fn_append_only();
