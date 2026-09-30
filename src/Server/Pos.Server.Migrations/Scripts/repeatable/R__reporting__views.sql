-- =====================================================================================================
-- R__reporting__views · Vistas del esquema reporting: el CONTRATO de los reportes (Fase 9, D9-02).
--
--   · Los reportes del módulo Reporting solo leen estas vistas, nunca las tablas de los módulos: si un módulo cambia sus tablas,
--     se ajusta la vista y los reportes siguen iguales. Una prueba verifica que todas compilan.
--   · Nombres ya resueltos (producto, categoría, marca, cajero, caja, cliente, proveedor, medio de pago).
--   · Script repetible: se recrea completo cuando cambia (DROP … CASCADE y CREATE).
--   · Ventas netas (D9-05): completadas − créditos de cambios − reintegros por garantía; las anuladas se muestran aparte.
-- =====================================================================================================

DROP VIEW IF EXISTS reporting.fiscal_documents CASCADE;
DROP VIEW IF EXISTS reporting.integrity_incidents CASCADE;
DROP VIEW IF EXISTS reporting.audit_verifications CASCADE;
DROP VIEW IF EXISTS reporting.audit_log CASCADE;
DROP VIEW IF EXISTS reporting.price_lists CASCADE;
DROP VIEW IF EXISTS reporting.taxes CASCADE;
DROP VIEW IF EXISTS reporting.antifraud_events CASCADE;
DROP VIEW IF EXISTS reporting.supplier_products CASCADE;
DROP VIEW IF EXISTS reporting.users CASCADE;
DROP VIEW IF EXISTS reporting.branches CASCADE;
DROP VIEW IF EXISTS reporting.data_requests CASCADE;
DROP VIEW IF EXISTS reporting.customer_consents CASCADE;
DROP VIEW IF EXISTS reporting.cash_movements CASCADE;
DROP VIEW IF EXISTS reporting.cash_session_totals CASCADE;
DROP VIEW IF EXISTS reporting.cash_sessions CASCADE;
DROP VIEW IF EXISTS reporting.expenses CASCADE;
DROP VIEW IF EXISTS reporting.payables CASCADE;
DROP VIEW IF EXISTS reporting.supplier_returns CASCADE;
DROP VIEW IF EXISTS reporting.purchase_line_taxes CASCADE;
DROP VIEW IF EXISTS reporting.purchase_lines CASCADE;
DROP VIEW IF EXISTS reporting.purchases CASCADE;
DROP VIEW IF EXISTS reporting.pending_orders CASCADE;
DROP VIEW IF EXISTS reporting.inventory_adjustments CASCADE;
DROP VIEW IF EXISTS reporting.lots CASCADE;
DROP VIEW IF EXISTS reporting.stock_policies CASCADE;
DROP VIEW IF EXISTS reporting.stock_movements CASCADE;
DROP VIEW IF EXISTS reporting.stock_balances CASCADE;
DROP VIEW IF EXISTS reporting.products CASCADE;
DROP VIEW IF EXISTS reporting.brands CASCADE;
DROP VIEW IF EXISTS reporting.categories CASCADE;
DROP VIEW IF EXISTS reporting.return_lines CASCADE;
DROP VIEW IF EXISTS reporting.returns CASCADE;
DROP VIEW IF EXISTS reporting.discounts CASCADE;
DROP VIEW IF EXISTS reporting.payments CASCADE;
DROP VIEW IF EXISTS reporting.sale_line_taxes CASCADE;
DROP VIEW IF EXISTS reporting.sale_lines CASCADE;
DROP VIEW IF EXISTS reporting.sales CASCADE;

-- -----------------------------------------------------------------------------------------------------
-- Apoyo: encabezado de los archivos (empresa y sucursal) y nombres de usuarios.
-- -----------------------------------------------------------------------------------------------------
CREATE VIEW reporting.branches AS
SELECT b.id AS branch_id, b.company_id, b.code AS branch_code, b.name AS branch_name, c.legal_name AS company_name, c.trade_name,
       c.identification_type, c.identification_number, c.check_digit
FROM org.branches b
JOIN org.companies c ON c.id = b.company_id;

CREATE VIEW reporting.users AS
SELECT u.id AS user_id, u.company_id, u.username, u.display_name
FROM identity.users u;

