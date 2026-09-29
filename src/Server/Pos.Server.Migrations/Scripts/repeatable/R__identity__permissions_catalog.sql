-- =====================================================================================================
-- R__identity__permissions_catalog · Catálogo de permisos (repetible).
-- FUENTE DE VERDAD: las clases *Permissions del código de cada módulo. La prueba
-- El_catalogo_de_permisos_del_codigo_coincide_con_el_de_la_BD falla si este archivo y el código difieren.
-- Los permisos nunca se borran: los que desaparecen del código se marcan is_deprecated.
-- =====================================================================================================

INSERT INTO identity.permissions (code, module, description, is_sensitive) VALUES
    ('audit.log.verify',              'audit',        'Verificar la integridad de la bitácora de auditoría',                    true),
    ('audit.log.view',                'audit',        'Consultar la bitácora de auditoría',                                    true),
    ('identity.permission.view',      'identity',     'Consultar el catálogo de permisos y los roles',                          false),
    ('identity.role.manage',          'identity',     'Crear, clonar y editar roles; asignar roles y excepciones',              true),
    ('identity.session.revoke',       'identity',     'Cerrar sesiones de otros usuarios',                                      true),
    ('identity.user.manage',          'identity',     'Crear, editar, activar y desactivar usuarios; restablecer contraseña o PIN', true),
    ('identity.user.view',            'identity',     'Consultar usuarios',                                                     false),
    ('organization.branch.manage',    'organization', 'Crear, modificar e inactivar sucursales',                               true),
    ('organization.branch.view',      'organization', 'Consultar sucursales, bodegas y cajas',                                 false),
    ('organization.company.manage',   'organization', 'Modificar los datos de la empresa',                                     true),
    ('organization.company.view',     'organization', 'Consultar los datos de la empresa',                                     false),
    ('organization.device.manage',    'organization', 'Generar códigos de emparejamiento y revocar equipos',                   true),
    ('organization.terminal.manage',  'organization', 'Crear, modificar e inactivar cajas',                                    true),
    ('organization.warehouse.manage', 'organization', 'Crear, modificar e inactivar bodegas',                                  true),
    ('settings.setting.manage',       'settings',     'Modificar la configuración general',                                    true),
    ('settings.setting.view',         'settings',     'Consultar la configuración general',                                    false)
ON CONFLICT (code) DO UPDATE SET module = EXCLUDED.module, description = EXCLUDED.description,
    is_sensitive = EXCLUDED.is_sensitive, is_deprecated = false;

-- Los permisos que ya no están en el código quedan obsoletos (no se borran).
UPDATE identity.permissions SET is_deprecated = true
WHERE code NOT IN (
    'audit.log.verify', 'audit.log.view', 'identity.permission.view', 'identity.role.manage', 'identity.session.revoke',
    'identity.user.manage', 'identity.user.view', 'organization.branch.manage', 'organization.branch.view',
    'organization.company.manage', 'organization.company.view', 'organization.device.manage',
    'organization.terminal.manage', 'organization.warehouse.manage', 'settings.setting.manage',
    'settings.setting.view');
