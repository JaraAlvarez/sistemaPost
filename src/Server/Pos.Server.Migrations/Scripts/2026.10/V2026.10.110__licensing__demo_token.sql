-- =====================================================================================================
-- V2026.10.110 · licensing · Demo por equipo (docs/fases/fase-demo-por-equipo.md).
--
--   · license_state.demo_token: registro de la demo de ESTE equipo firmado por la nube (pos-demo+jws): primer inicio de la demo que la
--     nube conoce para la huella y si soporte la reinició. Va firmado, así que no entra en state_mac: se verifica en cada lectura y solo
--     puede adelantar el inicio de la demo (salvo un reinicio de soporte). Nulo sin conexión o con licencia.
-- Los permisos de pos_app sobre la tabla no cambian (A__system__privileges.sql los da a nivel de tabla).
-- =====================================================================================================

ALTER TABLE licensing.license_state
    ADD COLUMN demo_token text,
    ADD CONSTRAINT ck_license_state__demo_token_length CHECK (demo_token IS NULL OR length(demo_token) <= 8192);

COMMENT ON COLUMN licensing.license_state.demo_token IS 'Registro firmado por la nube de la demo de este equipo (demo por equipo).';