-- -----------------------------------------------------------------------------------------------------
-- Ventas (todas, con su estado; los reportes filtran COMPLETED y muestran VOIDED aparte).
-- -----------------------------------------------------------------------------------------------------
CREATE VIEW reporting.sales AS
SELECT s.id AS sale_id, s.company_id, s.branch_id, s.pos_terminal_id, t.code AS terminal_code, t.name AS terminal_name,
       s.warehouse_id, s.cash_session_id, s.cashier_id, u.display_name AS cashier_name, s.business_date, s.status, s.return_status,
       s.number, s.customer_id, s.customer_name, s.customer_identification, s.price_list_id, s.price_list_code, s.customer_group_code,
       s.opened_at, s.completed_at, (s.completed_at AT TIME ZONE 'America/Bogota') AS completed_local, s.cancelled_at, s.cancel_reason,
       s.cancelled_by, s.cancel_authorized_by, s.voided_at, s.void_reason, s.voided_by, s.void_authorized_by,
       s.exchange_id, s.exchange_credit, s.gross, s.promotion_total, s.discount_total, s.subtotal, s.tax_total, s.rounding_adjustment,
       s.total, s.paid_total, s.change_total
FROM sales.sales s
JOIN org.pos_terminals t ON t.id = s.pos_terminal_id
JOIN identity.users u ON u.id = s.cashier_id;

-- Líneas ACTIVAS e inactivas con producto, categoría y marca; la utilidad usa el costo del kardex guardado al cobrar (D9-06).
CREATE VIEW reporting.sale_lines AS
SELECT l.id AS sale_line_id, l.sale_id, s.company_id, s.branch_id, s.business_date, s.status AS sale_status, s.cashier_id,
       s.pos_terminal_id, s.customer_id, s.price_list_code, l.line_no, l.status AS line_status, l.product_id, l.sku, l.name AS product_name,
       l.category_id, c.name AS category_name, c.path AS category_path, l.brand_id, b.name AS brand_name, l.base_unit_code,
       l.quantity, l.base_quantity, l.unit_price, l.price_source, l.price_overridden, l.original_unit_price, l.price_authorized_by,
       l.gross, l.promotion_id, l.promotion_name, l.promotion_discount, l.line_discount, l.global_discount_share, l.tax_base,
       l.tax_total, l.total, l.unit_cost, COALESCE(l.cost_total, 0) AS cost_total, l.lot_id, l.expired_authorized_by,
       l.voided_by, l.voided_at, l.returned_quantity
FROM sales.sale_lines l
JOIN sales.sales s ON s.id = l.sale_id
JOIN catalog.categories c ON c.id = l.category_id
LEFT JOIN catalog.brands b ON b.id = l.brand_id;

CREATE VIEW reporting.sale_line_taxes AS
SELECT x.sale_line_id, l.sale_id, s.company_id, s.branch_id, s.business_date, s.pos_terminal_id, s.status AS sale_status,
       l.status AS line_status, x.tax_id, x.code AS tax_code, x.kind AS tax_kind, x.rate, x.fixed_amount, x.tax_base, x.amount,
       tx.name AS tax_name, tx.is_exempt, tx.is_excluded
FROM sales.sale_line_taxes x
JOIN sales.sale_lines l ON l.id = x.sale_line_id
JOIN sales.sales s ON s.id = l.sale_id
JOIN catalog.taxes tx ON tx.id = x.tax_id;

CREATE VIEW reporting.payments AS
SELECT p.sale_id, s.company_id, s.branch_id, s.business_date, s.status AS sale_status, s.cashier_id, s.pos_terminal_id,
       p.payment_method_id, p.method_code, pm.name AS method_name, p.method_kind, p.affects_cash_drawer, p.tendered, p.applied, p.change
FROM sales.sale_payments p
JOIN sales.sales s ON s.id = p.sale_id
JOIN cash.payment_methods pm ON pm.id = p.payment_method_id;

-- Descuentos manuales con quién los aplicó y quién autorizó.
CREATE VIEW reporting.discounts AS
SELECT d.id AS discount_id, d.sale_id, s.company_id, s.branch_id, s.business_date, s.status AS sale_status, s.number AS sale_number,
       s.cashier_id, d.scope, d.sale_line_id, d.percent, d.amount, d.reason, d.applied_by, ua.display_name AS applied_by_name,
       d.authorized_by, uz.display_name AS authorized_by_name, d.applied_at, d.status
