-- =====================================================================================================
-- A__system__privileges · Privilegios de la BD de la NUBE. Se ejecuta al final de CADA migración.
--   pos_app    : SELECT/INSERT/UPDATE en portal, licensing y sync (nada se borra: ningún DELETE, L-09; única excepción sync.documents,
--                copia de los datos de las tiendas que se borra al descartar un paquete no verificado);
--                solo SELECT/INSERT en los eventos de suscripción, los check-ins y la auditoría.
--   pos_backup : lectura total (pg_read_all_data, otorgado al crear la BD).
-- =====================================================================================================

REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO pos_app, pos_backup;
GRANT USAGE ON SCHEMA system, audit, portal, licensing, sync TO pos_app, pos_backup;

-- Por defecto nada; luego se otorga explícitamente.
REVOKE ALL ON ALL TABLES IN SCHEMA system, audit, portal, licensing, sync FROM pos_app;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA system, audit, portal, licensing, sync FROM pos_app;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA system, audit, portal, licensing, sync FROM PUBLIC;

GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA portal, licensing, sync TO pos_app;
GRANT SELECT ON system.schema_migrations, system.cloud_node TO pos_app;

-- Historial de suscripciones y check-ins: solo agregar y leer (además tienen disparadores).
REVOKE UPDATE, DELETE, TRUNCATE ON licensing.subscription_events, licensing.checkins FROM pos_app;

-- Pago en línea (Fase 12-C): el historial de precios y los eventos de la pasarela solo se agregan y se leen (además tienen
-- disparadores); el consecutivo de las referencias BP-<año>-<n> se toma con nextval.
REVOKE UPDATE, DELETE, TRUNCATE ON licensing.prices, licensing.payment_events FROM pos_app;
GRANT USAGE ON SEQUENCE licensing.payment_order_number_seq TO pos_app;

-- Sincronización (revisión S2): descartar un paquete no verificado borra los documentos que dejó. Es el único DELETE: sync.documents
-- es una copia de lo que mandan las tiendas, no un registro de la nube.
GRANT DELETE ON sync.documents TO pos_app;

-- Auditoría: solo agregar y leer.
GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA audit TO pos_app;
GRANT EXECUTE ON FUNCTION audit.ensure_partitions(integer) TO pos_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA audit TO pos_app;
