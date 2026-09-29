-- =====================================================================================================
-- V2026.10.016 · promotions · Promociones automáticas (Fase 7, bloque 7.4).
-- Diseño: docs/fases/fase-07-propuesta.md §4.2 y D7-16.
--
-- Principios:
--   · Las administra el encargado de promociones, nunca la caja; se aplican solas al escanear.
--   · Una promoción activa no se edita (RN-PRM-04): se pausa o termina y se crea otra (historial exacto de lo cobrado).
--   · Rige en su vigencia, en los días de la semana (máscara: lunes = 1 … domingo = 64) y en el horario (hora de Colombia),
--     en todas las sucursales o en las elegidas.
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS promotions;

CREATE TABLE promotions.promotions (
    id                uuid           NOT NULL,
    company_id        uuid           NOT NULL,
    number            varchar(40)    NOT NULL,
    name              varchar(80)    NOT NULL,
    type              varchar(15)    NOT NULL,
    status            varchar(10)    NOT NULL,
    valid_from        timestamptz    NOT NULL,
    valid_to          timestamptz,
    days              integer        NOT NULL,
    start_time        time,
    end_time          time,
    all_branches      boolean        NOT NULL,
    max_applications  integer,
    ticket_text       varchar(40),
    buy_quantity      integer,
    pay_quantity      integer,
    price             numeric(19,2),
    percent           numeric(5,2),
    min_quantity      numeric(19,4),
    activated_at      timestamptz,
    activated_by      uuid,
    ended_at          timestamptz,
    row_version       bigint         NOT NULL DEFAULT 1,
    created_at        timestamptz    NOT NULL,
    created_by        uuid           NOT NULL,
    updated_at        timestamptz,
    updated_by        uuid,
    CONSTRAINT pk_promotions PRIMARY KEY (id),
    CONSTRAINT ux_promotions__company_number UNIQUE (company_id, number),
    CONSTRAINT fk_promotions__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_promotions__activated_by FOREIGN KEY (activated_by) REFERENCES identity.users (id),
    CONSTRAINT ck_promotions__type CHECK (type IN ('MULTI_BUY', 'SPECIAL_PRICE', 'PERCENT_OFF', 'QUANTITY_PRICE', 'COMBO')),
    CONSTRAINT ck_promotions__status CHECK (status IN ('DRAFT', 'ACTIVE', 'PAUSED', 'ENDED')),
    CONSTRAINT ck_promotions__validity CHECK (valid_to IS NULL OR valid_to > valid_from),
    CONSTRAINT ck_promotions__days CHECK (days BETWEEN 1 AND 127),
    CONSTRAINT ck_promotions__hours CHECK ((start_time IS NULL) = (end_time IS NULL) AND (start_time IS NULL OR start_time <> end_time)),
    CONSTRAINT ck_promotions__max_applications CHECK (max_applications IS NULL OR max_applications > 0),
    CONSTRAINT ck_promotions__rule CHECK (CASE type
        WHEN 'MULTI_BUY' THEN buy_quantity >= 2 AND pay_quantity >= 0 AND pay_quantity < buy_quantity
        WHEN 'SPECIAL_PRICE' THEN price > 0
        WHEN 'PERCENT_OFF' THEN percent > 0 AND percent <= 100
        WHEN 'QUANTITY_PRICE' THEN price > 0 AND min_quantity > 0
        WHEN 'COMBO' THEN price > 0
        ELSE false END),
    CONSTRAINT ck_promotions__active CHECK (status IN ('DRAFT') OR activated_at IS NOT NULL OR status = 'ENDED'),
    CONSTRAINT ck_promotions__row_version CHECK (row_version > 0)
);

CREATE INDEX ix_promotions__activated_by ON promotions.promotions (activated_by);
CREATE INDEX ix_promotions__active ON promotions.promotions (company_id, valid_from) WHERE status = 'ACTIVE';

-- A qué aplica: exactamente uno de producto (con presentación opcional), categoría (con sus subcategorías) o marca.
CREATE TABLE promotions.promotion_items (
    id            uuid           NOT NULL,
    promotion_id  uuid           NOT NULL,
    product_id    uuid,
    packaging_id  uuid,
    category_id   uuid,
    brand_id      uuid,
    quantity      numeric(19,4)  NOT NULL,
    CONSTRAINT pk_promotion_items PRIMARY KEY (id),
    CONSTRAINT fk_promotion_items__promotion FOREIGN KEY (promotion_id) REFERENCES promotions.promotions (id) ON DELETE CASCADE,
    CONSTRAINT fk_promotion_items__product FOREIGN KEY (product_id) REFERENCES catalog.products (id),
    CONSTRAINT fk_promotion_items__packaging FOREIGN KEY (packaging_id, product_id) REFERENCES catalog.product_packagings (id, product_id),
    CONSTRAINT fk_promotion_items__category FOREIGN KEY (category_id) REFERENCES catalog.categories (id),
    CONSTRAINT fk_promotion_items__brand FOREIGN KEY (brand_id) REFERENCES catalog.brands (id),
    CONSTRAINT ck_promotion_items__target CHECK (num_nonnulls(product_id, category_id, brand_id) = 1 AND (packaging_id IS NULL OR product_id IS NOT NULL)),
    CONSTRAINT ck_promotion_items__quantity CHECK (quantity > 0)
);

CREATE INDEX ix_promotion_items__promotion_id ON promotions.promotion_items (promotion_id);
CREATE INDEX ix_promotion_items__product_id ON promotions.promotion_items (product_id);
CREATE INDEX ix_promotion_items__packaging_id_product_id ON promotions.promotion_items (packaging_id, product_id);
CREATE INDEX ix_promotion_items__category_id ON promotions.promotion_items (category_id);
CREATE INDEX ix_promotion_items__brand_id ON promotions.promotion_items (brand_id);

-- Sucursales en que rige (sin filas y all_branches = true: todas).
CREATE TABLE promotions.promotion_branches (
    id            uuid  NOT NULL,
    promotion_id  uuid  NOT NULL,
    branch_id     uuid  NOT NULL,
    CONSTRAINT pk_promotion_branches PRIMARY KEY (id),
    CONSTRAINT ux_promotion_branches__branch UNIQUE (promotion_id, branch_id),
    CONSTRAINT fk_promotion_branches__promotion FOREIGN KEY (promotion_id) REFERENCES promotions.promotions (id) ON DELETE CASCADE,
    CONSTRAINT fk_promotion_branches__branch FOREIGN KEY (branch_id) REFERENCES org.branches (id)
);

CREATE INDEX ix_promotion_branches__branch_id ON promotions.promotion_branches (branch_id);