FROM sales.sale_discounts d
JOIN sales.sales s ON s.id = d.sale_id
JOIN identity.users ua ON ua.id = d.applied_by
LEFT JOIN identity.users uz ON uz.id = d.authorized_by;

-- Cambios y reintegros por garantía (restan de las ventas netas cuando están COMPLETED).
CREATE VIEW reporting.returns AS
SELECT r.id AS return_id, r.company_id, r.branch_id, r.pos_terminal_id, r.cash_session_id, r.original_sale_id, r.original_sale_number,
       r.kind, r.status, r.number, r.business_date, r.reason, r.credit_total, r.replacement_sale_id, r.refund_payment_method_id,
       r.received_by, r.authorized_by, r.received_at, r.completed_at, s.cashier_id AS original_cashier_id
FROM sales.customer_returns r
JOIN sales.sales s ON s.id = r.original_sale_id;

CREATE VIEW reporting.return_lines AS
SELECT rl.customer_return_id AS return_id, r.company_id, r.branch_id, r.business_date, r.status, r.kind, rl.sale_line_id,
       rl.product_id, rl.sku, rl.name AS product_name, l.category_id, l.brand_id, rl.quantity, rl.base_quantity, rl.unit_cost,
       rl.base_quantity * rl.unit_cost AS cost_total, rl.credit_amount, l.tax_base * rl.quantity / l.quantity AS tax_base_share,
       rl.destination
FROM sales.customer_return_lines rl
JOIN sales.customer_returns r ON r.id = rl.customer_return_id
JOIN sales.sale_lines l ON l.id = rl.sale_line_id;

-- Última decisión de cada titular por finalidad (RN-REP-05: el detalle por cliente solo con autorización SERVICE vigente).
CREATE VIEW reporting.customer_consents AS
SELECT DISTINCT ON (c.company_id, c.party_id, c.purpose) c.company_id, c.party_id, c.purpose, c.granted, c.occurred_at
FROM customers.customer_consents c
ORDER BY c.company_id, c.party_id, c.purpose, c.occurred_at DESC;

CREATE VIEW reporting.data_requests AS
SELECT r.id AS request_id, r.company_id, r.type, r.status, r.received_on, r.due_on
FROM customers.data_requests r;

-- -----------------------------------------------------------------------------------------------------
-- Inventario
-- -----------------------------------------------------------------------------------------------------
CREATE VIEW reporting.categories AS
SELECT c.id AS category_id, c.company_id, c.parent_id, c.name AS category_name, c.path AS category_path, c.level
FROM catalog.categories c;

CREATE VIEW reporting.brands AS
SELECT b.id AS brand_id, b.company_id, b.name AS brand_name
FROM catalog.brands b;

CREATE VIEW reporting.taxes AS
SELECT t.id AS tax_id, t.company_id, t.code AS tax_code, t.name AS tax_name, t.kind AS tax_kind
FROM catalog.taxes t;

CREATE VIEW reporting.price_lists AS
SELECT pl.id AS price_list_id, pl.company_id, pl.code AS price_list_code, pl.name AS price_list_name
FROM catalog.price_lists pl;

CREATE VIEW reporting.products AS
SELECT p.id AS product_id, p.company_id, p.sku, p.name AS product_name, p.category_id, c.name AS category_name, c.path AS category_path,
       p.brand_id, b.name AS brand_name, p.base_unit_code, p.product_type, p.tracks_expiry, p.status, (p.deleted_at IS NOT NULL) AS is_deleted
FROM catalog.products p
JOIN catalog.categories c ON c.id = p.category_id
LEFT JOIN catalog.brands b ON b.id = p.brand_id;

CREATE VIEW reporting.stock_balances AS
SELECT sb.company_id, sb.branch_id, sb.warehouse_id, w.code AS warehouse_code, w.name AS warehouse_name, sb.product_id, sb.lot_id,
       sb.quantity, sb.total_value, sb.average_cost, sb.last_movement_at
FROM inventory.stock_balances sb
JOIN org.warehouses w ON w.id = sb.warehouse_id;

-- Kardex: cada movimiento guarda el saldo resultante (cantidad, valor y costo promedio) del producto en la bodega (D9-07).
CREATE VIEW reporting.stock_movements AS
SELECT m.seq, m.company_id, m.branch_id, m.warehouse_id, w.code AS warehouse_code, w.name AS warehouse_name, m.product_id, m.lot_id,
       m.movement_type, m.direction, m.quantity, m.unit_cost, m.total_cost, m.balance_quantity, m.balance_value, m.balance_avg_cost,
       m.source_type, m.source_id, m.source_number, m.reason_id, m.business_date, m.occurred_at, m.user_id
