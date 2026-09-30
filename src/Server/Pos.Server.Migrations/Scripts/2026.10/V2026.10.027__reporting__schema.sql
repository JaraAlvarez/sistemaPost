-- =====================================================================================================
-- V2026.10.027 · reporting · Esquema de reportes e índices de consulta (Fase 9).
-- Diseño: docs/fases/fase-09-propuesta.md §4, D9-01, D9-02 y D9-08.
--
--   · El esquema reporting solo tiene VISTAS (R__reporting__views.sql): son el contrato de los reportes sobre las tablas de los
--     módulos. pos_app solo puede leerlas.
--   · Los reportes se ejecutan en transacciones de SOLO LECTURA con tiempo límite (lo fija el módulo Reporting).
--   · Índices para las consultas por fecha de negocio más frecuentes.
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS reporting;

CREATE INDEX IF NOT EXISTS ix_sales__branch_status_date ON sales.sales (branch_id, status, business_date);
CREATE INDEX IF NOT EXISTS ix_sale_lines__sale_id_status ON sales.sale_lines (sale_id, status);
CREATE INDEX IF NOT EXISTS ix_sale_payments__sale_id ON sales.sale_payments (sale_id);
CREATE INDEX IF NOT EXISTS ix_customer_returns__branch_date ON sales.customer_returns (branch_id, business_date) WHERE status = 'COMPLETED';
CREATE INDEX IF NOT EXISTS ix_stock_movements__branch_date ON inventory.stock_movements (branch_id, business_date);
CREATE INDEX IF NOT EXISTS ix_stock_movements__product_warehouse_seq ON inventory.stock_movements (product_id, warehouse_id, seq);
CREATE INDEX IF NOT EXISTS ix_cash_movements__type ON cash.cash_movements (movement_type, occurred_at);
CREATE INDEX IF NOT EXISTS ix_purchases__branch_status_date ON purchasing.purchases (branch_id, status, business_date);
CREATE INDEX IF NOT EXISTS ix_expenses__branch_date ON expenses.expenses (branch_id, business_date);
