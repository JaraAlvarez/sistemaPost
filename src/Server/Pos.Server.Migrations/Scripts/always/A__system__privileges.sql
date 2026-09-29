-- =====================================================================================================
-- A__system__privileges · Privilegios de los roles de la aplicación. Se ejecuta al final de CADA migración
-- (así las tablas nuevas quedan cubiertas). Diseño: docs/fases/fase-02-propuesta.md §6 "Roles y privilegios".
--   pos_app    : DML en tablas de negocio; solo SELECT/INSERT en audit; solo SELECT en ref y catálogos.
--   pos_backup : lectura total (pg_read_all_data, otorgado al crear la BD).
-- =====================================================================================================

REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO pos_app, pos_backup;
GRANT USAGE ON SCHEMA system, ref, org, identity, audit TO pos_app, pos_backup;

-- Por defecto nada; luego se otorga explícitamente.
REVOKE ALL ON ALL TABLES IN SCHEMA system, ref, org, identity, audit FROM pos_app;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA system, ref, org, identity, audit FROM pos_app;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA system, ref, org, identity, audit FROM PUBLIC;

-- Negocio
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA org, identity TO pos_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON
    system.document_series, system.settings, system.outbox_messages, system.inbox_messages,
    system.sync_cursors, system.idempotency_keys
    TO pos_app;
GRANT SELECT, INSERT, UPDATE ON system.installation TO pos_app;

-- Catálogos: solo lectura (se actualizan únicamente mediante migraciones)
GRANT SELECT ON ALL TABLES IN SCHEMA ref TO pos_app;
GRANT SELECT ON system.schema_migrations, system.document_types TO pos_app;
REVOKE INSERT, UPDATE, DELETE ON identity.permissions FROM pos_app;

-- Auditoría: solo agregar y leer
GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA audit TO pos_app;
GRANT EXECUTE ON FUNCTION audit.ensure_partitions(integer) TO pos_app;

-- Secuencias de identidad (inserción y lectura de last_value por el sellador de auditoría)
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA system, org, identity, audit TO pos_app;

-- Las funciones de trigger se ejecutan con los privilegios del dueño de la tabla; no hace falta EXECUTE.