FROM inventory.stock_movements m
JOIN org.warehouses w ON w.id = m.warehouse_id;

CREATE VIEW reporting.stock_policies AS
SELECT sp.company_id, sp.branch_id, sp.warehouse_id, sp.product_id, sp.min_qty, sp.max_qty, sp.reorder_point, sp.reorder_qty
FROM inventory.stock_policies sp;

CREATE VIEW reporting.lots AS
SELECT l.id AS lot_id, l.company_id, l.product_id, l.lot_number, l.expiry_date, l.status
FROM inventory.inventory_lots l;

-- Ajustes contabilizados por motivo; los rápidos de la caja tienen el motivo QUICK_ADJUSTMENT.
CREATE VIEW reporting.inventory_adjustments AS
SELECT a.id AS adjustment_id, a.company_id, a.branch_id, a.warehouse_id, a.number, a.business_date, a.status, a.reason_id,
       r.code AS reason_code, r.name AS reason_name, r.kind AS reason_kind, (r.code = 'QUICK_ADJUSTMENT') AS is_quick,
       COALESCE(a.total_value, 0) AS total_value, a.created_by, uc.display_name AS created_by_name, a.approved_by, a.posted_at,
       (SELECT count(*) FROM inventory.inventory_adjustment_lines al WHERE al.adjustment_id = a.id) AS line_count
FROM inventory.inventory_adjustments a
JOIN inventory.adjustment_reasons r ON r.id = a.reason_id
JOIN identity.users uc ON uc.id = a.created_by;

-- Pendiente por recibir de órdenes de compra vigentes (para el sugerido de compra).
CREATE VIEW reporting.pending_orders AS
SELECT o.company_id, o.branch_id, o.warehouse_id, ol.product_id, sum(ol.base_quantity - ol.received_base_quantity) AS pending_quantity
FROM purchasing.purchase_orders o
JOIN purchasing.purchase_order_lines ol ON ol.order_id = o.id
WHERE o.status IN ('APPROVED', 'SENT', 'PARTIALLY_RECEIVED') AND ol.base_quantity > ol.received_base_quantity
GROUP BY o.company_id, o.branch_id, o.warehouse_id, ol.product_id;

-- -----------------------------------------------------------------------------------------------------
-- Compras, cuentas por pagar y gastos
-- -----------------------------------------------------------------------------------------------------
CREATE VIEW reporting.supplier_products AS
SELECT spr.id AS supplier_product_id, spr.company_id, spr.supplier_id, sp.code AS supplier_code,
       COALESCE(NULLIF(pa.trade_name, ''), pa.legal_name, btrim(concat_ws(' ', pa.first_names, pa.last_names))) AS supplier_name,
       spr.product_id, spr.last_cost, spr.lead_time_days, spr.is_preferred
FROM purchasing.supplier_products spr
JOIN purchasing.suppliers sp ON sp.id = spr.supplier_id
JOIN parties.parties pa ON pa.id = sp.party_id
WHERE spr.deleted_at IS NULL;

CREATE VIEW reporting.purchases AS
SELECT pu.id AS purchase_id, pu.company_id, pu.branch_id, pu.warehouse_id, pu.supplier_id, sp.code AS supplier_code,
       COALESCE(NULLIF(pa.trade_name, ''), pa.legal_name, btrim(concat_ws(' ', pa.first_names, pa.last_names))) AS supplier_name,
       pa.identification_number AS supplier_identification, pu.number, pu.supplier_invoice_number, pu.invoice_date, pu.business_date,
       pu.due_date, pu.subtotal, pu.discount_total, pu.tax_total, pu.deductible_tax_total, pu.withholding_total, pu.total, pu.status,
       pu.posted_at
FROM purchasing.purchases pu
JOIN purchasing.suppliers sp ON sp.id = pu.supplier_id
JOIN parties.parties pa ON pa.id = sp.party_id;

