-- =====================================================================================================
-- V2026.10.024 · sales · Precio por cliente, snapshot fiscal del comprador e historial (Fase 8, bloques 8.2 y 8.3).
-- Diseño: docs/fases/fase-08-propuesta.md §4.4, D8-05, D8-09, D8-12 y D8-13.
--
--   · La venta guarda la lista aplicada (id y código), el grupo del cliente, sus datos fiscales completos (jsonb) y si pidió
--     factura electrónica; la línea guarda la lista y el ORIGEN del precio ("¿por qué se cobró este precio?").
--   · Las ventas existentes quedan con la lista general y price_source según sus datos.
--   · Índice del historial por cliente.
-- =====================================================================================================

ALTER TABLE sales.sales
    ADD COLUMN price_list_id uuid,
    ADD COLUMN price_list_code varchar(20),
    ADD COLUMN customer_group_code varchar(20),
    ADD COLUMN list_allows_promotions boolean NOT NULL DEFAULT true,
    ADD COLUMN customer_fiscal jsonb,
    ADD COLUMN invoice_requested boolean NOT NULL DEFAULT false;

ALTER TABLE sales.sales ADD CONSTRAINT fk_sales__price_list FOREIGN KEY (price_list_id, company_id) REFERENCES catalog.price_lists (id, company_id);
ALTER TABLE sales.sales ADD CONSTRAINT ck_sales__invoice_requested CHECK (NOT invoice_requested OR customer_id IS NOT NULL);
ALTER TABLE sales.sales ADD CONSTRAINT ck_sales__price_list CHECK ((price_list_id IS NULL) = (price_list_code IS NULL));

CREATE INDEX ix_sales__price_list_id_company_id ON sales.sales (price_list_id, company_id);
CREATE INDEX ix_sales__customer_history ON sales.sales (company_id, customer_id, completed_at DESC)
    WHERE customer_id IS NOT NULL AND status IN ('COMPLETED', 'VOIDED');

ALTER TABLE sales.sale_lines
    ADD COLUMN price_list_id uuid,
    ADD COLUMN price_source varchar(15) NOT NULL DEFAULT 'DEFAULT';

UPDATE sales.sale_lines SET price_source = CASE
    WHEN source = 'SCALE_PRICE' THEN 'SCALE_LABEL'
    WHEN price_overridden THEN 'OPEN'
    ELSE 'DEFAULT' END;

ALTER TABLE sales.sale_lines ADD CONSTRAINT fk_sale_lines__price_list FOREIGN KEY (price_list_id) REFERENCES catalog.price_lists (id);
ALTER TABLE sales.sale_lines ADD CONSTRAINT ck_sale_lines__price_source
    CHECK (price_source IN ('LIST', 'DERIVED', 'DEFAULT', 'OPEN', 'OVERRIDE', 'SCALE_LABEL'));

CREATE INDEX ix_sale_lines__price_list_id ON sales.sale_lines (price_list_id);
