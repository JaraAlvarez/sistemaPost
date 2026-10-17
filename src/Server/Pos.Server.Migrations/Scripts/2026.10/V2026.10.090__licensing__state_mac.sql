-- =====================================================================================================
-- V2026.10.090 · licensing · Endurecimiento de la licencia. Diseño: docs/fases/fase-licencia-endurecimiento.md.
--
--   · license_state.state_mac: firma HMAC-SHA256 (hex) del estado sensible (token, revocada, reactivación, liberado, diferencia
--     del reloj, mayor hora observada, último check-in, restringida desde, inicio de la demo y piso del tiempo sin conexión) con un
--     secreto de la instalación que NO está en la BD ({DataRoot}\config\license.vault, DPAPI de la máquina). Si no cuadra, la
--     licencia queda RESTRINGIDA con la ventana normal de 24 h hasta el próximo check-in o activación (H4). Las filas existentes
--     quedan sin firma y se firman en la primera escritura.
--   · license_state.restricted_since: hora confiable en que la licencia pasó a restringida. Durante 24 h desde ahí se puede seguir
--     vendiendo en la jornada abierta; después solo cerrar (H6). Nula si no está restringida.
--   · license_state.demo_started_at: inicio de la demostración, copiado de system.installation.created_at y dentro de la firma.
--   · license_state.offline_floor_utc: desde cuándo se cuentan los días sin conexión si es posterior a la emisión del token (N1):
--     las instalaciones que ya existían cuentan desde esta actualización, no desde un check-in viejo. Nula en instalaciones nuevas.
--   · system.installation.created_at pasa a ser INMUTABLE (la demo no se alarga cambiando la fecha de creación).
-- Los permisos de pos_app sobre la tabla no cambian (A__system__privileges.sql los da a nivel de tabla).
-- =====================================================================================================

ALTER TABLE licensing.license_state
    ADD COLUMN state_mac         varchar(64),
    ADD COLUMN restricted_since  timestamptz,
    ADD COLUMN demo_started_at   timestamptz,
    ADD COLUMN offline_floor_utc timestamptz;

UPDATE licensing.license_state s
SET demo_started_at = i.created_at,
    offline_floor_utc = now()
FROM system.installation i;

COMMENT ON COLUMN licensing.license_state.state_mac IS 'Firma HMAC-SHA256 del estado sensible con el secreto de la instalación (no está en la BD).';
COMMENT ON COLUMN licensing.license_state.restricted_since IS 'Hora confiable en que la licencia pasó a restringida (ventana de 24 h para vender); nula si no lo está.';
COMMENT ON COLUMN licensing.license_state.demo_started_at IS 'Inicio de la demostración (copia firmada de system.installation.created_at).';
COMMENT ON COLUMN licensing.license_state.offline_floor_utc IS 'Piso desde el que se cuentan los días sin conexión (la actualización que introdujo el límite).';

CREATE OR REPLACE FUNCTION system.fn_installation_protect() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'system.installation no se puede borrar' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.installation_id <> OLD.installation_id THEN
        RAISE EXCEPTION 'installation_id es inmutable' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'created_at de la instalación es inmutable' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$;
