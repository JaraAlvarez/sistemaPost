-- =====================================================================================================
-- R__system__document_types · Catálogo de tipos de documento con numeración interna (repetible).
-- series_scope: TERMINAL = una serie por caja (la caja numera sin el servidor); BRANCH = una por sucursal.
-- Cada fase agrega aquí los tipos de sus documentos. Nunca se borran filas.
-- =====================================================================================================

INSERT INTO system.document_types (code, module, name, series_scope) VALUES
    ('SALE',                 'sales',     'Venta',                     'TERMINAL'),
    ('CUSTOMER_RETURN',      'sales',     'Devolución de cliente',     'TERMINAL'),
    ('CASH_SESSION',         'cash',      'Jornada de caja',           'TERMINAL'),
    ('PURCHASE',             'purchasing','Compra',                    'BRANCH'),
    ('SUPPLIER_RETURN',      'purchasing','Devolución a proveedor',    'BRANCH'),
    ('PURCHASE_ORDER',       'purchasing','Orden de compra',           'BRANCH'),
    ('PAYABLE_PAYMENT',      'purchasing','Pago a proveedor',          'BRANCH'),
    ('INVENTORY_ADJUSTMENT', 'inventory', 'Ajuste de inventario',      'BRANCH'),
    ('INVENTORY_TRANSFER',   'inventory', 'Traslado entre bodegas',    'BRANCH'),
    ('INVENTORY_COUNT',      'inventory', 'Conteo de inventario',      'BRANCH'),
    ('EXPENSE',              'expenses',  'Gasto',                     'BRANCH')
ON CONFLICT (code) DO UPDATE SET module = EXCLUDED.module, name = EXCLUDED.name,
    series_scope = EXCLUDED.series_scope;
