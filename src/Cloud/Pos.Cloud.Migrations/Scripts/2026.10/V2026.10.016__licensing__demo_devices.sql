-- =====================================================================================================
-- V2026.10.016 · licensing · Demo por equipo (H9). Diseño: docs/fases/fase-demo-por-equipo.md.
--
--   · demo_devices: primer inicio de la demostración por equipo, identificado por los hashes de su huella (fp1: placa, disco del
--     sistema y MachineGuid; nunca los valores crudos). Un equipo es el mismo si coinciden 2 de los 3 componentes: reinstalar Windows
--     (cambia MachineGuid) o cambiar el disco no reinician la demo. first_started_at solo puede bajar.
--   Los permisos de pos_app salen de A__system__privileges.sql (SELECT, INSERT, UPDATE en el esquema licensing; nada se borra).
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
    CONSTRAINT pk_demo_devices PRIMARY KEY (id),
    CONSTRAINT ck_demo_devices__hashes CHECK (
        (board_hash IS NULL OR board_hash ~ '^[0-9a-f]{32}$')
        AND (disk_hash IS NULL OR disk_hash ~ '^[0-9a-f]{32}$')
        AND (machine_hash IS NULL OR machine_hash ~ '^[0-9a-f]{32}$')
        AND num_nonnulls(board_hash, disk_hash, machine_hash) >= 2),
    CONSTRAINT ck_demo_devices__requests CHECK (requests > 0)
);

CREATE INDEX ix_demo_devices__board_hash ON licensing.demo_devices (board_hash) WHERE board_hash IS NOT NULL;
CREATE INDEX ix_demo_devices__disk_hash ON licensing.demo_devices (disk_hash) WHERE disk_hash IS NOT NULL;
CREATE INDEX ix_demo_devices__machine_hash ON licensing.demo_devices (machine_hash) WHERE machine_hash IS NOT NULL;

COMMENT ON TABLE licensing.demo_devices IS 'Primer inicio de la demo por equipo (hashes de la huella, 2 de 3). Demo por equipo, H9.';
