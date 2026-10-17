-- =====================================================================================================
-- V2026.10.047 · identity · Permiso inventory.policy.manage (Reabastecimiento: mínimos y máximos).
--
-- Antes, guardar el mínimo y el máximo de un producto pedía inventory.adjustment.manage. Ahora tiene su propio permiso. Para que
-- nadie pierda lo que ya podía hacer al actualizar, TODO rol (de sistema o creado por la empresa) que tenga
-- inventory.adjustment.manage recibe también inventory.policy.manage. Los roles de sistema además lo reciben de su definición
-- (SystemRoleSynchronizer); el rol Compras lo recibe a propósito (quien compra define mínimos y máximos).
--
-- El permiso se inserta aquí porque el catálogo (R__identity__permissions_catalog.sql) corre DESPUÉS de los scripts versionados;
-- el repetible luego lo deja con su descripción definitiva. Los usuarios de esos roles cambian de versión de seguridad (sus
-- sesiones toman el permiso nuevo).
-- =====================================================================================================

INSERT INTO identity.permissions (code, module, description, is_sensitive)
VALUES ('inventory.policy.manage', 'inventory', 'Definir mínimos y máximos de existencias (a mano, desde Excel o calculados por la venta)', false)
ON CONFLICT (code) DO NOTHING;

WITH granted AS (
    INSERT INTO identity.role_permissions (role_id, permission_code)
    SELECT rp.role_id, 'inventory.policy.manage'
    FROM identity.role_permissions rp
    WHERE rp.permission_code = 'inventory.adjustment.manage'
    ON CONFLICT (role_id, permission_code) DO NOTHING
    RETURNING role_id)
UPDATE identity.users u
SET security_version = u.security_version + 1
WHERE u.id IN (SELECT ur.user_id FROM identity.user_roles ur JOIN granted g ON g.role_id = ur.role_id);