CREATE VIEW reporting.purchase_lines AS
SELECT pl.id AS purchase_line_id, pl.purchase_id, pu.company_id, pu.branch_id, pu.warehouse_id, pu.supplier_id, pu.business_date,
       pu.status, pl.product_id, pl.quantity, pl.base_quantity, pl.unit_cost, pl.gross_amount, pl.discount_amount, pl.charges_amount,
       pl.tax_amount, pl.non_deductible_tax, pl.line_total, pl.net_unit_cost
FROM purchasing.purchase_lines pl
JOIN purchasing.purchases pu ON pu.id = pl.purchase_id;

CREATE VIEW reporting.purchase_line_taxes AS
SELECT t.purchase_line_id, pl.purchase_id, pu.company_id, pu.branch_id, pu.business_date, pu.status, t.tax_id, t.tax_code, t.is_vat,
       t.rate, t.base, t.amount, t.is_deductible
FROM purchasing.purchase_line_taxes t
JOIN purchasing.purchase_lines pl ON pl.id = t.purchase_line_id
JOIN purchasing.purchases pu ON pu.id = pl.purchase_id;

CREATE VIEW reporting.supplier_returns AS
SELECT r.id AS supplier_return_id, r.company_id, r.branch_id, r.supplier_id, sp.code AS supplier_code,
       COALESCE(NULLIF(pa.trade_name, ''), pa.legal_name, btrim(concat_ws(' ', pa.first_names, pa.last_names))) AS supplier_name,
       r.number, r.business_date, r.reason, r.status, r.total, r.credit_total, r.settlement
FROM purchasing.supplier_returns r
JOIN purchasing.suppliers sp ON sp.id = r.supplier_id
JOIN parties.parties pa ON pa.id = sp.party_id;

CREATE VIEW reporting.payables AS
SELECT ap.id AS payable_id, ap.company_id, ap.branch_id, ap.supplier_id, sp.code AS supplier_code,
       COALESCE(NULLIF(pa.trade_name, ''), pa.legal_name, btrim(concat_ws(' ', pa.first_names, pa.last_names))) AS supplier_name,
       ap.purchase_id, ap.document_number, ap.issue_date, ap.due_date, ap.original_amount, ap.balance, ap.status
FROM purchasing.accounts_payable ap
JOIN purchasing.suppliers sp ON sp.id = ap.supplier_id
JOIN parties.parties pa ON pa.id = sp.party_id;

CREATE VIEW reporting.expenses AS
SELECT e.id AS expense_id, e.company_id, e.branch_id, e.number, e.business_date, e.category_id, c.name AS category_name,
       c.parent_id AS category_parent_id, pc.name AS category_parent_name, e.description, e.amount, e.tax_amount, e.payment_method_id,
       pm.name AS method_name, e.cash_session_id, e.status
FROM expenses.expenses e
JOIN expenses.expense_categories c ON c.id = e.category_id
LEFT JOIN expenses.expense_categories pc ON pc.id = c.parent_id
JOIN cash.payment_methods pm ON pm.id = e.payment_method_id;

-- -----------------------------------------------------------------------------------------------------
-- Caja
-- -----------------------------------------------------------------------------------------------------
CREATE VIEW reporting.cash_sessions AS
SELECT cs.id AS session_id, cs.company_id, cs.branch_id, cs.pos_terminal_id, t.code AS terminal_code, t.name AS terminal_name,
       cs.cashier_id, u.display_name AS cashier_name, cs.number, cs.business_date, cs.status, cs.opened_at, cs.closed_at,
       cs.closed_by_supervisor, cs.expected_total, cs.counted_total, cs.difference, cs.review_required, cs.reviewed_at, cs.reviewed_by
FROM cash.cash_sessions cs
JOIN org.pos_terminals t ON t.id = cs.pos_terminal_id
JOIN identity.users u ON u.id = cs.cashier_id;

CREATE VIEW reporting.cash_session_totals AS
SELECT st.session_id, cs.company_id, cs.branch_id, cs.business_date, cs.status, st.payment_method_id, pm.code AS method_code,
       pm.name AS method_name, st.expected, st.counted, st.difference, st.transactions
FROM cash.cash_session_totals st
JOIN cash.cash_sessions cs ON cs.id = st.session_id
JOIN cash.payment_methods pm ON pm.id = st.payment_method_id;

