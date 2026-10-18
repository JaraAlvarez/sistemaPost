-- =====================================================================================================
-- V2026.10.016 · licensing · Demo por equipo (H9). Diseño: docs/fases/fase-demo-por-equipo.md.
--
--   · demo_devices: primer inicio de la demostración por equipo, identificado por los hashes de su huella (fp1: placa, disco del
--     sistema y MachineGuid; nunca los valores crudos; los genéricos se guardan vacíos). Mismo equipo: 2 de 3 con la placa en los dos
--     lados; con placa genérica, el disco (o el MachineGuid). first_started_at solo puede bajar, salvo que soporte REINICIE la demo
--     (status RESET, first_started_at = hora del reinicio, auditado); como pos_app no borra, el reinicio actualiza la fila.
--   · registered_ip: cupo diario de equipos nuevos por IP.
--   · Limpieza: las filas vistas una sola vez hace más de 90 días se BORRAN (único DELETE de licensing; ver A__system__privileges.sql).
-- =====================================================================================================

CREATE TABLE licensing.demo_devices (
    id               uuid         NOT NULL,
    board_hash       varchar(32),
    disk_hash        varchar(32),
    machine_hash     varchar(32),
    first_started_at timestamptz  NOT NULL,
    registered_at    timestamptz  NOT NULL,
    last_seen_at     timestamptz  NOT NULL,
    requests         integer      NOT NULL DEFAULT 1,
    registered_ip    inet,
    status           varchar(10)  NOT NULL DEFAULT 'ACTIVE',
    reset_at         timestamptz,
    reset_by         uuid,
    reset_reason     varchar(300),
    CONSTRAINT pk_demo_devices PRIMARY KEY (id),
    CONSTRAINT ck_demo_devices__hashes CHECK (
        (board_hash IS NULL OR board_hash ~ '^[0-9a-f]{32}$')
        AND (disk_hash IS NULL OR disk_hash ~ '^[0-9a-f]{32}$')
        AND (machine_hash IS NULL OR machine_hash ~ '^[0-9a-f]{32}$')
        AND num_nonnulls(board_hash, disk_hash, machine_hash) >= 1),
    CONSTRAINT ck_demo_devices__requests CHECK (requests > 0),
    CONSTRAINT ck_demo_devices__status CHECK (status IN ('ACTIVE', 'RESET')),
    CONSTRAINT ck_demo_devices__reset CHECK ((status = 'RESET') = (reset_at IS NOT NULL) AND (reset_at IS NULL OR reset_reason IS NOT NULL))
);

CREATE INDEX ix_demo_devices__board_hash ON licensing.demo_devices (board_hash) WHERE board_hash IS NOT NULL;
CREATE INDEX ix_demo_devices__disk_hash ON licensing.demo_devices (disk_hash) WHERE disk_hash IS NOT NULL;
CREATE INDEX ix_demo_devices__machine_hash ON licensing.demo_devices (machine_hash) WHERE machine_hash IS NOT NULL;
CREATE INDEX ix_demo_devices__registered_ip ON licensing.demo_devices (registered_ip, registered_at) WHERE registered_ip IS NOT NULL;
CREATE INDEX ix_demo_devices__last_seen ON licensing.demo_devices (last_seen_at) WHERE requests = 1;

COMMENT ON TABLE licensing.demo_devices IS 'Primer inicio de la demo por equipo (hashes de la huella). Demo por equipo, H9.';
