-- =====================================================================================================
-- V2026.10.001 · system · Línea base de la BD de la NUBE (Fase 12-A): esquemas y nodo de la nube.
-- Es una BD independiente de la del POS (L-01). Se ejecuta como pos_owner (SET ROLE desde el migrador).
-- El esquema system y la tabla system.schema_migrations los crea el propio migrador antes de este script.
-- =====================================================================================================

-- Orden alfabético en español: se usa de forma EXPLÍCITA (COLLATE public.es_co) solo donde se muestra al usuario.
CREATE COLLATION IF NOT EXISTS public.es_co (provider = icu, locale = 'es-CO');

CREATE SCHEMA IF NOT EXISTS audit;
CREATE SCHEMA IF NOT EXISTS portal;
CREATE SCHEMA IF NOT EXISTS licensing;

-- -----------------------------------------------------------------------------------------------------
-- Nodo de la nube: identifica su cadena de auditoría (una sola fila, generada una vez e inmutable).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE system.cloud_node (
    id          boolean      NOT NULL DEFAULT true,
    node_id     uuid         NOT NULL,
    created_at  timestamptz  NOT NULL,
    CONSTRAINT pk_cloud_node PRIMARY KEY (id),
    CONSTRAINT ck_cloud_node__single_row CHECK (id)
);

INSERT INTO system.cloud_node (node_id, created_at) VALUES (uuidv7(), now());

CREATE FUNCTION system.fn_cloud_node_protect() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'system.cloud_node es inmutable' USING ERRCODE = 'P0001';
END;
$$;

CREATE TRIGGER trg_cloud_node_protect
    BEFORE UPDATE OR DELETE ON system.cloud_node
    FOR EACH ROW EXECUTE FUNCTION system.fn_cloud_node_protect();