CREATE VIEW reporting.cash_movements AS
SELECT m.id AS movement_id, m.company_id, cs.branch_id, m.session_id, cs.number AS session_number, cs.business_date, cs.pos_terminal_id,
       t.code AS terminal_code, cs.cashier_id, m.movement_type, m.payment_method_id, pm.name AS method_name, m.direction, m.amount,
       m.reason, m.authorized_by, ua.display_name AS authorized_by_name, m.user_id, uu.display_name AS user_name, m.occurred_at,
       m.source_type, m.source_id, m.source_number
FROM cash.cash_movements m
JOIN cash.cash_sessions cs ON cs.id = m.session_id
JOIN org.pos_terminals t ON t.id = cs.pos_terminal_id
JOIN cash.payment_methods pm ON pm.id = m.payment_method_id
JOIN identity.users uu ON uu.id = m.user_id
LEFT JOIN identity.users ua ON ua.id = m.authorized_by;

-- -----------------------------------------------------------------------------------------------------
-- Antifraude (D9-11): unión de los eventos sensibles de ventas, caja e inventario, atribuidos al cajero de la operación.
--   LINE_VOID · SALE_CANCEL · SALE_VOID · MANUAL_DISCOUNT · PRICE_OVERRIDE · NO_SALE_DRAWER · EXPIRED_LOT_SALE · QUICK_ADJUSTMENT ·
--   CASH_DIFFERENCE (valor absoluto de la diferencia del cierre).
-- -----------------------------------------------------------------------------------------------------
CREATE VIEW reporting.antifraud_events AS
SELECT 'LINE_VOID'::varchar(20) AS event_type, s.company_id, s.branch_id, s.business_date, s.cashier_id, s.pos_terminal_id,
       l.voided_at AS occurred_at, s.id AS sale_id, s.number AS sale_number, l.id AS source_id, l.name AS detail, l.gross AS amount,
       NULL::uuid AS authorized_by
FROM sales.sale_lines l JOIN sales.sales s ON s.id = l.sale_id
WHERE l.status = 'VOIDED'
UNION ALL
SELECT 'SALE_CANCEL', s.company_id, s.branch_id, s.business_date, s.cashier_id, s.pos_terminal_id, s.cancelled_at, s.id, s.number, s.id,
       s.cancel_reason, s.total, s.cancel_authorized_by
FROM sales.sales s WHERE s.status = 'CANCELLED'
UNION ALL
SELECT 'SALE_VOID', s.company_id, s.branch_id, s.business_date, s.cashier_id, s.pos_terminal_id, s.voided_at, s.id, s.number, s.id,
       s.void_reason, s.total, s.void_authorized_by
FROM sales.sales s WHERE s.status = 'VOIDED'
UNION ALL
SELECT 'MANUAL_DISCOUNT', s.company_id, s.branch_id, s.business_date, s.cashier_id, s.pos_terminal_id, d.applied_at, s.id, s.number, d.id,
       d.reason,
       CASE WHEN d.scope = 'LINE' THEN (SELECT l.line_discount FROM sales.sale_lines l WHERE l.id = d.sale_line_id)
            ELSE s.discount_total - COALESCE((SELECT sum(l.line_discount) FROM sales.sale_lines l WHERE l.sale_id = s.id AND l.status = 'ACTIVE'), 0) END,
       d.authorized_by
FROM sales.sale_discounts d JOIN sales.sales s ON s.id = d.sale_id
WHERE d.status = 'ACTIVE' AND s.status IN ('COMPLETED', 'VOIDED')
UNION ALL
SELECT 'PRICE_OVERRIDE', s.company_id, s.branch_id, s.business_date, s.cashier_id, s.pos_terminal_id, s.completed_at, s.id, s.number, l.id,
       l.name, GREATEST(l.original_unit_price - l.unit_price, 0) * l.quantity, l.price_authorized_by
FROM sales.sale_lines l JOIN sales.sales s ON s.id = l.sale_id
WHERE l.price_overridden AND l.status = 'ACTIVE' AND s.status IN ('COMPLETED', 'VOIDED')
UNION ALL
SELECT 'EXPIRED_LOT_SALE', s.company_id, s.branch_id, s.business_date, s.cashier_id, s.pos_terminal_id, s.completed_at, s.id, s.number, l.id,
       l.name, l.total, l.expired_authorized_by
FROM sales.sale_lines l JOIN sales.sales s ON s.id = l.sale_id
WHERE l.expired_authorized_by IS NOT NULL AND l.status = 'ACTIVE' AND s.status IN ('COMPLETED', 'VOIDED')
UNION ALL
SELECT 'NO_SALE_DRAWER', m.company_id, cs.branch_id, cs.business_date, cs.cashier_id, cs.pos_terminal_id, m.occurred_at, NULL, NULL, m.id,
       m.reason, 0::numeric, m.authorized_by
