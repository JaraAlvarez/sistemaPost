-- =====================================================================================================
-- V2026.10.013 · inventory · Lotes y vencimientos (completa la estructura de la Fase 4) y reversión de movimientos.
-- Diseño: docs/fases/fase-05-propuesta.md §4.4 y §5.2 (D5-05, D5-06, D5-08).
--
--   · El lote lleva SOLO cantidades: la fila de stock_balances con lot_id es la cantidad del lote (valor 0); la fila
--     sin lote sigue siendo el saldo valorizado del producto en la bodega (Σ de TODOS sus movimientos, con o sin lote).
--   · Los movimientos de un producto con lotes llevan lot_id; una salida sin lote indicado se reparte FEFO.
--   · Cada sucursal registra sus propios lotes (número único por sucursal y producto): dos tiendas que reciben el
--     mismo lote del fabricante sin conexión no chocan al sincronizar.
--   · Un movimiento se revierte una sola vez (anulación de una compra = movimientos inversos, RN-INV-02).
-- =====================================================================================================

-- La gestión de lotes empieza en esta fase: la tabla está vacía en las instalaciones existentes.
ALTER TABLE inventory.inventory_lots ADD COLUMN branch_id uuid;
ALTER TABLE inventory.inventory_lots ALTER COLUMN branch_id SET NOT NULL;
ALTER TABLE inventory.inventory_lots ADD CONSTRAINT fk_inventory_lots__branch FOREIGN KEY (branch_id, company_id)
    REFERENCES org.branches (id, company_id);
ALTER TABLE inventory.inventory_lots DROP CONSTRAINT ux_inventory_lots__product_lot;
ALTER TABLE inventory.inventory_lots ADD CONSTRAINT ux_inventory_lots__branch_product_lot UNIQUE (branch_id, product_id, lot_number);
ALTER TABLE inventory.inventory_lots ADD CONSTRAINT ck_inventory_lots__lot_number CHECK (btrim(lot_number) <> '');
ALTER TABLE inventory.inventory_lots ADD CONSTRAINT ck_inventory_lots__dates
    CHECK (manufactured_date IS NULL OR expiry_date IS NULL OR expiry_date >= manufactured_date);

CREATE INDEX ix_inventory_lots__branch_id_company_id ON inventory.inventory_lots (branch_id, company_id);
CREATE INDEX ix_inventory_lots__expiry ON inventory.inventory_lots (branch_id, expiry_date) WHERE expiry_date IS NOT NULL;

-- Las filas por lote no llevan valor (el costo es promedio por bodega, D5-05).
ALTER TABLE inventory.stock_balances ADD CONSTRAINT ck_stock_balances__lot_quantity_only
    CHECK (lot_id IS NULL OR (total_value = 0 AND average_cost = 0));

-- Un movimiento se revierte a lo sumo una vez.
DROP INDEX inventory.ix_stock_movements__reverses_movement_id;
CREATE UNIQUE INDEX ux_stock_movements__reverses ON inventory.stock_movements (reverses_movement_id);

-- Ajustes por lote (entrada a un lote nuevo o existente, salida de un lote concreto).
ALTER TABLE inventory.inventory_adjustment_lines ADD COLUMN lot_number varchar(40);
ALTER TABLE inventory.inventory_adjustment_lines ADD COLUMN expiry_date date;
ALTER TABLE inventory.inventory_adjustment_lines ADD CONSTRAINT ck_inventory_adjustment_lines__lot
    CHECK ((lot_number IS NULL OR btrim(lot_number) <> '') AND (expiry_date IS NULL OR lot_number IS NOT NULL));
