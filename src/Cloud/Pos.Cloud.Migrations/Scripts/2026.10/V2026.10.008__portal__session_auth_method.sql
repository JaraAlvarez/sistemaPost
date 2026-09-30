-- =====================================================================================================
-- V2026.10.008 · portal · Método de autenticación de la sesión (ADR-0062): contraseña o Google. Quien entra con Google no
-- queda obligado a cambiar una contraseña temporal (no la usó) y el doble factor pasa a ser opcional (Portal:RequireTotp).
-- =====================================================================================================

ALTER TABLE portal.portal_sessions ADD COLUMN auth_method varchar(10) NOT NULL DEFAULT 'PASSWORD';
ALTER TABLE portal.portal_sessions ADD CONSTRAINT ck_portal_sessions__auth_method CHECK (auth_method IN ('PASSWORD', 'GOOGLE'));

COMMENT ON COLUMN portal.portal_sessions.auth_method IS 'Cómo se autenticó la sesión: PASSWORD (contraseña, más TOTP si aplica) o GOOGLE (correo verificado de Google).';
COMMENT ON COLUMN portal.portal_sessions.second_factor_at IS 'Momento en que la sesión quedó ACTIVA (segundo factor, contraseña sin TOTP obligatorio o Google).';