FROM cash.cash_movements m JOIN cash.cash_sessions cs ON cs.id = m.session_id
WHERE m.movement_type = 'NO_SALE_DRAWER_OPEN'
UNION ALL
SELECT 'QUICK_ADJUSTMENT', a.company_id, a.branch_id, a.business_date, a.created_by, NULL, a.posted_at, NULL, a.number, a.id,
       a.notes, abs(COALESCE(a.total_value, 0)), a.approved_by
FROM inventory.inventory_adjustments a JOIN inventory.adjustment_reasons r ON r.id = a.reason_id
WHERE r.code = 'QUICK_ADJUSTMENT' AND a.status = 'POSTED'
UNION ALL
SELECT 'CASH_DIFFERENCE', cs.company_id, cs.branch_id, cs.business_date, cs.cashier_id, cs.pos_terminal_id, cs.closed_at, NULL, cs.number,
       cs.id, NULL, abs(cs.difference), cs.reviewed_by
FROM cash.cash_sessions cs
WHERE cs.status = 'CLOSED' AND COALESCE(cs.difference, 0) <> 0;

-- -----------------------------------------------------------------------------------------------------
-- Auditoría (Fase 10, D10-06): bitácora con el nombre de la acción, verificaciones e incidentes de integridad.
-- La bitácora es por nodo: se filtra por empresa (las filas del sistema no tienen empresa) y por fecha local.
-- -----------------------------------------------------------------------------------------------------
CREATE VIEW reporting.audit_log AS
SELECT l.id AS audit_id, l.occurred_at, (l.occurred_at AT TIME ZONE 'America/Bogota') AS occurred_local, l.node_id, l.seq, l.company_id,
       l.branch_id, l.pos_terminal_id, l.user_id, l.user_display_name, l.module, l.action, COALESCE(t.name, l.action) AS action_name,
       l.entity_type, l.entity_id, l.entity_label, l.old_values, l.new_values, l.summary, l.authorized_by, ua.display_name AS authorized_by_name,
       l.severity, host(l.ip_address) AS ip_address
FROM audit.audit_log l
LEFT JOIN audit.action_types t ON t.code = l.action
LEFT JOIN identity.users ua ON ua.id = l.authorized_by;

CREATE VIEW reporting.audit_verifications AS
SELECT r.id AS run_id, r.node_id, r.company_id, r.kind, r.started_at, (r.started_at AT TIME ZONE 'America/Bogota') AS started_local, r.finished_at,
       r.from_seal_no, r.last_seal_no, r.last_seal_code, r.seals_checked, r.rows_checked, r.unsealed_rows, r.is_valid, r.findings_count,
       i.id AS incident_id, i.summary AS incident_summary, a.acknowledged_at, u.display_name AS acknowledged_by_name, a.note AS acknowledgement_note
FROM audit.verification_runs r
LEFT JOIN audit.integrity_incidents i ON i.verification_run_id = r.id
LEFT JOIN audit.integrity_incident_acknowledgements a ON a.incident_id = i.id
LEFT JOIN identity.users u ON u.id = a.acknowledged_by;

CREATE VIEW reporting.integrity_incidents AS
SELECT i.id AS incident_id, i.node_id, i.company_id, i.detected_at, i.findings_count, i.summary, (a.id IS NULL) AS is_open
FROM audit.integrity_incidents i
LEFT JOIN audit.integrity_incident_acknowledgements a ON a.incident_id = i.id;

-- -----------------------------------------------------------------------------------------------------
-- Facturación electrónica (Fase 11-B, §6 flujo 6): documentos fiscales por origen para la conciliación ventas vs. documentos.
-- -----------------------------------------------------------------------------------------------------
CREATE VIEW reporting.fiscal_documents AS
SELECT f.id AS fiscal_document_id, f.company_id, f.branch_id, f.pos_terminal_id, f.source, f.source_id, f.source_number, f.document_type,
       f.status, f.provider, f.fiscal_number, f.attempts, f.business_date, f.subtotal, f.tax_total, f.total, f.issued_at, f.validated_at,
       f.related_document_id
FROM billing.fiscal_documents f;
