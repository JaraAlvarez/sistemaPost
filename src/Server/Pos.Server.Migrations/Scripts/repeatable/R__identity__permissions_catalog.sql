-- =====================================================================================================
-- R__identity__permissions_catalog · Catálogo de permisos (repetible).
-- FUENTE DE VERDAD: las clases *Permissions del código de cada módulo. La prueba
-- El_catalogo_de_permisos_del_codigo_coincide_con_el_de_la_BD falla si este archivo y el código difieren.
-- Los permisos nunca se borran: los que desaparecen del código se marcan is_deprecated.
-- =====================================================================================================

INSERT INTO identity.permissions (code, module, description, is_sensitive) VALUES
    ('audit.log.verify',              'audit',        'Verificar la integridad de la bitácora de auditoría',                           true),
    ('audit.log.view',                'audit',        'Consultar la bitácora de auditoría',                                            true),
    ('cash.payment_method.manage',    'cash',         'Crear y modificar medios de pago',                                              true),
    ('catalog.import.run',            'catalog',      'Importar productos y precios desde archivos',                                   true),
    ('catalog.master.manage',         'catalog',      'Categorías, marcas, listas de precios y reglas de báscula',                     false),
    ('catalog.price.manage',          'catalog',      'Fijar, programar y cancelar precios de venta',                                  true),
    ('catalog.product.manage',        'catalog',      'Crear y modificar productos, presentaciones, códigos e impuestos del producto', false),
    ('catalog.product.view',          'catalog',      'Consultar productos, códigos y precios vigentes',                               false),
    ('catalog.tax.manage',            'catalog',      'Crear impuestos y cambiar sus tarifas',                                         true),
    ('identity.permission.view',      'identity',     'Consultar el catálogo de permisos y los roles',                                 false),
    ('identity.role.manage',          'identity',     'Crear, clonar y editar roles; asignar roles y excepciones',                     true),
    ('identity.session.revoke',       'identity',     'Cerrar sesiones de otros usuarios',                                             true),
    ('identity.user.manage',          'identity',     'Crear, editar, activar y desactivar usuarios; restablecer contraseña o PIN',    true),
    ('identity.user.view',            'identity',     'Consultar usuarios',                                                            false),
    ('inventory.adjustment.approve',  'inventory',    'Aprobar ajustes que superan el umbral',                                         true),
    ('inventory.adjustment.manage',   'inventory',    'Crear ajustes de inventario y saldos iniciales',                                true),
    ('inventory.cost.view',           'inventory',    'Ver costos, valor del inventario y kardex valorizado',                          true),
    ('inventory.count.approve',       'inventory',    'Aprobar un conteo físico (genera los ajustes)',                                 true),
    ('inventory.count.manage',        'inventory',    'Crear, iniciar, revisar y cerrar conteos físicos',                              false),
    ('inventory.count.register',      'inventory',    'Registrar cantidades contadas',                                                 false),
    ('inventory.stock.verify',        'inventory',    'Verificar y reconstruir saldos contra el kardex',                               true),
    ('inventory.stock.view',          'inventory',    'Consultar existencias por bodega (sin costos)',                                 false),
    ('inventory.transfer.manage',     'inventory',    'Crear, despachar y recibir traslados entre bodegas',                            false),
    ('organization.branch.manage',    'organization', 'Crear, modificar e inactivar sucursales',                                       true),
    ('organization.branch.view',      'organization', 'Consultar sucursales, bodegas y cajas',                                         false),
    ('organization.company.manage',   'organization', 'Modificar los datos de la empresa',                                             true),
    ('organization.company.view',     'organization', 'Consultar los datos de la empresa',                                             false),
    ('organization.device.manage',    'organization', 'Generar códigos de emparejamiento y revocar equipos',                           true),
    ('organization.terminal.manage',  'organization', 'Crear, modificar e inactivar cajas',                                            true),
    ('organization.warehouse.manage', 'organization', 'Crear, modificar e inactivar bodegas',                                          true),
    ('parties.party.manage',          'parties',      'Crear y modificar terceros y sus contactos',                                    false),
    ('parties.party.view',            'parties',      'Consultar terceros (proveedores y clientes)',                                   false),
    ('purchasing.order.approve',      'purchasing',   'Aprobar órdenes de compra',                                                     true),
    ('purchasing.order.manage',       'purchasing',   'Crear, enviar y cerrar órdenes de compra',                                      false),
    ('purchasing.payable.pay',        'purchasing',   'Registrar y anular pagos a proveedores',                                        true),
    ('purchasing.payable.view',       'purchasing',   'Consultar cuentas por pagar, pagos y cartera por edades',                       true),
    ('purchasing.purchase.manage',    'purchasing',   'Registrar compras en borrador (factura, líneas, lotes, cargos y retenciones)',  false),
    ('purchasing.purchase.post',      'purchasing',   'Contabilizar compras (entran al inventario y a la cartera)',                    true),
    ('purchasing.purchase.view',      'purchasing',   'Consultar proveedores, órdenes, compras y devoluciones',                        false),
    ('purchasing.purchase.void',      'purchasing',   'Anular compras contabilizadas',                                                 true),
    ('purchasing.return.manage',      'purchasing',   'Registrar, contabilizar y liquidar devoluciones a proveedor',                   true),
    ('purchasing.supplier.manage',    'purchasing',   'Crear y modificar proveedores y los productos que suministran',                 false),
    ('settings.setting.manage',       'settings',     'Modificar la configuración general',                                            true),
    ('settings.setting.view',         'settings',     'Consultar la configuración general',                                            false)
ON CONFLICT (code) DO UPDATE SET module = EXCLUDED.module, description = EXCLUDED.description,
    is_sensitive = EXCLUDED.is_sensitive, is_deprecated = false;

-- Los permisos que ya no están en el código quedan obsoletos (no se borran).
UPDATE identity.permissions SET is_deprecated = true
WHERE code NOT IN (
    'audit.log.verify', 'audit.log.view', 'catalog.import.run', 'catalog.master.manage', 'catalog.price.manage',
    'catalog.product.manage', 'catalog.product.view', 'catalog.tax.manage', 'identity.permission.view',
    'identity.role.manage', 'identity.session.revoke', 'identity.user.manage', 'identity.user.view',
    'inventory.adjustment.approve', 'inventory.adjustment.manage', 'inventory.cost.view', 'inventory.count.approve',
    'inventory.count.manage', 'inventory.count.register', 'inventory.stock.verify', 'inventory.stock.view',
    'inventory.transfer.manage', 'organization.branch.manage', 'organization.branch.view',
    'organization.company.manage', 'organization.company.view', 'organization.device.manage',
    'organization.terminal.manage', 'organization.warehouse.manage', 'settings.setting.manage',
    'settings.setting.view', 'cash.payment_method.manage', 'parties.party.manage', 'parties.party.view',
    'purchasing.order.approve', 'purchasing.order.manage', 'purchasing.payable.pay', 'purchasing.payable.view',
    'purchasing.purchase.manage', 'purchasing.purchase.post', 'purchasing.purchase.view', 'purchasing.purchase.void',
    'purchasing.return.manage', 'purchasing.supplier.manage');
