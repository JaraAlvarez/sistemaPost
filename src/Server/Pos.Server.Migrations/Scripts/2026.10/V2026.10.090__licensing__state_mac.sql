-- =====================================================================================================
-- V2026.10.090 · licensing · Endurecimiento de la licencia. Diseño: docs/fases/fase-licencia-endurecimiento.md.
--
--   · license_state.state_mac: firma HMAC-SHA256 (hex) del estado sensible (token, revocada, reactivación, liberado, diferencia
--     del reloj, mayor hora observada, último check-in y restringida desde) con un secreto de la instalación que NO está en la BD
--     ({DataRoot}\config\license.vault, DPAPI de la máquina). Si no cuadra, la licencia queda RESTRINGIDA hasta el próximo
--     check-in o activación (H4). Las filas existentes quedan sin firma y se firman en la primera escritura.
--   · license_state.restricted_since: hora confiable en que la licencia pasó a restringida. Durante 24 h desde ahí se puede seguir
--     vendiendo en la jornada abierta; después solo cerrar (H6). Nula si no está restringida.
-- Los permisos de pos_app sobre la tabla no cambian (A__system__privileges.sql los da a nivel de tabla).
-- =====================================================================================================

ALTER TABLE licensing.license_state
    ADD COLUMN state_mac        varchar(64),
    ADD COLUMN restricted_since timestamptz;

COMMENT ON COLUMN licensing.license_state.state_mac IS 'Firma HMAC-SHA256 del estado sensible con el secreto de la instalación (no está en la BD).';
COMMENT ON COLUMN licensing.license_state.restricted_since IS 'Hora confiable en que la licencia pasó a restringida (ventana de 24 h para vender); nula si no lo está.';
