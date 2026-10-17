-- =====================================================================================================
-- V2026.10.035 · identity · Propietario con la cuenta del portal (Fase 13-B, D13B-09).
-- Diseño: docs/fases/fase-13b-propuesta.md §6.4 e "Informe parcial D/E".
--
--   · username admite un correo (hasta 120 caracteres): el Propietario entra a la administración con el MISMO correo y la MISMA
--     contraseña de su cuenta del portal. La contraseña la escribe el dueño en el instalador; el hash se calcula aquí, en el equipo
--     (nunca viaja un hash desde la nube).
--   · portal_linked_at: cuándo se creó el usuario con un enlace firmado de la nube (el usuario del portal es un CLIENTE dueño de la
--     empresa de la licencia). Nulo para los usuarios locales (cajeros, administradores creados en la tienda).
-- Los permisos de pos_app sobre la tabla no cambian (A__system__privileges.sql los da a nivel de tabla).
-- =====================================================================================================

-- La vista reporting.users lee username: se quita para cambiar el tipo y se vuelve a crear IGUAL (R__reporting__views.sql la define;
-- si ese script cambia, la recrea con su propio DROP … CASCADE).
DROP VIEW IF EXISTS reporting.users;

ALTER TABLE identity.users ALTER COLUMN username TYPE varchar(120);

CREATE VIEW reporting.users AS
SELECT u.id AS user_id, u.company_id, u.username, u.display_name
FROM identity.users u;

ALTER TABLE identity.users DROP CONSTRAINT ck_users__username;
ALTER TABLE identity.users ADD CONSTRAINT ck_users__username CHECK (username ~ '^[a-z0-9._@+-]{3,120}$');

ALTER TABLE identity.users ADD COLUMN portal_linked_at timestamptz;

COMMENT ON COLUMN identity.users.portal_linked_at IS
    'Fase 13-B (D13B-09): el usuario se creó con la cuenta del portal del dueño (enlace firmado por la nube); nulo si es local.';
