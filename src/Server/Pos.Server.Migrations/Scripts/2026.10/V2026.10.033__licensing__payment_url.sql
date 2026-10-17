-- =====================================================================================================
-- V2026.10.033 · licensing · Enlace de pago en línea de la suscripción (Fase 12-C, D12C-08).
-- Diseño: docs/fases/fase-12c-propuesta.md §3 y §5 (fila POS).
--
--   · license_state.payment_url: enlace de pago que devuelve la nube en la activación o el check-in cuando la licencia está por
--     vencer (15 días o menos), en gracia o restringida. Se reemplaza en cada respuesta de la nube y queda vacío cuando la nube
--     no lo envía (licencia al día) o al liberar el equipo. Solo se guardan direcciones http/https (lo valida el POS).
--     No va firmado: es solo un enlace que la administración muestra con su código QR.
-- Los permisos de pos_app sobre la tabla no cambian (A__system__privileges.sql los da a nivel de tabla).
-- =====================================================================================================

ALTER TABLE licensing.license_state ADD COLUMN payment_url varchar(500);

COMMENT ON COLUMN licensing.license_state.payment_url IS 'Enlace de pago en línea de la suscripción (Fase 12-C); nulo si la licencia está al día.';
