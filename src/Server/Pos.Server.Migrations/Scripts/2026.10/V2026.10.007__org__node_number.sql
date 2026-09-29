-- =====================================================================================================
-- V2026.10.007 · org · Número corto del nodo dentro de la empresa (1–999).
-- Lo asigna la autoridad de la empresa: el asistente inicial al primer nodo (1) y, con la sincronización, la nube a
-- las tiendas que se unen. Forma parte de los códigos internos de producto (SKU y EAN-13 con prefijo 29) para que
-- dos tiendas sin conexión nunca generen el mismo código (docs/fases/fase-04-propuesta.md, D4-07).
-- =====================================================================================================

ALTER TABLE org.nodes ADD COLUMN number smallint;

UPDATE org.nodes n
SET number = r.rn
FROM (SELECT id, row_number() OVER (PARTITION BY company_id ORDER BY registered_at, id) AS rn FROM org.nodes) r
WHERE n.id = r.id;

ALTER TABLE org.nodes ALTER COLUMN number SET NOT NULL;
ALTER TABLE org.nodes ADD CONSTRAINT ck_nodes__number CHECK (number BETWEEN 1 AND 999);
CREATE UNIQUE INDEX ux_nodes__company_number ON org.nodes (company_id, number);
