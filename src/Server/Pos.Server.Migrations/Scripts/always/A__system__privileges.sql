-- =====================================================================================================
-- A__system__privileges · Privilegios de los roles de la aplicación. Se ejecuta al final de CADA migración
-- (así las tablas nuevas quedan cubiertas). Diseño: docs/fases/fase-02-propuesta.md §6 "Roles y privilegios".
--   pos_app    : DML en tablas de negocio; solo SELECT/INSERT en audit; solo SELECT en ref y catálogos.
--   pos_backup : lectura total (pg_read_all_data, otorgado al crear la BD).
-- =====================================================================================================

REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO pos_app, pos_backup;
GRANT USAGE ON SCHEMA system, ref, org, identity, audit, catalog, inventory, parties, cash, purchasing, expenses, promotions, sales, billing, customers,
    reporting, backup TO pos_app, pos_backup;

-- Por defecto nada; luego se otorga explícitamente.
REVOKE ALL ON ALL TABLES IN SCHEMA system, ref, org, identity, audit, catalog, inventory, parties, cash, purchasing, expenses, promotions, sales, billing, customers, backup
    FROM pos_app;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA system, ref, org, identity, audit, catalog, inventory, parties, cash, purchasing, expenses, promotions, sales, billing, customers, backup
    FROM pos_app;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA system, ref, org, identity, audit, catalog, inventory, parties, cash, purchasing, expenses, promotions, sales, billing, customers, backup
    FROM PUBLIC;

-- Negocio
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA org, identity, catalog, inventory, parties, cash, purchasing, expenses, promotions, sales, billing, customers, backup
    TO pos_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON
    system.document_series, system.settings, system.outbox_messages, system.inbox_messages,
    system.sync_cursors, system.idempotency_keys
    TO pos_app;
GRANT SELECT, INSERT, UPDATE ON system.installation TO pos_app;

-- Catálogos: solo lectura (se actualizan únicamente mediante migraciones)
GRANT SELECT ON ALL TABLES IN SCHEMA ref TO pos_app;
GRANT SELECT ON system.schema_migrations, system.document_types TO pos_app;
REVOKE INSERT, UPDATE, DELETE ON identity.permissions FROM pos_app;

-- Kardex: solo agregar y leer (RN-INV-02: un error se corrige con un movimiento inverso). Además tiene un disparador.
REVOKE UPDATE, DELETE, TRUNCATE ON inventory.stock_movements FROM pos_app;

-- Libro de cuentas por pagar: igual que el kardex (D5-09).
REVOKE UPDATE, DELETE, TRUNCATE ON purchasing.payable_entries FROM pos_app;

-- Movimientos de caja: cada peso trazable (D6-01).
REVOKE UPDATE, DELETE, TRUNCATE ON cash.cash_movements FROM pos_app;

-- Eventos de los documentos fiscales: solo agregar y leer (D7-12).
REVOKE UPDATE, DELETE, TRUNCATE ON billing.fiscal_document_events FROM pos_app;

-- Autorizaciones de tratamiento de datos: la prueba ante la SIC no se modifica (D8-06).
REVOKE UPDATE, DELETE, TRUNCATE ON customers.customer_consents FROM pos_app;

-- Backups (Fase 11): historiales de solo inserción.
REVOKE UPDATE, DELETE, TRUNCATE ON backup.backup_runs, backup.restore_tests FROM pos_app;
REVOKE DELETE, TRUNCATE ON backup.recovery_keys FROM pos_app;

-- Reportes (D9-01/D9-02): solo lectura de las vistas del esquema reporting.
REVOKE ALL ON ALL TABLES IN SCHEMA reporting FROM pos_app;
GRANT SELECT ON ALL TABLES IN SCHEMA reporting TO pos_app;

-- Auditoría: solo agregar y leer
GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA audit TO pos_app;
GRANT EXECUTE ON FUNCTION audit.ensure_partitions(integer) TO pos_app;
-- Catálogo de acciones: solo lectura (lo llena la migración, D10-01).
REVOKE INSERT ON audit.action_types FROM pos_app;

-- Secuencias de identidad (inserción y lectura de last_value por el sellador de auditoría)
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA system, org, identity, audit, catalog, inventory TO pos_app;

-- Las funciones de trigger se ejecutan con los privilegios del dueño de la tabla; no hace falta EXECUTE.
